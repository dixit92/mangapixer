namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddUserRatings migration (1.52.0 lane R, migration #60, additive): a database at migration #59
/// (AddBackupCompress, an instance of 1.51.0) with users, catalog nodes and favorites in it gains the empty <c>user_ratings</c> table,
/// keeps every row, and the new table holds its rules - one rating per (user, node), a score of 1-10 only, gone with its node and with
/// its user.
/// </summary>
public sealed class AddUserRatingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddUserRatingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-ratings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AfterMigration59_AddsTheRatingsTable_KeepsEveryRow_AndHoldsItsRules()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        var now = DateTimeOffset.UtcNow;
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddUserRatings", StringComparison.Ordinal));
            Assert.Equal(59, index); // migration #60: the upgrade from 1.51.0 (#59)
            Assert.EndsWith("_AddBackupCompress", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            Assert.False(await TableExistsAsync(db));

            for (var u = 1; u <= 2; u++)
            {
                await LegacySchemaSeed.InsertAsync(db, new UserEntity
                {
                    Id = u,
                    PublicId = $"u{u}",
                    UserName = $"user{u}",
                    NormalizedUserName = $"USER{u}",
                    PasswordHash = "hash",
                    SecurityStamp = "stamp",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                });
            }
            await LegacySchemaSeed.InsertAsync(db, new LibraryEntity { Id = 1, PublicId = "lib1", DisplayName = "A", RootPath = "/synthetic/a", CreatedAt = now });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 10,
                PublicId = "series",
                LibraryId = 1,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = "Series",
                RelativePath = "Series",
                PathKey = "series",
                SortKey = "0series",
                CreatedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new CatalogNodeEntity
            {
                Id = 11,
                PublicId = "vol1",
                LibraryId = 1,
                ParentId = 10,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = "Vol 1.cbz",
                RelativePath = "Series/Vol 1.cbz",
                PathKey = "series/vol 1.cbz",
                SortKey = "1vol 1",
                CreatedAt = now,
            });
            await LegacySchemaSeed.InsertAsync(db, new FavoriteEntity { UserId = 1, CatalogNodeId = 10, CreatedAt = now });
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
            Assert.True(await TableExistsAsync(db));
            Assert.Empty(await db.UserRatings.ToListAsync());
            Assert.Equal(2, await db.Users.CountAsync());
            Assert.Equal(2, await db.CatalogNodes.CountAsync());
            Assert.Equal(1, await db.Favorites.CountAsync());

            db.UserRatings.Add(new UserRatingEntity { UserId = 1, CatalogNodeId = 10, Score = 7, RatedAt = now });
            db.UserRatings.Add(new UserRatingEntity { UserId = 1, CatalogNodeId = 11, Score = 10, RatedAt = now });
            db.UserRatings.Add(new UserRatingEntity { UserId = 2, CatalogNodeId = 10, Score = 1, RatedAt = now });
            await db.SaveChangesAsync();
        }

        // One rating per (user, node).
        await using (var db = NewContext(dbPath))
        {
            db.UserRatings.Add(new UserRatingEntity { UserId = 1, CatalogNodeId = 10, Score = 3, RatedAt = now });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // The score stays 1-10 in the database too (the CHECK constraint), whatever writes it.
        await using (var db = NewContext(dbPath))
        {
            foreach (var bad in new[] { 0, 11, -1 })
            {
                await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlAsync(
                    $"INSERT INTO user_ratings (UserId, CatalogNodeId, Score, RatedAt) VALUES (2, 11, {bad}, 0);"));
            }
            Assert.Equal(3, await db.UserRatings.CountAsync());
        }

        // Cascade: removing the archive removes its rating; deleting a user removes theirs; the other user's rating stays.
        await using (var db = NewContext(dbPath))
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM catalog_nodes WHERE Id = 11;");
            Assert.Equal(2, await db.UserRatings.CountAsync());
            await db.Database.ExecuteSqlRawAsync("DELETE FROM users WHERE Id = 1;");
            var left = await db.UserRatings.SingleAsync();
            Assert.Equal((2L, 10L, 1), (left.UserId, left.CatalogNodeId, left.Score));
        }
    }

    private static async Task<bool> TableExistsAsync(MangaPixerDbContext db) =>
        await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'user_ratings'").SingleAsync() == 1;
}
