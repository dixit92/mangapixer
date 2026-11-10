namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Ratings;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// 1.53.0 (lane T, vault note "MangaPixer - Tracker Ratings"): "Take my scores from &lt;tracker&gt;" - only on the user's click. Reads the
/// user's own scores page by page (<see cref="ITrackerScores.ReadScorePageAsync"/>: MyAnimeList's whole-list read with <c>score</c>, 1000 a
/// request; MangaUpdates' list pages, 100 a request), each request counted against the user's daily cap, and matches them BY TRACKER ID to
/// the series here (<see cref="TrackerSeriesResolver.RecordsByRemoteIdAsync"/> - never a title). For each series with a linked folder the user
/// can see: no MangaPixer rating -> the tracker's score is set on the series' first linked folder; the same -> nothing; another -> KEPT and
/// listed for the user ("Use &lt;tracker&gt;'s", <see cref="UseAsync"/>). Never overwrites silently; nothing is written to the tracker, and a
/// score taken from a tracker is not sent back to it (<see cref="TrackerScoreSink.SuppressFor"/>). Under Incognito only folders of visible
/// libraries count. Logs: user id, tracker, counts, timing.
/// </summary>
public sealed class TrackerScoreImportService(
    MangaPixerDbContext db,
    TrackerEntriesService entries,
    TrackerAccountService accounts,
    TrackerClients clients,
    TrackerSeriesResolver resolver,
    LibraryAuthorizationService libraries,
    RatingsService ratings,
    TimeProvider time,
    ILogger<TrackerScoreImportService> logger)
{
    /// <summary>A safety bound on the requests of one import (MyAnimeList: 1000 entries a request; MangaUpdates: 100 a list page).</summary>
    public const int MaxRequests = 400;

    public async Task<(TrackerScoreImportResultDto? Result, TrackerProblem? Problem)> ImportAsync(long userId, string tracker, bool incognito,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var (_, problem) = await entries.ConnectedAsync(userId, tracker, ct);
        if (problem is not null)
            return (null, problem);
        if (clients.Get(tracker) is not { } client || client is not ITrackerScores scores)
            return (null, TrackerProblem.Unavailable("tracker_unavailable", "This tracker cannot be reached from this server."));

        var account = await db.TrackerAccounts.FirstAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        var tokens = await accounts.TokensForSyncAsync(account, client, ct);
        if (tokens is null)
        {
            await db.SaveChangesAsync(ct);
            return (null, TrackerProblem.Conflict("needs_reconnect", "Connect your account again."));
        }

        var theirs = new Dictionary<string, int>(StringComparer.Ordinal);
        var requests = 0;
        var capReached = false;
        string? cursor = null;
        try
        {
            do
            {
                if (requests >= MaxRequests)
                    break;
                if (!await accounts.TryCountRequestsAsync(account, 1, ct))
                {
                    capReached = true;
                    break;
                }
                requests++;
                var page = await scores.ReadScorePageAsync(tokens, cursor, ct);
                foreach (var item in page.Items)
                    theirs[item.MediaId] = item.Score;
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = time.GetUtcNow();
            account.LastFailureCode = TrackerSyncService.CodeOf(ex.Failure);
            if (ex.Failure == TrackerFailure.NeedsReconnect)
                await accounts.MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
            else
                await db.SaveChangesAsync(ct);
            logger.LogWarning(LogEvents.Trackers.ScoresImported, "Tracker scores read for user {UserId} on {Tracker} failed after {Requests} request(s): {Failure} {Status}",
                userId, tracker, requests, ex.Failure, ex.StatusCode);
            return (null, ex.Failure switch
            {
                TrackerFailure.NeedsReconnect => TrackerProblem.Conflict("needs_reconnect", "Connect your account again."),
                TrackerFailure.Rejected => TrackerProblem.Conflict("rejected", "The tracker refused the request."),
                _ => TrackerProblem.Unavailable(TrackerSyncService.CodeOf(ex.Failure), "The tracker cannot be reached right now; try again later."),
            });
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            await db.SaveChangesAsync(ct);
            return (null, TrackerProblem.Unavailable("unavailable", "The tracker cannot be reached right now; try again later."));
        }
        account.LastSuccessAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // By tracker id; a scored entry with no series here is dropped now (nothing about it is kept).
        var byRemoteId = await resolver.RecordsByRemoteIdAsync(userId, tracker, ct);
        var wanted = new Dictionary<long, int>();
        foreach (var (mediaId, score) in theirs)
        {
            if (byRemoteId.TryGetValue(mediaId, out var records))
            {
                foreach (var recordId in records)
                    wanted[recordId] = score;
            }
        }
        var folders = await FoldersAsync(userId, wanted.Keys.ToList(), incognito, ct);

        int filled = 0, same = 0, matched = 0;
        var differ = new List<(long RecordId, string NodeId, int Mine, int Theirs)>();
        using (TrackerScoreSink.SuppressFor(tracker))
        {
            foreach (var (recordId, score) in wanted.OrderBy(w => w.Key))
            {
                if (!folders.TryGetValue(recordId, out var series))
                    continue;
                matched++;
                if (series.Rated is not { } rated)
                {
                    // No MangaPixer rating yet: take the tracker's.
                    if (await ratings.SetRatingAsync(userId, series.First, score, ct) == RatingsService.RatingResult.Ok)
                        filled++;
                }
                else if (TrackerScoreRules.Decide(rated.Score, score) == TrackerScoreImportOutcome.Same)
                {
                    same++;
                }
                else
                {
                    differ.Add((recordId, rated.NodeId, rated.Score, score));
                }
            }
        }

        var recordIds = differ.Select(d => d.RecordId).ToList();
        var titles = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => new { r.Id, r.PublicId, r.Title }).ToDictionaryAsync(r => r.Id, ct);
        var rows = differ.Where(d => titles.ContainsKey(d.RecordId))
            .Select(d => new TrackerScoreDifferenceDto
            {
                SeriesId = titles[d.RecordId].PublicId,
                Title = titles[d.RecordId].Title,
                NodeId = d.NodeId,
                MyScore = d.Mine,
                TrackerScore = d.Theirs,
            })
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SeriesId, StringComparer.Ordinal).ToList();

        logger.LogInformation(LogEvents.Trackers.ScoresImported,
            "Tracker scores read for user {UserId} on {Tracker}: {Requests} request(s), {Scored} scored, {Matched} here, {Filled} filled, {Same} same, {Differ} differ, cap {CapReached}, in {ElapsedMs} ms",
            userId, tracker, requests, theirs.Count, matched, filled, same, rows.Count, capReached, watch.ElapsedMilliseconds);
        return (new TrackerScoreImportResultDto
        {
            Scored = theirs.Count,
            Matched = matched,
            Filled = filled,
            Same = same,
            Differ = rows,
            Requests = requests,
            CapReached = capReached,
        }, null);
    }

    /// <summary>
    /// "Use &lt;tracker&gt;'s" for one listed series: the caller's rating of the series' rated folder (or, when none is rated any more, its
    /// first linked folder) becomes <paramref name="score"/>. No request to the tracker; not sent back to it.
    /// </summary>
    public async Task<TrackerProblem?> UseAsync(long userId, string tracker, string seriesId, int? score, bool incognito, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return TrackerProblem.UnknownTracker;
        if (score is not { } value || !RatingsService.IsValidScore(value))
            return TrackerProblem.Bad("invalid_score", "Give a score from 1 to 10.");
        var recordId = await entries.RecordIdAsync(userId, seriesId, ct);
        if (recordId is null || !(await FoldersAsync(userId, [recordId.Value], incognito, ct)).TryGetValue(recordId.Value, out var series))
            return TrackerProblem.NotFound("unknown_series", "There is no such series in your libraries.");
        using (TrackerScoreSink.SuppressFor(tracker))
        {
            return await ratings.SetRatingAsync(userId, series.Rated?.NodeId ?? series.First, value, ct) == RatingsService.RatingResult.Ok
                ? null
                : TrackerProblem.NotFound("unknown_series", "There is no such series in your libraries.");
        }
    }

    /// <summary>A series' folders as the import reads them: the first linked folder, and the first one the caller rated (by node id).</summary>
    private sealed record SeriesFolders(string First, (string NodeId, int Score)? Rated);

    /// <summary>
    /// Per record, the folders with their OWN Confirmed / Auto link in a library the caller can see (under Incognito not their Private ones;
    /// not tombstoned), by node id: the first one, and the first one the caller rated. Records without such a folder are left out.
    /// </summary>
    private async Task<Dictionary<long, SeriesFolders>> FoldersAsync(long userId, IReadOnlyList<long> recordIds, bool incognito, CancellationToken ct)
    {
        var result = new Dictionary<long, SeriesFolders>();
        if (recordIds.Count == 0)
            return result;
        var visible = (await libraries.GetVisibleLibraryIdsAsync(userId, incognito, ct)).ToList();
        var states = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        foreach (var chunk in recordIds.Chunk(500))
        {
            var rows = await (from l in db.NodeSeriesLinks.AsNoTracking()
                              join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                              join r in db.UserRatings.AsNoTracking().Where(r => r.UserId == userId) on n.Id equals r.CatalogNodeId into rated
                              from r in rated.DefaultIfEmpty()
                              where l.RecordId != null && chunk.Contains(l.RecordId.Value) && states.Contains(l.State) && n.Kind == folder
                                    && visible.Contains(n.LibraryId) && n.Availability != tombstoned
                              orderby n.Id
                              select new { RecordId = l.RecordId!.Value, n.PublicId, Score = r == null ? (int?)null : r.Score }).ToListAsync(ct);
            foreach (var group in rows.GroupBy(r => r.RecordId))
            {
                var first = group.First();
                var rated = group.FirstOrDefault(r => r.Score is not null);
                result[group.Key] = new SeriesFolders(first.PublicId, rated is null ? null : (rated.PublicId, rated.Score!.Value));
            }
        }
        return result;
    }
}
