namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddMetadataFoundations migration (1.24.0, the cycle's
/// only migration): on a pre-existing (1.23-era) database with a library, an archive
/// and a settings row it is purely additive - existing rows survive, both toggles
/// default to "fetch off / show on", the budget is unset (5000 applies), and the four
/// new tables accept rows that cascade with their node.
/// </summary>
public sealed class AddMetadataFoundationsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddMetadataFoundationsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-metamig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_OnAnExistingDatabase_IsAdditive_WithSafeDefaults()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddMetadataFoundations", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Pre-migration shapes of `libraries` / `app_settings`: seeded as SQL.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO libraries (Id, PublicId, DisplayName, RootPath, CaseComparisonPolicy, State, CatalogRevision, CreatedAt, DefaultReaderMode) " +
                "VALUES (1, 'lib1', 'Lib', '/synthetic/lib', 'ordinal', 'active', 3, 0, 1)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO app_settings (Id, UpdateCheckEnabled, BackupRetentionCount) VALUES (1, 1, 9)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO catalog_nodes (Id, PublicId, LibraryId, Kind, DisplayName, RelativePath, PathKey, SortKey, Availability, LastSeenScanRevision, CreatedAt) " +
                "VALUES (7, 'node7', 1, 1, 'vol01', 'vol01.cbz', 'vol01.cbz', '1vol01', 0, 1, 0)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO archive_items (NodeId, ArchiveFormat, ByteLength, ModificationTicks, ContentVersion, AnalysisState, PageCount, ThumbnailState) " +
                "VALUES (7, 1, 1024, 1, 3, 0, 12, 0)");
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

            var lib = await db.Libraries.SingleAsync();
            Assert.Equal(3, lib.CatalogRevision);
            Assert.Equal(1, lib.DefaultReaderMode);
            Assert.False(lib.MetadataEnabled);
            Assert.False(lib.MetadataSeriesInfoHidden);
            Assert.Null(lib.MetadataPrecedence);

            var settings = await db.AppSettings.SingleAsync();
            Assert.True(settings.UpdateCheckEnabled);
            Assert.Equal(9, settings.BackupRetentionCount);
            Assert.False(settings.MetadataEnabled);
            Assert.False(settings.MetadataSeriesInfoHidden);
            Assert.Null(settings.MetadataDailyBudget);
            Assert.Null(settings.MetadataConsentVersion);
            Assert.Equal(0, settings.MetadataBudgetUsed);

            var record = new MetadataRecordEntity
            {
                PublicId = "rec1",
                Provider = "mangaupdates",
                ExternalId = "1",
                Title = "T",
                CategoriesJson = "[{\"name\":\"Webtoon\",\"votes\":64}]",
                Origin = 1,
                Format = 0,
                Webtoon = true,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = 7, LibraryId = 1, State = 0, RecordId = record.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.EmbeddedMetadata.Add(new EmbeddedMetadataEntity { NodeId = 7, ContentVersion = 3, State = 1, Series = "S", ReadAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();

            // (Provider, ExternalId) is unique.
            db.MetadataRecords.Add(new MetadataRecordEntity { PublicId = "rec2", Provider = "mangaupdates", ExternalId = "1", Title = "Dup", FetchedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var db = NewContext(dbPath))
        {
            // A record cannot be deleted under a live link (RESTRICT)...
            await Assert.ThrowsAnyAsync<Exception>(() => db.MetadataRecords.ExecuteDeleteAsync());
            // ...but node deletion cascades the link and the ComicInfo row.
            await db.CatalogNodes.Where(n => n.Id == 7).ExecuteDeleteAsync();
            Assert.False(await db.NodeSeriesLinks.AnyAsync());
            Assert.False(await db.EmbeddedMetadata.AnyAsync());
            Assert.Equal(1, await db.MetadataRecords.CountAsync());
        }
    }
}
