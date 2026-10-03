namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// A reader that gives up (the reader aborts its page prefetch when the user turns on) is a cancellation, not a
/// pool failure: against real spawned workers, a cancelled request is reported as <c>cancelled</c> - never as
/// <c>busy</c> or <c>timeout</c> - logs nothing above Debug, and does not throw away a worker that is still
/// starting. Real busy / timeout take minutes, so a burst of them within seconds was always cancellations.
/// Category: Process - must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class WorkerPoolCancellationTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];

    public WorkerPoolCancellationTests(WorkerProcessFixture fixture) => _fixture = fixture;

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools)
        {
            try { await pool.StopAsync(); } catch { /* best effort */ }
            await pool.DisposeAsync();
        }
    }

    private MediaWorkerPool CreatePool(RecordingLogger<MediaWorkerPool> logger, int maxConcurrentJobs, TimeSpan? idleTimeout = null)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        options.WorkerIdleTimeout = idleTimeout ?? TimeSpan.Zero;
        var pool = new MediaWorkerPool(
            options, new JobScheduler(options), new ScratchWorkspaceManager(_fixture.ScratchRoot),
            logger, NullLoggerFactory.Instance);
        _pools.Add(pool);
        return pool;
    }

    private Task<PageExtractionOutcome> ExtractAsync(MediaWorkerPool pool, string zip, string entry, string output, CancellationToken ct)
    {
        var fi = new FileInfo(zip);
        return pool.ExtractPageAsync(zip, entry, "webp", fi.LastWriteTimeUtc.Ticks, fi.Length,
            Path.Combine(_fixture.ScratchRoot, output), 200, 80, ct);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task ColdPool_ReaderLeavesDuringWorkerStart_IsCancelled_AndTheStartedWorkerJoinsThePool()
    {
        var logger = new RecordingLogger<MediaWorkerPool>();
        // Long enough that the handed-back worker is still there when the test looks, short enough to retire the
        // pre-started one quickly.
        var pool = CreatePool(logger, maxConcurrentJobs: 2, idleTimeout: TimeSpan.FromSeconds(3));
        await pool.StartAsync();
        Assert.True(await WaitForAsync(() => pool.WorkerCount == 0, TimeSpan.FromSeconds(30)), "The pre-started worker was not retired");
        logger.Clear();

        // The worker process needs far longer than 30 ms to handshake, so the request is cancelled mid-start.
        var zip = _fixture.CreateValidImageZip("cancel-cold.zip");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var outcome = await ExtractAsync(pool, zip, "page001.png", "cancel-cold.webp", cts.Token);

        Assert.False(outcome.Success);
        Assert.Equal("cancelled", outcome.ErrorType);
        Assert.Empty(logger.AtLeast(LogLevel.Warning));

        // The abandoned start finishes in the background and the worker is handed back idle: the next request
        // finds it warm instead of paying the spawn again.
        // Waited for together (1.33.0): the worker is counted in the pool a moment before its busy mark is cleared, so asserting
        // the busy count right after the worker count failed about one run in five under load.
        Assert.True(await WaitForAsync(() => pool.WorkerCount == 1 && pool.BusyWorkerCount == 0, TimeSpan.FromSeconds(20)),
            $"The abandoned start did not join the pool idle (workers {pool.WorkerCount}, busy {pool.BusyWorkerCount})");
        Assert.Empty(logger.AtLeast(LogLevel.Warning));

        var next = await ExtractAsync(pool, zip, "page001.png", "cancel-cold-next.webp", CancellationToken.None);
        Assert.True(next.Success, $"The next request failed: {next.ErrorType} {next.ErrorMessage}");
    }

    [Fact]
    public async Task SaturatedPool_ReaderLeavesWhileWaitingForASlot_IsCancelledNotBusy()
    {
        var logger = new RecordingLogger<MediaWorkerPool>();
        var pool = CreatePool(logger, maxConcurrentJobs: 1);
        await pool.StartAsync();

        var slow = _fixture.CreateSlowImageZip("cancel-busy.zip", size: 4000);
        var holder = ExtractAsync(pool, slow, "page001.jpg", "cancel-busy-holder.webp", CancellationToken.None);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The slow extract never took the slot");

        // The only slot is taken, so this request waits (100 ms polls) and is then cancelled while it waits.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var waiting = await ExtractAsync(pool, slow, "page001.jpg", "cancel-busy-waiter.webp", cts.Token);

        Assert.Equal("cancelled", waiting.ErrorType);
        Assert.Empty(logger.AtLeast(LogLevel.Warning));
        // The holder was not disturbed by its neighbour leaving.
        var held = await holder;
        Assert.True(held.Success, $"The holder failed: {held.ErrorType} {held.ErrorMessage}");
    }

    [Fact]
    public async Task ReaderLeavesWhileTheWorkerIsExtracting_IsCancelledNotTimeout()
    {
        var logger = new RecordingLogger<MediaWorkerPool>();
        var pool = CreatePool(logger, maxConcurrentJobs: 1);
        await pool.StartAsync();

        var slow = _fixture.CreateSlowImageZip("cancel-run.zip", size: 4000);
        using var cts = new CancellationTokenSource();
        var running = ExtractAsync(pool, slow, "page001.jpg", "cancel-run.webp", cts.Token);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The slow extract never took the slot");
        cts.Cancel();

        var outcome = await running;
        // Guard against a vacuous pass: the extract must still have been running when the reader left.
        Assert.False(outcome.Success, "The fixture finished before the reader left; it proves nothing");
        Assert.Equal("cancelled", outcome.ErrorType);
        Assert.Empty(logger.AtLeast(LogLevel.Warning));
    }

    /// <summary>Records every entry so a test can assert nothing above Debug was logged.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public void Clear() => _entries.Clear();

        public IReadOnlyList<string> AtLeast(LogLevel level)
            => _entries.Where(e => e.Level >= level).Select(e => $"{e.Level}: {e.Message}").ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
