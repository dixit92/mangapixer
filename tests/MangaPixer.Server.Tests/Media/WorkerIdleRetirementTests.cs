namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Diagnostics;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Idle worker retirement (1.22.0) against real spawned worker processes:
/// an idle worker is shut down after <see cref="WorkerPoolOptions.WorkerIdleTimeout"/>,
/// the pool respawns on demand, a busy worker is never retired, the warm
/// floor holds, and an empty-queue dispatch never spawns a worker.
/// Category: Process — must run on Linux for reliable process management.
/// </summary>
[Trait("Category", "Process")]
public sealed class WorkerIdleRetirementTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];

    public WorkerIdleRetirementTests(WorkerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    private (MediaWorkerPool Pool, JobScheduler Scheduler) CreatePool(
        TimeSpan idleTimeout, int minWarmWorkers = 0, int maxConcurrentJobs = 2)
    {
        var options = _fixture.CreatePoolOptions();
        options.MaxConcurrentJobs = maxConcurrentJobs;
        options.WorkerIdleTimeout = idleTimeout;
        options.MinWarmWorkers = minWarmWorkers;
        var scheduler = new JobScheduler(options);
        var pool = new MediaWorkerPool(
            options, scheduler, new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance);
        _pools.Add(pool);
        return (pool, scheduler);
    }

    private static async Task<bool> WaitForWorkerCountAsync(MediaWorkerPool pool, int expected, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (pool.WorkerCount == expected)
                return true;
            await Task.Delay(25);
        }
        return pool.WorkerCount == expected;
    }

    private Task<PageExtractionOutcome> ExtractAsync(MediaWorkerPool pool, string zip, string entry, string output)
    {
        var fi = new FileInfo(zip);
        return pool.ExtractPageAsync(zip, entry, "webp", fi.LastWriteTimeUtc.Ticks, fi.Length,
            Path.Combine(_fixture.ScratchRoot, output), 200, 80);
    }

    [Fact]
    public async Task IdleWorker_IsRetiredAfterTimeout()
    {
        var (pool, _) = CreatePool(TimeSpan.FromMilliseconds(300));
        await pool.StartAsync();
        Assert.Equal(1, pool.WorkerCount); // pre-started worker

        Assert.True(await WaitForWorkerCountAsync(pool, 0, TimeSpan.FromSeconds(10)),
            $"Idle worker was not retired (pool still has {pool.WorkerCount})");

        await pool.StopAsync();
    }

    [Fact]
    public async Task RetiredPool_SpawnsAWorkerOnDemand_AndRetiresItAgain()
    {
        var (pool, _) = CreatePool(TimeSpan.FromMilliseconds(500));
        await pool.StartAsync();
        Assert.True(await WaitForWorkerCountAsync(pool, 0, TimeSpan.FromSeconds(10)));

        // Reader demand against an empty pool: a cold worker must be spawned.
        var zip = _fixture.CreateValidImageZip("idle-respawn.zip");
        var outcome = await ExtractAsync(pool, zip, "page001.png", "idle-respawn.webp");
        Assert.True(outcome.Success, $"On-demand extract after retirement failed: {outcome.ErrorType} {outcome.ErrorMessage}");

        Assert.True(await WaitForWorkerCountAsync(pool, 0, TimeSpan.FromSeconds(10)),
            "Respawned worker was not retired after going idle again");
        await pool.StopAsync();
    }

    [Fact]
    public async Task BusyWorker_IsNeverRetired_EvenWithAHairTriggerTimeout()
    {
        // 1 ms timeout and a sweep hammered every few ms: any worker that is
        // idle for an instant goes. The in-flight extract must still finish,
        // which it cannot if its worker is shut down mid-job.
        var (pool, _) = CreatePool(TimeSpan.FromMilliseconds(1), maxConcurrentJobs: 1);
        await pool.StartAsync();
        Assert.True(await WaitForWorkerCountAsync(pool, 0, TimeSpan.FromSeconds(10)));

        var zip = _fixture.CreateSlowImageZip("idle-busy.zip");
        var sw = Stopwatch.StartNew();
        var extract = ExtractAsync(pool, zip, "page001.jpg", "idle-busy.webp");

        var sweeps = 0;
        while (!extract.IsCompleted)
        {
            await pool.RetireIdleWorkersAsync();
            sweeps++;
            await Task.Delay(2);
        }
        sw.Stop();

        var outcome = await extract;
        Assert.True(outcome.Success, $"Extract failed while retirement was sweeping: {outcome.ErrorType} {outcome.ErrorMessage}");
        // Guard against a vacuous pass: the job must have overlapped many sweeps.
        Assert.True(sweeps >= 10, $"Only {sweeps} sweeps overlapped the extract ({sw.ElapsedMilliseconds} ms); fixture too fast to prove anything");
        await pool.StopAsync();
    }

    [Fact]
    public async Task MinWarmWorkers_KeepsTheFloorAlive()
    {
        var (pool, _) = CreatePool(TimeSpan.FromMilliseconds(200), minWarmWorkers: 1);
        await pool.StartAsync();
        Assert.Equal(1, pool.WorkerCount);

        await Task.Delay(TimeSpan.FromSeconds(1)); // five timeouts, ten sweeps
        Assert.Equal(0, await pool.RetireIdleWorkersAsync());
        Assert.Equal(1, pool.WorkerCount);
        await pool.StopAsync();
    }

    [Fact]
    public async Task EmptyQueueDispatch_DoesNotSpawnAWorker_WhileTheOnlyWorkerIsBusy()
    {
        // Regression: before 1.22.0 the dispatch loop's empty-queue poll, with
        // every existing worker busy on a reader extract, started one more
        // worker for nothing (the second idle worker seen in production).
        var (pool, scheduler) = CreatePool(TimeSpan.Zero, maxConcurrentJobs: 2);
        await pool.StartAsync();
        Assert.Equal(1, pool.WorkerCount);

        var zip = _fixture.CreateSlowImageZip("idle-empty-dispatch.zip");
        var extract = ExtractAsync(pool, zip, "page001.jpg", "idle-empty-dispatch.webp");
        var busyDeadline = DateTime.UtcNow.AddSeconds(5);
        while (pool.BusyWorkerCount == 0 && DateTime.UtcNow < busyDeadline)
            await Task.Delay(5);
        Assert.Equal(1, pool.BusyWorkerCount);

        Assert.Equal(0, scheduler.PendingCount);
        await pool.DispatchAsync();
        await pool.DispatchAsync();
        Assert.Equal(1, pool.WorkerCount);

        Assert.True((await extract).Success);
        await pool.StopAsync();
    }

    [Fact]
    public async Task ZeroTimeout_DisablesRetirement()
    {
        var (pool, _) = CreatePool(TimeSpan.Zero);
        await pool.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, await pool.RetireIdleWorkersAsync());
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
