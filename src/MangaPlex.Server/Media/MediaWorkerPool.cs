namespace com.lifepixer.mangaplex.Server.Media;

using com.lifepixer.mangaplex.Server.Logging;

using System.Diagnostics;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.MediaWorker.Protocol;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Manages a small pool of media worker processes. Dispatches jobs from the
/// JobScheduler to available workers. Handles worker lifecycle:
/// - Start workers on demand
/// - Restart crashed workers with backoff
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
    private readonly List<WorkerSlot> _workers = [];
    private readonly object _poolLock = new();
    private readonly CancellationTokenSource _readCts = new();
    private readonly object _throughputLock = new();
    private readonly SemaphoreSlim _dispatchSignal = new(0, int.MaxValue);
    private int _completedSinceSummary;
    private int _failedSinceSummary;
    private bool _isStarted;
    private bool _isShuttingDown;

    /// <summary>
    /// Count of <see cref="AcquireSlotAsync"/> calls (reader demand: on-demand
    /// page/thumbnail extraction) currently in flight, including ones that got
    /// a slot immediately. Read by background dispatch to decide whether to
    /// leave one slot free — see <see cref="EffectiveBackgroundCap"/>.
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
        AnalysisResultPersister? persister = null)
    {
        _options = options;
        _scheduler = scheduler;
        _scratchManager = scratchManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
        _persister = persister;
    }

    /// <summary>
    /// Starts the worker pool. Initializes scratch root and pre-starts workers.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isStarted)
            return;

        _logger.LogInformation(LogEvents.Worker.PoolStarting, "Starting worker pool (max concurrent jobs: {Max})", _options.MaxConcurrentJobs);
        _scratchManager.Initialize();
        _isStarted = true;

        // Pre-start one background worker
        await StartWorkerAsync(ct);
        _logger.LogInformation(LogEvents.Worker.PoolStarted, "Worker pool started with {Count} worker(s)", WorkerCount);
    }

    /// <summary>
    /// Fills every background-eligible slot in one pass instead of dispatching
    /// one job per call. Each dispatched job runs to completion in the
    /// background (not awaited here) so multiple jobs run concurrently across
    /// up to <see cref="WorkerPoolOptions.MaxConcurrentJobs"/> workers; this
    /// method returns once no further slot/job pair is available, not once all
    /// dispatched jobs finish. See <see cref="TryDispatchOneAsync"/> for the
    /// per-slot reservation and the reader-demand reservation rule.
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

    /// <summary>
    /// Background dispatch's concurrency ceiling. Normally equal to
    /// <see cref="WorkerPoolOptions.MaxConcurrentJobs"/>, so a pure background
    /// burst (the common unattended-scan case) can use every configured
    /// worker. While at least one <see cref="AcquireSlotAsync"/> caller
    /// (reader demand — <see cref="ExtractPageAsync"/>) is actually waiting
    /// for a slot, this drops by one so background dispatch always leaves a
    /// slot for it rather than racing it for every freed slot (background
    /// reacts to a freed slot immediately via <see cref="_dispatchSignal"/>,
    /// while a reader only polls every 100ms, so an unthrottled race would
    /// systematically starve the reader under a sustained burst). With
    /// MaxConcurrentJobs == 1 there is nothing to reserve.
    /// </summary>
    private int EffectiveBackgroundCap()
    {
        var max = _options.MaxConcurrentJobs;
        if (max <= 1)
            return max;
        return Volatile.Read(ref _waitingReaders) > 0 ? max - 1 : max;
    }

    /// <summary>
    /// Atomically checks the reservation rule and claims a free existing
    /// worker slot, or returns null if none may be claimed right now (either
    /// every worker is busy, or claiming the last idle one would exceed
    /// <see cref="EffectiveBackgroundCap"/>). Never starts a new worker.
    /// </summary>
    private WorkerSlot? TryClaimBackgroundSlot()
    {
        lock (_poolLock)
        {
            if (_workers.Count(w => w.IsBusy) >= EffectiveBackgroundCap())
                return null;
            var slot = _workers.FirstOrDefault(w => !w.IsBusy);
            if (slot is not null)
                slot.IsBusy = true;
            return slot;
        }
    }

    /// <summary>
    /// Reserves the right to start one more worker toward <paramref name="cap"/>,
    /// counting both existing workers and other starts already in flight
    /// (<see cref="_startingWorkers"/>), so two concurrent callers deciding
    /// "there's room" at the same instant cannot both start a worker and
    /// overshoot the cap. Pair with <see cref="ReleaseWorkerStartReservation"/>.
    /// </summary>
    private bool TryReserveWorkerStart(int cap)
    {
        lock (_poolLock)
        {
            if (_workers.Count + _startingWorkers >= cap)
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

        var slot = TryClaimBackgroundSlot();
        if (slot is null)
        {
            var cap = EffectiveBackgroundCap();
            if (TryReserveWorkerStart(cap))
            {
                _logger.LogDebug(LogEvents.Worker.PoolScaleUp, "No free worker slot; starting additional worker (current {Count}/{Max})",
                    WorkerCount, _options.MaxConcurrentJobs);
                try
                {
                    await StartWorkerAsync(ct);
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
                slot = TryClaimBackgroundSlot();
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
            slot.IsBusy = false;
            SignalDispatch();
            _logger.LogDebug(LogEvents.Worker.PoolIdleSlotReleased, "Slot {Slot} reserved but queue is empty; releasing slot", slot.Id);
            LogThroughputSummaryIfPending();
            return false;
        }

        // One Debug line per dispatched job: operation, correlation IDs, and the
        // queue depths needed to diagnose saturation. Completion is logged by the
        // scheduler with the duration; per-protocol messages stay at Trace.
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
    /// On-demand single-page extraction + variant encoding (C13). Borrows a worker
    /// slot, sends an "extract" request, and awaits the encoded output written to
    /// <paramref name="outputPath"/>. The server never opens the archive itself.
    /// Returns an outcome the controller maps to an HTTP response; never throws for
    /// worker-side failures.
    /// </summary>
    public async Task<PageExtractionOutcome> ExtractPageAsync(
        string archivePath,
        string sourceEntryKey,
        string variant,
        long expectedLastWriteTicks,
        long expectedByteLength,
        string outputPath,
        int thumbnailMaxDimension,
        int webpQuality,
        CancellationToken ct = default)
    {
        if (_isShuttingDown)
            return PageExtractionOutcome.Failed("unavailable", "Server is shutting down.");

        // Acquire a worker slot, briefly waiting if all are busy (e.g. mid-scan).
        var slot = await AcquireSlotAsync(ct);
        if (slot is null)
            return PageExtractionOutcome.Failed("busy", "No worker available; try again.");

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
                                ? PageExtractionOutcome.Ok(r.OutputPath, r.MediaType, r.Width, r.Height, r.ByteSize)
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
                await slot.Supervisor.SendMessageAsync(envelope, ct);

                var timeout = Task.Delay(_options.AnalysisTimeout + _options.SourceOpenTimeout, ct);
                var done = await Task.WhenAny(tcs.Task, timeout);
                if (done != tcs.Task)
                    return PageExtractionOutcome.Failed("timeout", "Extraction timed out.");
                return await tcs.Task;
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
            slot.IsBusy = false;
            SignalDispatch();
        }
    }

    /// <summary>
    /// Finds a free worker slot, starting one if under the concurrency cap, and
    /// otherwise waiting briefly for one to free up. Marks the returned slot busy.
    /// Marks itself as a waiting reader for the duration of the call (even the
    /// fast path where a slot is free immediately) so background dispatch can
    /// see reader demand exists and leave it a slot — see
    /// <see cref="EffectiveBackgroundCap"/>.
    /// </summary>
    private async Task<WorkerSlot?> AcquireSlotAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(_options.SourceOpenTimeout);
        _logger.LogDebug(LogEvents.Worker.ExtractSlotAcquire, "Acquiring worker slot for extract (timeout {TimeoutMs}ms, current {Count}/{Max})",
            _options.SourceOpenTimeout.TotalMilliseconds, WorkerCount, _options.MaxConcurrentJobs);
        Interlocked.Increment(ref _waitingReaders);
        try
        {
            while (!_isShuttingDown)
            {
                WorkerSlot? slot;
                lock (_poolLock)
                {
                    slot = _workers.FirstOrDefault(w => !w.IsBusy);
                    if (slot is not null) { slot.IsBusy = true; return slot; }
                }

                if (TryReserveWorkerStart(_options.MaxConcurrentJobs))
                {
                    try { await StartWorkerAsync(ct); }
                    catch (Exception ex) { _logger.LogWarning(LogEvents.Worker.ExtractWorkerStartFailed, ex, "Failed to start worker for extract: {Error}", ex.GetType().Name); }
                    finally { ReleaseWorkerStartReservation(); }

                    lock (_poolLock)
                    {
                        slot = _workers.FirstOrDefault(w => !w.IsBusy);
                        if (slot is not null) { slot.IsBusy = true; return slot; }
                    }
                }

                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogDebug(LogEvents.Worker.ExtractSlotAcquireTimeout, "Extract slot acquisition timed out after {TimeoutMs}ms (saturated at {Count}/{Max})",
                        _options.SourceOpenTimeout.TotalMilliseconds, WorkerCount, _options.MaxConcurrentJobs);
                    return null;
                }
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return null; }
            }
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _waitingReaders);
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

        List<WorkerSlot> workers;
        lock (_poolLock)
        {
            workers = _workers.ToList();
            _workers.Clear();
        }

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

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        var supervisor = CreateSupervisor();
        _logger.LogDebug(LogEvents.Worker.WorkerProcessStartAttempt, "Starting worker process (attempt {Count}/{Max})", _workers.Count + 1, _options.MaxConcurrentJobs);

        try
        {
            await supervisor.StartAsync(ct);

            var slot = new WorkerSlot
            {
                Id = _workers.Count,
                Supervisor = supervisor,
            };

            // Set up crash handler
            supervisor.OnWorkerExited += exitCode =>
            {
                _logger.LogWarning(LogEvents.Worker.WorkerExited, "Worker {Id} exited with code {ExitCode}", slot.Id, exitCode);
                lock (_poolLock)
                {
                    _workers.Remove(slot);
                }
                _logger.LogDebug(LogEvents.Worker.WorkerRemovedFromPool, "Worker {Id} removed from pool; remaining workers {Count}", slot.Id, _workers.Count);
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
    /// 2. Sibling <c>../worker/MangaPlex.MediaWorker.dll</c> (container layout)
    /// 3. Same directory as the server assembly
    /// 4. Dev sibling project output
    /// When the result is a .dll, the launch command is <c>dotnet &lt;dll&gt;</c>.
    /// </summary>
    private (string FileName, string Arguments) ResolveWorkerLaunch()
    {
        const string workerDllName = "MangaPlex.MediaWorker.dll";
        var assemblyDir = AppContext.BaseDirectory;

        // 1. Explicit configuration
        if (!string.IsNullOrWhiteSpace(_options.WorkerExecutablePath))
        {
            var configured = _options.WorkerExecutablePath!;
            return Path.GetExtension(configured).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? ("dotnet", configured)
                : (configured, string.Empty);
        }

        // 2. Container layout: /app/server/ + /app/worker/MangaPlex.MediaWorker.dll
        var containerPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "worker", workerDllName));
        if (File.Exists(containerPath))
            return ("dotnet", containerPath);

        // 3. Published alongside server
        var siblingPath = Path.Combine(assemblyDir, workerDllName);
        if (File.Exists(siblingPath))
            return ("dotnet", siblingPath);

        // 4. Dev: sibling project output (Release then Debug)
        var devRelease = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "MangaPlex.MediaWorker", "bin", "Release", "net10.0", workerDllName));
        if (File.Exists(devRelease))
            return ("dotnet", devRelease);

        var devDebug = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "MangaPlex.MediaWorker", "bin", "Debug", "net10.0", workerDllName));
        if (File.Exists(devDebug))
            return ("dotnet", devDebug);

        // Fallback: assume the worker is a native executable on PATH.
        _logger?.LogWarning(LogEvents.Worker.WorkerExecutableFallback, "Worker executable not discovered; falling back to {Name} on PATH", "MangaPlex.MediaWorker");
        return ("MangaPlex.MediaWorker", string.Empty);
    }

    private async Task ProcessJobAsync(WorkerSlot slot, PendingJob job, CancellationToken ct)
    {
        slot.IsBusy = true;
        _scheduler.MarkInFlight(job);

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
            slot.IsBusy = false;
            // Scratch workspace is cleaned up by the using statement
        }

        // Persist the result for EVERY job (background and reader-demand), so a
        // scanned library's covers/manifests populate without opening each item.
        // This runs AFTER the slot is released so that thumbnail generation (which
        // calls ExtractPageAsync and acquires its own slot) does not deadlock when
        // the pool is at capacity.
        await PersistResultAsync(job.ItemId, finalResult);

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
    private async Task PersistResultAsync(long nodeId, JobResult result)
    {
        if (_scopeFactory is null || _persister is null)
            return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            await _persister.PersistAsync(db, nodeId, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.PersistAfterJobFailed, ex, "Persisting analysis for item {ItemId} failed: {Error}", nodeId, ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _dispatchSignal.Dispose();
    }

    private sealed class WorkerSlot
    {
        public int Id { get; init; }
        public required WorkerSupervisor Supervisor { get; init; }
        public bool IsBusy { get; set; }
        public Task? ReadLoop { get; set; }
    }
}

/// <summary>
/// Result of an on-demand page extraction (C13). On success the encoded image is
/// at <see cref="OutputPath"/>; on failure <see cref="ErrorType"/> maps to an HTTP
/// response (e.g. "unsupported_solid", "page_not_found", "encrypted", "timeout").
/// </summary>
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

    public static PageExtractionOutcome Ok(string outputPath, string mediaType, int width, int height, long byteSize) =>
        new() { Success = true, OutputPath = outputPath, MediaType = mediaType, Width = width, Height = height, ByteSize = byteSize };

    public static PageExtractionOutcome Failed(string errorType, string errorMessage) =>
        new() { Success = false, ErrorType = errorType, ErrorMessage = errorMessage };
}
