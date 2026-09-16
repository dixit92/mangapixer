namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Worker-throughput tuning tests (1.5.0): event-driven dispatch replacing
/// the fixed 2s tick, concurrent dispatch up to MaxConcurrentJobs, and the
/// reserved-slot rule protecting reader demand (<see cref="MediaWorkerPool.ExtractPageAsync"/>)
/// from a background analysis burst. These spawn real worker processes —
/// same fixture as <see cref="WorkerProcessTests"/>.
/// Category: Process — must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class WorkerThroughputTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];

    public WorkerThroughputTests(WorkerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    private (MediaWorkerPool Pool, JobScheduler Scheduler) CreatePool(int maxConcurrentJobs)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        var scratchManager = new ScratchWorkspaceManager(_fixture.ScratchRoot);
        var scheduler = new JobScheduler(options);
        var pool = new MediaWorkerPool(
            options, scheduler, scratchManager,
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        _pools.Add(pool);
        return (pool, scheduler);
    }

    private static (long Ticks, long Length) Stamp(string path)
    {
        var fi = new FileInfo(path);
        return (fi.LastWriteTimeUtc.Ticks, fi.Length);
    }

    // Knob 2: a single DispatchAsync pump call must grow the pool to
    // MaxConcurrentJobs when enough work is queued. The old fixed-tick loop
    // dispatched one job per 2s tick and awaited it to completion, so with
    // sub-second jobs the pool could never reach more than one worker no
    // matter how deep the backlog was.
    [Fact]
    public async Task DispatchAsync_ScalesToMaxConcurrentJobs_WhenQueueHasEnoughWork()
    {
        var (pool, scheduler) = CreatePool(2);
        await pool.StartAsync();
        Assert.Equal(1, pool.WorkerCount); // pre-started worker only

        var zip = _fixture.CreateSimpleZip("throughput-scale.zip");
        var (ticks, length) = Stamp(zip);
        for (int i = 0; i < 4; i++)
        {
            _ = scheduler.EnqueueAsync(
                itemId: i, contentVersion: 1, operation: JobOperation.Analyze,
                priority: JobPriority.Background, archivePath: zip,
                expectedLastWriteTicks: ticks, expectedByteLength: length);
        }

        await pool.DispatchAsync();

        Assert.Equal(2, pool.WorkerCount);
        Assert.True(pool.WorkerCount <= 2, "Pool must never exceed MaxConcurrentJobs workers");

        await pool.StopAsync();
    }

    // Knob 2 (reserved slot): a deep background burst must not be able to
    // starve reader demand out of every slot. Once ExtractPageAsync starts
    // waiting for a slot, background dispatch must back off to
    // MaxConcurrentJobs - 1, so the reader gets the reserved slot promptly
    // instead of waiting for the (much slower) full background drain.
    [Fact]
    public async Task DispatchAsync_ReservedSlot_ReaderDemandNotStarvedByBackgroundBurst()
    {
        var (pool, scheduler) = CreatePool(2);
        await pool.StartAsync();

        var bgZip = _fixture.CreateSimpleZip("throughput-reserve-bg.zip");
        var (bgTicks, bgLength) = Stamp(bgZip);
        for (int i = 0; i < 50; i++)
        {
            _ = scheduler.EnqueueAsync(
                itemId: 1000 + i, contentVersion: 1, operation: JobOperation.Analyze,
                priority: JobPriority.Background, archivePath: bgZip,
                expectedLastWriteTicks: bgTicks, expectedByteLength: bgLength);
        }

        using var pumpCts = new CancellationTokenSource();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                while (!pumpCts.IsCancellationRequested)
                {
                    await pool.DispatchAsync(pumpCts.Token);
                    await Task.Delay(20, pumpCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            // Let the burst actually saturate the pool to MaxConcurrentJobs
            // workers before the reader shows up.
            var saturateDeadline = DateTime.UtcNow.AddSeconds(10);
            while (pool.WorkerCount < 2 && DateTime.UtcNow < saturateDeadline)
                await Task.Delay(20);
            Assert.Equal(2, pool.WorkerCount);

            var readerZip = _fixture.CreateValidImageZip("throughput-reserve-reader.zip");
            var (readerTicks, readerLength) = Stamp(readerZip);
            var outputPath = Path.Combine(_fixture.ScratchRoot, "reserve-reader-out.webp");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var readerTask = pool.ExtractPageAsync(
                readerZip, "page001.png", "webp", readerTicks, readerLength, outputPath, 200, 80);
            var completed = await Task.WhenAny(readerTask, Task.Delay(TimeSpan.FromSeconds(8)));
            sw.Stop();

            Assert.Same(readerTask, completed);
            var outcome = await readerTask;
            Assert.True(outcome.Success, $"Reader demand failed: {outcome.ErrorType} {outcome.ErrorMessage}");
            // A deep 50-job background queue would take far longer than 8s to
            // drain at ~0.4-1s/job; the reader completing well inside that
            // window shows the reserved slot — not queue drain — served it.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
                $"Reader demand took {sw.Elapsed} — the reserved slot was likely consumed by the background burst");
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask; } catch (OperationCanceledException) { }
            await pool.StopAsync();
        }
    }

    // Knob 3 companion: with MaxConcurrentJobs left at its 1-worker floor,
    // there is nothing to reserve — background dispatch must still be able to
    // use the single worker (no deadlock from an unreachable reservation).
    [Fact]
    public async Task DispatchAsync_SingleWorkerMode_NoReservationDeadlock()
    {
        var (pool, scheduler) = CreatePool(1);
        await pool.StartAsync();

        var zip = _fixture.CreateSimpleZip("throughput-single.zip");
        var (ticks, length) = Stamp(zip);
        var job = scheduler.EnqueueAsync(
            itemId: 1, contentVersion: 1, operation: JobOperation.Analyze,
            priority: JobPriority.Background, archivePath: zip,
            expectedLastWriteTicks: ticks, expectedByteLength: length);

        await pool.DispatchAsync();

        var completed = await Task.WhenAny(job, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(job, completed);
        var result = await job;
        Assert.True(result.Success, $"Single-worker dispatch failed: {result.ErrorType} {result.ErrorMessage}");
        Assert.Equal(1, pool.WorkerCount);

        await pool.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools)
        {
            try { await pool.DisposeAsync(); } catch { }
        }
        _pools.Clear();
    }
}
