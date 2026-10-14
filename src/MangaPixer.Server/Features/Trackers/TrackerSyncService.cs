namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What started a sync, for the network decision.</summary>
public enum TrackerSyncTrigger
{
    /// <summary>A new read mark (the relay): the tracker is read only when MangaPixer may be ahead of what it last sent or saw.</summary>
    ReadMark = 0,

    /// <summary>The user's own click (Sync now, Send, Set, Send all) or a retry: always reads the tracker first.</summary>
    User = 1,

    /// <summary>The first-connect preview: reads the tracker, NEVER writes; a series MangaPixer is ahead on waits for "Send".</summary>
    Preview = 2,

    /// <summary>A read mark was removed: recompute from stored numbers only, no request (the tracker is never lowered automatically).</summary>
    Removal = 3,
}

/// <summary>
/// The sync of one (user, tracker, series record) (1.43.0): gates (tracker allowed, account connected under the current consent, user
/// active, not "Don't sync", a known tracker id), the user's progress, ONE read of the user's entry, the compare rules
/// (<see cref="TrackerSyncRules"/>), at most ONE write, and the outcome stored in <c>tracker_entries</c>. Requests count against the
/// user's daily cap. A rate-limited write lands in <c>tracker_retries</c>. Logs carry the user id, slug, outcome and counts only.
/// </summary>
public sealed class TrackerSyncService(
    MangaPixerDbContext db,
    TrackerSettingsService settings,
    TrackerAccountService accounts,
    TrackerClients clients,
    TrackerSeriesResolver resolver,
    TimeProvider time,
    ILogger<TrackerSyncService> logger)
{
    /// <summary>A sanitized code for a failure (stored and returned; never a remote message).</summary>
    public static string CodeOf(TrackerFailure failure) => failure switch
    {
        TrackerFailure.NeedsReconnect => "needs_reconnect",
        TrackerFailure.RateLimited => "rate_limited",
        TrackerFailure.Rejected => "rejected",
        _ => "unavailable",
    };

    /// <summary>
    /// Syncs one series. <paramref name="setTo"/> is the user's explicit numbers for <see cref="TrackerSyncMode.SetTo"/>. Returns the
    /// stored entry, or null when the user has no usable account on the tracker (nothing is stored then).
    /// </summary>
    public async Task<TrackerEntryEntity?> SyncAsync(long userId, string tracker, long recordId, TrackerSyncMode mode, TrackerSyncTrigger trigger,
        TrackerNumbers? setTo, CancellationToken ct)
    {
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null || account.State != (int)TrackerAccountState.Ok || account.ConsentVersion != TrackerConsent.UserVersion(tracker))
            return null;
        if (!await settings.IsAllowedAsync(tracker, ct) || clients.Get(tracker) is not { } client)
            return null;
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct))
            return null;

        var now = time.GetUtcNow();
        var entry = await db.TrackerEntries.FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId, ct);
        if (entry is null)
        {
            entry = new TrackerEntryEntity { UserId = userId, Tracker = tracker, RecordId = recordId, State = (int)TrackerEntryState.Pending, UpdatedAt = now };
            db.TrackerEntries.Add(entry);
        }

        var progress = await resolver.ProgressAsync(userId, recordId, ct);
        entry.LocalChapters = progress.Numbers.Automatic.Chapters;
        entry.LocalVolumes = progress.Numbers.Automatic.Volumes;
        entry.UpdatedAt = now;

        if (entry.DontSync && mode != TrackerSyncMode.SetTo)
            return await DoneAsync(entry, TrackerEntryState.DontSync, null, "dont_sync", userId, tracker, ct);

        var remoteId = await resolver.RemoteIdAsync(recordId, tracker, ct);
        entry.RemoteId = remoteId?.Id;
        entry.RemoteIdSource = (int?)remoteId?.Source;
        if (remoteId is null)
            return await DoneAsync(entry, TrackerEntryState.NoId, null, "no_id", userId, tracker, ct);

        var local = mode switch
        {
            TrackerSyncMode.SetTo => setTo ?? TrackerNumbers.None,
            TrackerSyncMode.SendAll => progress.Numbers.All,
            _ => progress.Numbers.Automatic,
        };
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var known = new TrackerNumbers(Higher(entry.LastSentChapters, entry.RemoteChapters), Higher(entry.LastSentVolumes, entry.RemoteVolumes));

        // No request when nothing can be new: a removal, or a read mark that is not ahead of what was sent or seen.
        if (trigger == TrackerSyncTrigger.Removal || (trigger == TrackerSyncTrigger.ReadMark && !local.IsAheadOf(known)))
        {
            var offline = TrackerSyncRules.Decide(new TrackerSyncInput(mode, local, progress.Numbers.All, StoredRemote(entry),
                account.AddStartedSeries, PrivateEntry(account), progress.StartedOn, today, entry.LastSentChapters, entry.LastSentVolumes));
            SetHeld(entry, offline.Held);
            // Never read and nothing held: whether it is on a list is unknown until the next read.
            var state = entry.RemoteSeenAt is null && offline.State == TrackerEntryState.NotOnList ? TrackerEntryState.Pending : offline.State;
            return await DoneAsync(entry, state, null, "offline", userId, tracker, ct);
        }

        if (!accounts.TryCountRequests(account, 1))
            return await CapReachedAsync(entry, userId, tracker, ct);
        var tokens = await accounts.TokensForSyncAsync(account, client, ct);
        if (tokens is null)
            return await DoneAsync(entry, TrackerEntryState.Error, account.State == (int)TrackerAccountState.Ok ? "unavailable" : "needs_reconnect",
                "no_tokens", userId, tracker, ct);

        var watch = Stopwatch.StartNew();
        try
        {
            var remote = await client.ReadEntryAsync(tokens, remoteId.Id, ct);
            if (remote is not null && tracker == TrackerSlugs.MangaUpdates && remote.TotalChapters is null && remote.TotalVolumes is null)
            {
                // MangaUpdates list entries carry no series totals: the stored record's say whether the series is finished.
                var (_, record) = await resolver.MapAsync(recordId, ct);
                var (chapters, volumes, finished) = TrackerSeriesResolver.TotalsOf(record);
                remote = remote with { TotalChapters = chapters, TotalVolumes = volumes, SeriesFinished = finished };
            }
            entry.RemoteChapters = remote?.ChaptersRead;
            entry.RemoteVolumes = remote?.VolumesRead;
            entry.RemoteStatus = (int?)remote?.Status;
            entry.RemoteMissing = remote is null;
            entry.RemoteSeenAt = now;

            var decision = TrackerSyncRules.Decide(new TrackerSyncInput(mode, local, progress.Numbers.All, remote, account.AddStartedSeries,
                PrivateEntry(account), progress.StartedOn, today, entry.LastSentChapters, entry.LastSentVolumes));

            if (decision.Write is { } write && trigger == TrackerSyncTrigger.Preview)
            {
                // The preview never writes: what MangaPixer would send waits for the user's "Send".
                SetHeld(entry, progress.Numbers.All.IsAheadOf(local) ? progress.Numbers.All : local);
                account.LastSuccessAt = now;
                return await DoneAsync(entry, TrackerEntryState.Held, null, "preview", userId, tracker, ct);
            }
            if (decision.Write is { } send)
            {
                if (!accounts.TryCountRequests(account, 1))
                    return await CapReachedAsync(entry, userId, tracker, ct);
                await client.WriteEntryAsync(tokens, remoteId.Id, send, ct);
                entry.LastSentChapters = send.ChaptersRead ?? entry.LastSentChapters;
                entry.LastSentVolumes = send.VolumesRead ?? entry.LastSentVolumes;
                entry.LastSentStatus = (int)send.Status;
                entry.LastSentAt = now;
                entry.RemoteChapters = send.ChaptersRead ?? entry.RemoteChapters;
                entry.RemoteVolumes = send.VolumesRead ?? entry.RemoteVolumes;
                entry.RemoteStatus = (int)send.Status;
                entry.RemoteMissing = false;
            }
            SetHeld(entry, decision.Held);
            await db.TrackerRetries.Where(r => r.UserId == userId && r.Tracker == tracker && r.RecordId == recordId).ExecuteDeleteAsync(ct);
            account.LastSuccessAt = now;
            logger.LogInformation(LogEvents.Trackers.SyncOutcome,
                "Tracker sync for user {UserId} on {Tracker}: {Outcome}, {Requests} request(s) in {ElapsedMs} ms",
                userId, tracker, decision.Write is null ? "no_write" : "written", decision.Write is null ? 1 : 2, watch.ElapsedMilliseconds);
            return await DoneAsync(entry, decision.State, null, null, userId, tracker, ct);
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = now;
            account.LastFailureCode = CodeOf(ex.Failure);
            logger.LogWarning(LogEvents.Trackers.SyncFailed, "Tracker sync failed for user {UserId} on {Tracker}: {Failure} {Status} in {ElapsedMs} ms",
                userId, tracker, ex.Failure, ex.StatusCode, watch.ElapsedMilliseconds);
            switch (ex.Failure)
            {
                case TrackerFailure.NeedsReconnect:
                    await accounts.MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
                    return await DoneAsync(entry, TrackerEntryState.Error, "needs_reconnect", null, userId, tracker, ct);
                case TrackerFailure.RateLimited when trigger != TrackerSyncTrigger.Preview:
                    return await RetryLaterAsync(entry, mode == TrackerSyncMode.SendAll, ex.RetryAfter, userId, tracker, ct);
                default:
                    return await DoneAsync(entry, TrackerEntryState.Error, CodeOf(ex.Failure), null, userId, tracker, ct);
            }
        }
        catch (HttpRequestException)
        {
            return await DoneAsync(entry, TrackerEntryState.Error, "unavailable", null, userId, tracker, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return await DoneAsync(entry, TrackerEntryState.Error, "unavailable", null, userId, tracker, ct);
        }
    }

    private static int? Higher(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private bool PrivateEntry(TrackerAccountEntity account) => account.PrivateEntries && TrackerFacts.SupportsPrivateEntries(account.Tracker);

    /// <summary>The tracker's entry as last read (for decisions that make no request), or null when never read / on no list.</summary>
    private static TrackerEntry? StoredRemote(TrackerEntryEntity entry) =>
        entry.RemoteSeenAt is null || entry.RemoteMissing
            ? null
            : new TrackerEntry((TrackerEntryStatus)(entry.RemoteStatus ?? (int)TrackerEntryStatus.Reading), entry.RemoteChapters, entry.RemoteVolumes,
                null, null, false, null, null);

    private static void SetHeld(TrackerEntryEntity entry, TrackerNumbers held)
    {
        entry.HeldChapters = held.Chapters;
        entry.HeldVolumes = held.Volumes;
    }

    private async Task<TrackerEntryEntity> CapReachedAsync(TrackerEntryEntity entry, long userId, string tracker, CancellationToken ct)
    {
        logger.LogInformation(LogEvents.Trackers.DailyCapReached, "Tracker daily request cap reached for user {UserId} on {Tracker}", userId, tracker);
        return await DoneAsync(entry, TrackerEntryState.Waiting, "daily_cap", null, userId, tracker, ct);
    }

    private async Task<TrackerEntryEntity> RetryLaterAsync(TrackerEntryEntity entry, bool sendHeld, TimeSpan? retryAfter, long userId, string tracker,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var delay = retryAfter is { } r ? TimeSpan.FromSeconds(Math.Clamp(r.TotalSeconds, 1, 600)) : TimeSpan.FromSeconds(5);
        var retry = await db.TrackerRetries.FirstOrDefaultAsync(x => x.UserId == userId && x.Tracker == tracker && x.RecordId == entry.RecordId, ct);
        if (retry is null)
        {
            retry = new TrackerRetryEntity { UserId = userId, Tracker = tracker, RecordId = entry.RecordId, CreatedAt = now };
            db.TrackerRetries.Add(retry);
        }
        retry.Attempts++;
        retry.SendHeld |= sendHeld;
        retry.NotBefore = now + delay;
        logger.LogInformation(LogEvents.Trackers.SyncRateLimited, "Tracker write for user {UserId} on {Tracker} retried in {DelaySeconds} s (attempt {Attempt})",
            userId, tracker, (int)delay.TotalSeconds, retry.Attempts);
        if (retry.Attempts > TrackerFacts.MaxRetryAttempts)
        {
            db.TrackerRetries.Remove(retry);
            return await DoneAsync(entry, TrackerEntryState.Error, "rate_limited", null, userId, tracker, ct);
        }
        return await DoneAsync(entry, TrackerEntryState.Waiting, "rate_limited", null, userId, tracker, ct);
    }

    private async Task<TrackerEntryEntity> DoneAsync(TrackerEntryEntity entry, TrackerEntryState state, string? errorCode, string? debugOutcome,
        long userId, string tracker, CancellationToken ct)
    {
        entry.State = (int)state;
        entry.ErrorCode = errorCode;
        entry.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        if (debugOutcome is not null)
        {
            logger.LogDebug(LogEvents.Trackers.SyncDebug, "Tracker sync for user {UserId} on {Tracker}, record {RecordId}: {Outcome} -> {State}",
                userId, tracker, entry.RecordId, debugOutcome, state);
        }
        return entry;
    }

    /// <summary>The due retries of a user (or of everyone): each one syncs again in the mode it was asked in.</summary>
    public async Task<int> RunDueRetriesAsync(long? userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.TrackerRetries.AsNoTracking()
            .Where(r => r.NotBefore <= now && (userId == null || r.UserId == userId))
            .OrderBy(r => r.NotBefore).Take(50).ToListAsync(ct);
        foreach (var retry in due)
        {
            await SyncAsync(retry.UserId, retry.Tracker, retry.RecordId, retry.SendHeld ? TrackerSyncMode.SendAll : TrackerSyncMode.Automatic,
                TrackerSyncTrigger.User, null, ct);
        }
        return due.Count;
    }

    /// <summary>The earliest time a stored retry is due, or null when none waits.</summary>
    public Task<DateTimeOffset?> NextRetryAtAsync(CancellationToken ct) =>
        db.TrackerRetries.AsNoTracking().OrderBy(r => r.NotBefore).Select(r => (DateTimeOffset?)r.NotBefore).FirstOrDefaultAsync(ct);
}
