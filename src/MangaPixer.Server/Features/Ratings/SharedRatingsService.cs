namespace com.lifepixer.mangapixer.Server.Features.Ratings;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// The ratings generation (1.53.0 shared ratings): a process-wide counter that every change which can move a server average bumps - a
/// rating set or cleared, a user's "Share my ratings" choice, the admin's switch, a user's Private libraries. The averages are cached under
/// it, so a withdrawal (sharing off, a library made Private, the switch off) shows on the very next request. Singleton; the
/// <c>IFacetIndexGeneration</c> pattern.
/// </summary>
public sealed class RatingsGeneration
{
    private long _generation = 1;

    /// <summary>The current generation (starts at 1, only grows within a process).</summary>
    public long Current => Interlocked.Read(ref _generation);

    /// <summary>Marks every cached average as stale.</summary>
    public void Bump() => Interlocked.Increment(ref _generation);
}

/// <summary>Audit verbs of the shared ratings switch (1.53.0). Ids only - the row says who turned it on or off and when.</summary>
public static class RatingAuditActions
{
    public const string SharingEnable = "ratings.sharing.enable";
    public const string SharingDisable = "ratings.sharing.disable";
}

/// <summary>
/// Shared ratings (1.53.0 lane S; owner decisions 3a-3d of 2026-11-10): the server-wide switch (<see cref="AppSettingsEntity.RatingSharingEnabled"/>,
/// off until an admin turns it on), each user's "Share my ratings" choice (<see cref="ReaderPreferencesEntity.ShareRatings"/>, on by
/// default - never part of the <c>library-preferences</c> blob) and the per-node aggregate other users see: the average and the count of
/// the ratings of every user who shares, computed on read from <c>user_ratings</c> (no copy, no snapshot, so turning sharing off withdraws
/// at once). Never who rated what; no admin view of it; no minimum count.
/// <para>
/// A rating counts when (1) the switch is on, (2) its rater has not turned sharing off (no preferences row = the default, on), and (3) the
/// node is NOT in a library its RATER marked Private (whatever the viewer's Incognito state). No rollup: a folder's average is the folder's
/// own ratings. The single-node average is cached for <see cref="CacheLifetime"/> under the <see cref="RatingsGeneration"/>.
/// </para>
/// </summary>
public sealed class SharedRatingsService
{
    /// <summary>How long one node's average is kept (on top of the generation, which makes every change visible at once).</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly MangaPixerDbContext _db;
    private readonly RatingsGeneration _generation;
    private readonly IMemoryCache _cache;
    private readonly AuditService _audit;
    private readonly ILogger<SharedRatingsService> _logger;

    public SharedRatingsService(MangaPixerDbContext db, RatingsGeneration generation, IMemoryCache cache, AuditService audit,
        ILogger<SharedRatingsService> logger)
    {
        _db = db;
        _generation = generation;
        _cache = cache;
        _audit = audit;
        _logger = logger;
    }

    private sealed record CacheKey(long Generation, long NodeId);

    private sealed record CachedAverage(ServerRatingDto? Value);

    /// <summary>Whether the server-wide switch is on (false without a settings row).</summary>
    public static Task<bool> IsServerEnabledAsync(MangaPixerDbContext db, CancellationToken ct) =>
        db.AppSettings.Where(s => s.Id == AppSettingsEntity.SingletonId).Select(s => s.RatingSharingEnabled).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The ratings that count in the averages of <paramref name="libraryId"/>'s nodes: raters who share (no preferences row = on), the
    /// library not Private for the rater. The switch is NOT checked here (callers check it once).
    /// </summary>
    public static IQueryable<UserRatingEntity> CountingRatings(MangaPixerDbContext db, long libraryId) =>
        db.UserRatings.Where(r =>
            !db.ReaderPreferences.Any(p => p.UserId == r.UserId && !p.ShareRatings)
            && !db.PrivateLibraries.Any(p => p.UserId == r.UserId && p.LibraryId == libraryId));

    /// <summary>The admin switch.</summary>
    public async Task<AdminRatingSharingDto> GetServerAsync(CancellationToken ct = default) =>
        new() { Enabled = await IsServerEnabledAsync(_db, ct) };

    /// <summary>
    /// Turns the server-wide switch on or off (an admin). A change is written to the audit trail (who and when) and makes every cached
    /// average stale; no rating is deleted either way.
    /// </summary>
    public async Task<AdminRatingSharingDto> SetServerAsync(bool enabled, string? actorUserName, CancellationToken ct = default)
    {
        var settings = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (settings is null)
        {
            settings = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            _db.AppSettings.Add(settings);
        }
        var changed = settings.RatingSharingEnabled != enabled;
        settings.RatingSharingEnabled = enabled;
        await _db.SaveChangesAsync(ct);
        _generation.Bump();
        if (changed)
        {
            _logger.LogInformation(LogEvents.SharedRatings.ServerSharingChanged, "Shared ratings turned {State}", enabled ? "on" : "off");
            await _audit.RecordAsync(enabled ? RatingAuditActions.SharingEnable : RatingAuditActions.SharingDisable, AuditResults.Success,
                actorUserName, ct: ct);
        }
        return new AdminRatingSharingDto { Enabled = enabled };
    }

    /// <summary>The user's own choice (on without a preferences row) and the server switch.</summary>
    public async Task<RatingSharingDto> GetUserAsync(long userId, CancellationToken ct = default)
    {
        var share = await _db.ReaderPreferences.Where(p => p.UserId == userId).Select(p => (bool?)p.ShareRatings).FirstOrDefaultAsync(ct);
        return new RatingSharingDto { Share = share ?? true, ServerEnabled = await IsServerEnabledAsync(_db, ct) };
    }

    /// <summary>Sets the user's own choice (creates the preferences row with its defaults when absent); applies on the next request.</summary>
    public async Task SetUserAsync(long userId, bool share, CancellationToken ct = default)
    {
        var prefs = await _db.ReaderPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (prefs is null)
        {
            prefs = new ReaderPreferencesEntity { UserId = userId };
            _db.ReaderPreferences.Add(prefs);
        }
        prefs.ShareRatings = share;
        await _db.SaveChangesAsync(ct);
        _generation.Bump();
        _logger.LogDebug(LogEvents.SharedRatings.UserSharingChanged, "Rating sharing: user {UserId}, share {Share}", userId, share);
    }

    /// <summary>
    /// The server average of one node (<paramref name="nodeId"/> in <paramref name="libraryId"/>): null while the switch is off or when no
    /// counting rating exists. The caller has checked the viewer's access to the node. Cached per node and generation.
    /// </summary>
    public async Task<ServerRatingDto?> GetAverageAsync(long nodeId, long libraryId, CancellationToken ct = default)
    {
        if (!await IsServerEnabledAsync(_db, ct))
            return null;

        var key = new CacheKey(_generation.Current, nodeId); // read BEFORE querying: a change during the query makes this entry stale
        if (_cache.TryGetValue(key, out CachedAverage? cached) && cached is not null)
            return cached.Value;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var stats = await CountingRatings(_db, libraryId)
            .Where(r => r.CatalogNodeId == nodeId)
            .GroupBy(r => r.CatalogNodeId)
            .Select(g => new { Count = g.Count(), Sum = g.Sum(r => r.Score) })
            .FirstOrDefaultAsync(ct);
        var value = stats is null || stats.Count == 0
            ? null
            : new ServerRatingDto { Average = Math.Round((double)stats.Sum / stats.Count, 2, MidpointRounding.AwayFromZero), Count = stats.Count };
        _cache.SetSized(key, new CachedAverage(value), CacheLifetime, 1);
        _logger.LogDebug(LogEvents.SharedRatings.AverageComputed, "Server average: node {NodeId}, {Count} ratings, {ElapsedMs} ms",
            nodeId, stats?.Count ?? 0, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return value;
    }
}
