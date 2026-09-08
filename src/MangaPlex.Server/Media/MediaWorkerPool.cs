namespace com.lifepixer.mangaplex.Server.Media;

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
    private bool _isStarted;
    private bool _isShuttingDown;

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

        _scratchManager.Initialize();
        _isStarted = true;

        // Pre-start one background worker
        await StartWorkerAsync(ct);
    }

    /// <summary>
    /// Dispatches a job to an available worker. If no worker is available and
    /// the pool has not reached MaxConcurrentJobs, starts a new worker.
    /// Otherwise, the job remains in the scheduler queue.
    ///
    /// Audit defect D12: reserve a slot BEFORE dequeuing. If no slot is
    /// available, return without touching the queue so the job remains
    /// pending for the next dispatch cycle. Never fail a job for lack
    /// of a worker slot.
    /// </summary>
    public async Task DispatchAsync(CancellationToken ct = default)
    {
        if (_isShuttingDown)
            return;

        // Reserve a slot BEFORE dequeuing (audit defect D12). Find a free worker;
        // if none is free and we are under the concurrency cap, start ONE more
        // (properly, via StartWorkerAsync, which performs the handshake). The old
        // code only started a worker when the pool was completely empty, so a
        // single stuck/busy worker would deadlock all further dispatch.
        WorkerSlot? slot;
        bool startAnother = false;
        lock (_poolLock)
        {
            slot = _workers.FirstOrDefault(w => !w.IsBusy);
            if (slot is null && _workers.Count < _options.MaxConcurrentJobs)
                startAnother = true;
        }

        if (slot is null && startAnother)
        {
            try
            {
                await StartWorkerAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Failed to start additional worker: {Error}", ex.GetType().Name);
                return;
            }
            lock (_poolLock)
            {
                slot = _workers.FirstOrDefault(w => !w.IsBusy);
            }
        }

        if (slot is null)
        {
            // No available slot — leave the job in the queue (D12)
            return;
        }

        // Reserve the slot before dequeuing so a concurrent dispatch cannot grab
        // the same worker, then dequeue the highest-priority job.
        slot.IsBusy = true;
        var job = _scheduler.Dequeue();
        if (job is null)
        {
            slot.IsBusy = false;
            return;
        }

        await ProcessJobAsync(slot, job, ct);
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
            _logger?.LogWarning("Extract dispatch failed: {Error}", ex.GetType().Name);
            return PageExtractionOutcome.Failed("extraction_failed", "Extraction failed.");
        }
        finally
        {
            slot.IsBusy = false;
        }
    }

    /// <summary>
    /// Finds a free worker slot, starting one if under the concurrency cap, and
    /// otherwise waiting briefly for one to free up. Marks the returned slot busy.
    /// </summary>
    private async Task<WorkerSlot?> AcquireSlotAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(_options.SourceOpenTimeout);
        while (!_isShuttingDown)
        {
            WorkerSlot? slot;
            bool startAnother = false;
            lock (_poolLock)
            {
                slot = _workers.FirstOrDefault(w => !w.IsBusy);
                if (slot is not null) { slot.IsBusy = true; return slot; }
                if (_workers.Count < _options.MaxConcurrentJobs) startAnother = true;
            }

            if (startAnother)
            {
                try { await StartWorkerAsync(ct); }
                catch (Exception ex) { _logger?.LogWarning("Failed to start worker for extract: {Error}", ex.GetType().Name); }
                lock (_poolLock)
                {
                    slot = _workers.FirstOrDefault(w => !w.IsBusy);
                    if (slot is not null) { slot.IsBusy = true; return slot; }
                }
            }

            if (DateTime.UtcNow >= deadline) return null;
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Stops all workers gracefully. Stops dispatch, allows bounded grace,
    /// then terminates remaining workers.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
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
                _logger?.LogWarning("Error stopping worker {Id}: {Error}", slot.Id, ex.GetType().Name);
            }
        }

        _isStarted = false;
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

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        var supervisor = CreateSupervisor();

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
                _logger?.LogWarning("Worker {Id} exited with code {ExitCode}", slot.Id, exitCode);
                lock (_poolLock)
                {
                    _workers.Remove(slot);
                }
            };

            lock (_poolLock)
            {
                _workers.Add(slot);
            }

            // ONE persistent read loop per worker for its whole lifetime. Jobs only
            // swap the OnMessageReceived handler; they must not start their own read
            // loop, or competing readers would drop responses (a fast reply to job N
            // could be consumed by job N-1's orphaned loop and lost).
            slot.ReadLoop = Task.Run(() => supervisor.ReadMessagesAsync(_readCts.Token), _readCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to start worker: {Error}", ex.GetType().Name);
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
        _logger?.LogWarning("Worker executable not discovered; falling back to {Name} on PATH", "MangaPlex.MediaWorker");
        return ("MangaPlex.MediaWorker", string.Empty);
    }

    private async Task ProcessJobAsync(WorkerSlot slot, PendingJob job, CancellationToken ct)
    {
        slot.IsBusy = true;
        _scheduler.MarkInFlight(job);
        _logger?.LogDebug("Dispatching {Operation} job {JobId} (item {ItemId}) to worker {Slot}",
            job.Operation, job.JobId, job.ItemId, slot.Id);

        // Allocate scratch workspace
        using var workspace = _scratchManager.AllocateWorkspace();

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
            _logger?.LogDebug("Sent analyze request for job {JobId} (item {ItemId})", job.JobId, job.ItemId);

            // Wait for completion with timeout
            var timeoutTask = Task.Delay(_options.AnalysisTimeout + _options.SourceOpenTimeout, ct);
            var completedTask = await Task.WhenAny(completionTcs.Task, timeoutTask);

            slot.Supervisor.OnMessageReceived -= HandleMessage;

            JobResult finalResult;
            if (completedTask == timeoutTask)
            {
                // Timeout — cancel the job
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
                _scheduler.CompleteJob(job.DedupKey, finalResult);
            }

            // Persist the result for EVERY job (background and reader-demand), so a
            // scanned library's covers/manifests populate without opening each item.
            await PersistResultAsync(job.ItemId, finalResult);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Job {JobId} (item {ItemId}) failed during processing", job.JobId, job.ItemId);
            _scheduler.FailJob(job.DedupKey, ex);
            await PersistResultAsync(job.ItemId, new JobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorType = ex.GetType().Name,
                ErrorMessage = ex.Message,
                Result = null,
            });
        }
        finally
        {
            slot.IsBusy = false;
            // Scratch workspace is cleaned up by the using statement
        }
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
            _logger?.LogWarning("Persisting analysis for item {ItemId} failed: {Error}", nodeId, ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
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
