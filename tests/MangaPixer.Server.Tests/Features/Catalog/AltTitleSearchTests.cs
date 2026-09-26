namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for alt-title search (1.26.0): the series_search triggers
/// and the series-matches query in SearchAsync. Synthetic titles only.
/// </summary>
public sealed class AltTitleSearchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public AltTitleSearchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-alttitle-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "alt.db")))
            .Options;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record Fixture(MangaPixerDbContext Db, long UserId, LibraryEntity Lib, LibraryEntity LibB);

    private async Task<Fixture> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var lib = new LibraryEntity { PublicId = OpaqueId.Encode(1), DisplayName = "Lib A", RootPath = "/x/a", CreatedAt = DateTimeOffset.UtcNow };
        var libB = new LibraryEntity { PublicId = OpaqueId.Encode(2), DisplayName = "Lib B", RootPath = "/x/b", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.AddRange(lib, libB);
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(10),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "h",
            SecurityStamp = "s",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return new Fixture(db, db.Users.Single().Id, lib, libB);
    }

    private static async Task<CatalogNodeEntity> AddNodeAsync(MangaPixerDbContext db, LibraryEntity lib, string name, int kind = (int)CatalogNodeKind.Folder, long? parentId = null, int availability = (int)CatalogNodeAvailability.Available)
    {
        var n = new CatalogNodeEntity
        {
            PublicId = OpaqueId.Encode(1000 + db.CatalogNodes.Count() + 1),
            LibraryId = lib.Id,
            ParentId = parentId,
            Kind = kind,
            DisplayName = name,
            RelativePath = name,
            PathKey = name + lib.Id,
            SortKey = "1" + name.ToLowerInvariant(),
            Availability = availability,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(n);
        await db.SaveChangesAsync();
        return n;
    }

    private static async Task<MetadataRecordEntity> AddRecordAsync(MangaPixerDbContext db, string title, string? altJson)
    {
        var r = new MetadataRecordEntity
        {
            PublicId = OpaqueId.Encode(5000 + db.MetadataRecords.Count() + 1),
            Provider = "mangaupdates",
            ExternalId = (db.MetadataRecords.Count() + 1).ToString(),
            Title = title,
            AltTitlesJson = altJson,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private static async Task LinkAsync(MangaPixerDbContext db, CatalogNodeEntity node, MetadataRecordEntity? rec, SeriesLinkState state)
    {
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = (int)state,
            RecordId = rec?.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> IndexTitlesAsync(MangaPixerDbContext db, long recordId)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT title FROM series_search WHERE record_id = @id ORDER BY title";
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", recordId));
        var list = new List<string>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(r.GetString(0));
        return list;
    }

    private static CatalogBrowseService Svc(MangaPixerDbContext db) => new(db, new LibraryAuthorizationService(db));

    [Fact]
    public async Task Triggers_InsertUpdateDelete_KeepIndexInSync()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Main Title", "[\"Alpha Alias\",\"Beta Alias\"]");
        Assert.Equal(["Alpha Alias", "Beta Alias", "Main Title"], await IndexTitlesAsync(db, rec.Id));

        rec.AltTitlesJson = "[\"Gamma Alias\"]";
        await db.SaveChangesAsync();
        Assert.Equal(["Gamma Alias", "Main Title"], await IndexTitlesAsync(db, rec.Id));

        rec.Title = "Renamed Title";
        await db.SaveChangesAsync();
        Assert.Equal(["Gamma Alias", "Renamed Title"], await IndexTitlesAsync(db, rec.Id));

        db.MetadataRecords.Remove(rec);
        await db.SaveChangesAsync();
        Assert.Empty(await IndexTitlesAsync(db, rec.Id));
    }

    [Fact]
    public async Task Triggers_MalformedAltJson_DoesNotFailTheWrite()
    {
        var f = await SetupAsync();
        var rec = await AddRecordAsync(f.Db, "Solo Title", "not json");
        Assert.Equal(["Solo Title"], await IndexTitlesAsync(f.Db, rec.Id));
    }

    [Fact]
    public async Task Backfill_IndexesExistingRecords_AndIsIdempotent()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Old Record", "[\"Old Alias\"]");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM series_search");
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        Assert.Equal(["Old Alias", "Old Record"], await IndexTitlesAsync(db, rec.Id));
    }

    [Fact]
    public async Task Search_AltTitle_ReturnsAnchorWithMatchedTitle_FirstPageOnly()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var folder = await AddNodeAsync(db, f.Lib, "Dungeon Folder");
        var rec = await AddRecordAsync(db, "Delicious Dungeon Long Title", "[\"Delicious in Dungeon\",\"Delicious Dungeon Extra Long Alias\"]");
        await LinkAsync(db, folder, rec, SeriesLinkState.Confirmed);

        var page1 = await Svc(db).SearchAsync(f.UserId, "delicious", pageSize: 50);
        Assert.NotNull(page1.SeriesMatches);
        var match = Assert.Single(page1.SeriesMatches!);
        Assert.Equal(folder.PublicId, match.Node.Id);
        Assert.Equal("Delicious in Dungeon", match.MatchedTitle);

        var page2 = await Svc(db).SearchAsync(f.UserId, "delicious", cursor: "0", pageSize: 50);
        Assert.Null(page2.SeriesMatches);
    }

    [Fact]
    public async Task Search_AutoLinkHits_NeedsReviewAndDontMatchNever()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Shared Series", "[\"Shared Alias\"]");
        var auto = await AddNodeAsync(db, f.Lib, "Auto Node");
        var review = await AddNodeAsync(db, f.Lib, "Review Node");
        var dont = await AddNodeAsync(db, f.Lib, "Dont Node");
        await LinkAsync(db, auto, rec, SeriesLinkState.Auto);
        await LinkAsync(db, review, rec, SeriesLinkState.NeedsReview);
        await LinkAsync(db, dont, null, SeriesLinkState.DontMatch);

        var res = await Svc(db).SearchAsync(f.UserId, "Shared Alias");
        var match = Assert.Single(res.SeriesMatches!);
        Assert.Equal(auto.PublicId, match.Node.Id);
    }

    [Fact]
    public async Task Search_ArchiveLevelLink_ReturnsTheArchive()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var archive = await AddNodeAsync(db, f.Lib, "one-shot.cbz", (int)CatalogNodeKind.Archive);
        var rec = await AddRecordAsync(db, "One Shot Record", "[\"Standalone Alias\"]");
        await LinkAsync(db, archive, rec, SeriesLinkState.Confirmed);

        var res = await Svc(db).SearchAsync(f.UserId, "Standalone Alias");
        var match = Assert.Single(res.SeriesMatches!);
        Assert.Equal(archive.PublicId, match.Node.Id);
        Assert.Equal(CatalogNodeKind.Archive, match.Node.Kind);
    }

    [Fact]
    public async Task Search_TombstonedAnchor_NotReturned()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var gone = await AddNodeAsync(db, f.Lib, "Gone Folder", availability: (int)CatalogNodeAvailability.Tombstoned);
        var rec = await AddRecordAsync(db, "Gone Record", "[\"Vanished Alias\"]");
        await LinkAsync(db, gone, rec, SeriesLinkState.Confirmed);

        var res = await Svc(db).SearchAsync(f.UserId, "Vanished Alias");
        Assert.Empty(res.SeriesMatches!);
    }

    [Fact]
    public async Task Search_LibraryWithSeriesInfoHidden_ExcludedOthersStay()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Twin Record", "[\"Twin Alias\"]");
        var a = await AddNodeAsync(db, f.Lib, "Twin In A");
        var b = await AddNodeAsync(db, f.LibB, "Twin In B");
        await LinkAsync(db, a, rec, SeriesLinkState.Confirmed);
        await LinkAsync(db, b, rec, SeriesLinkState.Confirmed);

        f.LibB.MetadataSeriesInfoHidden = true;
        await db.SaveChangesAsync();

        var res = await Svc(db).SearchAsync(f.UserId, "Twin Alias");
        Assert.Equal(a.PublicId, Assert.Single(res.SeriesMatches!).Node.Id);
    }

    [Fact]
    public async Task Search_GlobalSeriesInfoHidden_NoSeriesMatches()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Global Record", "[\"Global Alias\"]");
        await LinkAsync(db, await AddNodeAsync(db, f.Lib, "Global Node"), rec, SeriesLinkState.Confirmed);
        Assert.Single((await Svc(db).SearchAsync(f.UserId, "Global Alias")).SeriesMatches!);

        var settings = await db.AppSettings.SingleOrDefaultAsync();
        if (settings is null)
        {
            settings = new AppSettingsEntity();
            db.AppSettings.Add(settings);
        }
        settings.MetadataSeriesInfoHidden = true;
        await db.SaveChangesAsync();

        Assert.Empty((await Svc(db).SearchAsync(f.UserId, "Global Alias")).SeriesMatches!);
    }

    [Fact]
    public async Task Search_Incognito_HidesPrivateLibraryMatches()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Secret Record", "[\"Secret Alias\"]");
        await LinkAsync(db, await AddNodeAsync(db, f.LibB, "Secret Node"), rec, SeriesLinkState.Confirmed);
        db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = f.UserId, LibraryId = f.LibB.Id, MarkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Single((await Svc(db).SearchAsync(f.UserId, "Secret Alias", incognito: false)).SeriesMatches!);
        Assert.Empty((await Svc(db).SearchAsync(f.UserId, "Secret Alias", incognito: true)).SeriesMatches!);
    }

    [Fact]
    public async Task Search_CapsAtTwentyAnchors_OrderedBySortKey_QueryQuotesEscaped()
    {
        var f = await SetupAsync();
        var db = f.Db;
        var rec = await AddRecordAsync(db, "Many Record", "[\"Many \\\"Quoted\\\" Alias\"]");
        for (var i = 0; i < 25; i++)
            await LinkAsync(db, await AddNodeAsync(db, f.Lib, $"Many {i:D2}"), rec, SeriesLinkState.Confirmed);

        var res = await Svc(db).SearchAsync(f.UserId, "Many \"Quoted\" Alias");
        Assert.Equal(20, res.SeriesMatches!.Count);
        Assert.Equal("Many 00", res.SeriesMatches![0].Node.DisplayName);
        Assert.Equal("Many 19", res.SeriesMatches![19].Node.DisplayName);
    }
}

/// <summary>Latency guard: the extra series query must not move search p95 on a ~20k-node library.</summary>
public sealed class AltTitleSearchLatencyTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-altlat-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static double P95(List<double> ms)
    {
        ms.Sort();
        return ms[(int)Math.Ceiling(ms.Count * 0.95) - 1];
    }

    [Fact]
    public async Task Search_With20kNodes_SeriesQueryStaysWithinNoise()
    {
        Directory.CreateDirectory(_tempDir);
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "lat.db")))
            .Options;
        using var db = new MangaPixerDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        db.Libraries.Add(new LibraryEntity { PublicId = OpaqueId.Encode(1), DisplayName = "L", RootPath = "/x", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(10),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "h",
            SecurityStamp = "s",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var libId = db.Libraries.Single().Id;
        var userId = db.Users.Single().Id;

        await db.Database.ExecuteSqlAsync($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 20000)
            INSERT INTO catalog_nodes (PublicId, LibraryId, Kind, DisplayName, RelativePath, PathKey, SortKey, Availability, CreatedAt, LastSeenScanRevision)
            SELECT 'n' || i, {libId}, 0, 'Series Folder ' || i, 'Series Folder ' || i, 'k' || i, '1series folder ' || printf('%06d', i), 0, '2026-01-01', 0
            FROM n;
            """);
        await db.Database.ExecuteSqlAsync($"""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300)
            INSERT INTO metadata_records (PublicId, Provider, ExternalId, SourceKind, RecordKind, Title, AltTitlesJson, ImageState, ImageVersion, FetchedAt, FetchState)
            SELECT 'r' || i, 'mangaupdates', i, 0, 0, 'Record ' || i, '["Series Alias ' || i || '","Other Name ' || i || '"]', 0, 0, '2026-01-01', 0
            FROM n;
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO node_series_links (NodeId, LibraryId, State, RecordId, CreatedAt, UpdatedAt)
            SELECT cn.Id, {libId}, 0, r.Id, '2026-01-01', '2026-01-01'
            FROM metadata_records r JOIN catalog_nodes cn ON cn.PublicId = 'n' || r.Id;
            """);

        var svc = new CatalogBrowseService(db, new LibraryAuthorizationService(db));

        async Task<double> MeasureAsync()
        {
            await svc.SearchAsync(userId, "Series Alias 1"); // warm
            var samples = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = await svc.SearchAsync(userId, "Series Alias " + (i % 9 + 1));
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                Assert.NotNull(r.SeriesMatches);
            }
            return P95(samples);
        }

        var withSeries = await MeasureAsync();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM series_search");
        var baseline = await MeasureAsync();

        Assert.True(withSeries <= baseline + 100, $"p95 with series index {withSeries:F1} ms vs baseline {baseline:F1} ms");
    }
}
