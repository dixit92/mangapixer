namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The per-series actions of the Tracking page that are new in the 1.43.0 redesign (lane G):
/// <list type="bullet">
/// <item>"Set progress from &lt;tracker&gt; to MangaPixer" (owner, 2026-10-15; reverses D2 Q10 for an explicit click only): ONE read of the
/// user's entry, then every archive of the series in the user's libraries that the tracker's numbers cover (<see cref="TrackerReadOn"/>) is
/// marked read with source <c>tracker</c> - a read mark only, like a folder mark-read (no page position changes). Never unmarks. The relay
/// ignores these marks: nothing goes out to any tracker for them.</item>
/// <item>The user's start / finish dates for a series on a tracker that keeps dates (owner A1): stored with the user's entry, sent on the
/// next write, cleared = automatic again.</item>
/// </list>
/// Logs and the audit carry the user id, the tracker and counts only.
/// </summary>
public sealed class TrackerPullService(
    MangaPixerDbContext db,
    TrackerEntriesService entries,
    TrackerAccountService accounts,
    TrackerClients clients,
    TrackerSeriesResolver resolver,
    TrackerSyncService sync,
    TrackerListService list,
    TimeProvider time,
    ILogger<TrackerPullService> logger)
{
    public async Task<(TrackerFromTrackerResultDto? Result, TrackerProblem? Problem)> FromTrackerAsync(long userId, string tracker, string seriesId,
        CancellationToken ct)
    {
        var (recordId, problem) = await entries.UsableAsync(userId, tracker, seriesId, ct);
        if (problem is not null)
            return (null, problem);
        if (clients.Get(tracker) is not { } client)
            return (null, TrackerProblem.Conflict("not_syncing", "Your account on this tracker cannot sync right now."));
        var remoteId = await resolver.RemoteIdAsync(recordId, tracker, ct);
        if (remoteId is null)
            return (null, TrackerProblem.Conflict("no_id", "MangaPixer does not know this series' number on this tracker."));

        var account = await db.TrackerAccounts.FirstAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (!await accounts.TryCountRequestsAsync(account, 1, ct))
        {
            await db.SaveChangesAsync(ct);
            return (null, TrackerProblem.Conflict("daily_cap", "Today's requests to this tracker are used up; try again tomorrow."));
        }
        var tokens = await accounts.TokensForSyncAsync(account, client, ct);
        if (tokens is null)
        {
            await db.SaveChangesAsync(ct);
            return (null, TrackerProblem.Conflict("needs_reconnect", "Connect your account again."));
        }

        var watch = Stopwatch.StartNew();
        TrackerEntry? remote;
        try
        {
            remote = await client.ReadEntryAsync(tokens, remoteId.Id, ct);
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = time.GetUtcNow();
            account.LastFailureCode = TrackerSyncService.CodeOf(ex.Failure);
            if (ex.Failure == TrackerFailure.NeedsReconnect)
                await accounts.MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
            else
                await db.SaveChangesAsync(ct);
            logger.LogWarning(LogEvents.Trackers.SyncFailed, "Tracker read for user {UserId} on {Tracker} failed: {Failure} {Status} in {ElapsedMs} ms",
                userId, tracker, ex.Failure, ex.StatusCode, watch.ElapsedMilliseconds);
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
        var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId, ct);
        if (entry is null)
        {
            entry = new TrackerEntryEntity { UserId = userId, Tracker = tracker, RecordId = recordId, State = (int)TrackerEntryState.Pending, UpdatedAt = now };
            db.TrackerEntries.Add(entry);
        }
        entry.RemoteId = remoteId.Id;
        entry.RemoteIdSource = (int)remoteId.Source;
        TrackerSyncService.StoreRemote(entry, remote, now);
        await db.SaveChangesAsync(ct);
        if (remote is null)
            return (null, TrackerProblem.Conflict("not_on_list", "This series is on none of your lists on this tracker."));

        // Mark every covered archive the user has not marked yet (one save; the relay ignores source "tracker").
        var remoteNumbers = new TrackerNumbers(remote.ChaptersRead, remote.VolumesRead);
        var (archives, map) = await resolver.ArchivesAsync(userId, recordId, ct);
        var covered = archives.Where(a => TrackerReadOn.Covers(a.Units, remoteNumbers, map, a.DeclaredEdition)).ToList();
        var already = covered.Count(a => a.Read);
        var toMark = covered.Where(a => !a.Read).Select(a => a.NodeId).ToList();
        var marked = 0;
        if (toMark.Count > 0)
        {
            // A mark made since the scope was read is left as it is (never two marks for one archive).
            var existing = (await db.ReadMarks.AsNoTracking().Where(m => m.UserId == userId && toMark.Contains(m.ItemId)).Select(m => m.ItemId)
                .ToListAsync(ct)).ToHashSet();
            foreach (var id in toMark.Where(id => !existing.Contains(id)))
            {
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = id, MarkedAt = now, Source = TrackerSeriesResolver.TrackerMarkSource });
                marked++;
            }
            already += existing.Count;
            if (marked > 0)
                await db.SaveChangesAsync(ct);
        }

        // The stored numbers follow the new marks without a request (as after a removed mark).
        var updated = await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.Automatic, TrackerSyncTrigger.Removal, null, ct) ?? entry;
        logger.LogInformation(LogEvents.Trackers.PulledFromTracker,
            "Tracker progress pulled for user {UserId} from {Tracker}: {Marked} marked, {Already} already read, in {ElapsedMs} ms",
            userId, tracker, marked, already, watch.ElapsedMilliseconds);
        var row = (await list.RowsAsync(userId, tracker, [updated], ct)).Single();
        return (new TrackerFromTrackerResultDto
        {
            Marked = marked,
            AlreadyRead = already,
            RemoteChapters = remote.ChaptersRead,
            RemoteVolumes = remote.VolumesRead,
            Entry = row,
        }, null);
    }

    /// <summary>Sets (or with null clears) the user's start / finish date of the series on a tracker that keeps dates.</summary>
    public async Task<(TrackerSeriesRowDto? Row, TrackerProblem? Problem)> SetDatesAsync(long userId, string tracker, string seriesId,
        TrackerDatesRequest request, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        if (!TrackerFacts.SupportsDates(tracker))
            return (null, TrackerProblem.Bad("dates_not_supported", "This tracker keeps no dates."));
        var recordId = await entries.RecordIdAsync(userId, seriesId, ct);
        if (recordId is null)
            return (null, TrackerProblem.NotFound("unknown_series", "There is no such series in your libraries."));
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        static bool Plausible(DateOnly? day, DateOnly today) => day is not { } d || (d.Year >= 1900 && d <= today.AddDays(1));
        if (!Plausible(request.StartedOn, today) || !Plausible(request.FinishedOn, today))
            return (null, TrackerProblem.Bad("invalid_date", "Give a date from 1900 to today."));
        if (request is { StartedOn: { } start, FinishedOn: { } finish } && finish < start)
            return (null, TrackerProblem.Bad("invalid_date", "The finish date is before the start date."));

        var now = time.GetUtcNow();
        var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId, ct);
        if (entry is null)
        {
            entry = new TrackerEntryEntity { UserId = userId, Tracker = tracker, RecordId = recordId.Value, State = (int)TrackerEntryState.Pending, UpdatedAt = now };
            db.TrackerEntries.Add(entry);
        }
        entry.StartedOnOverride = TrackerListService.DayOf(request.StartedOn);
        entry.FinishedOnOverride = TrackerListService.DayOf(request.FinishedOn);
        entry.DatesPending = TrackerSyncService.DatesDiffer(entry);
        entry.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(LogEvents.Trackers.DatesChanged, "Tracker dates for user {UserId} on {Tracker}: start {Start}, finish {Finish}",
            userId, tracker, request.StartedOn is null ? "automatic" : "set", request.FinishedOn is null ? "automatic" : "set");
        return ((await list.RowsAsync(userId, tracker, [entry], ct)).Single(), null);
    }
}
