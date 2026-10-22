using System.Data.Common;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Home;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Features.Home;

/// <summary>
/// 1.47.0 lane S (owner decision 12): the home row's stack covers kept in memory (<see cref="HomeCoverCache"/>) give the same answer as
/// covers resolved on every request, for three viewers (admin; a reader with one grant; a reader with two grants and a Private library),
/// Incognito off / on, after a change of EVERY input the cover layer reads (<see cref="CoverLayerVersion"/>): an admin choice set,
/// changed and removed; an automatic decision; a stored web cover and its file version; the library's "Show saved web covers"; a folder's
/// cover preference; the web-cover and crop switches; a series link and its record's poster; an archive's content version; a scan.
/// The cache is shown to be really used (a write that bypasses EF Core - no code does that - is NOT seen, on purpose), and which SQL moves
/// the version is checked on the statements EF Core really sends, including the busy-table updates that must not move it.
/// </summary>
public sealed class HomeCoverCacheTests : IDisposable
{
    private readonly MoveTestHarness _h = new();
    private readonly HomeStackCache _stacks = new();
    private readonly HomeCoverCache _covers = new();
    private int _serial;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task EveryCoverInput_EveryViewer_TheKeptCoversGiveTheSameRows()
    {
        await _h.InitAsync();
        var alpha = await _h.AddLibraryAsync("Alpha");
        var beta = await _h.AddLibraryAsync("Beta");
        var admin = await _h.AddUserAsync("admin", admin: true);
        var oneGrant = await _h.AddUserAsync("reader1");
        var twoGrants = await _h.AddUserAsync("reader2");
        await using (var db = _h.NewContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = oneGrant, LibraryId = alpha, GrantedAt = now });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = twoGrants, LibraryId = alpha, GrantedAt = now });
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = twoGrants, LibraryId = beta, GrantedAt = now });
            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = twoGrants, LibraryId = beta, MarkedAt = now });
            await db.SaveChangesAsync();
        }
        long[] viewers = [admin, oneGrant, twoGrants];

        _h.WriteArchive(alpha, "Series One/v01.cbz", 1);
        _h.WriteArchive(alpha, "Series One/v02.cbz", 2);
        _h.WriteArchive(alpha, "Series Two/c001.cbz", 3);
        _h.WriteArchive(alpha, "Series Two/c002.cbz", 4);
        _h.WriteArchive(alpha, "Loose One.cbz", 5);
        _h.WriteArchive(beta, "Series Four/c001.cbz", 6);
        await _h.ScanAsync(alpha);
        await _h.ScanAsync(beta);
        var one = await _h.NodeAsync(alpha, "Series One");
        var two = await _h.NodeAsync(alpha, "Series Two");
        var v01 = await _h.NodeAsync(alpha, "Series One/v01.cbz");
        var v02 = await _h.NodeAsync(alpha, "Series One/v02.cbz");
        var c001 = await _h.NodeAsync(alpha, "Series Two/c001.cbz");

        await AssertSameForAllAsync(viewers, "first scan");
        var hits = _covers.Hits;
        await AssertSameForAllAsync(viewers, "nothing changed");
        Assert.True(_covers.Hits > hits, "the kept covers were reused while nothing changed");

        // 1. An admin choice: another archive's cover, then a pin (an update), then removed.
        var oneBefore = await CoverOfAsync(admin, one);
        await WriteAsync(db => db.NodeCoverChoices.Add(new NodeCoverChoiceEntity
        {
            NodeId = one.Id, Mode = (int)CoverChoiceMode.Archive, ArchiveNodeId = v02.Id, Version = 1, SetAt = DateTimeOffset.UtcNow,
        }));
        await AssertSameForAllAsync(viewers, "a cover choice");
        Assert.NotEqual(oneBefore, await CoverOfAsync(admin, one));
        await using (var db = _h.NewContext())
        {
            await db.NodeCoverChoices.Where(c => c.NodeId == one.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Mode, (int)CoverChoiceMode.FilePinned).SetProperty(c => c.Version, 2));
        }
        await AssertSameForAllAsync(viewers, "the choice changed to a pin");
        await using (var db = _h.NewContext())
            await db.NodeCoverChoices.Where(c => c.NodeId == one.Id).ExecuteDeleteAsync();
        await AssertSameForAllAsync(viewers, "the choice removed");
        Assert.Equal(oneBefore, await CoverOfAsync(admin, one));

        // 2. An automatic decision (a series folder shows its local volume 1).
        await WriteAsync(db => db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = two.Id, Source = (int)AutoCoverSource.LocalVolume1, ArchiveNodeId = c001.Id, Version = 1, DecidedAt = DateTimeOffset.UtcNow,
        }));
        await AssertSameForAllAsync(viewers, "an automatic decision");

        // 3. A stored web cover decided for Series One; its file version; the library switch; the folder preference; the global switch.
        var record = await AddRecordAsync();
        var cover = new VolumeCoverEntity
        {
            PublicId = "vc" + ++_serial, ProviderRecordId = record, Volume = 1, Locale = "en", RemoteId = "r", RemoteFile = "f",
            State = (int)VolumeCoverState.Stored, StoredVersion = 1,
        };
        await WriteAsync(db => db.VolumeCovers.Add(cover));
        await WriteAsync(db => db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = one.Id, Source = (int)AutoCoverSource.WebVolume, VolumeCoverId = cover.Id, Version = 1, DecidedAt = DateTimeOffset.UtcNow,
        }));
        await AssertSameForAllAsync(viewers, "a web cover decided");
        var web = await CoverOfAsync(admin, one);
        Assert.StartsWith($"/api/v1/nodes/{one.PublicId}/cover?v=", web, StringComparison.Ordinal);
        await using (var db = _h.NewContext())
            await db.VolumeCovers.Where(c => c.Id == cover.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.StoredVersion, 2));
        await AssertSameForAllAsync(viewers, "the web cover's file version");
        Assert.NotEqual(web, await CoverOfAsync(admin, one));
        await using (var db = _h.NewContext())
            await db.Libraries.Where(l => l.Id == alpha).ExecuteUpdateAsync(u => u.SetProperty(l => l.WebCoversHidden, true));
        await AssertSameForAllAsync(viewers, "the library hides saved web covers");
        Assert.Equal(oneBefore, await CoverOfAsync(admin, one));
        await WriteAsync(db => db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = one.Id, Preference = (int)FolderCoverPreference.Web }));
        await AssertSameForAllAsync(viewers, "a folder prefers web covers");
        Assert.NotEqual(oneBefore, await CoverOfAsync(admin, one));
        await using (var db = _h.NewContext())
            await db.AppSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.MetadataVolumeCoversEnabled, false));
        await AssertSameForAllAsync(viewers, "the web-cover switch off");
        Assert.Equal(oneBefore, await CoverOfAsync(admin, one));
        await using (var db = _h.NewContext())
            await db.AppSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.MetadataVolumeCoversEnabled, true).SetProperty(s => s.CoverSpreadCropEnabled, false));
        await AssertSameForAllAsync(viewers, "the web-cover switch on, the crop switch off");

        // 4. A series link and its record's poster (an automatic poster decision shows the linked record's stored image).
        await WriteAsync(db => db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = two.Id, LibraryId = alpha, RecordId = record, State = (int)SeriesLinkState.Confirmed,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        }));
        await using (var db = _h.NewContext())
        {
            await db.NodeAutoCovers.Where(a => a.NodeId == two.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.Source, (int)AutoCoverSource.Poster).SetProperty(a => a.ArchiveNodeId, (long?)null));
        }
        await AssertSameForAllAsync(viewers, "a poster decision without an image");
        await using (var db = _h.NewContext())
            await db.MetadataRecords.Where(r => r.Id == record).ExecuteUpdateAsync(u => u.SetProperty(r => r.ImageState, 1).SetProperty(r => r.ImageVersion, 1));
        await AssertSameForAllAsync(viewers, "the record's poster stored");
        var poster = await CoverOfAsync(admin, two);
        await using (var db = _h.NewContext())
            await db.MetadataRecords.Where(r => r.Id == record).ExecuteUpdateAsync(u => u.SetProperty(r => r.ImageVersion, 2));
        await AssertSameForAllAsync(viewers, "the record's poster replaced");
        Assert.NotEqual(poster, await CoverOfAsync(admin, two));
        await using (var db = _h.NewContext())
            await db.NodeSeriesLinks.Where(l => l.NodeId == two.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, (int)SeriesLinkState.DontMatch));
        await AssertSameForAllAsync(viewers, "the link set to Don't match");

        // 5. An archive's content version (its file changed): the file cover's URL version.
        await using (var db = _h.NewContext())
            await db.ArchiveItems.Where(a => a.NodeId == v01.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ContentVersion, a => a.ContentVersion + 1));
        await AssertSameForAllAsync(viewers, "a content version");

        // 6. A scan: a new archive that sorts first in its folder becomes the folder's file cover.
        _h.WriteArchive(alpha, "Series Two/c000.cbz", 7);
        await _h.ScanAsync(alpha);
        await AssertSameForAllAsync(viewers, "a scan");
    }

    [Fact]
    public async Task AWriteThatBypassesEfCore_IsNotSeen_SoTheKeptCoverIsReallyUsed_AndAnEfWriteMovesIt()
    {
        var (library, admin) = await SeedOneLibraryAsync();
        var series = await _h.NodeAsync(library, "Series One");
        var v02 = await _h.NodeAsync(library, "Series One/v02.cbz");
        var kept = await CoverOfAsync(admin, series);

        // A raw ADO insert (no code writes the cover tables like that): the version does not move, the kept cover is served.
        await using (var db = _h.NewContext())
        {
            await db.Database.OpenConnectionAsync();
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"INSERT INTO node_cover_choices (NodeId, Mode, ArchiveNodeId, Version, SetAt) VALUES ({series.Id}, {(int)CoverChoiceMode.Archive}, {v02.Id}, 1, 0)";
            await command.ExecuteNonQueryAsync();
        }
        Assert.NotEqual(kept, await CoverOfAsync(admin, series, cached: false));
        Assert.Equal(kept, await CoverOfAsync(admin, series));

        // The same row touched through EF Core: the version moves and the kept cover is dropped.
        await using (var db = _h.NewContext())
            await db.NodeCoverChoices.Where(c => c.NodeId == series.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Version, 2));
        Assert.Equal(await CoverOfAsync(admin, series, cached: false), await CoverOfAsync(admin, series));
        Assert.NotEqual(kept, await CoverOfAsync(admin, series));
    }

    [Fact]
    public async Task InsideATransaction_TheVersionMovesAgainAfterTheCommit()
    {
        var (library, _) = await SeedOneLibraryAsync();
        var series = await _h.NodeAsync(library, "Series One");
        await using var db = _h.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        db.FolderCoverPreferences.Add(new FolderCoverPreferenceEntity { NodeId = series.Id, Preference = (int)FolderCoverPreference.File });
        await db.SaveChangesAsync();
        var beforeCommit = CoverLayerVersion.Of(db);
        await tx.CommitAsync();
        Assert.True(CoverLayerVersion.Of(db) > beforeCommit, "a reader that took the version before the commit cannot keep a pre-commit answer");
    }

    [Fact]
    public async Task TheVersionMoves_ForExactlyTheCoverInputs_OnTheStatementsEfCoreSends()
    {
        var (library, _) = await SeedOneLibraryAsync();
        var series = await _h.NodeAsync(library, "Series One");
        var archive = await _h.NodeAsync(library, "Series One/v01.cbz");
        var record = await AddRecordAsync();
        var recorder = new Recorder();
        string connectionString;
        await using (var probe = _h.NewContext())
            connectionString = probe.Database.GetConnectionString()!;

        async Task<bool> MovesAsync(Func<MangaPixerDbContext, Task> write)
        {
            recorder.Texts.Clear();
            await using var db = new MangaPixerDbContext(new DbContextOptionsBuilder<MangaPixerDbContext>()
                .UseSqlite(connectionString).AddInterceptors(recorder).Options);
            await write(db);
            Assert.NotEmpty(recorder.Texts);
            return recorder.Texts.Any(CoverLayerVersion.WritesCoverInputs);
        }

        // Inputs (tracked saves and ExecuteUpdate / ExecuteDelete).
        Assert.True(await MovesAsync(async db =>
        {
            db.NodeCoverChoices.Add(new NodeCoverChoiceEntity { NodeId = series.Id, Mode = (int)CoverChoiceMode.FilePinned, Version = 1, SetAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }));
        Assert.True(await MovesAsync(db => db.NodeCoverChoices.Where(c => c.NodeId == series.Id).ExecuteDeleteAsync()));
        Assert.True(await MovesAsync(db => db.NodeAutoCovers.Where(c => c.NodeId == series.Id).ExecuteDeleteAsync()));
        Assert.True(await MovesAsync(db => db.FolderCoverPreferences.Where(c => c.NodeId == series.Id).ExecuteDeleteAsync()));
        Assert.True(await MovesAsync(db => db.VolumeCovers.Where(c => c.ProviderRecordId == record).ExecuteDeleteAsync()));
        Assert.True(await MovesAsync(db => db.NodeSeriesLinks.Where(c => c.NodeId == series.Id).ExecuteDeleteAsync()));
        Assert.True(await MovesAsync(async db =>
        {
            var node = await db.CatalogNodes.SingleAsync(n => n.Id == archive.Id);
            node.Availability = (int)CatalogNodeAvailabilityTombstoned;
            await db.SaveChangesAsync();
            node.Availability = 0;
            await db.SaveChangesAsync();
        }));
        Assert.True(await MovesAsync(db => db.ArchiveItems.Where(a => a.NodeId == archive.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ContentVersion, a => a.ContentVersion))));
        Assert.True(await MovesAsync(db => db.MetadataRecords.Where(r => r.Id == record).ExecuteUpdateAsync(u => u.SetProperty(r => r.ImageVersion, 3))));
        Assert.True(await MovesAsync(async db =>
        {
            var lib = await db.Libraries.SingleAsync(l => l.Id == library);
            lib.WebCoversHidden = !lib.WebCoversHidden;
            await db.SaveChangesAsync();
        }));
        Assert.True(await MovesAsync(async db =>
        {
            var settings = await db.AppSettings.SingleAsync();
            settings.CoverSpreadCropEnabled = !settings.CoverSpreadCropEnabled;
            await db.SaveChangesAsync();
        }));
        Assert.True(await MovesAsync(async db =>
        {
            db.MetadataRecords.Add(new MetadataRecordEntity { PublicId = "rx" + ++_serial, Provider = "mangaupdates", ExternalId = "x" + _serial, Title = "Synthetic", FetchedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }));

        // Busy-table updates that are not cover inputs.
        Assert.False(await MovesAsync(async db =>
        {
            var lib = await db.Libraries.SingleAsync(l => l.Id == library);
            lib.DisplayName += " renamed";
            await db.SaveChangesAsync();
        }));
        Assert.False(await MovesAsync(db => db.MetadataRecords.Where(r => r.Id == record).ExecuteUpdateAsync(u => u.SetProperty(r => r.Title, "Synthetic renamed"))));
        Assert.False(await MovesAsync(db => db.ArchiveItems.Where(a => a.NodeId == archive.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ThumbnailState, 1).SetProperty(a => a.ThumbnailContentVersion, 7))));
        Assert.False(await MovesAsync(db => db.CatalogNodes.Where(n => n.Id == archive.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.LastSeenScanRevision, 42))));
        Assert.False(await MovesAsync(async db =>
        {
            var settings = await db.AppSettings.SingleAsync();
            settings.UpdateCheckEnabled = !settings.UpdateCheckEnabled;
            await db.SaveChangesAsync();
        }));
        Assert.False(await MovesAsync(async db =>
        {
            db.ReadMarks.Add(new ReadMarkEntity { UserId = (await db.Users.FirstAsync()).Id, ItemId = archive.Id, MarkedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }));
        Assert.False(await MovesAsync(db => db.CatalogNodes.Where(n => n.Id == archive.Id).ToListAsync()));
    }

    [Theory]
    [InlineData("SELECT \"n\".\"Id\" FROM \"node_cover_choices\" AS \"n\"", false)]
    [InlineData("  select * from node_auto_covers", false)]
    [InlineData("PRAGMA foreign_keys = ON;", false)]
    [InlineData("INSERT INTO \"node_auto_covers\" (\"NodeId\") VALUES (@p0) RETURNING \"Id\";", true)]
    [InlineData("INSERT OR REPLACE INTO folder_cover_preferences (NodeId, Preference) VALUES (1, 0)", true)]
    [InlineData("DELETE FROM \"volume_covers\" AS \"v\" WHERE \"v\".\"Id\" = 3", true)]
    [InlineData("UPDATE \"catalog_nodes\" SET \"LastSeenScanRevision\" = @p0 WHERE \"Id\" = @p1 RETURNING 1;", false)]
    [InlineData("UPDATE \"catalog_nodes\" SET \"SortKey\" = @p0 WHERE \"Id\" = @p1 RETURNING 1;", true)]
    [InlineData("UPDATE \"read_marks\" SET \"MarkedAt\" = @p0;\nUPDATE \"node_series_links\" SET \"State\" = @p1;", true)]
    [InlineData("WITH x AS (SELECT 1) UPDATE \"archive_items\" SET \"ThumbnailContentVersion\" = 2", false)]
    [InlineData("INSERT INTO \"reading_progress\" (\"ItemId\") VALUES (@p0)", false)]
    [InlineData("", false)]
    public void WritesCoverInputs_ReadsTheStatement(string sql, bool expected) =>
        Assert.Equal(expected, CoverLayerVersion.WritesCoverInputs(sql));

    // ---------------------------------------------------------------- helpers

    private const int CatalogNodeAvailabilityTombstoned = 5;

    private sealed class Recorder : DbCommandInterceptor
    {
        public List<string> Texts { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Texts.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private async Task WriteAsync(Action<MangaPixerDbContext> add)
    {
        await using var db = _h.NewContext();
        add(db);
        await db.SaveChangesAsync();
    }

    /// <summary>For every viewer, Incognito off / on: the row with kept covers equals the row with covers resolved per request.</summary>
    private async Task AssertSameForAllAsync(long[] viewers, string step)
    {
        foreach (var viewer in viewers)
        {
            foreach (var incognito in new[] { false, true })
            {
                var expected = await AskAsync(cached: false, viewer, incognito);
                var actual = await AskAsync(cached: true, viewer, incognito);
                Assert.True(expected == actual, $"{step}: viewer {viewer}, incognito {incognito}:\n{actual}\nexpected\n{expected}");
            }
        }
    }

    private async Task<string?> CoverOfAsync(long userId, CatalogNodeEntity node, bool cached = true) =>
        (await AskDtoAsync(cached, userId, incognito: false)).Libraries.SelectMany(l => l.Stacks).Single(s => s.Id == node.PublicId).CoverUrl;

    private async Task<string> AskAsync(bool cached, long userId, bool incognito) =>
        JsonSerializer.Serialize(await AskDtoAsync(cached, userId, incognito));

    /// <summary>One request as the host serves it (both with the kept stack sources); only the covers differ: kept or resolved.</summary>
    private async Task<RecentChaptersDto> AskDtoAsync(bool cached, long userId, bool incognito)
    {
        await using var db = _h.NewContext();
        var service = new RecentChaptersService(db, new LibraryAuthorizationService(db),
            covers: new LayeredCoverResolver(new CoverResolutionService(db)), stackCache: _stacks, coverCache: cached ? _covers : null);
        return await service.GetRecentChaptersAsync(userId, null, incognito);
    }

    private async Task<(long Library, long Admin)> SeedOneLibraryAsync()
    {
        await _h.InitAsync();
        var library = await _h.AddLibraryAsync("Alpha");
        var admin = await _h.AddUserAsync("admin", admin: true);
        _h.WriteArchive(library, "Series One/v01.cbz", 11);
        _h.WriteArchive(library, "Series One/v02.cbz", 12);
        await _h.ScanAsync(library);
        return (library, admin);
    }

    private async Task<long> AddRecordAsync()
    {
        await using var db = _h.NewContext();
        var record = new MetadataRecordEntity
        {
            PublicId = "rec" + ++_serial,
            Provider = "mangaupdates",
            ExternalId = "ext" + _serial,
            Title = "Synthetic record " + _serial,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        return record.Id;
    }
}
