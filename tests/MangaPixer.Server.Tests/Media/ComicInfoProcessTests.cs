namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.MediaWorker.Protocol;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Process tests for the protocol v3 ComicInfo read (1.24.0): real spawned worker
/// processes behind the server's <see cref="MediaWorkerPool"/>. Covers the
/// <c>comicinfo</c> message outcomes, ComicInfo persisted from an <c>analyze</c>
/// result, the backfill pass end to end, and the loud v2/v3 mismatch.
/// </summary>
[Trait("Category", "Process")]
public sealed class ComicInfoProcessTests : IClassFixture<WorkerProcessFixture>
{
    private readonly WorkerProcessFixture _fixture;

    public ComicInfoProcessTests(WorkerProcessFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(MediaWorkerPool Pool, JobScheduler Scheduler)> StartPoolAsync(IServiceScopeFactory? scopeFactory = null)
    {
        var options = _fixture.CreatePoolOptions();
        var scheduler = new JobScheduler(options);
        var pool = new MediaWorkerPool(
            options, scheduler, new ScratchWorkspaceManager(_fixture.ScratchRoot),
            NullLogger<MediaWorkerPool>.Instance, NullLoggerFactory.Instance,
            scopeFactory, scopeFactory is null ? null : new AnalysisResultPersister());
        await pool.StartAsync();
        return (pool, scheduler);
    }

    private static async Task StopAsync(MediaWorkerPool pool)
    {
        await pool.StopAsync();
        await pool.DisposeAsync();
    }

    private async Task<ComicInfoReadOutcome> ReadAsync(string path)
    {
        var info = new FileInfo(path);
        var (pool, _) = await StartPoolAsync();
        try
        {
            return await pool.ReadComicInfoAsync(path, info.LastWriteTimeUtc.Ticks, info.Length);
        }
        finally
        {
            await StopAsync(pool);
        }
    }

    [Fact]
    public async Task ComicInfo_ZipWithComicInfo_IsParsed()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-parsed.cbz", ComicInfoFixtures.SampleXml());

        var outcome = await ReadAsync(path);

        Assert.True(outcome.Success, outcome.ErrorType);
        Assert.Equal(ComicInfoStatus.Parsed, outcome.Outcome!.Status);
        Assert.Equal("Synthetic Saga", outcome.Outcome.Payload!.Series);
    }

    [Fact]
    public async Task ComicInfo_ZipWithout_IsAbsent()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-absent.cbz", xml: null);

        var outcome = await ReadAsync(path);

        Assert.True(outcome.Success, outcome.ErrorType);
        Assert.Equal(ComicInfoStatus.Absent, outcome.Outcome!.Status);
    }

    [Fact]
    public async Task ComicInfo_Malformed_And_Xxe_AreMalformed()
    {
        var garbage = ComicInfoFixtures.CreateZipWithComicInfoBytes(_fixture.FixtureDir, "ci-garbage.cbz", Encoding.UTF8.GetBytes("<ComicInfo><Series>"));
        var xxe = ComicInfoFixtures.CreateZipWithComicInfoBytes(_fixture.FixtureDir, "ci-xxe.cbz", Encoding.UTF8.GetBytes(
            "<!DOCTYPE ComicInfo [<!ENTITY x SYSTEM \"file:///etc/hostname\">]><ComicInfo><Series>&x;</Series></ComicInfo>"));

        Assert.Equal(ComicInfoStatus.Malformed, (await ReadAsync(garbage)).Outcome!.Status);
        Assert.Equal(ComicInfoStatus.Malformed, (await ReadAsync(xxe)).Outcome!.Status);
    }

    [Fact]
    public async Task ComicInfo_OverOneMiB_IsTooLarge()
    {
        var big = new byte[ComicInfoLimits.MaxXmlBytes + 10];
        Array.Fill(big, (byte)' ');
        var path = ComicInfoFixtures.CreateZipWithComicInfoBytes(_fixture.FixtureDir, "ci-big.cbz", big);

        var outcome = await ReadAsync(path);

        Assert.Equal(ComicInfoStatus.TooLarge, outcome.Outcome!.Status);
    }

    [Fact]
    public async Task ComicInfo_SolidSevenZip_IsSkippedSolid()
    {
        var path = SevenZipFixtureGenerator.CreateSevenZip(_fixture.FixtureDir, "ci-solid.cb7", solid: true, "page001.png", "ComicInfo.xml");
        if (new FileInfo(path).Length <= 8)
            return; // 7z CLI unavailable - same skip as the other 7z tests

        var outcome = await ReadAsync(path);

        Assert.True(outcome.Success, outcome.ErrorType);
        Assert.Equal(ComicInfoStatus.SkippedSolid, outcome.Outcome!.Status);
    }

    [Fact]
    public async Task ComicInfo_SourceStampMismatch_IsSourceChanged_AndNothingIsRead()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-stamp.cbz", ComicInfoFixtures.SampleXml());
        var info = new FileInfo(path);
        var (pool, _) = await StartPoolAsync();
        try
        {
            var outcome = await pool.ReadComicInfoAsync(path, info.LastWriteTimeUtc.Ticks + 1, info.Length);

            Assert.False(outcome.Success);
            Assert.Equal("source_changed", outcome.ErrorType);
        }
        finally
        {
            await StopAsync(pool);
        }
    }

    [Fact]
    public async Task Analyze_PersistsComicInfo_ForTheCurrentContentVersion()
    {
        var path = ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-analyze.cbz", ComicInfoFixtures.SampleXml(series: "Analyzed Series"));
        var info = new FileInfo(path);
        await using var provider = BuildDbProvider(out var dbPath);
        var nodeId = await SeedArchiveAsync(provider, "ci-analyze.cbz", analysisState: 1, contentVersion: 4);

        var (pool, scheduler) = await StartPoolAsync(provider.GetRequiredService<IServiceScopeFactory>());
        try
        {
            var job = scheduler.EnqueueAsync(
                itemId: nodeId, contentVersion: 4, operation: JobOperation.Analyze, priority: JobPriority.CurrentPage,
                archivePath: path, expectedLastWriteTicks: info.LastWriteTimeUtc.Ticks, expectedByteLength: info.Length);
            await pool.DispatchAsync();
            var result = await job.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(result.Success, result.ErrorType);

            EmbeddedMetadataEntity? row = null;
            for (var i = 0; i < 100 && row is null; i++)
            {
                await Task.Delay(100);
                using var scope = provider.CreateScope();
                row = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>()
                    .EmbeddedMetadata.AsNoTracking().SingleOrDefaultAsync(e => e.NodeId == nodeId);
            }

            Assert.NotNull(row);
            Assert.Equal(1, row!.State);
            Assert.Equal(4, row.ContentVersion);
            Assert.Equal("Analyzed Series", row.Series);
        }
        finally
        {
            await StopAsync(pool);
        }
    }

    [Fact]
    public async Task Backfill_ReadsEveryReadyArchiveOnce_StoringAbsentToo()
    {
        ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-bf-with.cbz", ComicInfoFixtures.SampleXml(series: "Backfilled"));
        ComicInfoFixtures.CreateZipWithComicInfo(_fixture.FixtureDir, "ci-bf-without.cbz", xml: null);
        await using var provider = BuildDbProvider(out _);
        var withId = await SeedArchiveAsync(provider, "ci-bf-with.cbz", analysisState: 0, contentVersion: 1);
        var withoutId = await SeedArchiveAsync(provider, "ci-bf-without.cbz", analysisState: 0, contentVersion: 1);

        var (pool, _) = await StartPoolAsync(provider.GetRequiredService<IServiceScopeFactory>());
        try
        {
            var backfill = new ComicInfoBackfillService(
                provider.GetRequiredService<IServiceScopeFactory>(), pool, NullLogger<ComicInfoBackfillService>.Instance);

            var first = await backfill.RunPassAsync(CancellationToken.None);
            var second = await backfill.RunPassAsync(CancellationToken.None);

            Assert.Equal(2, first.Attempted);
            Assert.Equal(2, first.Stored);
            Assert.Equal(1, first.Found);
            Assert.Equal(0, second.Attempted); // read once per content version

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            Assert.Equal(1, (await db.EmbeddedMetadata.SingleAsync(e => e.NodeId == withId)).State);
            Assert.Equal("Backfilled", (await db.EmbeddedMetadata.SingleAsync(e => e.NodeId == withId)).Series);
            Assert.Equal(0, (await db.EmbeddedMetadata.SingleAsync(e => e.NodeId == withoutId)).State);
        }
        finally
        {
            await StopAsync(pool);
        }
    }

    [Fact]
    public async Task Worker_RejectsAVersion2Envelope_Loudly()
    {
        var supervisor = new WorkerSupervisor(
            _fixture.WorkerExePath, _fixture.WorkerArguments, _fixture.CreatePoolOptions(),
            NullLogger<WorkerSupervisor>.Instance);
        await using var _ = supervisor;
        await supervisor.StartAsync();

        var tcs = new TaskCompletionSource<WorkerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.OnMessageReceived += envelope =>
        {
            if (envelope.CorrelationId == "old-peer") tcs.TrySetResult(envelope);
            return Task.CompletedTask;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var readLoop = supervisor.ReadMessagesAsync(cts.Token);

        var current = WorkerProtocolFraming.CreateEnvelope("comicinfo", "old-peer", new ComicInfoRequest
        {
            JobId = "old-peer",
            ArchivePath = "/nonexistent",
            ExpectedLastWriteTicks = 0,
            ExpectedByteLength = 0,
            Deadline = DateTimeOffset.UtcNow.AddSeconds(10),
        });
        var request = current with { ProtocolVersion = 2 };
        await supervisor.SendMessageAsync(request);

        var reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var error = WorkerProtocolFraming.GetPayload<AnalyzeError>(reply);
        Assert.Equal("protocol_version_mismatch", error!.ErrorType);
        Assert.Equal(3, WorkerProtocolVersion.Current);
        cts.Cancel();
        try { await readLoop; } catch (OperationCanceledException) { }
    }

    private ServiceProvider BuildDbProvider(out string dbPath)
    {
        dbPath = Path.Combine(_fixture.TempRoot, "ci-" + Guid.NewGuid().ToString("N")[..6] + ".db");
        var services = new ServiceCollection();
        var connection = DatabaseInitialization.BuildConnectionString(dbPath);
        services.AddDbContext<MangaPixerDbContext>(o => o.UseSqlite(connection));
        return services.BuildServiceProvider();
    }

    private async Task<long> SeedArchiveAsync(ServiceProvider provider, string fileName, int analysisState, long contentVersion)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await db.Database.MigrateAsync();
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "cil");
        if (library is null)
        {
            library = new LibraryEntity { PublicId = "cil", DisplayName = "CI", RootPath = _fixture.FixtureDir, CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var info = new FileInfo(Path.Combine(_fixture.FixtureDir, fileName));
        var node = new CatalogNodeEntity
        {
            PublicId = "ci-" + Guid.NewGuid().ToString("N")[..8],
            LibraryId = library.Id,
            Kind = 1,
            DisplayName = fileName,
            RelativePath = fileName,
            PathKey = fileName,
            SortKey = "1" + fileName,
            LastSeenScanRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ArchiveItem = new ArchiveItemEntity
            {
                ContentVersion = contentVersion,
                AnalysisState = analysisState,
                ByteLength = info.Length,
                ModificationTicks = info.LastWriteTimeUtc.Ticks,
            },
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        return node.Id;
    }
}
