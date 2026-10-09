namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddMetadataAuthors migration (1.38.0, artists' other names): additive - an existing database gains an
/// empty <c>metadata_authors</c> table and keeps its stored series records; one row per provider + author id.
/// </summary>
public sealed class AddMetadataAuthorsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddMetadataAuthorsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-authmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AddsAnEmptyAuthorTable_KeepsRecords_AndOneRowPerAuthorId()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddMetadataAuthors", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            // A stored series record as the previous migration left it (columns a later migration adds are skipped).
            await LegacySchemaSeed.InsertAsync(db, new MetadataRecordEntity
            {
                PublicId = "r1",
                Provider = "mangaupdates",
                ExternalId = "51239621230",
                Title = "Berserk",
                CreatorsJson = """[{"name":"MIURA Kentaro","role":"author","providerId":"22635311083"}]""",
                FetchedAt = DateTimeOffset.UnixEpoch,
            });
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
            Assert.Empty(await db.MetadataAuthors.ToListAsync());
            Assert.Equal("Berserk", (await db.MetadataRecords.SingleAsync()).Title);

            var fetchedAt = new DateTimeOffset(2026, 10, 9, 6, 0, 0, TimeSpan.FromHours(-4));
            db.MetadataAuthors.Add(new MetadataAuthorEntity
            {
                Provider = "mangaupdates",
                ExternalId = "22635311083",
                Name = "MIURA Kentaro",
                OtherNamesJson = """["Kentaro Miura"]""",
                FetchedAt = fetchedAt,
                Status = 0,
            });
            await db.SaveChangesAsync();
            Assert.Equal(fetchedAt, (await db.MetadataAuthors.AsNoTracking().SingleAsync()).FetchedAt);

            db.MetadataAuthors.Add(new MetadataAuthorEntity { Provider = "mangaupdates", ExternalId = "22635311083", FetchedAt = fetchedAt, Status = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
