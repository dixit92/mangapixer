namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Why a score was or was not sent (logged; never a series id or a score).</summary>
public enum TrackerScoreOutcome
{
    Sent = 0,
    Unchanged = 1,
    NotOnList = 2,
    NotLinked = 3,
    NotEligible = 4,
    DontSync = 5,
    NoId = 6,
    NotSyncing = 7,
    DailyCap = 8,
    NoTokens = 9,
    RateLimited = 10,
    Failed = 11,
}

/// <summary>
/// 1.53.0 (lane T, vault note "MangaPixer - Tracker Ratings"): sends one user's rating of one folder to the trackers they keep their score in
/// step on, from the tracker relay (<see cref="TrackerRelayService"/>). For each tracker, ALL must hold: the switch is on for a connected
/// account under the current consent, the tracker is allowed, the user is active; the folder has its OWN Confirmed / Auto link to a series
/// (an archive, or a folder below the linked one, is never sent); the folder may sync (the progress sync's rule - not in the user's Private
/// library and not doujinshi unless the user turned sync on there; never below a sync-off switch); not "Don't sync" for that series; a known
/// tracker id (never a title search). Then: ONE read of the user's entry - none on any list sends nothing (a score never adds an entry) -,
/// the current score (MyAnimeList: in that read; MangaUpdates: one more read), and ONE write only when it differs from the user's CURRENT
/// rating (a clear removes the score). Every request counts against the user's daily cap; pacing and slow-down are the clients' (as for
/// progress). A rate-limited tracker is returned for an in-process retry (at most <see cref="TrackerFacts.MaxRetryAttempts"/>); a refused
/// sign-in turns the account "needs reconnect". Logs: user id, tracker, outcome, request count, timing - never a series id or a score.
/// </summary>
public sealed class TrackerScoreSync(
    MangaPixerDbContext db,
    TrackerSettingsService settings,
    TrackerAccountService accounts,
    TrackerClients clients,
    TrackerSeriesResolver resolver,
    TimeProvider time,
    ILogger<TrackerScoreSync> logger)
{
    /// <summary>Sends the change to each of its trackers; returns the trackers that asked to slow down, with when to try again.</summary>
    public async Task<IReadOnlyList<(string Tracker, DateTimeOffset At)>> SendAsync(TrackerScoreWork work, CancellationToken ct)
    {
        var retries = new List<(string, DateTimeOffset)>();
        if (settings.NetworkDisabledByConfig)
            return retries;
        foreach (var tracker in work.Trackers.Distinct(StringComparer.Ordinal))
        {
            var (outcome, requests, retryAt, elapsed) = await SendOneAsync(work, tracker, ct);
            if (retryAt is { } at)
                retries.Add((tracker, at));
            logger.LogInformation(LogEvents.Trackers.ScoreSent,
                "Tracker score for user {UserId} on {Tracker}: {Outcome}, {Requests} request(s) in {ElapsedMs} ms",
                work.UserId, tracker, outcome, requests, elapsed);
        }
        return retries;
    }

    private async Task<(TrackerScoreOutcome Outcome, int Requests, DateTimeOffset? RetryAt, long ElapsedMs)> SendOneAsync(TrackerScoreWork work,
        string tracker, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var userId = work.UserId;
        var account = await db.TrackerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.Tracker == tracker, ct);
        if (account is null || !account.SyncScores || account.State != (int)TrackerAccountState.Ok
            || account.ConsentVersion != TrackerConsent.UserVersion(tracker)
            || !await settings.IsAllowedAsync(tracker, ct) || clients.Get(tracker) is not { } client || client is not ITrackerScores scores
            || !await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct))
            return (TrackerScoreOutcome.NotSyncing, 0, null, watch.ElapsedMilliseconds);

        if (await resolver.OwnLinkedRecordOfFolderAsync(work.NodeId, ct) is not { } recordId)
            return (TrackerScoreOutcome.NotLinked, 0, null, watch.ElapsedMilliseconds);
        logger.LogDebug(LogEvents.Trackers.ScoreDebug, "Tracker score for user {UserId} on {Tracker}: record {RecordId}", userId, tracker, recordId);
        if (!await resolver.MaySyncNodeAsync(userId, work.NodeId, recordId, ct))
            return (TrackerScoreOutcome.NotEligible, 0, null, watch.ElapsedMilliseconds);
        if (await db.TrackerEntries.AsNoTracking().AnyAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId && e.DontSync, ct))
            return (TrackerScoreOutcome.DontSync, 0, null, watch.ElapsedMilliseconds);
        if (await resolver.RemoteIdAsync(recordId, tracker, ct) is not { } remoteId)
            return (TrackerScoreOutcome.NoId, 0, null, watch.ElapsedMilliseconds);

        // The CURRENT rating (a later change may already have replaced the one that queued this).
        var mine = await db.UserRatings.AsNoTracking().Where(r => r.UserId == userId && r.CatalogNodeId == work.NodeId)
            .Select(r => (int?)r.Score).FirstOrDefaultAsync(ct);
        if (!TrackerScoreRules.IsSendable(mine))
            return (TrackerScoreOutcome.Failed, 0, null, watch.ElapsedMilliseconds);

        var requests = 0;
        var now = time.GetUtcNow();
        try
        {
            if (!await accounts.TryCountRequestsAsync(account, 1, ct))
                return await DoneAsync(TrackerScoreOutcome.DailyCap, requests, null, watch);
            var tokens = await accounts.TokensForSyncAsync(account, client, ct);
            if (tokens is null)
                return await DoneAsync(TrackerScoreOutcome.NoTokens, requests, null, watch);

            requests++;
            var entry = await client.ReadEntryAsync(tokens, remoteId.Id, ct);
            if (entry is null)
                return await DoneAsync(TrackerScoreOutcome.NotOnList, requests, null, watch, account, now);

            if (!scores.ScoreInEntry && !await accounts.TryCountRequestsAsync(account, 1, ct))
                return await DoneAsync(TrackerScoreOutcome.DailyCap, requests, null, watch);
            if (!scores.ScoreInEntry)
                requests++;
            var theirs = await scores.ReadScoreAsync(tokens, remoteId.Id, entry, ct);
            if (!TrackerScoreRules.NeedsWrite(theirs, mine))
                return await DoneAsync(TrackerScoreOutcome.Unchanged, requests, null, watch, account, now);

            if (!await accounts.TryCountRequestsAsync(account, 1, ct))
                return await DoneAsync(TrackerScoreOutcome.DailyCap, requests, null, watch);
            requests++;
            await scores.WriteScoreAsync(tokens, remoteId.Id, mine, ct);
            return await DoneAsync(TrackerScoreOutcome.Sent, requests, null, watch, account, now);
        }
        catch (TrackerException ex)
        {
            account.LastFailureAt = now;
            account.LastFailureCode = TrackerSyncService.CodeOf(ex.Failure);
            logger.LogWarning(LogEvents.Trackers.ScoreFailed, "Tracker score for user {UserId} on {Tracker} failed: {Failure} {Status} in {ElapsedMs} ms",
                userId, tracker, ex.Failure, ex.StatusCode, watch.ElapsedMilliseconds);
            if (ex.Failure == TrackerFailure.NeedsReconnect)
            {
                await accounts.MarkNeedsReconnectAsync(account, "needs_reconnect", ct);
                return (TrackerScoreOutcome.Failed, requests, null, watch.ElapsedMilliseconds);
            }
            await db.SaveChangesAsync(ct);
            if (ex.Failure == TrackerFailure.RateLimited && work.Attempt < TrackerFacts.MaxRetryAttempts)
            {
                var delay = ex.RetryAfter is { } r ? TimeSpan.FromSeconds(Math.Clamp(r.TotalSeconds, 1, 600)) : TimeSpan.FromSeconds(5);
                logger.LogInformation(LogEvents.Trackers.ScoreRateLimited, "Tracker score for user {UserId} on {Tracker} retried in {DelaySeconds} s (attempt {Attempt})",
                    userId, tracker, (int)delay.TotalSeconds, work.Attempt + 1);
                return (TrackerScoreOutcome.RateLimited, requests, time.GetUtcNow() + delay, watch.ElapsedMilliseconds);
            }
            return (TrackerScoreOutcome.Failed, requests, null, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            account.LastFailureAt = now;
            account.LastFailureCode = "unavailable";
            await db.SaveChangesAsync(ct);
            return (TrackerScoreOutcome.Failed, requests, null, watch.ElapsedMilliseconds);
        }
    }

    private async Task<(TrackerScoreOutcome, int, DateTimeOffset?, long)> DoneAsync(TrackerScoreOutcome outcome, int requests, DateTimeOffset? retryAt,
        Stopwatch watch, TrackerAccountEntity? success = null, DateTimeOffset? now = null)
    {
        if (success is not null && now is { } at)
            success.LastSuccessAt = at;
        await db.SaveChangesAsync(CancellationToken.None);
        return (outcome, requests, retryAt, watch.ElapsedMilliseconds);
    }
}
