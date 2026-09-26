namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddArchiveSpreadLayout migration (1.23.0): applied to a
/// pre-existing (1.22-era) database holding a library, an archive node and its archive
/// item, it is purely additive - existing rows survive untouched, no layout exists for
/// any archive (the reader keeps its device fallback), and the new table accepts a
/// layout that cascades away with its node.
/// </summary>
public sealed class AddArchiveSpreadLayoutMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddArchiveSpreadLayoutMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-spreadmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_OnAnExistingDatabase_IsAdditive_AndTheTableCascadesWithItsNode()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddArchiveSpreadLayout (looked up
            // dynamically so an integrator re-sequence does not break the test).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddArchiveSpreadLayout", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Nodes and items are seeded through the EF model: no later migration
            // changed those tables, so the current model matches them exactly.
            // The library row goes in as SQL: later migrations (1.24.0 metadata columns)
            // added columns to `libraries`, so the current model no longer matches this
            // pre-migration table.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO libraries (Id, PublicId, DisplayName, RootPath, CaseComparisonPolicy, State, CatalogRevision, CreatedAt) " +
                "VALUES (1, 'lib1', 'Lib', '/synthetic/lib', 'ordinal', 'active', 0, 0)");
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                Id = 7,
                PublicId = "node7",
                LibraryId = 1,
                Kind = 1,
                DisplayName = "vol01.cbz",
                RelativePath = "vol01.cbz",
                PathKey = "vol01.cbz",
                SortKey = "vol01",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = 7,
                ByteLength = 1024,
                ModificationTicks = 1,
                ContentVersion = 3,
                AnalysisState = 0,
                PageCount = 12,
            });
            await db.SaveChangesAsync();
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

            // Existing rows untouched; nothing has a saved layout yet.
            var item = await db.ArchiveItems.SingleAsync(a => a.NodeId == 7);
            Assert.Equal(3, item.ContentVersion);
            Assert.Equal(12, item.PageCount);
            Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());

            db.ArchiveSpreadLayouts.Add(new ArchiveSpreadLayoutEntity
            {
                NodeId = 7,
                ContentVersion = 3,
                SpreadStartsJson = "[1,5]",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var row = await db.ArchiveSpreadLayouts.SingleAsync();
            Assert.Equal("[1,5]", row.SpreadStartsJson);

            await db.CatalogNodes.Where(n => n.Id == 7).ExecuteDeleteAsync();
            Assert.False(await db.ArchiveSpreadLayouts.AnyAsync());
        }
    }
}
