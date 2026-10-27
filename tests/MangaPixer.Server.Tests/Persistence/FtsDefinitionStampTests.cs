namespace com.lifepixer.mangapixer.Tests.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.49.0 lane S: the FTS definition stamp. Before 1.49.0 <c>catalog_search</c> / <c>series_search</c> were <c>CREATE ... IF NOT EXISTS</c>
/// with a missing-row backfill only, so a changed definition never reached an existing database. A database with the OLD definitions (no
/// stamp, trigram without accent folding, the path column) is rebuilt once at start-up - every row, working triggers, the stamp written -
/// and a database already on the current definition is not touched. Service-with-DB tests; synthetic names.
/// </summary>
public sealed class FtsDefinitionStampTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mangapixer-fts-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MangaPixerDbContext NewDb()
    {
        Directory.CreateDirectory(_dir);
        var options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_dir, "t.db")))
            .Options;
        return new MangaPixerDbContext(options);
    }

    /// <summary>The 1.26.0 - 1.48.0 definitions, created the way those versions did (no stamp table).</summary>
    private static async Task CreateOldDefinitionsAsync(MangaPixerDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE catalog_search USING fts5(display_name, relative_path, library_id UNINDEXED, node_id UNINDEXED, tokenize='trigram');
            CREATE TRIGGER catalog_search_ai AFTER INSERT ON catalog_nodes BEGIN
                INSERT INTO catalog_search(display_name, relative_path, library_id, node_id) VALUES (new.DisplayName, new.RelativePath, new.LibraryId, new.Id);
            END;
            CREATE TRIGGER catalog_search_au AFTER UPDATE OF DisplayName, RelativePath, LibraryId ON catalog_nodes BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
                INSERT INTO catalog_search(display_name, relative_path, library_id, node_id) VALUES (new.DisplayName, new.RelativePath, new.LibraryId, new.Id);
            END;
            CREATE TRIGGER catalog_search_ad AFTER DELETE ON catalog_nodes BEGIN
                DELETE FROM catalog_search WHERE node_id = old.Id;
            END;
            CREATE VIRTUAL TABLE series_search USING fts5(title, record_id UNINDEXED, tokenize='trigram');
            """);
    }

    private static async Task<long> SeedAsync(MangaPixerDbContext db)
    {
        var library = new LibraryEntity { PublicId = "ftslib", DisplayName = "L", RootPath = "/x", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        foreach (var (pid, name) in new[] { ("f1", "Pokémon Adventures"), ("f2", "Café Stories"), ("f3", "Plain Name") })
        {
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = pid,
                LibraryId = library.Id,
                Kind = 0,
                DisplayName = name,
                RelativePath = name,
                PathKey = name,
                SortKey = "0" + name.ToLowerInvariant(),
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        db.MetadataRecords.Add(new MetadataRecordEntity
        {
            PublicId = "ftsrec",
            Provider = "mangaupdates",
            ExternalId = "1",
            Title = "Pokémon Special",
            AltTitlesJson = "[\"Pocket Monsters SPECIAL\"]",
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return library.Id;
    }

    private static async Task<long> ScalarAsync(MangaPixerDbContext db, string sql, params (string Name, object Value)[] args)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.Add(new SqliteParameter(name, value));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TableSqlAsync(MangaPixerDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $n";
        command.Parameters.Add(new SqliteParameter("$n", table));
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AnOldDefinitionDatabase_IsRebuiltOnce_WithEveryRow_FoldedAccents_AndWorkingTriggers()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        await CreateOldDefinitionsAsync(db);
        var libraryId = await SeedAsync(db);
        // The old index found the accented name only as spelled.
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"pokemon\"'"));
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search"));

        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        Assert.Contains("remove_diacritics 1", await TableSqlAsync(db, "catalog_search"), StringComparison.Ordinal);
        Assert.DoesNotContain("relative_path", await TableSqlAsync(db, "catalog_search"), StringComparison.Ordinal);
        Assert.Contains("remove_diacritics 1", await TableSqlAsync(db, "series_search"), StringComparison.Ordinal);
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"pokemon\"'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"CAFE\"'"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) FROM series_search"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM series_search WHERE series_search MATCH '\"pokemon\"'"));
        Assert.Equal(2, await ScalarAsync(db, "SELECT COUNT(*) FROM fts_definitions"));

        // The recreated triggers keep the index in sync: insert, rename (the path alone no longer re-indexes), delete.
        var node = new CatalogNodeEntity
        {
            PublicId = "f4",
            LibraryId = libraryId,
            Kind = 1,
            DisplayName = "Señor Archive.cbz",
            RelativePath = "Señor Archive.cbz",
            PathKey = "Señor Archive.cbz",
            SortKey = "1señor archive.cbz",
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"senor\"'"));
        node.DisplayName = "Renamed Archive.cbz";
        await db.SaveChangesAsync();
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"senor\"'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE catalog_search MATCH '\"renamed\"'"));
        db.CatalogNodes.Remove(node);
        await db.SaveChangesAsync();
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search"));
    }

    [Fact]
    public async Task ACurrentDefinitionDatabase_IsNotRebuilt_OnlyMissingRowsAreAdded()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        await SeedAsync(db);

        // A marker row only a rebuild would drop, and one node row deleted that the missing-row backfill puts back.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO catalog_search(display_name, library_id, node_id) VALUES ('Marker Row', 0, -1);");
        var f3 = await ScalarAsync(db, "SELECT Id FROM catalog_nodes WHERE PublicId = 'f3'");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog_search WHERE node_id = {0};", f3);

        Assert.False(await DatabaseInitialization.EnsureFtsDefinitionAsync(db, DatabaseInitialization.CatalogSearch, null, default));
        Assert.False(await DatabaseInitialization.EnsureFtsDefinitionAsync(db, DatabaseInitialization.SeriesSearch, null, default));
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE node_id = -1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE node_id = $id", ("$id", f3)));
        Assert.Equal(4, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search"));
    }

    [Fact]
    public async Task AChangedStamp_RebuildsThatTableOnly()
    {
        using var db = NewDb();
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync("INSERT INTO catalog_search(display_name, library_id, node_id) VALUES ('Marker Row', 0, -1);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO series_search(title, record_id) VALUES ('Marker Title', -1);");
        await db.Database.ExecuteSqlRawAsync("UPDATE fts_definitions SET definition = 'an older definition' WHERE name = 'catalog_search';");

        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search WHERE node_id = -1"));
        Assert.Equal(3, await ScalarAsync(db, "SELECT COUNT(*) FROM catalog_search"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM series_search WHERE record_id = -1"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM fts_definitions WHERE name = 'catalog_search' AND definition = $d",
            ("$d", DatabaseInitialization.CatalogSearch.CreateTable)));
    }
}
