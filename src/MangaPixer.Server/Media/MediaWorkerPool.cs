namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Protocol;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Manages a small pool of media worker processes. Dispatches jobs from the
/// JobScheduler to available workers. Handles worker lifecycle:
/// - Start workers on demand
/// - Restart crashed workers with backoff
/// - Retire workers that stay idle past WorkerIdleTimeout (1.22.0), keeping MinWarmWorkers
/// - Stop workers on server shutdown
/// - Enforce at most MaxConcurrentJobs active jobs
///
/// The pool is NOT a security sandbox. Workers are fault/resource-isolation boundaries.
/// </summary>
public sealed class MediaWorkerPool : IAsyncDisposable
{
    private readonly WorkerPoolOptions _options;
    private readonly JobScheduler _scheduler;
    private readonly ScratchWorkspaceManager _scratchManager;
    private readonly ILogger<MediaWorkerPool> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly AnalysisResultPersister? _persister;
    private readonly MediaMetrics _metrics;
    private readonly bool _ownsMetrics;
    private readonly List<WorkerSlot> _workers = [];
    private readonly object _poolLock = new();
    private readonly CancellationTokenSource _readCts = new();
    private readonly object _throughputLock = new();
    private readonly SemaphoreSlim _dispatchSignal = new(0, int.MaxValue);
    private int _completedSinceSummary;
    private int _failedSinceSummary;
    private bool _isStarted;
    private bool _isShuttingDown;
    private int _nextWorkerId;
    private Task? _retirementLoop;

    /// <summary>
    /// Count of <see cref="AcquireSlotAsync"/> calls currently in flight (any kind), including ones that got a slot
    /// immediately - the "waiting slot callers" gauge. Priority and the background limit use the waiter queue
    /// (1.42.0), not this count.
    /// </summary>
    private int _waitingReaders;

    /// <summary>
    /// Worker-start attempts reserved but not yet resolved (success or
    /// failure), guarded by <see cref="_poolLock"/> together with
    /// <see cref="_workers"/>. Prevents two concurrent callers (background
    /// dispatch and reader-demand <see cref="AcquireSlotAsync"/>) from both
    /// deciding to start a worker from the same headroom and jointly
    /// overshooting <see cref="WorkerPoolOptions.MaxConcurrentJobs"/>.
    /// </summary>
    private int _startingWorkers;

    /// <summary>
    /// Number of completed jobs between Information-level throughput summaries.
    /// Per-job events are Debug; this is the default-level view of scan progress.
    /// </summary>
    internal const int ThroughputSummaryInterval = 25;

    public MediaWorkerPool(
        WorkerPoolOptions options,
        JobScheduler scheduler,
        ScratchWorkspaceManager scratchManager,
        ILogger<MediaWorkerPool> logger,
        ILoggerFactory? loggerFactory = null,
        IServiceScopeFactory? scopeFactory = null,
        AnalysisResultPersister? persister = null,
        MediaMetrics? metrics = null)
    {
        _options = options;
        _scheduler = scheduler;
        _scratchManager = scratchManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
        _persister = persister;
        _ownsMetrics = metrics is null;
        _metrics = metrics ?? new MediaMetrics();
        _metrics.ObservePool(() => new MediaPoolGauges(
            _options.MaxConcurrentJobs, WorkerCount, BusyWorkerCount, Volatile.Read(ref _waitingReaders),
            _scheduler.PendingCount, _scheduler.InFlightCount));
    }

    /// <summary>The media metrics this pool records into (1.42.0 profiling).</summary>
    public MediaMetrics Metrics => _metrics;

    /// <summary>
    /// Starts the worker pool. Initializes scratch root, pre-starts workers and,
    /// when <see cref="WorkerPoolOptions.WorkerIdleTimeout"/> is positive, starts
    /// the idle-retirement sweep.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isStarted)
            return;

        _logger.LogInformation(LogEvents.Worker.PoolStarting, "Starting worker pool (max concurrent jobs: {Max}, idle timeout {IdleTimeoutSeconds}s, min warm {MinWarm})",
            _options.MaxConcurrentJobs, _options.WorkerIdleTimeout.TotalSeconds, MinWarmWorkers);
        _scratchManager.Initialize();
        _isStarted = true;

        // Pre-start at least one worker even when MinWarmWorkers is 0: it proves
        // the worker launches (a broken install surfaces at boot, not on the
        // first page) and serves the startup scan that usually follows. Without
        // a warm floor it retires like any other worker once the pool goes quiet.
        var prestart = Math.Max(1, MinWarmWorkers);
        for (var i = 0; i < prestart; i++)
            await StartWorkerAsync(ct);
        _logger.LogInformation(LogEvents.Worker.PoolStarted, "Worker pool started with {Count} worker(s)", WorkerCount);

        if (_options.WorkerIdleTimeout > TimeSpan.Zero)
            _retirementLoop = Task.Run(() => RetirementLoopAsync(_readCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// <see cref="WorkerPoolOptions.MinWarmWorkers"/> clamped to
    /// [0, <see cref="WorkerPoolOptions.MaxConcurrentJobs"/>].
    /// </summary>
    private int MinWarmWorkers => Math.Clamp(_options.MinWarmWorkers, 0, Math.Max(1, _options.MaxConcurrentJobs));

    /// <summary>
    /// Periodically retires idle workers until the pool stops. Runs on the
    /// pool's lifetime token, which <see cref="StopAsync"/> cancels and then
    /// awaits, so a retirement in progress finishes before shutdown stops the
    /// remaining workers.
    /// </summary>
    private async Task RetirementLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(WorkerRetirementPolicy.SweepInterval(_options.WorkerIdleTimeout));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await RetireIdleWorkersAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.Worker.WorkerRetireFailed, ex, "Idle worker retirement failed: {Error}", ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Retires every worker the <see cref="WorkerRetirementPolicy"/> selects.
    /// Selection and removal happen atomically under <see cref="_poolLock"/>,
    /// the same lock every slot claim takes, so a selected worker is idle at
    /// removal and can no longer be claimed afterwards: dispatch and reader
    /// demand simply see a smaller pool and start a fresh worker if they need
    /// one. The process is then stopped gracefully (shutdown message, grace
    /// period, kill) outside the lock. Returns the number retired.
    /// </summary>
    internal async Task<int> RetireIdleWorkersAsync()
    {
        if (_isShuttingDown || _options.WorkerIdleTimeout <= TimeSpan.Zero)
            return 0;

        List<WorkerSlot> retiring;
        int remaining;
        var now = Environment.TickCount64;
        lock (_poolLock)
        {
            var snapshot = _workers.Select(w => new WorkerIdleState(w.Id, w.IsBusy, w.IdleSinceMs)).ToList();
            var ids = WorkerRetirementPolicy.SelectForRetirement(snapshot, now, _options.WorkerIdleTimeout, MinWarmWorkers);
            if (ids.Count == 0)
                return 0;
            retiring = _workers.Where(w => ids.Contains(w.Id)).ToList();
            foreach (var slot in retiring)
            {
                slot.IsRetiring = true;
                _workers.Remove(slot);
            }
            remaining = _workers.Count;
        }

        foreach (var slot in retiring)
        {
            _logger.LogInformation(LogEvents.Worker.WorkerRetiredIdle, "Retiring worker {Id} after {IdleSeconds:F0}s idle; pool now {Count}/{Max}",
                slot.Id, (now - slot.IdleSinceMs) / 1000.0, remaining, _options.MaxConcurrentJobs);
            try
            {
                await slot.Supervisor.StopAsync("idle_timeout");
                await slot.Supervisor.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Worker.WorkerRetireFailed, ex, "Error retiring worker {Id}: {Error}", slot.Id, ex.GetType().Name);
            }
        }
        return retiring.Count;
    }

    /// <summary>
    /// Frees a claimed slot (1.42.0: hands it over). Under <see cref="_poolLock"/> the slot goes straight to the
    /// waiter that should run next (<see cref="TakeNextWaiterLocked"/>: reader pages first, background last), so a
    /// reader never has to win a race against background work for a freed slot. With no eligible waiter the slot
    /// becomes free and background dispatch is woken. A worker above a lowered <see cref="WorkerPoolOptions.MaxConcurrentJobs"/>
    /// is retired instead. When <paramref name="didWork"/> is true the slot's idle clock restarts; a slot claimed and
    /// released without running anything (empty queue race) keeps its original idle time so polling can never keep a
    /// worker alive.
    /// </summary>
    private void ReleaseSlot(WorkerSlot slot, bool didWork = true)
    {
        SlotWaiter? next = null;
        var retire = false;
        lock (_poolLock)
        {
            slot.IsBusy = false;
            slot.HeldByBackground = false;
            if (didWork)
                slot.IdleSinceMs = Environment.TickCount64;
            if (!_workers.Contains(slot))
                return; // retired or exited meanwhile
            if (_workers.Count > _options.MaxConcurrentJobs && !_isShuttingDown)
            {
                retire = true;
                slot.IsRetiring = true;
                _workers.Remove(slot);
            }
            else
            {
                next = TakeNextWaiterLocked(Environment.TickCount64);
                if (next is not null)
                {
                    slot.IsBusy = true;
                    slot.HeldByBackground = next.Class == SlotClass.Background;
                }
            }
        }

        if (retire)
        {
            _ = StopRetiredWorkerAsync(slot, "pool_resized");
            PumpWaiters();
            return;
        }
        if (next is null)
        {
            SignalDispatch();
            return;
        }
        if (!next.Tcs.TrySetResult(slot))
            ReleaseSlot(slot, didWork: false); // the waiter left at the same instant: offer the slot to the next one
    }

    private async Task StopRetiredWorkerAsync(WorkerSlot slot, string reason)
    {
        try
        {
            _logger.LogInformation(LogEvents.Worker.WorkerRetiredIdle, "Retiring worker {Id} ({Reason}); pool now {Count}/{Max}",
                slot.Id, reason, WorkerCount, _options.MaxConcurrentJobs);
            await slot.Supervisor.StopAsync(reason);
            await slot.Supervisor.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.WorkerRetireFailed, ex, "Error retiring worker {Id}: {Error}", slot.Id, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Fills every background-eligible slot in one pass instead of dispatching
    /// one job per call. Each dispatched job runs to completion in the
    /// background (not awaited here) so multiple jobs run concurrently across
    /// up to <see cref="WorkerPoolOptions.MaxConcurrentJobs"/> workers; this
    /// method returns once no further slot/job pair is available, not once all
    /// dispatched jobs finish. See <see cref="TryDispatchOneAsync"/> for the
    /// per-slot reservation and the reader-first rule.
    /// </summary>
    public async Task DispatchAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var dispatched = await TryDispatchOneAsync(ct);
            if (!dispatched)
                break;
        }
    }

    // --- Reader-first slot hand-off (1.42.0) -------------------------------------------------------------------

    /// <summary>How long after the last reader request the pool still counts as "someone is reading".</summary>
    internal static readonly TimeSpan ReaderActivityWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The minimum background share: a background request that has waited this long is served before reader
    /// prefetches (never before a reader's current page, and never above the background limit), so background work
    /// cannot starve while people read - except with <see cref="BackgroundWhileReading.Pause"/>, which means pause.
    /// </summary>
    internal static readonly TimeSpan BackgroundAging = TimeSpan.FromSeconds(10);

    private readonly List<SlotWaiter> _waiters = [];
    private long _waiterSeq;
    private long _lastReaderActivityMs = long.MinValue / 2;
    private BackgroundWhileReading _backgroundWhileReading = BackgroundWhileReading.Slow;

    /// <summary>
    /// What background work (analysis, cover thumbnails, backfills, cover renders, hashes, ComicInfo) may do while
    /// someone reads (a reader page request within <see cref="ReaderActivityWindow"/>). Live: a change re-evaluates
    /// waiting requests at once.
    /// </summary>
    public BackgroundWhileReading BackgroundWhileReading
    {
        get { lock (_poolLock) return _backgroundWhileReading; }
        set
        {
            lock (_poolLock) _backgroundWhileReading = value;
            PumpWaiters();
            SignalDispatch();
        }
    }

    /// <summary>The worker cap in effect (live; <see cref="SetMaxConcurrentJobs"/>).</summary>
    public int MaxConcurrentJobs
    {
        get { lock (_poolLock) return _options.MaxConcurrentJobs; }
    }

    /// <summary>
    /// Changes the worker cap while running (1.42.0 admin setting). A higher cap lets waiting requests start
    /// workers at once; a lower one retires idle workers above it now and busy ones when they finish.
    /// </summary>
    public void SetMaxConcurrentJobs(int value)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        List<WorkerSlot> retiring;
        lock (_poolLock)
        {
            _options.MaxConcurrentJobs = value;
            retiring = _workers.Where(w => !w.IsBusy).Take(Math.Max(0, _workers.Count - value)).ToList();
            foreach (var slot in retiring)
            {
                slot.IsRetiring = true;
                _workers.Remove(slot);
            }
        }
        foreach (var slot in retiring)
            _ = StopRetiredWorkerAsync(slot, "pool_resized");
        PumpWaiters();
        SignalDispatch();
    }

    /// <summary>Who runs first when a slot frees: lower is earlier. Analysis dispatch comes after all of them.</summary>
    internal static SlotClass ClassOf(MediaSlotKind kind) => kind switch
    {
        MediaSlotKind.Page => SlotClass.Reader,
        MediaSlotKind.PagePrefetch or MediaSlotKind.PageThumbnail => SlotClass.ReaderPrefetch,
        _ => SlotClass.Background,
    };

    private bool ReaderActiveLocked(long nowMs) =>
        nowMs - _lastReaderActivityMs < (long)ReaderActivityWindow.TotalMilliseconds || _waiters.Any(w => w.Class != SlotClass.Background);

    /// <summary>How many slots background work (analysis included) may hold right now.</summary>
    private int BackgroundLimitLocked(long nowMs)
    {
        var max = _options.MaxConcurrentJobs;
        if (!ReaderActiveLocked(nowMs))
            return max;
        return _backgroundWhileReading switch
        {
            BackgroundWhileReading.Pause => 0,
            BackgroundWhileReading.Normal => max <= 1 ? max : max - 1,
            _ => Math.Min(1, max),
        };
    }

    private int BackgroundBusyLocked() => _workers.Count(w => w.IsBusy && w.HeldByBackground);

    private int OrderOf(SlotWaiter w, long nowMs) => w.Class switch
    {
        SlotClass.Reader => 0,
        SlotClass.ReaderPrefetch => 2,
        _ => _backgroundWhileReading != BackgroundWhileReading.Pause && nowMs - w.EnqueuedMs >= (long)BackgroundAging.TotalMilliseconds ? 1 : 3,
    };

    /// <summary>Removes and returns the waiter that should get a free slot now, or null (none, or background over its limit).</summary>
    private SlotWaiter? TakeNextWaiterLocked(long nowMs)
    {
        if (_waiters.Count == 0)
            return null;
        var backgroundAllowed = BackgroundBusyLocked() < BackgroundLimitLocked(nowMs);
        SlotWaiter? best = null;
        var bestOrder = int.MaxValue;
        foreach (var w in _waiters)
        {
            if (w.Class == SlotClass.Background && !backgroundAllowed)
                continue;
            var order = OrderOf(w, nowMs);
            if (order < bestOrder || (order == bestOrder && w.Seq < best!.Seq))
            {
                best = w;
                bestOrder = order;
            }
        }
        if (best is not null)
            _waiters.Remove(best);
        return best;
    }

    /// <summary>
    /// Re-evaluates the waiters when something other than a freed slot changed (policy, cap, the reader window
    /// expiring, a worker exiting): hands free slots over and starts workers for waiters while under the cap.
    /// </summary>
    private void PumpWaiters()
    {
        while (true)
        {
            SlotWaiter? next;
            WorkerSlot? free;
            var start = false;
            lock (_poolLock)
            {
                if (_isShuttingDown || _waiters.Count == 0)
                    return;
                var now = Environment.TickCount64;
                free = _workers.FirstOrDefault(w => !w.IsBusy);
                if (free is null && _workers.Count + _startingWorkers >= _options.MaxConcurrentJobs)
                    return;
                next = TakeNextWaiterLocked(now);
                if (next is null)
                    return;
                if (free is not null)
                {
                    free.IsBusy = true;
                    free.HeldByBackground = next.Class == SlotClass.Background;
                }
                else
                {
                    _startingWorkers++;
                    start = true;
                }
            }

            if (start)
            {
                _ = StartWorkerForWaiterAsync(next);
                continue;
            }
            if (!next.Tcs.TrySetResult(free!))
                ReleaseSlot(free!, didWork: false);
        }
    }

    /// <summary>Starts a worker (already reserved in <see cref="_startingWorkers"/>) and hands it to <paramref name="waiter"/>.</summary>
    private async Task StartWorkerForWaiterAsync(SlotWaiter waiter)
    {
        WorkerSlot slot;
        try
        {
            slot = await StartWorkerAsync(_readCts.Token, claim: true, heldByBackground: waiter.Class == SlotClass.Background).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReleaseWorkerStartReservation();
            if (!_isShuttingDown)
                _logger.LogWarning(LogEvents.Worker.ExtractWorkerStartFailed, ex, "Failed to start worker for a waiting request: {Error}", ex.GetType().Name);
            // A reader waits on (a later release or pump serves it, or its own deadline ends the wait); background work,
            // which has no deadline, gives up now instead of retrying a broken worker start every second.
            if (waiter.Class == SlotClass.Background)
            {
                waiter.Tcs.TrySetResult(null);
                return;
            }
            lock (_poolLock)
            {
                if (!waiter.Tcs.Task.IsCompleted)
                    _waiters.Add(waiter);
            }
            return;
        }
        ReleaseWorkerStartReservation();
        if (_isShuttingDown)
        {
            lock (_poolLock) { _workers.Remove(slot); }
            waiter.Tcs.TrySetResult(null);
            await slot.Supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!waiter.Tcs.TrySetResult(slot))
            ReleaseSlot(slot, didWork: false);
    }

    /// <summary>
    /// Background dispatch's claim on a slot for one analysis job: only when no request waits (every waiting request
    /// runs before analysis) and background work is under its limit (<see cref="BackgroundWhileReading"/>). Never
    /// starts a worker.
    /// </summary>
    private WorkerSlot? TryClaimBackgroundSlot()
    {
        lock (_poolLock)
        {
            if (_waiters.Count > 0 || BackgroundBusyLocked() >= BackgroundLimitLocked(Environment.TickCount64))
                return null;
            var slot = _workers.FirstOrDefault(w => !w.IsBusy);
            if (slot is not null)
            {
                slot.IsBusy = true;
                slot.HeldByBackground = true;
            }
            return slot;
        }
    }

    /// <summary>
    /// Re-checks the rule for a slot that background dispatch claimed at creation (a request may have started
    /// waiting while the worker spawned). Hands it on (without restarting its idle clock) and returns false when
    /// analysis may not keep it.
    /// </summary>
    private bool KeepBackgroundClaim(WorkerSlot slot)
    {
        lock (_poolLock)
        {
            if (_waiters.Count == 0 && BackgroundBusyLocked() <= BackgroundLimitLocked(Environment.TickCount64))
                return true;
        }
        ReleaseSlot(slot, didWork: false);
        return false;
    }

    /// <summary>
    /// Reserves the right to start one more worker for background dispatch, counting existing workers and starts in
    /// flight (<see cref="_startingWorkers"/>) against the cap, and only while no request waits and background work is
    /// under its limit. Pair with <see cref="ReleaseWorkerStartReservation"/>.
    /// </summary>
    private bool TryReserveBackgroundWorkerStart()
    {
        lock (_poolLock)
        {
            if (_waiters.Count > 0 || BackgroundBusyLocked() >= BackgroundLimitLocked(Environment.TickCount64))
                return false;
            if (_workers.Count + _startingWorkers >= _options.MaxConcurrentJobs)
                return false;
            _startingWorkers++;
            return true;
        }
    }

    private void ReleaseWorkerStartReservation()
    {
        lock (_poolLock) { _startingWorkers--; }
    }

    /// <summary>
    /// Releases anyone waiting in <see cref="WaitForDispatchSignalAsync"/> —
    /// called whenever a worker slot frees (background job or reader-demand
    /// extraction completes), so the dispatch loop reacts immediately instead
    /// of waiting out its fallback poll interval.
    /// </summary>
    private void SignalDispatch()
    {
        try { _dispatchSignal.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Waits for a dispatch-relevant event (a worker slot freeing) or
    /// <paramref name="fallbackPoll"/>, whichever comes first. The fallback
    /// catches jobs enqueued directly via the <see cref="JobScheduler"/>
    /// without a manual dispatch nudge (background analysis resume, admin
    /// re-analyze), which this pool is not otherwise notified of.
    /// </summary>
    public async Task WaitForDispatchSignalAsync(TimeSpan fallbackPoll, CancellationToken ct)
    {
        try
        {
            await _dispatchSignal.WaitAsync(fallbackPoll, ct);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Attempts to claim one slot for one background job and, if successful,
    /// starts processing it without awaiting completion — so the pump loop in
    /// <see cref="DispatchAsync"/> can immediately try to fill the next slot.
    /// Returns false when nothing could be dispatched (saturated, reserved
    /// for reader demand, or the queue is empty), which tells the pump to
    /// stop.
    ///
    /// Audit defect D12: reserve a slot BEFORE dequeuing. If no slot is
    /// available, return without touching the queue so the job remains
    /// pending for the next dispatch cycle. Never fail a job for lack
    /// of a worker slot.
    /// </summary>
    private async Task<bool> TryDispatchOneAsync(CancellationToken ct)
    {
        if (_isShuttingDown)
            return false;

        // Nothing queued: do not claim (which would reset a slot's idle clock)
        // and above all do not start a worker. Before 1.22.0 an empty-queue
        // poll with every existing worker busy on a reader extract started an
        // extra worker for nothing, which then idled forever.
        if (_scheduler.PendingCount == 0)
        {
            LogThroughputSummaryIfPending();
            return false;
        }

        var slot = TryClaimBackgroundSlot();
        if (slot is null)
        {
            if (TryReserveBackgroundWorkerStart())
            {
                _logger.LogDebug(LogEvents.Worker.PoolScaleUp, "No free worker slot; starting additional worker (current {Count}/{Max})",
                    WorkerCount, _options.MaxConcurrentJobs);
                try
                {
                    // Claimed at creation: the reservation above already
                    // proved busy workers stay under the background limit.
                    slot = await StartWorkerAsync(ct, claim: true, heldByBackground: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.Worker.PoolScaleUpFailed, ex, "Failed to start additional worker: {Error}", ex.GetType().Name);
                    return false;
                }
                finally
                {
                    ReleaseWorkerStartReservation();
                }

                // A request may have started waiting while the worker spawned;
                // if so, the new worker goes to it instead (reader-first rule).
                if (!KeepBackgroundClaim(slot))
                    return false;
            }
        }

        if (slot is null)
        {
            // No available slot — leave the job in the queue (D12)
            _logger.LogDebug(LogEvents.Worker.PoolSaturatedJobQueued, "No worker slot available (saturated at {Count}/{Max}); job remains queued (pending: {Pending})",
                WorkerCount, _options.MaxConcurrentJobs, _scheduler.PendingCount);
            return false;
        }

        // Slot is already reserved (IsBusy = true) atomically by
        // TryClaimBackgroundSlot; dequeue the highest-priority job.
        var job = _scheduler.Dequeue();
        if (job is null)
        {
            ReleaseSlot(slot, didWork: false);
            SignalDispatch();
            _logger.LogDebug(LogEvents.Worker.PoolIdleSlotReleased, "Slot {Slot} reserved but queue is empty; releasing slot", slot.Id);
            LogThroughputSummaryIfPending();
            return false;
        }

        // One Debug line per dispatched job: operation, correlation IDs, and the
        // queue depths needed to diagnose saturation. Completion is logged by the
        // scheduler with the duration; per-protocol messages stay at Trace.
        _metrics.RecordMs(MediaMetrics.AnalysisQueueWait, job.Priority.ToString().ToLowerInvariant(),
            (DateTimeOffset.UtcNow - job.EnqueuedAt).TotalMilliseconds);

        _logger.LogDebug(LogEvents.Worker.JobDispatched,
            "Dispatching {Operation} job {JobId} (item {ItemId}, priority {Priority}) to worker {Slot}; pending {Pending}, in-flight {InFlight}",
            job.Operation, job.JobId, job.ItemId, job.Priority, slot.Id, _scheduler.PendingCount, _scheduler.InFlightCount);

        _ = RunJobAsync(slot, job, ct);
        return true;
    }

    /// <summary>
    /// Runs one dispatched job to completion and signals dispatch afterward so
    /// the freed slot is picked up immediately. Not awaited by the caller —
    /// this is what lets <see cref="DispatchAsync"/> fill multiple slots
    /// concurrently instead of one job per call.
    /// </summary>
    private async Task RunJobAsync(WorkerSlot slot, PendingJob job, CancellationToken ct)
    {
        try
        {
            await ProcessJobAsync(slot, job, ct);
        }
        catch (Exception ex)
        {
            // ProcessJobAsync already catches and records job-level failures in
            // its own try/catch/finally; this only guards against something
            // escaping that (e.g. a bug in the post-completion persistence/
            // thumbnail steps), so a fire-and-forget dispatch never becomes an
            // unobserved task exception.
            _logger.LogWarning(LogEvents.Worker.JobProcessingFailed, ex, "Unhandled error running dispatched job {JobId} (item {ItemId})", job.JobId, job.ItemId);
        }
        finally
        {
            SignalDispatch();
        }
    }

    /// <summary>
    /// On-demand single-page extraction + variant encoding. Borrows a worker
    /// slot, sends an "extract" request, and awaits the encoded output written to
    /// <paramref name="outputPath"/>. The server never opens the archive itself.
    /// Returns an outcome the controller maps to an HTTP response; never throws for
    /// worker-side failures.
    /// </summary>
    /// <param name="maxDimension">
    /// Longest edge (px) for sized page variants ("webp@&lt;n&gt;", 1.19.0); 0 means
    /// no resize. Declared after <paramref name="ct"/> deliberately: every existing
    /// caller passes the cancellation token positionally, so appending the new
    /// optional parameter is the only placement that keeps them compiling
    /// unchanged.
    /// </param>
    /// <param name="resizeFilter">
    /// Resampling kernel name for sized page variants (1.20.0), one of
    /// <see cref="Core.Media.PageVariantFilters"/>. Null means the worker's
    /// pre-1.20.0 Lanczos default. Appended after <paramref name="maxDimension"/>
    /// for the same source-compatibility reason.
    /// </param>
    public async Task<PageExtractionOutcome> ExtractPageAsync(
        string archivePath,
        string sourceEntryKey,
        string variant,
        long expectedLastWriteTicks,
        long expectedByteLength,
        string outputPath,
        int thumbnailMaxDimension,
        int webpQuality,
        CancellationToken ct = default,
        int maxDimension = 0,
        string? resizeFilter = null,
        MediaSlotKind slotKind = MediaSlotKind.Page)
    {
        var slotWait = new double[1];
        var outcome = await ExtractPageCoreAsync(archivePath, sourceEntryKey, variant, expectedLastWriteTicks, expectedByteLength,
            outputPath, thumbnailMaxDimension, webpQuality, ct, maxDimension, resizeFilter, slotKind, slotWait);
        // The slot wait of this request (1.42.0 profiling; null when it never asked for a slot).
        return outcome with { SlotWaitMs = slotWait[0] >= 0 ? slotWait[0] : null };
    }

    private async Task<PageExtractionOutcome> ExtractPageCoreAsync(
        string archivePath,
        string sourceEntryKey,
        string variant,
        long expectedLastWriteTicks,
        long expectedByteLength,
        string outputPath,
        int thumbnailMaxDimension,
        int webpQuality,
        CancellationToken ct,
        int maxDimension,
        string? resizeFilter,
        MediaSlotKind slotKind,
        double[] slotWait)
    {
        slotWait[0] = -1;
        if (_isShuttingDown)
            return PageExtractionOutcome.Failed("unavailable", "Server is shutting down.");

        // Acquire a worker slot, briefly waiting if all are busy (e.g. mid-scan).
        var waitStarted = Stopwatch.GetTimestamp();
        var slot = await AcquireSlotAsync(slotKind, ct);
        slotWait[0] = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
        if (slot is null)
            return ct.IsCancellationRequested
                ? PageExtractionOutcome.Failed("cancelled", "Request cancelled.")
                : PageExtractionOutcome.Failed("busy", "No worker available; try again.");

        var jobId = "extract-" + Guid.NewGuid().ToString("N");
        try
        {
            var request = new ExtractRequest
            {
                JobId = jobId,
                ArchivePath = archivePath,
                SourceEntryKey = sourceEntryKey,
                Variant = variant,
                ExpectedLastWriteTicks = expectedLastWriteTicks,
                ExpectedByteLength = expectedByteLength,
                OutputPath = outputPath,
                Deadline = DateTimeOffset.UtcNow.Add(_options.AnalysisTimeout),
                ThumbnailMaxDimension = thumbnailMaxDimension,
                WebpQuality = webpQuality,
                MaxDimension = maxDimension,
                ResizeFilter = resizeFilter,
            };

            var tcs = new TaskCompletionSource<PageExtractionOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task HandleMessage(WorkerEnvelope envelope)
            {
                if (envelope.CorrelationId != jobId) return Task.CompletedTask;
                switch (envelope.Type)
                {
                    case "extract_result":
                        {
                            var r = WorkerProtocolFraming.GetPayload<ExtractResult>(envelope);
                            tcs.TrySetResult(r is not null
                                ? PageExtractionOutcome.Ok(r.OutputPath, r.MediaType, r.Width, r.Height, r.ByteSize) with
                                {
                                    OpenMs = r.OpenMs,
                                    ReadMs = r.ReadMs,
                                    EncodeMs = r.EncodeMs,
                                }
                                : PageExtractionOutcome.Failed("extraction_failed", "Empty extract result."));
                            break;
                        }
                    case "extract_error":
                        {
                            var e = WorkerProtocolFraming.GetPayload<ExtractError>(envelope);
                            tcs.TrySetResult(PageExtractionOutcome.Failed(
                                e?.ErrorType ?? "extraction_failed", e?.ErrorMessage ?? "Extraction failed."));
                            break;
                        }
                }
                return Task.CompletedTask;
            }

            slot.Supervisor.OnMessageReceived += HandleMessage;
            try
            {
                var envelope = WorkerProtocolFraming.CreateEnvelope("extract", jobId, request);
                var sent = Stopwatch.GetTimestamp();
                await slot.Supervisor.SendMessageAsync(envelope, ct);

                var timeout = Task.Delay(_options.AnalysisTimeout + _options.SourceOpenTimeout, ct);
                var done = await Task.WhenAny(tcs.Task, timeout);
                RecordWorkerCall(slot, slotKind, sent, completed: done == tcs.Task, ct);
                if (done != tcs.Task)
                {
                    // The delay also completes (cancelled) when the CLIENT gives up; that is not a timeout.
                    ct.ThrowIfCancellationRequested();
                    return PageExtractionOutcome.Failed("timeout", "Extraction timed out.");
                }
                var extracted = await tcs.Task;
                RecordExtractPhases(slotKind, extracted);
                return extracted;
            }
            finally
            {
                slot.Supervisor.OnMessageReceived -= HandleMessage;
            }
        }
        catch (OperationCanceledException)
        {
            return PageExtractionOutcome.Failed("cancelled", "Request cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ExtractDispatchFailed, ex, "Extract dispatch failed: {Error}", ex.GetType().Name);
            return PageExtractionOutcome.Failed("extraction_failed", "Extraction failed.");
        }
        finally
        {
            ReleaseDirectSlot(slot, slotKind);
        }
    }

    /// <summary>
    /// Reads only an archive's ComicInfo.xml in a worker (protocol v3, 1.24.0) - the
    /// on-demand call behind the ComicInfo backfill, shaped like
    /// <see cref="ExtractPageAsync"/> (a direct slot, not the analysis scheduler).
    /// The caller throttles itself on <see cref="IsSaturated"/> /
    /// <see cref="SchedulerPendingCount"/>. Never throws for worker-side problems:
    /// every failure is a <see cref="ComicInfoReadOutcome"/> error type.
    /// </summary>
    public async Task<ComicInfoReadOutcome> ReadComicInfoAsync(
        string archivePath,
        long expectedLastWriteTicks,
        long expectedByteLength,
        CancellationToken ct = default)
    {
        if (_isShuttingDown)
            return ComicInfoReadOutcome.Failed("unavailable");

        var slot = await AcquireSlotAsync(MediaSlotKind.ComicInfo, ct);
        if (slot is null)
            return ComicInfoReadOutcome.Failed(ct.IsCancellationRequested ? "cancelled" : "busy");

        var jobId = "comicinfo-" + Guid.NewGuid().ToString("N");
        try
        {
            var request = new ComicInfoRequest
            {
                JobId = jobId,
                ArchivePath = archivePath,
                ExpectedLastWriteTicks = expectedLastWriteTicks,
                ExpectedByteLength = expectedByteLength,
                Deadline = DateTimeOffset.UtcNow.Add(ComicInfoReadTimeout),
                MaxXmlBytes = ComicInfoLimits.MaxXmlBytes,
            };

            var tcs = new TaskCompletionSource<ComicInfoReadOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task HandleMessage(WorkerEnvelope envelope)
            {
                if (envelope.CorrelationId != jobId) return Task.CompletedTask;
                switch (envelope.Type)
                {
                    case "comicinfo_result":
                        {
                            var r = WorkerProtocolFraming.GetPayload<ComicInfoResult>(envelope);
                            if (r is null)
                                tcs.TrySetResult(ComicInfoReadOutcome.Failed("read_failed"));
                            else if (r.ObservedLastWriteTicks != expectedLastWriteTicks || r.ObservedByteLength != expectedByteLength)
                                tcs.TrySetResult(ComicInfoReadOutcome.Failed("source_changed"));
                            else
                                tcs.TrySetResult(ComicInfoReadOutcome.Ok(r.Outcome));
                            break;
                        }
                    case "comicinfo_error":
                        {
                            var e = WorkerProtocolFraming.GetPayload<ComicInfoError>(envelope);
                            tcs.TrySetResult(ComicInfoReadOutcome.Failed(e?.ErrorType ?? "read_failed"));
                            break;
                        }
                    case "analyze_error":
                        {
                            // A worker-level error for this correlation id (e.g. an
                            // unknown message type from a mismatched worker).
                            var e = WorkerProtocolFraming.GetPayload<AnalyzeError>(envelope);
                            tcs.TrySetResult(ComicInfoReadOutcome.Failed(e?.ErrorType ?? "read_failed"));
                            break;
                        }
                }
                return Task.CompletedTask;
            }

            slot.Supervisor.OnMessageReceived += HandleMessage;
            try
            {
                var envelope = WorkerProtocolFraming.CreateEnvelope("comicinfo", jobId, request);
                var sent = Stopwatch.GetTimestamp();
                await slot.Supervisor.SendMessageAsync(envelope, ct);

                var timeout = Task.Delay(ComicInfoReadTimeout + _options.SourceOpenTimeout, ct);
                var done = await Task.WhenAny(tcs.Task, timeout);
                RecordWorkerCall(slot, MediaSlotKind.ComicInfo, sent, completed: done == tcs.Task, ct);
                if (done != tcs.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    return ComicInfoReadOutcome.Failed("timeout");
                }
                return await tcs.Task;
            }
            finally
            {
                slot.Supervisor.OnMessageReceived -= HandleMessage;
            }
        }
        catch (OperationCanceledException)
        {
            return ComicInfoReadOutcome.Failed("cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ExtractDispatchFailed, ex, "ComicInfo dispatch failed: {Error}", ex.GetType().Name);
            return ComicInfoReadOutcome.Failed("read_failed");
        }
        finally
        {
            ReleaseDirectSlot(slot, MediaSlotKind.ComicInfo);
        }
    }

    /// <summary>Deadline for one ComicInfo read (directory + at most one 1 MiB entry).</summary>
    internal static readonly TimeSpan ComicInfoReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Hashes one SERVER-OWNED image file (a stored cover thumbnail, or provider image bytes written to
    /// scratch) in a worker (protocol v4 <c>image_hash</c>, 1.28.0): the worker decodes it, the server never
    /// does. A direct slot like <see cref="ReadComicInfoAsync"/>. Never throws for worker-side problems: every
    /// failure is an <see cref="ImageHashOutcome"/> error type.
    /// </summary>
    public async Task<ImageHashOutcome> HashImageAsync(string imagePath, CancellationToken ct = default)
    {
        if (_isShuttingDown)
            return ImageHashOutcome.Failed("unavailable");

        var slot = await AcquireSlotAsync(MediaSlotKind.ImageHash, ct);
        if (slot is null)
            return ImageHashOutcome.Failed(ct.IsCancellationRequested ? "cancelled" : "busy");

        var jobId = "imagehash-" + Guid.NewGuid().ToString("N");
        try
        {
            var request = new ImageHashRequest { JobId = jobId, ImagePath = imagePath };
            var tcs = new TaskCompletionSource<ImageHashOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task HandleMessage(WorkerEnvelope envelope)
            {
                if (envelope.CorrelationId != jobId) return Task.CompletedTask;
                switch (envelope.Type)
                {
                    case "image_hash_result":
                        {
                            var r = WorkerProtocolFraming.GetPayload<ImageHashResult>(envelope);
                            tcs.TrySetResult(r is null ? ImageHashOutcome.Failed("hash_failed") : ImageHashOutcome.Ok(r.Hash));
                            break;
                        }
                    case "image_hash_error":
                        {
                            var e = WorkerProtocolFraming.GetPayload<ImageHashError>(envelope);
                            tcs.TrySetResult(ImageHashOutcome.Failed(e?.ErrorType ?? "hash_failed"));
                            break;
                        }
                    case "analyze_error":
                        {
                            // A worker-level error for this correlation id (e.g. an unknown message type).
                            var e = WorkerProtocolFraming.GetPayload<AnalyzeError>(envelope);
                            tcs.TrySetResult(ImageHashOutcome.Failed(e?.ErrorType ?? "hash_failed"));
                            break;
                        }
                }
                return Task.CompletedTask;
            }

            slot.Supervisor.OnMessageReceived += HandleMessage;
            try
            {
                var sent = Stopwatch.GetTimestamp();
                await slot.Supervisor.SendMessageAsync(WorkerProtocolFraming.CreateEnvelope("image_hash", jobId, request), ct);
                var timeout = Task.Delay(ImageHashTimeout, ct);
                var done = await Task.WhenAny(tcs.Task, timeout);
                RecordWorkerCall(slot, MediaSlotKind.ImageHash, sent, completed: done == tcs.Task, ct);
                if (done == tcs.Task)
                    return await tcs.Task;
                ct.ThrowIfCancellationRequested();
                return ImageHashOutcome.Failed("timeout");
            }
            finally
            {
                slot.Supervisor.OnMessageReceived -= HandleMessage;
            }
        }
        catch (OperationCanceledException)
        {
            return ImageHashOutcome.Failed("cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ExtractDispatchFailed, ex, "Image hash dispatch failed: {Error}", ex.GetType().Name);
            return ImageHashOutcome.Failed("hash_failed");
        }
        finally
        {
            ReleaseDirectSlot(slot, MediaSlotKind.ImageHash);
        }
    }

    /// <summary>Deadline for one image hash (one decode of an image of at most 4 MiB).</summary>
    internal static readonly TimeSpan ImageHashTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Renders one cover thumbnail in a worker (protocol v5 <c>cover_render</c>, 1.29.0): an archive page (optionally one
    /// half of a jacket spread) or a SERVER-OWNED image file (provider bytes in scratch), downscaled to WebP and hashed.
    /// The request's <c>JobId</c> is replaced by the pool's own. A direct slot like <see cref="HashImageAsync"/>; never throws
    /// for worker-side problems: every failure is a <see cref="CoverRenderOutcome"/> error type.
    /// </summary>
    public async Task<CoverRenderOutcome> RenderCoverAsync(CoverRenderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_isShuttingDown)
            return CoverRenderOutcome.Failed("unavailable");

        // Provider bytes in scratch (MangaDex volume covers, posters) vs an archive page (a cover crop).
        var slotKind = request.ImagePath is not null ? MediaSlotKind.CoverRenderImage : MediaSlotKind.CoverRenderArchive;
        var slot = await AcquireSlotAsync(slotKind, ct);
        if (slot is null)
            return CoverRenderOutcome.Failed(ct.IsCancellationRequested ? "cancelled" : "busy");

        var jobId = "cover-" + Guid.NewGuid().ToString("N");
        try
        {
            var tcs = new TaskCompletionSource<CoverRenderOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

            Task HandleMessage(WorkerEnvelope envelope)
            {
                if (envelope.CorrelationId != jobId) return Task.CompletedTask;
                switch (envelope.Type)
                {
                    case "cover_render_result":
                        {
                            var r = WorkerProtocolFraming.GetPayload<CoverRenderResult>(envelope);
                            tcs.TrySetResult(r is null ? CoverRenderOutcome.Failed("render_failed") : CoverRenderOutcome.Ok(r));
                            break;
                        }
                    case "cover_render_error":
                        {
                            var e = WorkerProtocolFraming.GetPayload<CoverRenderError>(envelope);
                            tcs.TrySetResult(CoverRenderOutcome.Failed(e?.ErrorType ?? "render_failed"));
                            break;
                        }
                    case "analyze_error":
                        {
                            // A worker-level error for this correlation id (e.g. an unknown message type from an older worker).
                            var e = WorkerProtocolFraming.GetPayload<AnalyzeError>(envelope);
                            tcs.TrySetResult(CoverRenderOutcome.Failed(e?.ErrorType ?? "render_failed"));
                            break;
                        }
                }
                return Task.CompletedTask;
            }

            slot.Supervisor.OnMessageReceived += HandleMessage;
            try
            {
                var sent = Stopwatch.GetTimestamp();
                await slot.Supervisor.SendMessageAsync(
                    WorkerProtocolFraming.CreateEnvelope("cover_render", jobId, request with { JobId = jobId }), ct);
                var timeout = Task.Delay(CoverRenderTimeout, ct);
                var done = await Task.WhenAny(tcs.Task, timeout);
                RecordWorkerCall(slot, slotKind, sent, completed: done == tcs.Task, ct);
                if (done == tcs.Task)
                    return await tcs.Task;
                ct.ThrowIfCancellationRequested();
                return CoverRenderOutcome.Failed("timeout");
            }
            finally
            {
                slot.Supervisor.OnMessageReceived -= HandleMessage;
            }
        }
        catch (OperationCanceledException)
        {
            return CoverRenderOutcome.Failed("cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ExtractDispatchFailed, ex, "Cover render dispatch failed: {Error}", ex.GetType().Name);
            return CoverRenderOutcome.Failed("render_failed");
        }
        finally
        {
            ReleaseDirectSlot(slot, slotKind);
        }
    }

    /// <summary>Deadline for one cover render (one page read from a possibly cold archive, one decode, one encode).</summary>
    internal static readonly TimeSpan CoverRenderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets a worker slot for a direct request of <paramref name="kind"/> (1.42.0: reader-first hand-off). A free slot
    /// is taken at once when no request of the same or a higher priority waits and - for background work - the
    /// background limit allows it; under the cap a worker is started for it; otherwise the request queues and is handed
    /// the next freed slot in priority order (<see cref="ClassOf"/>), with no polling. Reader requests give up after
    /// <see cref="WorkerPoolOptions.SourceOpenTimeout"/> (503); background requests wait until served or cancelled, so a
    /// paused cover thumbnail is never recorded as failed. Returns null when cancelled, timed out or shutting down.
    /// </summary>
    private async Task<WorkerSlot?> AcquireSlotAsync(MediaSlotKind kind, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var slot = await AcquireSlotCoreAsync(kind, ct);
        var tag = MediaMetrics.KindTag(kind);
        _metrics.RecordMs(MediaMetrics.SlotWait, tag, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _metrics.Count(MediaMetrics.SlotOutcome, tag + ":" + (slot is not null ? "acquired" : ct.IsCancellationRequested ? "cancelled" : "timeout"));
        if (slot is not null)
            _metrics.SlotTaken(kind);
        return slot;
    }

    /// <summary>Returns a slot taken by <see cref="AcquireSlotAsync"/> and wakes background dispatch.</summary>
    private void ReleaseDirectSlot(WorkerSlot slot, MediaSlotKind kind)
    {
        _metrics.SlotReleased(kind);
        ReleaseSlot(slot);
        SignalDispatch();
    }

    /// <summary>
    /// Records how long the worker took to answer a direct request, or - when the caller stopped waiting (timeout or
    /// the client left) - that the slot is being freed while the worker process is still busy with that request. The
    /// next request sent to that worker queues behind it (the worker is serial), so its time is recorded under
    /// <c>&lt;kind&gt;_after_abandon</c>.
    /// </summary>
    private void RecordWorkerCall(WorkerSlot slot, MediaSlotKind kind, long sentTimestamp, bool completed, CancellationToken ct)
    {
        var tag = MediaMetrics.KindTag(kind);
        if (completed)
        {
            _metrics.RecordMs(MediaMetrics.WorkerCall, slot.AbandonedRequestPending ? tag + "_after_abandon" : tag,
                Stopwatch.GetElapsedTime(sentTimestamp).TotalMilliseconds);
            slot.AbandonedRequestPending = false;
        }
        else
        {
            _metrics.Count(MediaMetrics.SlotReleasedWhileWorkerBusy, tag + ":" + (ct.IsCancellationRequested ? "cancelled" : "timeout"));
            slot.AbandonedRequestPending = true;
        }
    }

    private void RecordExtractPhases(MediaSlotKind kind, PageExtractionOutcome outcome)
    {
        if (!outcome.Success)
            return;
        var tag = MediaMetrics.KindTag(kind);
        if (outcome.OpenMs is { } open)
            _metrics.RecordMs(MediaMetrics.ExtractOpen, tag, open);
        if (outcome.ReadMs is { } read)
            _metrics.RecordMs(MediaMetrics.ExtractRead, tag, read);
        if (outcome.EncodeMs is { } encode)
            _metrics.RecordMs(MediaMetrics.ExtractEncode, tag, encode);
    }

    private async Task<WorkerSlot?> AcquireSlotCoreAsync(MediaSlotKind kind, CancellationToken ct)
    {
        var cls = ClassOf(kind);
        _logger.LogDebug(LogEvents.Worker.ExtractSlotAcquire, "Acquiring worker slot for {Kind} (current {Count}/{Max})",
            MediaMetrics.KindTag(kind), WorkerCount, _options.MaxConcurrentJobs);
        Interlocked.Increment(ref _waitingReaders);
        try
        {
            SlotWaiter? waiter = null;
            var start = false;
            lock (_poolLock)
            {
                if (_isShuttingDown)
                    return null;
                var now = Environment.TickCount64;
                if (cls != SlotClass.Background)
                    _lastReaderActivityMs = now;
                var order = cls switch { SlotClass.Reader => 0, SlotClass.ReaderPrefetch => 2, _ => 3 };
                var ahead = _waiters.Any(w => OrderOf(w, now) <= order);
                var allowed = cls != SlotClass.Background || BackgroundBusyLocked() < BackgroundLimitLocked(now);
                if (!ahead && allowed)
                {
                    var free = _workers.FirstOrDefault(w => !w.IsBusy);
                    if (free is not null)
                    {
                        free.IsBusy = true;
                        free.HeldByBackground = cls == SlotClass.Background;
                        return free;
                    }
                    if (_workers.Count + _startingWorkers < _options.MaxConcurrentJobs)
                    {
                        _startingWorkers++;
                        start = true;
                    }
                }
                if (!start)
                {
                    waiter = new SlotWaiter(kind, cls, ++_waiterSeq, now);
                    _waiters.Add(waiter);
                }
            }

            if (start)
            {
                // The started worker joins the pool already claimed for this caller, so neither background dispatch
                // nor idle retirement can take it first. The spawn is NOT tied to this caller's token: a reader that
                // leaves must not kill a worker mid-handshake; the worker then joins the pool idle (or goes to the
                // next waiter).
                var startTask = StartWorkerAsync(_readCts.Token, claim: true, heldByBackground: cls == SlotClass.Background);
                try
                {
                    var started = await startTask.WaitAsync(ct);
                    ReleaseWorkerStartReservation();
                    return started;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _ = HandOverAbandonedStartAsync(startTask);
                    return null;
                }
                catch (Exception ex)
                {
                    ReleaseWorkerStartReservation();
                    if (_isShuttingDown)
                        return null;
                    _logger.LogWarning(LogEvents.Worker.ExtractWorkerStartFailed, ex, "Failed to start worker for extract: {Error}", ex.GetType().Name);
                    if (cls == SlotClass.Background)
                        return null; // no deadline to retry against (see StartWorkerForWaiterAsync)
                    lock (_poolLock)
                    {
                        waiter = new SlotWaiter(kind, cls, ++_waiterSeq, Environment.TickCount64);
                        _waiters.Add(waiter);
                    }
                }
            }

            return await WaitForHandOffAsync(waiter!, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingReaders);
        }
    }

    /// <summary>
    /// Waits for a queued request's hand-off. Background requests re-check the queue once a second (the reader
    /// window may have expired, which lifts the background limit); reader requests end at their deadline.
    /// </summary>
    private async Task<WorkerSlot?> WaitForHandOffAsync(SlotWaiter waiter, CancellationToken ct)
    {
        var deadline = waiter.Class == SlotClass.Background ? (long?)null : Environment.TickCount64 + (long)_options.SourceOpenTimeout.TotalMilliseconds;
        try
        {
            while (true)
            {
                var wait = waiter.Class == SlotClass.Background
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromMilliseconds(Math.Max(0, deadline!.Value - Environment.TickCount64));
                try
                {
                    return await waiter.Tcs.Task.WaitAsync(wait, ct);
                }
                catch (TimeoutException)
                {
                    if (deadline is not null)
                        break;
                    PumpWaiters();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        lock (_poolLock)
        {
            if (_waiters.Remove(waiter))
            {
                _logger.LogDebug(LogEvents.Worker.ExtractSlotAcquireTimeout, "Slot wait for {Kind} ended without a slot (cancelled or timed out)",
                    MediaMetrics.KindTag(waiter.Kind));
                return null;
            }
        }
        // Handed a slot at the same instant it gave up: pass the slot on.
        if (waiter.Tcs.Task.IsCompletedSuccessfully && waiter.Tcs.Task.Result is { } handed)
            ReleaseSlot(handed, didWork: false);
        return null;
    }

    /// <summary>
    /// A reader gave up while its worker was still starting. Lets the start finish, then returns the
    /// (claimed) worker to the pool (or to the next waiter) so the next request finds it warm; the start
    /// reservation is released only now, so the pool cap holds meanwhile. A failed start was already logged by
    /// <see cref="StartWorkerAsync"/>.
    /// </summary>
    private async Task HandOverAbandonedStartAsync(Task<WorkerSlot> start)
    {
        try
        {
            var slot = await start.ConfigureAwait(false);
            if (_isShuttingDown)
            {
                // StopAsync already emptied the pool; do not leave a process behind.
                lock (_poolLock) { _workers.Remove(slot); }
                await slot.Supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            ReleaseSlot(slot);
        }
        catch (Exception)
        {
            // Already logged at the source.
        }
        finally
        {
            ReleaseWorkerStartReservation();
        }
    }

    /// <summary>
    /// Stops all workers gracefully. Stops dispatch, allows bounded grace,
    /// then terminates remaining workers.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation(LogEvents.Worker.PoolStopping, "Stopping worker pool ({Count} workers)", WorkerCount);
        _isShuttingDown = true;
        try { _readCts.Cancel(); } catch (ObjectDisposedException) { }

        // Let an in-progress retirement finish stopping its worker (bounded by
        // the supervisor's grace period) before the remaining workers stop.
        if (_retirementLoop is { } retirementLoop)
        {
            try { await retirementLoop.WaitAsync(_options.CancellationGracePeriod + TimeSpan.FromSeconds(1), ct); }
            catch (Exception) { /* best effort */ }
            _retirementLoop = null;
        }

        List<WorkerSlot> workers;
        List<SlotWaiter> waiters;
        lock (_poolLock)
        {
            workers = _workers.ToList();
            _workers.Clear();
            waiters = _waiters.ToList();
            _waiters.Clear();
        }
        foreach (var waiter in waiters)
            waiter.Tcs.TrySetResult(null);

        foreach (var slot in workers)
        {
            try
            {
                await slot.Supervisor.StopAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Worker.PoolStopWorkerError, ex, "Error stopping worker {Id}: {Error}", slot.Id, ex.GetType().Name);
            }
        }

        _isStarted = false;
        _logger.LogInformation(LogEvents.Worker.PoolStopped, "Worker pool stopped");
    }

    /// <summary>
    /// Number of active worker slots.
    /// </summary>
    public int WorkerCount
    {
        get
        {
            lock (_poolLock) { return _workers.Count; }
        }
    }

    /// <summary>
    /// Number of busy worker slots.
    /// </summary>
    public int BusyWorkerCount
    {
        get
        {
            lock (_poolLock) { return _workers.Count(w => w.IsBusy); }
        }
    }

    /// <summary>
    /// True when no worker slot is free (all existing workers busy and the pool
    /// is at its concurrency cap). Read-only observation for the thumbnail
    /// backfill to yield when the pool is under pressure — does not affect
    /// dispatch or scheduling. Page/thumbnail extracts acquire slots directly
    /// (first-come-first-served), so the backfill throttles itself rather than
    /// relying on scheduler priority (which only orders analysis jobs).
    /// </summary>
    public bool IsSaturated
    {
        get
        {
            lock (_poolLock)
            {
                return _workers.Count >= _options.MaxConcurrentJobs
                    && _workers.All(w => w.IsBusy);
            }
        }
    }

    /// <summary>
    /// Number of pending (not-yet-dispatched) jobs in the scheduler. Read-only
    /// observation for the thumbnail backfill: when analysis work is queued, the
    /// backfill yields so analysis-time thumbnail generation (which produces
    /// most thumbnails) drains first.
    /// </summary>
    public int SchedulerPendingCount => _scheduler.PendingCount;

    /// <summary>
    /// Starts one worker process and adds it to the pool. With
    /// <paramref name="claim"/> the slot is added already busy (atomically, under
    /// the pool lock) and owned by the caller, who must release it.
    /// </summary>
    private async Task<WorkerSlot> StartWorkerAsync(CancellationToken ct, bool claim = false, bool heldByBackground = false)
    {
        var supervisor = CreateSupervisor();
        _logger.LogDebug(LogEvents.Worker.WorkerProcessStartAttempt, "Starting worker process (attempt {Count}/{Max})", _workers.Count + 1, _options.MaxConcurrentJobs);

        try
        {
            await supervisor.StartAsync(ct);

            var slot = new WorkerSlot
            {
                Id = Interlocked.Increment(ref _nextWorkerId) - 1,
                Supervisor = supervisor,
                IdleSinceMs = Environment.TickCount64,
                IsBusy = claim,
                HeldByBackground = claim && heldByBackground,
            };

            // Set up crash handler. A retired worker is already out of the
            // pool and its exit is expected, so it is not reported as a crash.
            supervisor.OnWorkerExited += exitCode =>
            {
                if (slot.IsRetiring)
                    return;
                _logger.LogWarning(LogEvents.Worker.WorkerExited, "Worker {Id} exited with code {ExitCode}", slot.Id, exitCode);
                lock (_poolLock)
                {
                    _workers.Remove(slot);
                }
                _logger.LogDebug(LogEvents.Worker.WorkerRemovedFromPool, "Worker {Id} removed from pool; remaining workers {Count}", slot.Id, _workers.Count);
                // Requests waiting for a slot may now start a replacement worker.
                PumpWaiters();
            };

            lock (_poolLock)
            {
                _workers.Add(slot);
            }

            _logger.LogDebug(LogEvents.Worker.WorkerReadyInPool, "Worker {Id} started and ready (pool now {Count}/{Max})", slot.Id, _workers.Count, _options.MaxConcurrentJobs);

            // ONE persistent read loop per worker for its whole lifetime. Jobs only
            // swap the OnMessageReceived handler; they must not start their own read
            // loop, or competing readers would drop responses (a fast reply to job N
            // could be consumed by job N-1's orphaned loop and lost).
            slot.ReadLoop = Task.Run(() => supervisor.ReadMessagesAsync(_readCts.Token), _readCts.Token);
            return slot;
        }
        catch (Exception ex)
        {
            _logger.LogError(LogEvents.Worker.WorkerStartFailed, ex, "Failed to start worker: {Error}", ex.GetType().Name);
            await supervisor.DisposeAsync();
            throw;
        }
    }

    private WorkerSupervisor CreateSupervisor()
    {
        var (exePath, arguments) = ResolveWorkerLaunch();
        var supervisorLogger = _loggerFactory?.CreateLogger<WorkerSupervisor>();
        return new WorkerSupervisor(exePath, arguments, _options, supervisorLogger);
    }

    /// <summary>
    /// Resolves the worker executable and arguments. Discovery order:
    /// 1. <c>Media:WorkerExecutablePath</c> (explicit; may be a .dll or native exe)
    /// 2. Sibling <c>../worker/MangaPixer.MediaWorker.dll</c> (container layout)
    /// 3. Same directory as the server assembly
    /// 4. Dev sibling project output
    /// When the result is a .dll, the launch command is <c>dotnet &lt;dll&gt;</c>.
    /// </summary>
    private (string FileName, string Arguments) ResolveWorkerLaunch()
    {
        const string workerDllName = "MangaPixer.MediaWorker.dll";
        var assemblyDir = AppContext.BaseDirectory;

        // 1. Explicit configuration
        if (!string.IsNullOrWhiteSpace(_options.WorkerExecutablePath))
        {
            var configured = _options.WorkerExecutablePath!;
            return Path.GetExtension(configured).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", configured)
                : (configured, string.Empty);
        }

        // 2. Container layout: /app/server/ + /app/worker/MangaPixer.MediaWorker.dll
        var containerPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "worker", workerDllName));
        if (File.Exists(containerPath))
            return ("dotnet", containerPath);

        // 3. Published alongside server
        var siblingPath = Path.Combine(assemblyDir, workerDllName);
        if (File.Exists(siblingPath))
            return ("dotnet", siblingPath);

        // 4. Dev: sibling project output (Release then Debug)
        var devRelease = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "MangaPixer.MediaWorker", "bin", "Release", "net10.0", workerDllName));
        if (File.Exists(devRelease))
            return ("dotnet", devRelease);

        var devDebug = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "MangaPixer.MediaWorker", "bin", "Debug", "net10.0", workerDllName));
        if (File.Exists(devDebug))
            return ("dotnet", devDebug);

        // Fallback: assume the worker is a native executable on PATH.
        _logger?.LogWarning(LogEvents.Worker.WorkerExecutableFallback, "Worker executable not discovered; falling back to {Name} on PATH", "MangaPixer.MediaWorker");
        return ("MangaPixer.MediaWorker", string.Empty);
    }

    private async Task ProcessJobAsync(WorkerSlot slot, PendingJob job, CancellationToken ct)
    {
        slot.IsBusy = true;
        _scheduler.MarkInFlight(job);
        var runStarted = Stopwatch.GetTimestamp();

        // Allocate scratch workspace
        using var workspace = _scratchManager.AllocateWorkspace();

        JobResult finalResult;
        try
        {
            // Build the analyze request
            var request = new AnalyzeRequest
            {
                JobId = job.JobId,
                ArchivePath = job.ArchivePath,
                ContentVersion = job.ContentVersion,
                ExpectedLastWriteTicks = job.ExpectedLastWriteTicks,
                ExpectedByteLength = job.ExpectedByteLength,
                ScratchWorkspacePath = workspace.Path,
                Deadline = DateTimeOffset.UtcNow.Add(_options.AnalysisTimeout),
            };

            // Set up message handler for this job
            var completionTcs = new TaskCompletionSource<JobResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task HandleMessage(WorkerEnvelope envelope)
            {
                switch (envelope.Type)
                {
                    case "analyze_result":
                        {
                            var result = WorkerProtocolFraming.GetPayload<AnalyzeResult>(envelope);
                            if (result is not null)
                            {
                                // Validate source stamp
                                if (result.ObservedLastWriteTicks != job.ExpectedLastWriteTicks ||
                                    result.ObservedByteLength != job.ExpectedByteLength)
                                {
                                    _logger?.LogDebug(LogEvents.Worker.JobSourceStampRejected, "Job {JobId} (item {ItemId}) source stamp changed during processing; rejecting result",
                                        job.JobId, job.ItemId);
                                    completionTcs.TrySetResult(new JobResult
                                    {
                                        JobId = job.JobId,
                                        Success = false,
                                        ErrorType = "source_changed",
                                        ErrorMessage = "Source changed during processing",
                                        Result = null,
                                    });
                                }
                                else
                                {
                                    completionTcs.TrySetResult(new JobResult
                                    {
                                        JobId = job.JobId,
                                        Success = true,
                                        ErrorType = null,
                                        ErrorMessage = null,
                                        Result = result,
                                    });
                                }
                            }
                            break;
                        }
                    case "analyze_error":
                        {
                            var error = WorkerProtocolFraming.GetPayload<AnalyzeError>(envelope);
                            completionTcs.TrySetResult(new JobResult
                            {
                                JobId = job.JobId,
                                Success = false,
                                ErrorType = error?.ErrorType ?? "unknown",
                                ErrorMessage = error?.ErrorMessage ?? "Unknown error",
                                Result = null,
                            });
                            break;
                        }
                }
            }

            slot.Supervisor.OnMessageReceived += HandleMessage;

            // The worker's persistent read loop (started in StartWorkerAsync)
            // delivers responses to the handler registered above — this method must
            // NOT start its own read loop.

            // Send the analyze request
            var requestEnvelope = WorkerProtocolFraming.CreateEnvelope("analyze", job.JobId, request);
            await slot.Supervisor.SendMessageAsync(requestEnvelope, ct);

            // Wait for completion with timeout
            var timeoutTask = Task.Delay(_options.AnalysisTimeout + _options.SourceOpenTimeout, ct);
            var completedTask = await Task.WhenAny(completionTcs.Task, timeoutTask);

            slot.Supervisor.OnMessageReceived -= HandleMessage;

            if (completedTask == timeoutTask)
            {
                // Timeout — cancel the job
                _logger?.LogWarning(LogEvents.Worker.JobTimedOut, "Job {JobId} (item {ItemId}) timed out after {TimeoutMs}ms; sending cancel",
                    job.JobId, job.ItemId, (_options.AnalysisTimeout + _options.SourceOpenTimeout).TotalMilliseconds);
                try
                {
                    var cancelEnvelope = WorkerProtocolFraming.CreateEnvelope("cancel", job.JobId,
                        new CancelRequest { JobId = job.JobId });
                    await slot.Supervisor.SendMessageAsync(cancelEnvelope, CancellationToken.None);
                }
                catch { /* best effort */ }

                finalResult = new JobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorType = "timeout",
                    ErrorMessage = "Job exceeded timeout",
                    Result = null,
                };
                _scheduler.CompleteJob(job.DedupKey, finalResult);
            }
            else
            {
                finalResult = await completionTcs.Task;
                // Completion (with duration) is logged once by the scheduler.
                _scheduler.CompleteJob(job.DedupKey, finalResult);
            }

            RecordJobOutcome(finalResult.Success);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Worker.JobProcessingFailed, ex, "Job {JobId} (item {ItemId}) failed during processing", job.JobId, job.ItemId);
            _scheduler.FailJob(job.DedupKey, ex);
            RecordJobOutcome(success: false);
            finalResult = new JobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorType = ex.GetType().Name,
                ErrorMessage = ex.Message,
                Result = null,
            };
        }
        finally
        {
            _metrics.RecordMs(MediaMetrics.AnalysisRun, job.Operation.ToString().ToLowerInvariant(), Stopwatch.GetElapsedTime(runStarted).TotalMilliseconds);
            ReleaseSlot(slot);
            // Scratch workspace is cleaned up by the using statement
        }

        // Persist the result for EVERY job (background and reader-demand), so a
        // scanned library's covers/manifests populate without opening each item.
        // This runs AFTER the slot is released so that thumbnail generation (which
        // calls ExtractPageAsync and acquires its own slot) does not deadlock when
        // the pool is at capacity.
        await PersistResultAsync(job.ItemId, finalResult, ComputeContentSignature(job, finalResult));

        // Generate the durable cover thumbnail for successful analyses. The
        // thumbnail is the first page, downscaled to WebP by the worker and
        // persisted into the durable ThumbnailStore (not the evictable cache).
        // Resolved lazily from the scope factory to avoid a circular DI
        // dependency (ThumbnailGenerationService depends on this pool).
        if (finalResult.Success && _scopeFactory is not null)
        {
            try
            {
                using var thumbScope = _scopeFactory.CreateScope();
                var thumbService = thumbScope.ServiceProvider.GetService<ThumbnailGenerationService>();
                if (thumbService is not null)
                    await thumbService.GenerateForItemAsync(job.ItemId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed, ex, "Post-analysis thumbnail generation failed (item {ItemId}): {Error}", job.ItemId, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Records a job outcome and logs an Information-level throughput summary
    /// every <see cref="ThroughputSummaryInterval"/> jobs. This is the
    /// default-level view of background analysis progress; per-job events stay
    /// at Debug.
    /// </summary>
    private void RecordJobOutcome(bool success)
    {
        bool due;
        lock (_throughputLock)
        {
            if (success) _completedSinceSummary++; else _failedSinceSummary++;
            due = _completedSinceSummary + _failedSinceSummary >= ThroughputSummaryInterval;
        }

        if (due)
            LogThroughputSummary("interval");
    }

    /// <summary>
    /// Logs a throughput summary at Information when the queue has drained and
    /// unsummarized jobs remain, so the tail of a batch is not lost until the
    /// next interval. Called when a dispatch finds the queue empty.
    /// </summary>
    private void LogThroughputSummaryIfPending()
    {
        lock (_throughputLock)
        {
            if (_completedSinceSummary + _failedSinceSummary == 0)
                return;
        }

        LogThroughputSummary("queue drained");
    }

    private void LogThroughputSummary(string trigger)
    {
        int completed, failed;
        lock (_throughputLock)
        {
            completed = _completedSinceSummary;
            failed = _failedSinceSummary;
            _completedSinceSummary = 0;
            _failedSinceSummary = 0;
        }

        _logger.LogInformation(LogEvents.Worker.AnalysisThroughputSummary,
            "Analysis throughput: {Completed} completed, {Failed} failed in the last {Interval} job(s), trigger {Trigger}; pending {Pending}, in-flight {InFlight}",
            completed, failed, completed + failed, trigger, _scheduler.PendingCount, _scheduler.InFlightCount);
    }

    /// <summary>
    /// Persists a completed job's result into the catalog using a fresh scope.
    /// No-ops when the pool was constructed without a scope factory/persister
    /// (e.g. in unit tests that exercise dispatch behaviour only).
    /// </summary>
    private async Task PersistResultAsync(long nodeId, JobResult result, string? contentSignature)
    {
        if (_scopeFactory is null || _persister is null)
            return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await _persister.PersistAsync(db, nodeId, result, contentSignature);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.PersistAfterJobFailed, ex, "Persisting analysis for item {ItemId} failed: {Error}", nodeId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Computes the cheap content signature (1.5.0 move detection) for a
    /// successfully analysed source. Reads at most 128 KiB, read-only with read
    /// sharing. The source stamp is re-checked after hashing so a file that is
    /// being rewritten while we sample it never yields a signature that does not
    /// match the bytes the worker analysed. Returns null on any doubt; a missing
    /// signature only means "never treat this row as a move".
    /// </summary>
    private static string? ComputeContentSignature(PendingJob job, JobResult result)
    {
        if (!result.Success || result.Result is not AnalyzeResult)
            return null;
        try
        {
            var signature = ContentSignature.TryComputeFile(job.ArchivePath);
            if (signature is null)
                return null;

            var info = new FileInfo(job.ArchivePath);
            if (!info.Exists || info.Length != job.ExpectedByteLength || info.LastWriteTimeUtc.Ticks != job.ExpectedLastWriteTicks)
                return null;

            return signature;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _dispatchSignal.Dispose();
        if (_ownsMetrics)
            _metrics.Dispose();
    }

    private sealed class WorkerSlot
    {
        public int Id { get; init; }
        public required WorkerSupervisor Supervisor { get; init; }
        public bool IsBusy { get; set; }
        public Task? ReadLoop { get; set; }

        /// <summary>Monotonic time (ms) the slot last finished work; guarded by the pool lock.</summary>
        public long IdleSinceMs { get; set; }

        /// <summary>Set once, under the pool lock, when the slot is removed for idle retirement.</summary>
        public bool IsRetiring { get; set; }

        /// <summary>
        /// The last direct request on this worker was given up (timeout / client left) while the worker still ran it;
        /// cleared by the next answered request (1.42.0 profiling).
        /// </summary>
        public bool AbandonedRequestPending { get; set; }

        /// <summary>The holder is background work (analysis or a background request); guarded by the pool lock.</summary>
        public bool HeldByBackground { get; set; }
    }

    /// <summary>A direct request queued for a slot (1.42.0); guarded by the pool lock.</summary>
    private sealed class SlotWaiter(MediaSlotKind kind, SlotClass cls, long seq, long enqueuedMs)
    {
        public MediaSlotKind Kind { get; } = kind;
        public SlotClass Class { get; } = cls;
        public long Seq { get; } = seq;
        public long EnqueuedMs { get; } = enqueuedMs;
        public TaskCompletionSource<WorkerSlot?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Result of an on-demand page extraction. On success the encoded image is
/// at <see cref="OutputPath"/>; on failure <see cref="ErrorType"/> maps to an HTTP
/// response (e.g. "unsupported_solid", "page_not_found", "encrypted", "timeout").
/// </summary>
/// <summary>Result of <see cref="MediaWorkerPool.ReadComicInfoAsync"/>.</summary>
public sealed record ComicInfoReadOutcome
{
    public required bool Success { get; init; }
    public ComicInfoOutcome? Outcome { get; init; }
    public string? ErrorType { get; init; }

    public static ComicInfoReadOutcome Ok(ComicInfoOutcome outcome) => new() { Success = true, Outcome = outcome };

    public static ComicInfoReadOutcome Failed(string errorType) => new() { Success = false, ErrorType = errorType };
}

/// <summary>
/// The outcome of <see cref="MediaWorkerPool.RenderCoverAsync"/>: the written thumbnail (path, size, source size, hash), or an
/// error type (a <c>CoverRenderErrors</c> code, or <c>unavailable</c> / <c>busy</c> / <c>timeout</c> / <c>cancelled</c> /
/// <c>render_failed</c>, or <c>protocol_version_mismatch</c> / <c>unknown_message_type</c> from a mismatched worker).
/// </summary>
public sealed record CoverRenderOutcome
{
    public bool Success { get; init; }
    public string? ErrorType { get; init; }
    public string? OutputPath { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public ulong? Hash { get; init; }

    public static CoverRenderOutcome Ok(CoverRenderResult r) => new()
    {
        Success = true,
        OutputPath = r.OutputPath,
        Width = r.Width,
        Height = r.Height,
        SourceWidth = r.SourceWidth,
        SourceHeight = r.SourceHeight,
        Hash = r.Hash,
    };

    public static CoverRenderOutcome Failed(string errorType) => new() { Success = false, ErrorType = errorType };
}

/// <summary>The outcome of <see cref="MediaWorkerPool.HashImageAsync"/>: a 64-bit perceptual hash, or an error type.</summary>
public sealed record ImageHashOutcome
{
    public required bool Success { get; init; }
    public ulong Hash { get; init; }
    public string? ErrorType { get; init; }

    public static ImageHashOutcome Ok(ulong hash) => new() { Success = true, Hash = hash };

    public static ImageHashOutcome Failed(string errorType) => new() { Success = false, ErrorType = errorType };
}

public sealed record PageExtractionOutcome
{
    public required bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? MediaType { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long ByteSize { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The worker's open / read / encode milliseconds (1.42.0; null from a worker that does not report them).</summary>
    public int? OpenMs { get; init; }

    public int? ReadMs { get; init; }
    public int? EncodeMs { get; init; }

    /// <summary>How long this request waited for a worker slot, in ms (1.42.0; null when it never asked for one).</summary>
    public double? SlotWaitMs { get; init; }

    public static PageExtractionOutcome Ok(string outputPath, string mediaType, int width, int height, long byteSize) =>
        new() { Success = true, OutputPath = outputPath, MediaType = mediaType, Width = width, Height = height, ByteSize = byteSize };

    public static PageExtractionOutcome Failed(string errorType, string errorMessage) =>
        new() { Success = false, ErrorType = errorType, ErrorMessage = errorMessage };
}
