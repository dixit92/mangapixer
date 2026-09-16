namespace com.lifepixer.mangapixer.Server.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Write priority levels. Account/progress writes have priority over scan batches.
/// </summary>
public enum WritePriority
{
    /// <summary>
    /// Account/login/session writes. Highest priority.
    /// </summary>
    Account = 0,

    /// <summary>
    /// Reading progress and bookmark saves. High priority.
    /// </summary>
    Progress = 1,

    /// <summary>
    /// Administration (grants, library config). Medium priority.
    /// </summary>
    Admin = 2,

    /// <summary>
    /// Scan reconciliation batches. Lower priority, fairness-protected.
    /// </summary>
    Scan = 3,

    /// <summary>
    /// Background jobs (hashing, cache metadata). Lowest priority.
    /// </summary>
    Background = 4
}

/// <summary>
/// Server-wide write coordinator that serializes short database write units.
/// Account/progress writes have priority over scan batches.
/// No filesystem I/O, archive decompression, hashing, or HTTP streaming
/// is allowed inside a write transaction.
///
/// The coordinator uses a single SemaphoreSlim(1,1) for write serialization
/// and a priority queue for ordering. This prevents SQLITE_BUSY errors
/// and ensures responsive user-facing writes even during large scans.
/// </summary>
public sealed class WriteCoordinator : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly PriorityQueue<WriteRequest, (int priority, long sequence)> _queue = new();
    private readonly object _queueLock = new();
    private long _sequenceCounter;
    private bool _disposed;

    /// <summary>
    /// Executes a write operation with the specified priority.
    /// The operation receives a DbContext that is already in a transaction.
    /// Do not perform any I/O other than database writes inside the operation.
    /// </summary>
    public async Task<T> ExecuteWriteAsync<T>(
        Func<MangaPixerDbContext, CancellationToken, Task<T>> operation,
        WritePriority priority,
        MangaPixerDbContextFactory dbContextFactory,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new WriteRequest
        {
            Priority = priority,
            Sequence = Interlocked.Increment(ref _sequenceCounter),
            Operation = async (db, token) =>
            {
                var result = await operation(db, token);
                tcs.TrySetResult(result);
            },
            DbContextFactory = dbContextFactory,
            CancellationToken = ct,
        };

        Enqueue(request);
        await ProcessQueueAsync();

        if (request.Exception is not null)
            tcs.TrySetException(request.Exception);

        return await tcs.Task;
    }

    private void Enqueue(WriteRequest request)
    {
        lock (_queueLock)
        {
            _queue.Enqueue(request, ((int)request.Priority, request.Sequence));
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (await _writeLock.WaitAsync(0))
        {
            WriteRequest? request;
            lock (_queueLock)
            {
                if (_queue.Count == 0)
                {
                    _writeLock.Release();
                    return;
                }
                request = _queue.Dequeue();
            }

            try
            {
                await ExecuteRequestAsync(request);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }

    private static async Task ExecuteRequestAsync(WriteRequest request)
    {
        try
        {
            await using var db = request.DbContextFactory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, request.CancellationToken);

            await request.Operation(db, request.CancellationToken);
            await transaction.CommitAsync(request.CancellationToken);
        }
        catch (Exception ex)
        {
            // The TaskCompletionSource is set by the operation itself on success.
            // On failure, we need to propagate the exception.
            // We store it for the caller to observe.
            request.Exception = ex;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
    }

    private sealed class WriteRequest
    {
        public required WritePriority Priority { get; init; }
        public required long Sequence { get; init; }
        public required Func<MangaPixerDbContext, CancellationToken, Task> Operation { get; init; }
        public required MangaPixerDbContextFactory DbContextFactory { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public Exception? Exception { get; set; }
    }
}

/// <summary>
/// Factory for creating short-lived DbContext instances.
/// Each write operation gets its own DbContext — never shared across threads.
/// </summary>
public sealed class MangaPixerDbContextFactory
{
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public MangaPixerDbContextFactory(DbContextOptions<MangaPixerDbContext> options)
    {
        _options = options;
    }

    public MangaPixerDbContext CreateDbContext()
    {
        return new MangaPixerDbContext(_options);
    }
}
