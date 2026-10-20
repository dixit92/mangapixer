namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests for the start-up warm-up's worker step (1.46.0 lane D): a real media worker encodes the generated page as the reader's
/// sized WebP (decode, resize, encode all run once), the scratch workspace is gone afterwards, and no worker is ever started for it.
/// </summary>
[Trait("Category", "Process")]
public sealed class StartupWarmupWorkerProcessTests : IClassFixture<WorkerProcessFixture>, IAsyncDisposable
{
    private readonly WorkerProcessFixture _fixture;
    private readonly List<MediaWorkerPool> _pools = [];

    public StartupWarmupWorkerProcessTests(WorkerProcessFixture fixture) => _fixture = fixture;

    public async ValueTask DisposeAsync()
    {
        foreach (var pool in _pools)
        {
            try { await pool.StopAsync(); } catch { /* best effort */ }
            await pool.DisposeAsync();
        }
    }

    private (StartupWarmup Warmup, MediaWorkerPool Pool, string Scratch) Create()
    {
        var options = _fixture.CreatePoolOptions();
        options.WorkerIdleTimeout = TimeSpan.Zero;
        var scratchRoot = Path.Combine(_fixture.TempRoot, "warm-" + Guid.NewGuid().ToString("N")[..8]);
        var scratch = new ScratchWorkspaceManager(scratchRoot);
        scratch.Initialize();
        var pool = new MediaWorkerPool(options, new JobScheduler(options), scratch, NullLogger<MediaWorkerPool>.Instance,
            NullLoggerFactory.Instance);
        _pools.Add(pool);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var warmup = new StartupWarmup(scopes, new RequestActivity(), new StartupWarmupOptions(), pool, scratch,
            NullLogger<StartupWarmup>.Instance);
        return (warmup, pool, scratchRoot);
    }

    [Fact]
    public async Task WithTheBootWorker_ThePageIsEncoded_AndTheScratchWorkspaceIsGone()
    {
        var (warmup, pool, scratchRoot) = Create();
        await pool.StartAsync();
        Assert.Equal(1, pool.WorkerCount);

        Assert.Equal("warm", await warmup.WarmWorkerAsync(CancellationToken.None));

        Assert.Equal(1, pool.WorkerCount); // the boot worker did it; none was started
        Assert.Empty(Directory.GetDirectories(scratchRoot));
        var calls = pool.Metrics.Snapshot().Timings
            .Where(t => t.Name == MediaMetrics.WorkerCall && t.Kind == MediaMetrics.KindTag(MediaSlotKind.Thumbnail)).Sum(t => t.Count);
        Assert.Equal(1L, calls); // one worker call, through a background (thumbnail-kind) slot
    }

    [Fact]
    public async Task WithoutARunningWorker_ItIsSkipped_AndStartsNone()
    {
        var (warmup, pool, scratchRoot) = Create();

        Assert.Equal("skipped", await warmup.WarmWorkerAsync(CancellationToken.None));
        Assert.Equal(0, pool.WorkerCount);
        Assert.Empty(Directory.GetDirectories(scratchRoot));
    }
}
