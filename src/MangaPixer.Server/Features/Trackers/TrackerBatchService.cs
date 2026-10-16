namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The Tracking list's Select mode (1.43.2, owner 2026-10-16; it replaces "Send all"): ONE action for up to
/// <see cref="TrackerFacts.MaxBatchSeries"/> of the caller's series, one series after another, each getting exactly what its per-series
/// route does - "Set progress from &lt;tracker&gt; to MangaPixer" = <see cref="TrackerPullService.FromTrackerAsync"/>, "Set progress from
/// MangaPixer to &lt;tracker&gt;" = Send (<see cref="TrackerSyncMode.SendAll"/>: every mark, never lowers), Sync now, Don't sync / Sync again.
/// A series the action does not apply to (by its stored row, the list rules' own <see cref="TrackerListRules.FromTrackerBlock"/> /
/// <see cref="TrackerListRules.ToTrackerBlock"/>) is left as it is and counted by reason; the batch stops at the daily cap or when the
/// account can no longer sync. Only the caller's rows: a series id not on the caller's list for this tracker, or in no library the caller
/// can open, is skipped. Logs: user id, tracker, action, counts and timing only.
/// </summary>
public sealed class TrackerBatchService(
    MangaPixerDbContext db,
    TrackerEntriesService entries,
    TrackerPullService pull,
    TrackerSyncService sync,
    TrackerListService list,
    ILogger<TrackerBatchService> logger)
{
    /// <summary>Problem codes after which nothing else in the batch can succeed: the batch stops.</summary>
    private static readonly HashSet<string> StopCodes = new(StringComparer.Ordinal)
    {
        "needs_reconnect", "not_syncing", "not_connected", "tracker_not_allowed",
    };

    public async Task<(TrackerBatchResultDto? Result, TrackerProblem? Problem)> ApplyAsync(long userId, string tracker, TrackerBatchRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TrackerFacts.IsKnown(tracker))
            return (null, TrackerProblem.UnknownTracker);
        if (!Enum.IsDefined(request.Action))
            return (null, TrackerProblem.Bad("invalid_action", "Unknown action."));
        var seriesIds = (request.SeriesIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (seriesIds.Count is 0 or > TrackerFacts.MaxBatchSeries || seriesIds.Any(id => id.Length > 64))
            return (null, TrackerProblem.Bad("invalid_series", $"Select 1 to {TrackerFacts.MaxBatchSeries} series per request."));
        var reaches = request.Action is TrackerBatchAction.FromTracker or TrackerBatchAction.ToTracker or TrackerBatchAction.SyncNow;
        if (reaches)
        {
            // As the per-series routes: the account must be able to sync before anything is tried.
            var account = await entries.ConnectedAsync(userId, tracker, ct);
            if (account.Problem is not null)
                return (null, account.Problem);
        }

        var watch = Stopwatch.StartNew();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => seriesIds.Contains(r.PublicId))
            .Select(r => new { r.Id, r.PublicId }).ToDictionaryAsync(r => r.PublicId, r => r.Id, StringComparer.Ordinal, ct);
        var listed = await list.ListedEntriesAsync(userId, tracker, records.Values.ToList(), ct);

        var tally = new Tally();
        var touched = new List<long>();
        for (var i = 0; i < seriesIds.Count; i++)
        {
            var seriesId = seriesIds[i];
            if (!records.TryGetValue(seriesId, out var recordId) || !listed.TryGetValue(recordId, out var row))
            {
                tally.Skip(TrackerBatchSkip.NotListed);
                continue;
            }
            touched.Add(recordId);
            var stop = request.Action switch
            {
                TrackerBatchAction.FromTracker => await FromTrackerAsync(userId, tracker, seriesId, row.Facts, tally, ct),
                TrackerBatchAction.ToTracker => await ToTrackerAsync(userId, tracker, recordId, row.Entry, row.Facts, tally, ct),
                TrackerBatchAction.SyncNow => await SyncNowAsync(userId, tracker, recordId, row.Entry, row.Facts, tally, ct),
                _ => await SwitchAsync(userId, tracker, seriesId, row.Entry, request.Action == TrackerBatchAction.DontSync, tally, ct),
            };
            if (stop)
            {
                tally.NotReached = seriesIds.Count - i - 1;
                break;
            }
        }

        db.ChangeTracker.Clear();
        var updated = await db.TrackerEntries.AsNoTracking()
            .Where(e => e.UserId == userId && e.Tracker == tracker && touched.Contains(e.RecordId)).ToListAsync(ct);
        var rows = await list.RowsAsync(userId, tracker, updated, ct);
        logger.LogInformation(LogEvents.Trackers.BatchApplied,
            "Tracker batch for user {UserId} on {Tracker}: {Action}, {Series} series, {Done} done, {Skipped} skipped, {Failed} failed, "
            + "{NotReached} not reached, cap {CapReached}, {Marked} marked, in {ElapsedMs} ms",
            userId, tracker, request.Action, seriesIds.Count, tally.Done, tally.Skipped, tally.Failed, tally.NotReached, tally.CapReached,
            tally.Marked, watch.ElapsedMilliseconds);
        return (new TrackerBatchResultDto
        {
            Done = tally.Done,
            Skipped = tally.Skipped,
            Failed = tally.Failed,
            CapReached = tally.CapReached,
            NotReached = tally.NotReached,
            Marked = tally.Marked,
            AlreadyRead = tally.AlreadyRead,
            StoppedCode = tally.StoppedCode,
            Skips = tally.Skips.OrderBy(s => s.Key).Select(s => new TrackerBatchSkipCountDto { Reason = s.Key, Count = s.Value }).ToList(),
            Rows = rows,
        }, null);
    }

    /// <summary>"Set progress from the tracker to MangaPixer" for one series; true = stop the batch.</summary>
    private async Task<bool> FromTrackerAsync(long userId, string tracker, string seriesId, TrackerRowFacts facts, Tally tally, CancellationToken ct)
    {
        var block = TrackerListRules.FromTrackerBlock(facts);
        if (block != TrackerActionBlock.None)
        {
            tally.Skip(SkipOf(block));
            return false;
        }
        var (result, problem) = await pull.FromTrackerAsync(userId, tracker, seriesId, ct);
        db.ChangeTracker.Clear();
        if (problem is null)
        {
            tally.Done++;
            tally.Marked += result!.Marked;
            tally.AlreadyRead += result.AlreadyRead;
            return false;
        }
        switch (problem.Code)
        {
            case "daily_cap":
                tally.CapReached = true;
                return true;
            case "not_on_list":
                tally.Skip(TrackerBatchSkip.NotOnList);
                return false;
            case "no_id":
                tally.Skip(TrackerBatchSkip.NoId);
                return false;
            case "unknown_series":
                tally.Skip(TrackerBatchSkip.NotListed);
                return false;
            default:
                tally.Failed++;
                return Stopped(problem.Code, tally);
        }
    }

    /// <summary>"Set progress from MangaPixer to the tracker" (Send) for one series; true = stop the batch.</summary>
    private async Task<bool> ToTrackerAsync(long userId, string tracker, long recordId, TrackerEntryEntity before, TrackerRowFacts facts, Tally tally,
        CancellationToken ct)
    {
        var block = TrackerListRules.ToTrackerBlock(facts);
        if (block != TrackerActionBlock.None)
        {
            tally.Skip(SkipOf(block));
            return false;
        }
        var entry = await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.SendAll, TrackerSyncTrigger.User, null, ct);
        db.ChangeTracker.Clear();
        return Outcome(entry, written: entry is not null && entry.LastSentAt != before.LastSentAt, tally);
    }

    /// <summary>"Sync now" for one series; true = stop the batch.</summary>
    private async Task<bool> SyncNowAsync(long userId, string tracker, long recordId, TrackerEntryEntity before, TrackerRowFacts facts, Tally tally,
        CancellationToken ct)
    {
        if (!TrackerListService.CanSync(before, facts))
        {
            tally.Skip(before.DontSync ? TrackerBatchSkip.DontSync : TrackerBatchSkip.NoId);
            return false;
        }
        var entry = await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.Automatic, TrackerSyncTrigger.User, null, ct);
        db.ChangeTracker.Clear();
        // Sync now is done once the tracker was read (whether or not it had to write).
        return Outcome(entry, written: entry is not null && entry.RemoteSeenAt != before.RemoteSeenAt, tally);
    }

    /// <summary>The result of one engine sync in the batch's counts; true = stop the batch.</summary>
    private static bool Outcome(TrackerEntryEntity? entry, bool written, Tally tally)
    {
        if (entry is null)
        {
            // The account cannot sync any more (nothing was stored).
            tally.StoppedCode = "not_syncing";
            return true;
        }
        if (entry.ErrorCode == "daily_cap")
        {
            tally.CapReached = true;
            return true;
        }
        if (entry.State == (int)TrackerEntryState.Error)
        {
            tally.Failed++;
            return Stopped(entry.ErrorCode, tally);
        }
        if (entry.State == (int)TrackerEntryState.Waiting && entry.ErrorCode == "rate_limited")
        {
            tally.Skip(TrackerBatchSkip.RetryLater);
            return false;
        }
        if (written)
        {
            tally.Done++;
            return false;
        }
        // Read, nothing to write: why, from what the tracker said.
        tally.Skip((TrackerEntryState)entry.State switch
        {
            TrackerEntryState.LeftAsIs => TrackerBatchSkip.LeftAsIs,
            TrackerEntryState.NotOnList => TrackerBatchSkip.NotOnList,
            TrackerEntryState.NoId => TrackerBatchSkip.NoId,
            TrackerEntryState.DontSync => TrackerBatchSkip.DontSync,
            _ => TrackerBatchSkip.NotAhead,
        });
        return false;
    }

    /// <summary>Don't sync (true) / Sync again (false) for one series - no request; never stops the batch.</summary>
    private async Task<bool> SwitchAsync(long userId, string tracker, string seriesId, TrackerEntryEntity before, bool dontSync, Tally tally,
        CancellationToken ct)
    {
        if (before.DontSync == dontSync)
        {
            tally.Skip(dontSync ? TrackerBatchSkip.AlreadyOff : TrackerBatchSkip.AlreadyOn);
            return false;
        }
        var (_, problem) = await entries.SetDontSyncAsync(userId, tracker, seriesId, dontSync, ct);
        db.ChangeTracker.Clear();
        if (problem is null)
            tally.Done++;
        else
            tally.Skip(TrackerBatchSkip.NotListed);
        return false;
    }

    private static bool Stopped(string? code, Tally tally)
    {
        if (code is null || !StopCodes.Contains(code))
            return false;
        tally.StoppedCode = code;
        return true;
    }

    private static TrackerBatchSkip SkipOf(TrackerActionBlock block) => block switch
    {
        TrackerActionBlock.NotChecked => TrackerBatchSkip.NotChecked,
        TrackerActionBlock.NoId => TrackerBatchSkip.NoId,
        TrackerActionBlock.DontSync => TrackerBatchSkip.DontSync,
        TrackerActionBlock.LeftAsIs => TrackerBatchSkip.LeftAsIs,
        TrackerActionBlock.NotOnList => TrackerBatchSkip.NotOnList,
        _ => TrackerBatchSkip.NotAhead,
    };

    private sealed class Tally
    {
        public int Done { get; set; }
        public int Failed { get; set; }
        public int NotReached { get; set; }
        public bool CapReached { get; set; }
        public int Marked { get; set; }
        public int AlreadyRead { get; set; }
        public string? StoppedCode { get; set; }
        public Dictionary<TrackerBatchSkip, int> Skips { get; } = [];
        public int Skipped => Skips.Values.Sum();

        public void Skip(TrackerBatchSkip reason) => Skips[reason] = Skips.GetValueOrDefault(reason) + 1;
    }
}
