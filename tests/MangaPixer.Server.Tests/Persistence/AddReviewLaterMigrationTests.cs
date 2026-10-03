namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddReviewLater migration (1.33.0): an existing link row (here one waiting in Needs review) keeps
/// everything it had and is not set aside; the new column then stores and returns a time.
/// </summary>
public sealed class AddReviewLaterMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddReviewLaterMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-latermig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_LeavesExistingLinksAsTheyWere_NotSetAside()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddReviewLater", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 1, PublicId = "lib1", DisplayName = "A", RootPath = "/synthetic/a", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 10,
                PublicId = "n10",
                LibraryId = 1,
                Kind = 0,
                DisplayName = "Review Saga",
                RelativePath = "Review Saga",
                PathKey = "review saga",
                SortKey = "0review saga",
                CreatedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new NodeSeriesLinkEntity
            {
                Id = 3,
                NodeId = 10,
                LibraryId = 1,
                State = 2,
                MatchMethod = 3,
                MatchScore = 0.8,
                CreatedAt = now,
                UpdatedAt = now,
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
            var link = await db.NodeSeriesLinks.SingleAsync();
            Assert.Equal((3L, 10L, 2, 3, 0.8, now), (link.Id, link.NodeId, link.State, link.MatchMethod!.Value, link.MatchScore!.Value, link.UpdatedAt));
            Assert.Null(link.LaterAt);

            link.LaterAt = now.AddMinutes(5);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
            Assert.Equal(now.AddMinutes(5), (await db.NodeSeriesLinks.AsNoTracking().SingleAsync()).LaterAt);
    }
}
