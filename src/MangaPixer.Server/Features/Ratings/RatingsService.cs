namespace com.lifepixer.mangapixer.Server.Features.Ratings;

using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Per-user ratings writes (1.52.0 lane R): set / change / clear one user's 1-10 score of a catalog node (a folder or an archive). Reads
/// (<c>CatalogNodeDto.MyRating</c>, the "My rating" sorts, the Favorites filter and the <c>rating:</c> search term) live in
/// <c>CatalogBrowseService</c>, next to the favourite flag they are read with.
///
/// Authorization: exactly the favourite routes' check - ACCESS to the node's library
/// (<see cref="LibraryAuthorizationService.GetAccessibleLibraryIdsAsync"/>), so a node in a library the user marked Private can be rated
/// under Incognito too (Incognito only hides listings, never blocks a write). An unknown or not accessible node answers
/// <see cref="RatingResult.NodeNotFound"/>, so existence is not leaked. Logs carry the user id, node id and score only, at Debug.
///
/// Change sinks (1.53.0 step 0): after a rating was saved - set, changed or cleared; never when nothing changed - every registered
/// <see cref="IRatingChangeSink"/> is called once with the new score (null = cleared). A sink that throws never fails the request: it is
/// logged at Warning (<see cref="LogEvents.Ratings.RatingSinkFailed"/>: user id, node id and the exception type only) and the next sink
/// still runs. None is registered by default.
/// </summary>
public sealed class RatingsService
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;
    private readonly TimeProvider _time;
    private readonly ILogger<RatingsService>? _logger;
    private readonly IRatingChangeSink[] _sinks;
    private readonly RatingsGeneration? _generation;

    public RatingsService(
        MangaPixerDbContext db, LibraryAuthorizationService auth, TimeProvider? time = null, ILogger<RatingsService>? logger = null,
        IEnumerable<IRatingChangeSink>? sinks = null, RatingsGeneration? generation = null)
    {
        _generation = generation; // 1.53.0 lane S: a change makes the cached server averages stale
        _db = db;
        _auth = auth;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _sinks = sinks?.ToArray() ?? [];
    }

    public enum RatingResult
    {
        /// <summary>The rating holds the asked score after the call (set now, changed, or already that score).</summary>
        Ok,

        /// <summary>The score is outside 1-10.</summary>
        InvalidScore,

        /// <summary>The node does not exist or is not accessible to this user.</summary>
        NodeNotFound,
    }

    /// <summary>Whether <paramref name="score"/> is a storable score (1-10: half a star to five stars).</summary>
    public static bool IsValidScore(int score) => score is >= UserRatingEntity.MinScore and <= UserRatingEntity.MaxScore;

    /// <summary>
    /// Sets the user's rating of <paramref name="nodePublicId"/> to <paramref name="score"/> (an upsert). Idempotent: the same score again
    /// changes nothing (not even <see cref="UserRatingEntity.RatedAt"/>).
    /// </summary>
    public async Task<RatingResult> SetRatingAsync(long userId, string nodePublicId, int score, CancellationToken ct = default)
    {
        if (!IsValidScore(score))
            return RatingResult.InvalidScore;

        var node = await _db.CatalogNodes.Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId })
            .FirstOrDefaultAsync(ct);
        if (node is null)
            return RatingResult.NodeNotFound;

        var accessible = await _auth.GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessible.Contains(node.LibraryId))
            return RatingResult.NodeNotFound;

        var changed = false;
        for (var attempt = 0; ; attempt++)
        {
            var existing = await _db.UserRatings.FirstOrDefaultAsync(r => r.UserId == userId && r.CatalogNodeId == node.Id, ct);
            if (existing is not null)
            {
                if (existing.Score != score)
                {
                    existing.Score = score;
                    existing.RatedAt = _time.GetUtcNow();
                    await _db.SaveChangesAsync(ct);
                    changed = true;
                }
                break;
            }

            var added = _db.UserRatings.Add(new UserRatingEntity
            {
                UserId = userId,
                CatalogNodeId = node.Id,
                Score = score,
                RatedAt = _time.GetUtcNow(),
            });
            try
            {
                // The scope keeps EF's own error line for the lost race below out of the log (see ExpectedRaceScope).
                using (ExpectedRaceScope.Begin())
                    await _db.SaveChangesAsync(ct);
                changed = true;
                break;
            }
            catch (DbUpdateException ex) when (attempt == 0 && ExpectedRaceScope.IsConstraintViolation(ex))
            {
                // Lost a race against a concurrent first rating: the unique (UserId, CatalogNodeId) index rejected the duplicate. Drop
                // the failed insert and apply the score to the row that won, once.
                added.State = EntityState.Detached;
            }
        }

        _logger?.LogDebug(LogEvents.Ratings.RatingSet, "Rating set: user {UserId}, node {NodeId}, score {Score}", userId, node.Id, score);
        if (changed)
        {
            _generation?.Bump();
            await NotifySinksAsync(userId, node.Id, score, ct);
        }
        return RatingResult.Ok;
    }

    /// <summary>
    /// Clears the user's rating of <paramref name="nodePublicId"/>. Idempotent: an unknown node, a node the user cannot open, or a node
    /// without a rating still answers <see cref="RatingResult.Ok"/> - DELETE is defined by the end state (the favourite routes' rule).
    /// </summary>
    public async Task<RatingResult> ClearRatingAsync(long userId, string nodePublicId, CancellationToken ct = default)
    {
        var nodeId = await _db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct);
        if (nodeId is null)
            return RatingResult.Ok;

        var removed = await _db.UserRatings.Where(r => r.UserId == userId && r.CatalogNodeId == nodeId.Value).ExecuteDeleteAsync(ct);
        _logger?.LogDebug(LogEvents.Ratings.RatingCleared, "Rating cleared: user {UserId}, node {NodeId}, removed {Removed}", userId, nodeId.Value, removed);
        if (removed > 0)
        {
            _generation?.Bump();
            await NotifySinksAsync(userId, nodeId.Value, null, ct);
        }
        return RatingResult.Ok;
    }

    /// <summary>
    /// Calls every <see cref="IRatingChangeSink"/> once, in registration order, after the change was saved. A sink's failure is logged
    /// (Warning, ids and the exception type only - never its message, which may carry a title or a path) and swallowed, so the request
    /// still succeeds and the next sink still runs; only the request's own cancellation propagates.
    /// </summary>
    private async Task NotifySinksAsync(long userId, long nodeId, int? score, CancellationToken ct)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.RatingChangedAsync(userId, nodeId, score, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(LogEvents.Ratings.RatingSinkFailed,
                    "Rating change sink failed: user {UserId}, node {NodeId}, {ExceptionType}", userId, nodeId, ex.GetType().Name);
            }
        }
    }
}
