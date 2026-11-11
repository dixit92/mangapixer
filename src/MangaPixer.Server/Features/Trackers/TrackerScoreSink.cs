namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Ratings;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.53.0 (lane T, vault note "MangaPixer - Tracker Ratings"): "Keep my score in step with my MangaPixer rating" - the
/// <see cref="IRatingChangeSink"/> of tracker sync. After a user's rating of a FOLDER was saved (set, changed or cleared), and only when the
/// user turned the switch on for a connected account (<c>tracker_accounts.SyncScores</c>), it enqueues the change into the tracker relay
/// (<see cref="TrackerRelay.EnqueueScore"/>) - no network and at most two small queries in the rating request; <see cref="TrackerScoreSync"/>
/// sends it. An archive's rating is never sent. A rating taken FROM a tracker (<see cref="SuppressFor"/>: the import and "Use
/// &lt;tracker&gt;'s") is not sent back to that tracker; another tracker the user keeps in step still gets it.
/// </summary>
public sealed class TrackerScoreSink(MangaPixerDbContext db, TrackerRelay relay, TrackerSettingsService settings, ILogger<TrackerScoreSink> logger)
    : IRatingChangeSink
{
    private static readonly AsyncLocal<string?> Suppressed = new();

    /// <summary>
    /// While the returned scope is open (in this async flow), rating changes are not sent to <paramref name="tracker"/>: they came from it.
    /// </summary>
    public static IDisposable SuppressFor(string tracker)
    {
        var previous = Suppressed.Value;
        Suppressed.Value = tracker;
        return new Scope(previous);
    }

    public async Task RatingChangedAsync(long userId, long catalogNodeId, int? score, CancellationToken ct)
    {
        if (settings.NetworkDisabledByConfig)
            return;
        var ok = (int)TrackerAccountState.Ok;
        var trackers = await db.TrackerAccounts.AsNoTracking()
            .Where(a => a.UserId == userId && a.SyncScores && a.State == ok)
            .Select(a => a.Tracker).ToListAsync(ct);
        trackers.RemoveAll(t => t == Suppressed.Value || !TrackerFacts.IsKnown(t));
        if (trackers.Count == 0)
            return;
        var folder = (int)CatalogNodeKind.Folder;
        if (!await db.CatalogNodes.AsNoTracking().AnyAsync(n => n.Id == catalogNodeId && n.Kind == folder, ct))
        {
            logger.LogDebug(LogEvents.Trackers.ScoreSinkSkipped, "Tracker score not sent for user {UserId}: {Reason}", userId, "not_a_folder");
            return;
        }
        relay.EnqueueScore(new TrackerScoreWork(userId, catalogNodeId, trackers.Order(StringComparer.Ordinal).ToList()));
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Suppressed.Value = previous;
    }
}
