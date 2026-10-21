namespace com.lifepixer.mangapixer.Tests.Server.Features.Covers;

using System.Data.Common;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the cover-decision sweep's writes (1.46.0 fix #9): a sweep over unchanged inputs - also the FULL
/// sweep after a restart or a cover-setting change - issues no INSERT / UPDATE / DELETE and leaves every decision as it was;
/// a real change writes exactly the rows it changes. Before the fix every node whose decision is "nothing automatic" (a
/// chapter, a single-page volume without a web cover, a Don't match subtree) got a <c>DELETE</c> on every visit, row or not.
/// The sweep runs through <see cref="CoverDecisionHostedService"/> with its own DI scope, as in the app.
/// </summary>
public sealed class CoverSweepWritesTests
{
    private const ulong Local = 0x0F0F_3C3C_5A5A_A5A5UL;
    private static readonly ulong Web = Local ^ ((1UL << 30) - 1);

    private sealed record Seeded(CatalogNodeEntity Series, CatalogNodeEntity V01, CatalogNodeEntity V02, CatalogNodeEntity C010,
        CatalogNodeEntity Loose, CatalogNodeEntity LooseV01, CatalogNodeEntity Skip, CatalogNodeEntity SkipV01, MetadataRecordEntity Companion,
        VolumeCoverEntity WebV1);

    /// <summary>
    /// A linked series (v01 with a clearly different web volume 1 cover; v02 with none; a spread-shaped chapter), an unlinked
    /// folder with a spread volume and a chapter, and a Don't match folder with a spread volume.
    /// </summary>
    private static async Task<Seeded> SeedAsync(CoverLayerTestKit kit)
    {
        var series = await kit.Db.AddFolderAsync(null, "Series");
        var record = await kit.Db.AddRecordAsync("901", "Series");
        await kit.Db.AddLinkAsync(series, record);
        var companion = await kit.AddCompanionAsync(record);
        var webV1 = await kit.AddStoredCoverAsync(companion, 1, "en", Web);
        var v01 = await kit.AddBookAsync(series, "Series v01", 700, 1000, Local);
        var v02 = await kit.AddBookAsync(series, "Series v02", 700, 1000, Local);
        var c010 = await kit.AddBookAsync(series, "Series c010", 1400, 1000, Local);

        var loose = await kit.Db.AddFolderAsync(null, "Loose");
        var looseV01 = await kit.AddBookAsync(loose, "Loose v01", 1400, 1000, Local);
        await kit.AddBookAsync(loose, "Loose c001", 700, 1000, Local);

        var skip = await kit.Db.AddFolderAsync(null, "Skip");
        await kit.Db.AddLinkAsync(skip, null, SeriesLinkState.DontMatch);
        var skipV01 = await kit.AddBookAsync(skip, "Skip v01", 1400, 1000, Local);
        return new Seeded(series, v01, v02, c010, loose, looseV01, skip, skipV01, companion, webV1);
    }

    [Fact]
    public async Task FullSweepOverUnchangedInputs_WritesNothing_AndKeepsEveryDecision()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedAsync(kit);
        await using var app = SweepHost.Create(kit);

        await app.NewService().SweepAsync(default);
        var decided = await RowsAsync(kit);
        // The decisions themselves (the same before and after the fix): the series folder its local volume 1, v01 the web cover
        // (clearly different), the unlinked spread volume its front half (left-to-right: the right half). Nothing else has a row.
        var expected = new List<(long, int, int, int?, long?, long?)>
        {
            (s.Series.Id, (int)AutoCoverSource.LocalVolume1, (int)AutoCoverReason.SeriesLocalVolume1, null, null, s.V01.Id),
            (s.V01.Id, (int)AutoCoverSource.WebVolume, (int)AutoCoverReason.LocalNotCover, null, s.WebV1.Id, null),
            (s.LooseV01.Id, (int)AutoCoverSource.Crop, (int)AutoCoverReason.Spread, (int)CoverCropSide.Right, null, null),
        };
        Assert.Equal(expected, decided.Select(r => (r.NodeId, r.Source, r.Reason, r.CropSide, r.VolumeCoverId, r.ArchiveNodeId)).ToList());
        Assert.True(app.Counter.Writes > 0); // the first sweep did write (sanity: the counter sees the sweep's statements)

        // A restart (a new service: watermark MinValue -> the FULL sweep, the spread backfill again) over the same inputs.
        app.Counter.Reset();
        var restarted = app.NewService();
        await restarted.SweepAsync(default);
        Assert.Equal(0, app.Counter.Writes);
        Assert.Equal(decided, await RowsAsync(kit));

        // The next periodic sweep of the same service (nothing changed since its watermark).
        app.Counter.Reset();
        await restarted.SweepAsync(default);
        Assert.Equal(0, app.Counter.Writes);
        Assert.Equal(decided, await RowsAsync(kit));
    }

    [Fact]
    public async Task RealChanges_WriteExactlyTheRowsTheyChange()
    {
        await using var kit = await CoverLayerTestKit.CreateAsync();
        var s = await SeedAsync(kit);
        await using var app = SweepHost.Create(kit);
        var sweeper = app.NewService();
        await sweeper.SweepAsync(default);
        var before = await RowsAsync(kit);

        // A web volume 2 cover is stored: v02 gets its decision (one INSERT), nothing else is touched.
        var webV2 = await kit.AddStoredCoverAsync(s.Companion, 2, "en", Web);
        app.Counter.Reset();
        await sweeper.SweepAsync(default);
        Assert.Equal(1, app.Counter.Writes);
        var after = await RowsAsync(kit);
        var v02 = Assert.Single(after, r => r.NodeId == s.V02.Id);
        Assert.Equal((int)AutoCoverSource.WebVolume, v02.Source);
        Assert.Equal(webV2.Id, v02.VolumeCoverId);
        Assert.Equal(before, after.Where(r => r.NodeId != s.V02.Id).ToList());

        // The series becomes Don't match: its three rows are deleted (one DELETE each), the unlinked spread keeps its row.
        var link = await kit.Db.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == s.Series.Id);
        link.State = (int)SeriesLinkState.DontMatch;
        link.UpdatedAt = DateTimeOffset.UtcNow;
        await kit.Db.Db.SaveChangesAsync();
        app.Counter.Reset();
        await sweeper.SweepAsync(default);
        Assert.Equal(3, app.Counter.Writes);
        Assert.Equal(new[] { s.LooseV01.Id }, (await RowsAsync(kit)).Select(r => r.NodeId));
    }

    /// <summary>Every column of a <c>node_auto_covers</c> row (record equality: a rewrite of the same decision changes DecidedAt).</summary>
    private sealed record Row(long NodeId, int Source, int Reason, int? CropSide, long? VolumeCoverId, long? ArchiveNodeId, long? LocalHash,
        string InputsKey, int Version, DateTimeOffset DecidedAt, DateTimeOffset? RecheckAt);

    private static async Task<List<Row>> RowsAsync(CoverLayerTestKit kit) =>
        (await kit.Db.Db.NodeAutoCovers.AsNoTracking().ToListAsync())
            .Select(r => new Row(r.NodeId, r.Source, r.Reason, r.CropSide, r.VolumeCoverId, r.ArchiveNodeId, r.LocalHash, r.InputsKey, r.Version,
                r.DecidedAt, r.RecheckAt))
            .OrderBy(r => r.NodeId).ToList();

    /// <summary>The sweep's DI as in the app (scoped context + decisions), over the kit's database, with a write-statement counter.</summary>
    private sealed class SweepHost : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        private SweepHost(ServiceProvider provider, WriteCounter counter)
        {
            _provider = provider;
            Counter = counter;
        }

        public WriteCounter Counter { get; }

        public static SweepHost Create(CoverLayerTestKit kit)
        {
            var counter = new WriteCounter();
            var connection = kit.Db.Db.Database.GetConnectionString();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<MangaPixerDbContext>(o => o.UseSqlite(connection).AddInterceptors(counter));
            services.AddSingleton<ICoverRenderer>(kit.Renderer);
            services.AddSingleton<ICoverHasher>(kit.Hasher);
            services.AddSingleton(kit.Files);
            services.AddSingleton(kit.Thumbnails);
            services.AddSingleton(kit.Posters);
            services.AddScoped<CoverCropService>();
            services.AddScoped<CoverDirectionResolver>();
            services.AddScoped<CoverDecisionService>();
            return new SweepHost(services.BuildServiceProvider(), counter);
        }

        /// <summary>A fresh hosted service (as after a start): its watermark and spread memory are empty.</summary>
        public CoverDecisionHostedService NewService() => new(_provider.GetRequiredService<IServiceScopeFactory>(), new CoverDecisionQueue(),
            new ConfigurationBuilder().Build(), NullLogger<CoverDecisionHostedService>.Instance);

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    /// <summary>Counts the INSERT / UPDATE / DELETE statements EF sends (SaveChanges batches and ExecuteDelete / ExecuteUpdate).</summary>
    private sealed class WriteCounter : DbCommandInterceptor
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public void Reset() => Interlocked.Exchange(ref _writes, 0);

        private void Count(DbCommand command)
        {
            foreach (var statement in command.CommandText.Split(';'))
            {
                var text = statement.TrimStart();
                if (text.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) || text.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Increment(ref _writes);
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Count(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Count(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
