namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>One entry of the user's whole list on a tracker: the tracker's id of the series and the user's entry (never a title).</summary>
public sealed record TrackerListItem(string MediaId, TrackerEntry Entry);

/// <summary>One page of the user's whole list, and the offset of the next page (null = the last page).</summary>
public sealed record TrackerListPage(IReadOnlyList<TrackerListItem> Items, int? NextOffset);

/// <summary>
/// 1.43.0 (owner option C, 2026-10-15): a tracker client that can read the user's WHOLE list, page by page, with the user's own sign-in
/// (MyAnimeList: <c>GET /v2/users/@me/mangalist</c>). Optional, so the step-0 <see cref="ITrackerClient"/> stays as it is; a client without
/// it (MangaUpdates) keeps the per-series check.
/// </summary>
public interface ITrackerListReader
{
    bool ReadsWholeList { get; }

    Task<TrackerListPage> ReadListPageAsync(TrackerTokens tokens, int offset, CancellationToken ct);
}

/// <summary>What "Check my list" found by reading the whole list (the notice of the Tracking page).</summary>
public sealed record TrackerListCheckResult(int Pages, int ListEntries, int Matched, int Created, bool CapReached);

/// <summary>
/// "Check my list on &lt;tracker&gt;" with a whole-list read (1.43.0, owner option C): reads every page of the user's list (each page one
/// request against the user's daily cap; the cap stops it mid-list), matches each entry to the series here BY TRACKER ID exactly as
/// <see cref="TrackerSeriesResolver.RemoteIdAsync"/> resolves them (only series the user can open), and stores the tracker's numbers, status,
/// totals and dates on the user's entry of each match - creating it when the series had none - as a single read would. A list entry with no
/// series here is dropped: nothing about it is stored. Series the user read here (eligible marks) that are NOT on the list are stored as
/// "on none of your lists" (no request). Nothing is ever written to the tracker; the states are decided offline
/// (<see cref="TrackerSyncTrigger.ListRead"/>), so a series on the list but never read here shows "tracker ahead". Logs: user id, tracker,
/// pages, entries, matches, timing.
/// </summary>
public sealed class TrackerListCheckService(
    MangaPixerDbContext db,
    TrackerAccountService accounts,
    TrackerSeriesResolver resolver,
    TrackerSyncService sync,
    TimeProvider time,
    ILogger<TrackerListCheckService> logger)
{
    /// <summary>A safety bound on pages (MyAnimeList pages hold 1000 entries).</summary>
    public const int MaxPages = 100;

    public async Task<(TrackerListCheckResult? Result, TrackerProblem? Problem)> CheckAsync(long userId, string tracker, ITrackerClient client,
        ITrackerListReader reader, IReadOnlyCollection<long> markedRecords, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var account = await db.TrackerAccounts.FirstAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        var tokens = await accounts.TokensForSyncAsync(account, client, ct);
        if (tokens is null)
        {
            await db.SaveChangesAsync(ct);
            return (null, TrackerProblem.Conflict("needs_reconnect", "Connect your account again."));
        }

        var items = new Dictionary<string, TrackerEntry>(StringComparer.Ordinal);
        int pages = 0, listEntries = 0;
        var capReached = false;
        int? offset = 0;
        try
        {
            while (offset is { } at && pages < MaxPages)
            {
                if (!await accounts.TryCountRequestsAsync(account, 1, ct))
                {
                    capReached = true;
                    break;
                }
                var page = await reader.ReadListPageAsync(tokens, at, ct);
                pages++;
                listEntries += page.Items.Count;
                foreach (var item in page.Items)
                    items[item.MediaId] = item.Entry;
                offset = page.NextOffset is { } next && next > at ? next : null;
            }
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = time.GetUtcNow();
            account.LastFailureCode = TrackerSyncService.CodeOf(ex.Failure);
            if (ex.Failure == TrackerFailure.NeedsReconnect)
                await accounts.MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
            else
                await db.SaveChangesAsync(ct);
            logger.LogWarning(LogEvents.Trackers.ListChecked, "Tracker list read for user {UserId} on {Tracker} failed after {Pages} page(s): {Failure} {Status}",
                userId, tracker, pages, ex.Failure, ex.StatusCode);
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

        var now = time.GetUtcNow();
        account.LastSuccessAt = now;
        await db.SaveChangesAsync(ct);

        // Match by tracker id; everything else on the list is dropped here and now.
        var byRemoteId = await resolver.RecordsByRemoteIdAsync(userId, tracker, ct);
        var matches = new Dictionary<long, (string RemoteId, TrackerEntry Entry)>();
        foreach (var (mediaId, entry) in items)
        {
            if (byRemoteId.TryGetValue(mediaId, out var records))
            {
                foreach (var recordId in records)
                    matches[recordId] = (mediaId, entry);
            }
        }
        // Read here but on none of the user's lists - only knowable when the whole list was read, and only for a series whose tracker id
        // is known (1.43.1: a series without one could not be looked up; it gets its row as "No <tracker> id", never "not on your list").
        var known = byRemoteId.Values.SelectMany(r => r).ToHashSet();
        var unmatched = markedRecords.Where(r => !matches.ContainsKey(r)).Distinct().ToList();
        var missing = capReached ? [] : unmatched.Where(known.Contains).ToList();
        var noId = unmatched.Where(r => !known.Contains(r)).ToList();

        var wanted = matches.Keys.Concat(missing).Concat(noId).ToList();
        var existing = new Dictionary<long, TrackerEntryEntity>();
        foreach (var chunk in wanted.Chunk(500))
        {
            foreach (var e in await db.TrackerEntries.Where(e => e.UserId == userId && e.Tracker == tracker && chunk.Contains(e.RecordId)).ToListAsync(ct))
                existing[e.RecordId] = e;
        }
        var created = 0;
        TrackerEntryEntity EntryOf(long recordId)
        {
            if (existing.TryGetValue(recordId, out var e))
                return e;
            e = new TrackerEntryEntity { UserId = userId, Tracker = tracker, RecordId = recordId, State = (int)TrackerEntryState.Pending, UpdatedAt = now };
            db.TrackerEntries.Add(e);
            existing[recordId] = e;
            created++;
            return e;
        }
        foreach (var (recordId, (remoteId, entry)) in matches)
        {
            var e = EntryOf(recordId);
            e.RemoteId ??= remoteId;
            TrackerSyncService.StoreRemote(e, entry, now);
        }
        foreach (var recordId in missing)
            TrackerSyncService.StoreRemote(EntryOf(recordId), null, now);
        foreach (var recordId in noId)
            EntryOf(recordId).RemoteMissing = false; // clears 1.43.0's "not on your list" on a series it could not look up
        await db.SaveChangesAsync(ct);

        // The states, decided from what was just stored (no request, never a write).
        foreach (var recordId in wanted)
            await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.SendAll, TrackerSyncTrigger.ListRead, null, ct);

        logger.LogInformation(LogEvents.Trackers.ListChecked,
            "Tracker list read for user {UserId} on {Tracker}: {Pages} page(s), {Entries} entries, {Matched} matched, {Created} new, {Missing} not on the list, cap {CapReached}, in {ElapsedMs} ms",
            userId, tracker, pages, listEntries, matches.Count, created, missing.Count, capReached, watch.ElapsedMilliseconds);
        return (new TrackerListCheckResult(pages, listEntries, matches.Count, created, capReached), null);
    }
}
