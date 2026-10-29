namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Reader-first worker slots (1.42.0, win 1 + 5) against real spawned workers: a freed slot goes to a waiting
/// reader page before background work that asks again at once (the analysis -> cover thumbnail pattern that
/// starved readers under polling), a reader's current page goes before its prefetches, the "Background work while
/// someone reads" policy applies live, and the worker cap changes live.
/// Category: Process - must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class ReaderFirstSlotTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];

    public ReaderFirstSlotTests(WorkerProcessFixture fixture) => _fixture = fixture;

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools)
        {
            try { await pool.StopAsync(); } catch { /* best effort */ }
            await pool.DisposeAsync();
        }
    }

    private MediaWorkerPool CreatePool(int maxConcurrentJobs, ILoggerFactory? loggerFactory = null, TimeSpan? analysisTimeout = null)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        options.WorkerIdleTimeout = TimeSpan.Zero;
        if (analysisTimeout is { } timeout)
            options.AnalysisTimeout = timeout;
        var pool = new MediaWorkerPool(
            options, new JobScheduler(options), new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, loggerFactory ?? NullLoggerFactory.Instance);
        _pools.Add(pool);
        return pool;
    }

    private async Task<PageExtractionOutcome> ExtractAsync(
        MediaWorkerPool pool, string zip, string entry, MediaSlotKind kind, CancellationToken ct = default)
    {
        var fi = new FileInfo(zip);
        var output = Path.Combine(_fixture.ScratchRoot, "rf-" + Guid.NewGuid().ToString("N")[..8] + ".webp");
        try
        {
            return await pool.ExtractPageAsync(zip, entry, "webp", fi.LastWriteTimeUtc.Ticks, fi.Length, output, 200, 80, ct, slotKind: kind);
        }
        finally
        {
            // The suite's temp folder is a memory-backed tmpfs in the verify container: do not leave encoded pages behind.
            try { File.Delete(output); } catch { /* best effort */ }
        }
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
    public async Task ReaderPage_GetsTheNextFreedSlot_BeforeBackgroundWorkThatAsksAgainAtOnce()
    {
        var pool = CreatePool(maxConcurrentJobs: 1);
        await pool.StartAsync();
        var slow = _fixture.CreateSlowImageZip("rf-starve.zip", size: 1600);

        // Background work that asks for the slot again the moment it lets go - what an analysis job followed by its
        // cover thumbnail does. Under the old 100 ms polling the reader never won that race.
        var backgroundDone = 0;
        using var stop = new CancellationTokenSource();
        var background = Task.Run(async () =>
        {
            for (var i = 0; i < 6 && !stop.IsCancellationRequested; i++)
            {
                var o = await ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Thumbnail);
                Assert.True(o.Success, $"{o.ErrorType} {o.ErrorMessage}");
                Interlocked.Increment(ref backgroundDone);
            }
        });
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The background work never took the slot");

        var page = await ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
        var doneWhenPageServed = Volatile.Read(ref backgroundDone);
        stop.Cancel();
        await background;

        Assert.True(page.Success, $"{page.ErrorType} {page.ErrorMessage}");
        // The reader waited for the extract that was running when it arrived - and for nothing else.
        Assert.True(doneWhenPageServed <= 1, $"The reader page waited behind {doneWhenPageServed} background extracts");
    }

    [Fact]
    public async Task CurrentPage_IsServedBefore_PrefetchesQueuedEarlier()
    {
        var pool = CreatePool(maxConcurrentJobs: 1);
        pool.BackgroundWhileReading = BackgroundWhileReading.Normal;
        await pool.StartAsync();
        var slow = _fixture.CreateSlowImageZip("rf-order.zip", size: 2400);
        var holder = ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The holder never took the slot");

        var order = new List<string>();
        async Task Track(string name, MediaSlotKind kind)
        {
            var o = await ExtractAsync(pool, slow, "page001.jpg", kind);
            Assert.True(o.Success, $"{name}: {o.ErrorType}");
            lock (order) order.Add(name);
        }

        var prefetches = new[] { Track("prefetch-1", MediaSlotKind.PagePrefetch), Track("prefetch-2", MediaSlotKind.PagePrefetch) };
        // Both prefetches are queued before the current page asks.
        Assert.True(await WaitForAsync(() => pool.SlotWaiterCount == 2, TimeSpan.FromSeconds(10)), "The prefetches never queued");
        var current = Track("current", MediaSlotKind.Page);
        await Task.WhenAll(prefetches.Append(current).Append(holder));

        Assert.Equal("current", order[0]);
    }

    [Fact]
    public async Task Pause_HoldsBackgroundWorkWhileSomeoneReads_AndNormalReleasesItLive()
    {
        var pool = CreatePool(maxConcurrentJobs: 2);
        pool.BackgroundWhileReading = BackgroundWhileReading.Pause;
        await pool.StartAsync();
        var zip = _fixture.CreateValidImageZip("rf-pause.zip");

        // A reader page makes "someone is reading" true for the next 30 s.
        var read = await ExtractAsync(pool, zip, "page001.png", MediaSlotKind.Page);
        Assert.True(read.Success);

        // A free slot exists, but background work is paused while someone reads: the request queues instead of
        // taking it (a request that is granted a slot never becomes a waiter).
        var background = ExtractAsync(pool, zip, "page001.png", MediaSlotKind.Thumbnail);
        Assert.True(await WaitForAsync(() => pool.SlotWaiterCount == 1 || background.IsCompleted, TimeSpan.FromSeconds(10)),
            "The background request neither queued nor finished");
        Assert.False(background.IsCompleted, "Background work ran while paused");
        Assert.Equal(1, pool.SlotWaiterCount);

        pool.BackgroundWhileReading = BackgroundWhileReading.Normal;
        var done = await background.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(done.Success, $"{done.ErrorType} {done.ErrorMessage}");
    }

    /// <summary>
    /// Records the process id of every worker the pool starts, from the supervisor's "Worker process ready (pid N)"
    /// event, so a test can pause one worker without any hook in the pool.
    /// </summary>
    private sealed class WorkerPidLog : ILoggerProvider, ILogger
    {
        private readonly List<int> _pids = [];

        public IReadOnlyList<int> Pids
        {
            get { lock (_pids) return _pids.ToList(); }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != LogEvents.Worker.SupervisorHandshakeReady)
                return;
            var match = Regex.Match(formatter(state, exception), @"pid (\d+)");
            if (match.Success)
                lock (_pids) _pids.Add(int.Parse(match.Groups[1].Value));
        }

        public void Dispose()
        {
        }
    }

    private static class Signals
    {
        private const int SigCont = 18;
        private const int SigStop = 19;

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Kill(int pid, int signal);

        /// <summary>Freezes the process: a worker mid-extract stays busy, with its job unfinished, until <see cref="Resume"/>.</summary>
        public static void Pause(int pid) => Assert.Equal(0, Kill(pid, SigStop));

        public static void Resume(int pid) => Kill(pid, SigCont);
    }

    [Fact]
    public async Task WorkerCap_GrowsAndShrinks_WhileRunning()
    {
        // Process control by signal needs a Linux host, like the rest of this category's process management.
        Assert.True(OperatingSystem.IsLinux(), "Worker process tests run on Linux.");

        // 1.47.0 lane T: this test used to wait for BusyWorkerCount == 2 behind a fixed 200 ms delay. Whether the first
        // extract was still running when the second worker finished starting depended on machine load, so under load the
        // first one finished first and the passing state never held. Now the first extract is held by pausing its worker
        // process (SIGSTOP): it stays busy for exactly as long as the test needs, whatever the load, and every wait below is
        // on a state that persists until the test changes it.
        var pids = new WorkerPidLog();
        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(pids));
        var pool = CreatePool(maxConcurrentJobs: 1, loggerFactory, analysisTimeout: TimeSpan.FromMinutes(5));
        await pool.StartAsync();
        Assert.True(await WaitForAsync(() => pids.Pids.Count == 1, TimeSpan.FromSeconds(30)), "The first worker never reported ready");
        var firstWorker = pids.Pids[0];
        var slow = _fixture.CreateSlowImageZip("rf-resize.zip", size: 2400);

        Task<PageExtractionOutcome>? first = null;
        try
        {
            first = ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
            Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(30)), "The first extract never took the slot");
            Signals.Pause(firstWorker);

            // The second request queues behind the paused extract: a cap of 1 has no other slot to give it.
            var second = ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
            Assert.True(await WaitForAsync(() => pool.Metrics.Snapshot().Pool.WaitingSlotCallers == 1, TimeSpan.FromSeconds(30)),
                "The second request never queued for a slot");
            Assert.False(second.IsCompleted);
            Assert.Equal(1, pool.WorkerCount);

            // A higher cap starts a worker for the waiting request at once - it does not wait for the first extract,
            // which cannot finish while its worker is paused.
            pool.SetMaxConcurrentJobs(2);
            Assert.Equal(2, pool.MaxConcurrentJobs);
            var served = await second.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(served.Success, $"{served.ErrorType} {served.ErrorMessage}");
            Assert.False(first.IsCompleted, "The first extract finished while its worker was paused");
            Assert.Equal(2, pool.WorkerCount);
            Assert.Equal(1, pool.BusyWorkerCount);

            Signals.Resume(firstWorker);
            var finished = await first.WaitAsync(TimeSpan.FromSeconds(120));
            Assert.True(finished.Success, $"{finished.ErrorType} {finished.ErrorMessage}");
        }
        finally
        {
            // Never leave a paused worker behind if an assertion failed.
            Signals.Resume(firstWorker);
        }

        // A lower cap retires the idle worker above it.
        pool.SetMaxConcurrentJobs(1);
        Assert.True(await WaitForAsync(() => pool.WorkerCount == 1, TimeSpan.FromSeconds(30)), $"Pool still has {pool.WorkerCount} workers");
    }
}
