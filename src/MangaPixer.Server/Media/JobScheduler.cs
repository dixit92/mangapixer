namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

using System.Collections.Concurrent;

/// <summary>
/// Schedules media worker jobs with deduplication and priority ordering.
///
/// Priority: CurrentPage > Prefetch > Background.
/// Identical (itemId, contentVersion, operation) requests are deduplicated:
/// multiple callers share one server-side operation.
/// Solid archive preparation is shared rather than independently repeated.
///
/// Cancelling a browser request detaches that caller without cancelling
/// shared work needed by another reader.
/// </summary>
public sealed class JobScheduler
{
    private readonly ConcurrentDictionary<string, PendingJob> _pendingJobs = new();
    private readonly ConcurrentDictionary<string, InFlightJob> _inFlightJobs = new();
    private readonly object _dispatchLock = new();
    private readonly WorkerPoolOptions _options;
    private readonly ILogger<JobScheduler>? _logger;

    public JobScheduler(WorkerPoolOptions options, ILogger<JobScheduler>? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a job for processing. If an identical job is already pending or in-flight,
    /// returns the existing job's completion source (deduplication).
    /// </summary>
    public Task<JobResult> EnqueueAsync(
        long itemId,
        long contentVersion,
        JobOperation operation,
        JobPriority priority,
        string archivePath,
        long expectedLastWriteTicks,
        long expectedByteLength,
        CancellationToken callerToken = default)
    {
        var dedupKey = BuildDedupKey(itemId, contentVersion, operation);

        // Check for existing in-flight job
        if (_inFlightJobs.TryGetValue(dedupKey, out var inFlight))
        {
            // Attach caller to existing job — don't cancel shared work
            _logger?.LogDebug(LogEvents.Worker.SchedulerJobDedupedInFlight, "Job {JobId} (item {ItemId}) deduplicated onto in-flight job (operation {Operation})",
                inFlight.JobId, itemId, operation);
            return AttachToInFlightJob(inFlight, callerToken);
        }

        // Check for existing pending job
        if (_pendingJobs.TryGetValue(dedupKey, out var pending))
        {
            // Upgrade priority if the new request has higher priority
            if (priority > pending.Priority)
            {
                _logger?.LogDebug(LogEvents.Worker.SchedulerPriorityUpgraded, "Job {JobId} (item {ItemId}) priority upgraded {Old} -> {New} by concurrent enqueue",
                    pending.JobId, itemId, pending.Priority, priority);
                pending.Priority = priority;
            }

            return AttachToPendingJob(pending, callerToken);
        }

        // Create a new job
        var job = new PendingJob
        {
            DedupKey = dedupKey,
            ItemId = itemId,
            ContentVersion = contentVersion,
            Operation = operation,
            Priority = priority,
            ArchivePath = archivePath,
            ExpectedLastWriteTicks = expectedLastWriteTicks,
            ExpectedByteLength = expectedByteLength,
            JobId = Guid.NewGuid().ToString("N"),
            CompletionSource = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };

        if (!_pendingJobs.TryAdd(dedupKey, job))
        {
            // Race — another thread added the same key
            _logger?.LogDebug(LogEvents.Worker.SchedulerEnqueueRace, "Enqueue race on dedup key {DedupKey} (item {ItemId}); retrying attach",
                dedupKey, itemId);
            return EnqueueAsync(itemId, contentVersion, operation, priority,
                archivePath, expectedLastWriteTicks, expectedByteLength, callerToken);
        }

        _logger?.LogDebug(LogEvents.Worker.SchedulerJobEnqueued, "Job {JobId} enqueued (item {ItemId}, operation {Operation}, priority {Priority}); pending now {Pending}",
            job.JobId, itemId, operation, priority, _pendingJobs.Count);

        // Attach caller cancellation
        return WaitForJobCompletion(job, callerToken);
    }

    /// <summary>
    /// Dequeues the highest-priority pending job. Returns null if no jobs are pending.
    /// Called by the MediaWorkerPool when a worker becomes available.
    /// </summary>
    /// <summary>
    /// Dequeues the highest-priority pending job. Returns null if no jobs are pending.
    /// Called by the MediaWorkerPool when a worker becomes available; the pool logs
    /// the dispatch (with queue depth) at Debug, so dequeue is not logged separately.
    /// </summary>
    public PendingJob? Dequeue()
    {
        lock (_dispatchLock)
        {
            if (_pendingJobs.IsEmpty)
                return null;

            // Select highest priority job, then FIFO within same priority
            var best = _pendingJobs.Values
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.EnqueuedAt)
                .FirstOrDefault();

            if (best is null)
                return null;

            _pendingJobs.TryRemove(best.DedupKey, out _);
            return best;
        }
    }

    /// <summary>
    /// Marks a job as in-flight (dispatched to a worker).
    /// </summary>
    public void MarkInFlight(PendingJob job)
    {
        var inFlight = new InFlightJob
        {
            DedupKey = job.DedupKey,
            JobId = job.JobId,
            CompletionSource = job.CompletionSource,
            StartedAt = DateTimeOffset.UtcNow,
        };
        _inFlightJobs.TryAdd(job.DedupKey, inFlight);
    }

    /// <summary>
    /// Completes an in-flight job with a result. Notifies all waiting callers.
    /// </summary>
    public void CompleteJob(string dedupKey, JobResult result)
    {
        if (_inFlightJobs.TryRemove(dedupKey, out var inFlight))
        {
            _logger?.LogDebug(LogEvents.Worker.SchedulerJobCompleted, "Job {JobId} completed in {DurationMs}ms (success={Success}, errorType={ErrorType})",
                inFlight.JobId, (DateTimeOffset.UtcNow - inFlight.StartedAt).TotalMilliseconds,
                result.Success, result.ErrorType);
            inFlight.CompletionSource.TrySetResult(result);
        }
    }

    /// <summary>
    /// Fails an in-flight job with an error. Notifies all waiting callers.
    /// </summary>
    public void FailJob(string dedupKey, Exception error)
    {
        if (_inFlightJobs.TryRemove(dedupKey, out var inFlight))
        {
            _logger?.LogWarning(LogEvents.Worker.SchedulerJobFailed, error, "Job {JobId} failed after {DurationMs}ms: {ErrorType}",
                inFlight.JobId, (DateTimeOffset.UtcNow - inFlight.StartedAt).TotalMilliseconds, error.GetType().Name);
            inFlight.CompletionSource.TrySetException(error);
        }
    }

    /// <summary>
    /// Cancels all pending jobs for a specific item (e.g., when changing chapters).
    /// Does not cancel in-flight shared work.
    /// </summary>
    public void CancelPendingForItem(long itemId)
    {
        var toRemove = _pendingJobs.Values
            .Where(j => j.ItemId == itemId)
            .ToList();

        foreach (var job in toRemove)
        {
            if (_pendingJobs.TryRemove(job.DedupKey, out _))
            {
                _logger?.LogDebug(LogEvents.Worker.SchedulerPendingCancelled, "Cancelling pending job {JobId} for item {ItemId}", job.JobId, itemId);
                job.CompletionSource.TrySetCanceled();
            }
        }
    }

    /// <summary>
    /// Number of pending jobs.
    /// </summary>
    public int PendingCount => _pendingJobs.Count;

    /// <summary>
    /// Number of in-flight jobs.
    /// </summary>
    public int InFlightCount => _inFlightJobs.Count;

    private static string BuildDedupKey(long itemId, long contentVersion, JobOperation operation)
    {
        return $"{itemId}:{contentVersion}:{operation}";
    }

    private static async Task<JobResult> AttachToInFlightJob(InFlightJob inFlight, CancellationToken callerToken)
    {
        // Register caller for cancellation without cancelling the shared job
        using var registration = callerToken.Register(() => { /* detach only */ });
        return await inFlight.CompletionSource.Task;
    }

    private static async Task<JobResult> AttachToPendingJob(PendingJob pending, CancellationToken callerToken)
    {
        using var registration = callerToken.Register(() => { /* detach only */ });
        return await pending.CompletionSource.Task;
    }

    private static async Task<JobResult> WaitForJobCompletion(PendingJob job, CancellationToken callerToken)
    {
        using var registration = callerToken.Register(() =>
        {
            // Caller cancelled — detach. The job continues for other callers.
            // If this was the only caller, the job still runs (it was already enqueued).
        });
        return await job.CompletionSource.Task;
    }
}

/// <summary>
/// A pending job waiting to be dispatched to a worker.
/// </summary>
public sealed class PendingJob
{
    public required string DedupKey { get; init; }
    public required string JobId { get; init; }
    public required long ItemId { get; init; }
    public required long ContentVersion { get; init; }
    public required JobOperation Operation { get; init; }
    public JobPriority Priority { get; set; }
    public required string ArchivePath { get; init; }
    public required long ExpectedLastWriteTicks { get; init; }
    public required long ExpectedByteLength { get; init; }
    public required TaskCompletionSource<JobResult> CompletionSource { get; init; }
    public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A job currently being processed by a worker.
/// </summary>
sealed class InFlightJob
{
    public required string DedupKey { get; init; }
    public required string JobId { get; init; }
    public required TaskCompletionSource<JobResult> CompletionSource { get; init; }
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>
/// Result of a completed media worker job.
/// </summary>
public sealed record JobResult
{
    public required string JobId { get; init; }
    public required bool Success { get; init; }
    public required string? ErrorType { get; init; }
    public required string? ErrorMessage { get; init; }
    public required object? Result { get; init; }
}
