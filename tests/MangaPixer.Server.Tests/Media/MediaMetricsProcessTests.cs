namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Media metrics (1.42.0 profiling) against real spawned workers: a page extract records its slot wait, the worker's
/// answer and the worker's open / read / encode split under its caller kind; a request that waits behind a busy slot
/// records that wait; a reader that leaves while the worker still extracts is counted as a slot freed while the worker
/// is busy, and the next request on that worker is timed as queued behind the abandoned one.
/// Category: Process - must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class MediaMetricsProcessTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];
    private readonly List<MediaMetrics> _metrics = [];

    public MediaMetricsProcessTests(WorkerProcessFixture fixture) => _fixture = fixture;

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools)
        {
            try { await pool.StopAsync(); } catch { /* best effort */ }
            await pool.DisposeAsync();
        }
        foreach (var metrics in _metrics)
            metrics.Dispose();
    }

    private (MediaWorkerPool Pool, MediaMetrics Metrics) CreatePool(int maxConcurrentJobs)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        options.WorkerIdleTimeout = TimeSpan.Zero;
        var metrics = new MediaMetrics();
        _metrics.Add(metrics);
        var pool = new MediaWorkerPool(
            options, new JobScheduler(options), new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance, metrics: metrics);
        _pools.Add(pool);
        return (pool, metrics);
    }

    private Task<PageExtractionOutcome> ExtractAsync(
        MediaWorkerPool pool, string zip, string entry, string output, CancellationToken ct, MediaSlotKind kind = MediaSlotKind.Page)
    {
        var fi = new FileInfo(zip);
        return pool.ExtractPageAsync(zip, entry, "webp", fi.LastWriteTimeUtc.Ticks, fi.Length,
            Path.Combine(_fixture.ScratchRoot, output), 200, 80, ct, slotKind: kind);
    }

    private static MediaTimingDiagnostics? Timing(MediaDiagnostics s, string name, string kind) =>
        s.Timings.FirstOrDefault(t => t.Name == name && t.Kind == kind);

    private static long CountOf(MediaDiagnostics s, string name, string kind) =>
        s.Counts.FirstOrDefault(c => c.Name == name && c.Kind == kind)?.Value ?? 0;

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
    public async Task PageExtract_RecordsSlotWait_WorkerCall_AndTheWorkersPhaseSplit()
    {
        var (pool, metrics) = CreatePool(maxConcurrentJobs: 1);
        await pool.StartAsync();
        var zip = _fixture.CreateValidImageZip("metrics-page.zip");

        var outcome = await ExtractAsync(pool, zip, "page001.png", "metrics-page.webp", CancellationToken.None);

        Assert.True(outcome.Success, $"{outcome.ErrorType} {outcome.ErrorMessage}");
        // The worker reports where the time went, and the pool hands the slot wait back to the caller.
        Assert.NotNull(outcome.OpenMs);
        Assert.NotNull(outcome.ReadMs);
        Assert.NotNull(outcome.EncodeMs);
        Assert.NotNull(outcome.SlotWaitMs);

        var s = metrics.Snapshot();
        Assert.Equal(1, Timing(s, MediaMetrics.SlotWait, "page")?.Count);
        Assert.Equal(1, Timing(s, MediaMetrics.WorkerCall, "page")?.Count);
        Assert.Equal(1, Timing(s, MediaMetrics.ExtractOpen, "page")?.Count);
        Assert.Equal(1, Timing(s, MediaMetrics.ExtractRead, "page")?.Count);
        Assert.Equal(1, Timing(s, MediaMetrics.ExtractEncode, "page")?.Count);
        Assert.Equal(1, CountOf(s, MediaMetrics.SlotOutcome, "page:acquired"));
        // Nothing holds a slot once the request is answered; the pool gauges are live.
        Assert.Empty(s.Pool.SlotsHeld);
        Assert.Equal(1, s.Pool.MaxConcurrentJobs);
        Assert.Equal(1, s.Pool.Workers);
    }

    [Fact]
    public async Task RequestBehindABusySlot_RecordsItsWait_UnderItsOwnKind()
    {
        var (pool, metrics) = CreatePool(maxConcurrentJobs: 1);
        await pool.StartAsync();
        var slow = _fixture.CreateSlowImageZip("metrics-busy.zip", size: 4000);

        var holder = ExtractAsync(pool, slow, "page001.jpg", "metrics-busy-holder.webp", CancellationToken.None, MediaSlotKind.Thumbnail);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The slow extract never took the slot");
        // While the thumbnail holds the only slot, the snapshot shows who holds it.
        Assert.Contains(metrics.Snapshot().Pool.SlotsHeld, c => c.Kind == "thumbnail" && c.Value == 1);

        var waiter = await ExtractAsync(pool, slow, "page001.jpg", "metrics-busy-waiter.webp", CancellationToken.None);
        var held = await holder;

        Assert.True(held.Success && waiter.Success, $"{held.ErrorType} / {waiter.ErrorType}");
        var s = metrics.Snapshot();
        var pageWait = Timing(s, MediaMetrics.SlotWait, "page");
        Assert.NotNull(pageWait);
        // It polled at least once (100 ms) before the holder let go.
        Assert.True(pageWait!.MaxMs >= 100, $"page slot wait {pageWait.MaxMs} ms");
        Assert.True(waiter.SlotWaitMs >= 100, $"returned slot wait {waiter.SlotWaitMs} ms");
        Assert.Equal(1, Timing(s, MediaMetrics.SlotWait, "thumbnail")?.Count);
    }

    [Fact]
    public async Task ReaderLeavesWhileTheWorkerExtracts_IsCountedAsASlotFreedWhileTheWorkerIsBusy()
    {
        var (pool, metrics) = CreatePool(maxConcurrentJobs: 1);
        await pool.StartAsync();
        var slow = _fixture.CreateSlowImageZip("metrics-abandon.zip", size: 4000);

        using var cts = new CancellationTokenSource();
        var running = ExtractAsync(pool, slow, "page001.jpg", "metrics-abandon.webp", cts.Token);
        Assert.True(await WaitForAsync(() => pool.BusyWorkerCount == 1, TimeSpan.FromSeconds(10)), "The slow extract never took the slot");
        cts.Cancel();
        var outcome = await running;
        Assert.False(outcome.Success, "The fixture finished before the reader left; it proves nothing");
        Assert.Equal("cancelled", outcome.ErrorType);

        Assert.Equal(1, CountOf(metrics.Snapshot(), MediaMetrics.SlotReleasedWhileWorkerBusy, "page:cancelled"));

        // The slot is free at once, but the worker still runs the abandoned extract: the next request queues behind it.
        var next = await ExtractAsync(pool, slow, "page001.jpg", "metrics-abandon-next.webp", CancellationToken.None);
        Assert.True(next.Success, $"{next.ErrorType} {next.ErrorMessage}");
        Assert.Equal(1, Timing(metrics.Snapshot(), MediaMetrics.WorkerCall, "page_after_abandon")?.Count);
    }
}
