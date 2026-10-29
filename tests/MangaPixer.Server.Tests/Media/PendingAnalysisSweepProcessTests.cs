namespace com.lifepixer.mangapixer.Tests.Server.Media;

using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// 1.50.0 lane X, the stranding that the pending-archive sweep closes, with a real spawned worker: a job runs and succeeds but the
/// save of its result fails (event 4027, swallowed by the pool), so the archive stays at "pending" with no job. Before the sweep it
/// stayed there until the next restart; now the pool asks the sweep, which re-queues it once the pool is idle, and the second try
/// saves.
/// </summary>
[Trait("Category", "Process")]
public sealed class PendingAnalysisSweepProcessTests : IClassFixture<WorkerProcessFixture>
{
    private readonly WorkerProcessFixture _fixture;

    public PendingAnalysisSweepProcessTests(WorkerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FailedSave_LeavesNothingPending_TheSweepRequeuesAndTheSecondTrySaves()
    {
        var dbPath = Path.Combine(_fixture.TempRoot, "sweep-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        var failOnce = new FailFirstSave();
        var services = new ServiceCollection();
        services.AddDbContext<MangaPixerDbContext>(o => o.UseSqlite(DatabaseInitialization.BuildConnectionString(dbPath)).AddInterceptors(failOnce));
        await using var provider = services.BuildServiceProvider();

        var zipPath = _fixture.CreateSimpleZip("sweep.zip");
        long nodeId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await db.Database.MigrateAsync();
            var library = new LibraryEntity { PublicId = "sweepl", DisplayName = "Sweep", RootPath = _fixture.FixtureDir, CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var node = new CatalogNodeEntity
            {
                PublicId = "sweepn",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "sweep.zip",
                RelativePath = "sweep.zip",
                PathKey = "sweep.zip",
                SortKey = "1sweep.zip",
                LastSeenScanRevision = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ArchiveItem = new ArchiveItemEntity { ContentVersion = 1, AnalysisState = 1 },
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            nodeId = node.Id;
        }
        failOnce.Armed = true; // from here the first save of an analysis result throws

        var options = _fixture.CreatePoolOptions();
        var scheduler = new JobScheduler(options);
        var sweep = new PendingAnalysisSweepService(
            provider.GetRequiredService<IServiceScopeFactory>(), scheduler,
            new PendingAnalysisSweepOptions { QuietPeriod = TimeSpan.Zero, MinInterval = TimeSpan.Zero, IdlePoll = TimeSpan.FromMilliseconds(20) },
            TimeProvider.System, NullLogger<PendingAnalysisSweepService>.Instance);
        var pool = new MediaWorkerPool(
            options, scheduler, new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(), new AnalysisResultPersister(), metrics: null, sweepTrigger: sweep);
        await pool.StartAsync();
        await sweep.StartAsync(CancellationToken.None);
        try
        {
            var fileInfo = new FileInfo(zipPath);
            var job = scheduler.EnqueueAsync(
                itemId: nodeId, contentVersion: 1, operation: JobOperation.Analyze, priority: JobPriority.Background,
                archivePath: zipPath, expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks, expectedByteLength: fileInfo.Length);
            await pool.DispatchAsync();
            Assert.True((await job.WaitAsync(TimeSpan.FromSeconds(40))).Success);

            // The first save failed: the archive is pending and nothing is queued for it - until the sweep re-queues it.
            ArchiveItemEntity? item = null;
            for (var i = 0; i < 400 && item?.AnalysisState != 0; i++)
            {
                await Task.Delay(100);
                await pool.DispatchAsync(); // the production dispatch loop does this on its own signal
                using var scope = provider.CreateScope();
                item = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>()
                    .ArchiveItems.AsNoTracking().SingleAsync(a => a.NodeId == nodeId);
            }

            Assert.Equal(1, failOnce.Failures);
            Assert.True(sweep.SweepsRun >= 1);
            Assert.NotNull(item);
            Assert.Equal(0, item!.AnalysisState);
            Assert.Equal(3, item.PageCount);
        }
        finally
        {
            await sweep.StopAsync(CancellationToken.None);
            await pool.StopAsync();
            await pool.DisposeAsync();
        }
    }

    /// <summary>Throws on the first save while armed (the analysis result's), passes every later one.</summary>
    private sealed class FailFirstSave : SaveChangesInterceptor
    {
        private int _failures;

        public bool Armed { get; set; }

        public int Failures => Volatile.Read(ref _failures);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && Interlocked.CompareExchange(ref _failures, 1, 0) == 0)
                throw new InvalidOperationException("simulated save failure");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
