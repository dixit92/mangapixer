namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Threading.Channels;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The tracker relay's observer (1.43.0): MyAnimeList sends no CORS headers, so the server relays each user's update WHILE they read
/// (owner, 2026-10-13). It only ENQUEUES committed read-mark changes into an in-memory channel - no database, no network on the saving
/// thread - and <see cref="TrackerRelayService"/> consumes them. No timer-driven queue: nothing happens when nobody reads, except a
/// short in-process retry of a rate-limited write. A full channel drops the batch (logged by count): the next read mark, or the user's
/// Sync now, recomputes from committed state.
/// </summary>
public sealed class TrackerRelay : IReadMarkObserver
{
    public const int Capacity = 10_000;

    private readonly Channel<TrackerRelayWork> _channel = Channel.CreateBounded<TrackerRelayWork>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ILogger<TrackerRelay> _logger;
    private readonly object _gate = new();
    private int _pending;
    private TaskCompletionSource _idle = NewIdle(completed: true);

    public TrackerRelay(ILogger<TrackerRelay> logger)
    {
        _logger = logger;
    }

    internal ChannelReader<TrackerRelayWork> Reader => _channel.Reader;

    public void OnReadMarksChanged(IReadOnlyList<ReadMarkChange> changes)
    {
        if (changes.Count == 0)
            return;
        Enqueue(new TrackerRelayWork(changes, null));
    }

    /// <summary>Asks the consumer to run the due retries (of one user, or of everyone).</summary>
    public void EnqueueRetries(long? userId) => Enqueue(new TrackerRelayWork([], userId ?? TrackerRelayWork.AllUsers));

    private void Enqueue(TrackerRelayWork work)
    {
        lock (_gate)
        {
            if (_pending++ == 0)
                _idle = NewIdle(completed: false);
        }
        if (!_channel.Writer.TryWrite(work))
        {
            Done();
            _logger.LogWarning(LogEvents.Trackers.RelayQueueFull, "Tracker relay queue full: {Count} change(s) dropped", work.Changes.Count);
        }
    }

    /// <summary>Called by the consumer after each work item (also when it failed).</summary>
    internal void Done()
    {
        lock (_gate)
        {
            if (--_pending == 0)
                _idle.TrySetResult();
        }
    }

    /// <summary>Completes when every enqueued change has been processed (tests; a graceful wait).</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _idle.Task;
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
            tcs.SetResult();
        return tcs;
    }
}

/// <summary>One item of the relay: committed read-mark changes, or a request to run due retries (<see cref="RetriesFor"/>).</summary>
public sealed record TrackerRelayWork(IReadOnlyList<ReadMarkChange> Changes, long? RetriesFor)
{
    public const long AllUsers = -1;
}

/// <summary>
/// The relay's consumer (1.43.0): one item at a time, in its own DI scope. For each user with a usable tracker account: a new mark
/// (<c>completion</c> / <c>manual</c> syncs at once; <c>bulk</c> / <c>import</c> counts but its jump waits for "Send"; <c>upgrade</c> is a
/// carry-over, not new reading - nothing is sent for it) syncs the series it belongs to; a removed mark recomputes the stored state
/// without a request (trackers are never lowered automatically). Rate-limited writes are retried after the tracker's delay. A failure
/// is logged (type and counts only) and never stops the loop.
/// </summary>
public sealed class TrackerRelayService(TrackerRelay relay, IServiceScopeFactory scopes, TimeProvider time, ILogger<TrackerRelayService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in relay.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<TrackerRelayProcessor>();
                var nextRetry = await processor.ProcessAsync(work, stoppingToken);
                if (nextRetry is { } at)
                    ScheduleRetries(at, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                relay.Done();
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(LogEvents.Trackers.RelayFailed, "Tracker relay failed for {Count} change(s): {ErrorType}",
                    work.Changes.Count, ex.GetType().Name);
            }
            relay.Done();
        }
    }

    /// <summary>A short in-process wait for a rate-limited write (seconds); a restart forgets it - the next read or Sync now retries.</summary>
    private void ScheduleRetries(DateTimeOffset at, CancellationToken stoppingToken)
    {
        var delay = at - time.GetUtcNow();
        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromMinutes(10))
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, time, stoppingToken);
                relay.EnqueueRetries(null);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }
}

/// <summary>The relay's per-item work (scoped): resolve, gate, sync. Returns when the next stored retry is due, if any.</summary>
public sealed class TrackerRelayProcessor(
    MangaPixerDbContext db,
    TrackerSeriesResolver resolver,
    TrackerSyncService sync,
    TrackerSettingsService settings,
    ILogger<TrackerRelayProcessor> logger)
{
    public async Task<DateTimeOffset?> ProcessAsync(TrackerRelayWork work, CancellationToken ct)
    {
        if (settings.NetworkDisabledByConfig)
            return null;
        if (work.RetriesFor is { } retryUser)
        {
            await sync.RunDueRetriesAsync(retryUser == TrackerRelayWork.AllUsers ? null : retryUser, ct);
            return await sync.NextRetryAtAsync(ct);
        }

        var userIds = work.Changes.Select(c => c.UserId).Distinct().ToList();
        // One query decides the common case: nobody in this batch has a usable account -> nothing to do.
        var accounts = await db.TrackerAccounts.AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && a.State == (int)TrackerAccountState.Ok)
            .Select(a => new { a.UserId, a.Tracker }).ToListAsync(ct);
        if (accounts.Count == 0)
            return null;

        var written = 0;
        foreach (var user in accounts.GroupBy(a => a.UserId))
        {
            var userId = user.Key;
            var trackers = new List<string>();
            foreach (var tracker in user.Select(a => a.Tracker).Distinct())
            {
                if (await settings.IsAllowedAsync(tracker, ct))
                    trackers.Add(tracker);
            }
            if (trackers.Count == 0)
                continue;
            var changes = work.Changes.Where(c => c.UserId == userId).ToList();
            // An upgrade mark is a carry-over to a volume file (no new reading): it triggers nothing on its own.
            var added = changes.Where(c => c.Kind == ReadMarkChangeKind.Added && c.Source != Library.Moves.UpgradeCarryOverService.MarkSource).Select(c => c.ItemId).Distinct().ToList();
            var removed = changes.Where(c => c.Kind == ReadMarkChangeKind.Removed).Select(c => c.ItemId).Distinct().ToList();
            var addedRecords = added.Count == 0 ? new List<long>() : (await resolver.RecordsOfAsync(userId, added, ct)).Values.Distinct().ToList();
            var removedRecords = removed.Count == 0 ? new List<long>() : (await resolver.RecordsOfAsync(userId, removed, ct)).Values.Distinct()
                .Except(addedRecords).ToList();

            foreach (var tracker in trackers)
            {
                foreach (var recordId in addedRecords)
                {
                    await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.Automatic, TrackerSyncTrigger.ReadMark, null, ct);
                    written++;
                }
                // A removal only updates series this user already has an entry for (no request).
                var known = removedRecords.Count == 0
                    ? new List<long>()
                    : await db.TrackerEntries.AsNoTracking()
                        .Where(e => e.UserId == userId && e.Tracker == tracker && removedRecords.Contains(e.RecordId))
                        .Select(e => e.RecordId).ToListAsync(ct);
                foreach (var recordId in known)
                    await sync.SyncAsync(userId, tracker, recordId, TrackerSyncMode.Automatic, TrackerSyncTrigger.Removal, null, ct);
            }
            // A user's next read event also runs their due retries.
            await sync.RunDueRetriesAsync(userId, ct);
        }
        logger.LogInformation(LogEvents.Trackers.RelayBatch, "Tracker relay: {Changes} change(s), {Users} user(s), {Syncs} series sync(s)",
            work.Changes.Count, accounts.Select(a => a.UserId).Distinct().Count(), written);
        return await sync.NextRetryAtAsync(ct);
    }
}
