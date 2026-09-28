namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddDeclaredFacts migration (1.28.0, the cycle's only migration): applied to a
/// populated 1.27-era database it is purely additive - the existing settings row keeps its values and reads
/// "Compare covers" as ON and the provider allowlist as unset (defaults), a new settings row starts ON while
/// OFF still round-trips, and the new <c>declared_facts</c> table takes folder and library rows that go with
/// their node or library.
/// </summary>
public sealed class AddDeclaredFactsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddDeclaredFactsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-declmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_OnAPopulatedDatabase_IsAdditive_WithSafeDefaults()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddDeclaredFacts (looked up dynamically so an
            // integrator re-sequence does not break the test).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddDeclaredFacts", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Pre-migration shapes, seeded as SQL (the current model's app_settings has the new columns).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO libraries (Id, PublicId, DisplayName, RootPath, CaseComparisonPolicy, State, CatalogRevision, CreatedAt, DefaultReaderMode) " +
                "VALUES (1, 'lib1', 'Lib', '/synthetic/lib', 'ordinal', 'active', 3, 0, 1), " +
                "(2, 'lib2', 'Lib Two', '/synthetic/lib2', 'ordinal', 'active', 1, 0, NULL)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO app_settings (Id, UpdateCheckEnabled, BackupRetentionCount, MetadataEnabled, MetadataAutoMatchEnabled, MetadataDailyBudget) " +
                "VALUES (1, 1, 9, 1, 1, 1234)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog_nodes (Id, PublicId, LibraryId, Kind, DisplayName, RelativePath, PathKey, SortKey, Availability, LastSeenScanRevision, CreatedAt) " +
                "VALUES (7, 'node7', 1, 0, 'Shelf', 'Shelf', 'shelf', '0shelf', 0, 1, 0), " +
                "(8, 'node8', 1, 0, 'Series', 'Shelf/Series', 'shelf/series', '0series', 0, 1, 0), " +
                "(9, 'node9', 2, 0, 'Other', 'Other', 'other', '0other', 0, 1, 0)");
            await db.Database.ExecuteSqlRawAsync("UPDATE catalog_nodes SET ParentId = 7 WHERE Id = 8");
        }

        await using (var db = NewContext(dbPath))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, path =>
            {
                File.WriteAllText(path, "snapshot");
                return Task.FromResult(true);
            });
        }

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            // Existing settings row: untouched values, the two new columns at their defaults.
            var settings = await db.AppSettings.SingleAsync();
            Assert.True(settings.UpdateCheckEnabled);
            Assert.Equal(9, settings.BackupRetentionCount);
            Assert.True(settings.MetadataEnabled);
            Assert.True(settings.MetadataAutoMatchEnabled);
            Assert.Equal(1234, settings.MetadataDailyBudget);
            Assert.True(settings.MetadataCoverCompareEnabled);
            Assert.Null(settings.MetadataProvidersJson);

            // OFF is stored as written (no DB default overriding false); the allowlist JSON round-trips.
            settings.MetadataCoverCompareEnabled = false;
            settings.MetadataProvidersJson = "{\"removed\":[\"example\"]}";

            var now = DateTimeOffset.UtcNow;
            db.DeclaredFacts.AddRange(
                new DeclaredFactEntity { LibraryId = 1, NodeId = null, Key = "type", Value = "manga", CreatedAt = now, UpdatedAt = now },
                new DeclaredFactEntity { LibraryId = 1, NodeId = 7, Key = "type", Value = "manhwa", CreatedAt = now, UpdatedAt = now },
                new DeclaredFactEntity { LibraryId = 1, NodeId = 8, Key = "creator", Value = "Some Author", Role = "artist", CreatedAt = now, UpdatedAt = now },
                new DeclaredFactEntity { LibraryId = 2, NodeId = 9, Key = "creator", Value = "Other Author", CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var settings = await db.AppSettings.SingleAsync();
            Assert.False(settings.MetadataCoverCompareEnabled);
            Assert.Equal("{\"removed\":[\"example\"]}", settings.MetadataProvidersJson);
            Assert.True(new AppSettingsEntity().MetadataCoverCompareEnabled);

            var artist = await db.DeclaredFacts.SingleAsync(f => f.NodeId == 8);
            Assert.Equal(("creator", "Some Author", "artist"), (artist.Key, artist.Value, artist.Role));

            // A node delete cascades its own rows only; a library delete takes the rest of its rows.
            await db.CatalogNodes.Where(n => n.Id == 8).ExecuteDeleteAsync();
            Assert.Equal(3, await db.DeclaredFacts.CountAsync());
            await db.Libraries.Where(l => l.Id == 1).ExecuteDeleteAsync();
            Assert.Equal(9, (await db.DeclaredFacts.SingleAsync()).NodeId);
        }
    }
}
