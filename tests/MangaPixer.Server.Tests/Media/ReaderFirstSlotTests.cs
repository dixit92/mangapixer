namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
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

    private MediaWorkerPool CreatePool(int maxConcurrentJobs)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        options.WorkerIdleTimeout = TimeSpan.Zero;
        var pool = new MediaWorkerPool(
            options, new JobScheduler(options), new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        _pools.Add(pool);
        return pool;
    }

    private Task<PageExtractionOutcome> ExtractAsync(
        MediaWorkerPool pool, string zip, string entry, MediaSlotKind kind, CancellationToken ct = default)
    {
        var fi = new FileInfo(zip);
        return pool.ExtractPageAsync(zip, entry, "webp", fi.LastWriteTimeUtc.Ticks, fi.Length,
            Path.Combine(_fixture.ScratchRoot, "rf-" + Guid.NewGuid().ToString("N")[..8] + ".webp"), 200, 80, ct, slotKind: kind);
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
        var slow = _fixture.CreateSlowImageZip("rf-starve.zip", size: 3000);

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
        await Task.Delay(100); // both prefetches are queued before the current page asks
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

        // A free slot exists, but background work is paused while someone reads.
        var background = ExtractAsync(pool, zip, "page001.png", MediaSlotKind.Thumbnail);
        await Task.Delay(1500);
        Assert.False(background.IsCompleted, "Background work ran while paused");

        pool.BackgroundWhileReading = BackgroundWhileReading.Normal;
        var done = await background.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(done.Success, $"{done.ErrorType} {done.ErrorMessage}");
    }

    [Fact]
    public async Task WorkerCap_GrowsAndShrinks_WhileRunning()
    {
        var pool = CreatePool(maxConcurrentJobs: 1);
        await pool.StartAsync();
        var slow = _fixture.CreateSlowImageZip("rf-resize.zip", size: 4000);

        var first = ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The first extract never took the slot");
        var second = ExtractAsync(pool, slow, "page001.jpg", MediaSlotKind.Page);
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        // A higher cap starts a worker for the waiting request at once - it does not wait for the first extract.
        pool.SetMaxConcurrentJobs(2);
        Assert.Equal(2, pool.MaxConcurrentJobs);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 2, TimeSpan.FromSeconds(15)), "No second worker started for the waiting request");
        Assert.All(await Task.WhenAll(first, second), o => Assert.True(o.Success, o.ErrorType));

        // A lower cap retires the idle worker above it.
        pool.SetMaxConcurrentJobs(1);
        Assert.True(await WaitForAsync(() => pool.WorkerCount == 1, TimeSpan.FromSeconds(10)), $"Pool still has {pool.WorkerCount} workers");
    }
}
