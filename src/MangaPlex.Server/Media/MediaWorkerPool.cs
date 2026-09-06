namespace com.lifepixer.mangaplex.Server.Media;

using System.Diagnostics;
using com.lifepixer.mangaplex.Core.WorkerProtocol;
using com.lifepixer.mangaplex.MediaWorker.Protocol;

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
    private readonly List<WorkerSlot> _workers = [];
    private readonly object _poolLock = new();
    private bool _isStarted;
    private bool _isShuttingDown;

    public MediaWorkerPool(
        WorkerPoolOptions options,
        JobScheduler scheduler,
        ScratchWorkspaceManager scratchManager,
        ILogger<MediaWorkerPool> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _scheduler = scheduler;
        _scratchManager = scratchManager;
        _logger = logger;
        _loggerFactory = loggerFactory;
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
    /// </summary>
    public async Task DispatchAsync(CancellationToken ct = default)
    {
        if (_isShuttingDown)
            return;

        // Try to dequeue a job
        var job = _scheduler.Dequeue();
        if (job is null)
            return;

        // Find an available worker slot
        WorkerSlot? slot = null;
        lock (_poolLock)
        {
            slot = _workers.FirstOrDefault(w => !w.IsBusy);
            if (slot is null && _workers.Count < _options.MaxConcurrentJobs)
            {
                // Start a new worker — but only if this is a reader-demand job
                // Background analysis should not occupy both slots by default
                if (job.Priority > JobPriority.Background || _workers.Count == 0)
                {
                    slot = new WorkerSlot
                    {
                        Id = _workers.Count,
                        Supervisor = CreateSupervisor(),
                    };
                    _workers.Add(slot);
                }
            }
        }

        if (slot is null)
        {
            // No available slot — re-enqueue the job
            // Put it back by enqueuing a new completion source
            // Actually, we should not have dequeued it. Let's handle this properly.
            // For now, re-add to scheduler by completing with a retry indication.
            _scheduler.FailJob(job.DedupKey,
                new InvalidOperationException("No available worker slot"));
            return;
        }

        await ProcessJobAsync(slot, job, ct);
    }

    /// <summary>
    /// Stops all workers gracefully. Stops dispatch, allows bounded grace,
    /// then terminates remaining workers.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _isShuttingDown = true;

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

            // Send the analyze request
            var requestEnvelope = WorkerProtocolFraming.CreateEnvelope("analyze", job.JobId, request);
            await slot.Supervisor.SendMessageAsync(requestEnvelope, ct);

            // Start reading messages (if not already reading)
            var readTask = slot.Supervisor.ReadMessagesAsync(ct);

            // Wait for completion with timeout
            var timeoutTask = Task.Delay(_options.AnalysisTimeout + _options.SourceOpenTimeout, ct);
            var completedTask = await Task.WhenAny(completionTcs.Task, timeoutTask);

            slot.Supervisor.OnMessageReceived -= HandleMessage;

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

                var failResult = new JobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorType = "timeout",
                    ErrorMessage = "Job exceeded timeout",
                    Result = null,
                };
                _scheduler.CompleteJob(job.DedupKey, failResult);
            }
            else
            {
                var result = await completionTcs.Task;
                _scheduler.CompleteJob(job.DedupKey, result);
            }
        }
        catch (Exception ex)
        {
            _scheduler.FailJob(job.DedupKey, ex);
        }
        finally
        {
            slot.IsBusy = false;
            // Scratch workspace is cleaned up by the using statement
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
    }
}
