namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddHomeRowOrder migration (1.54.0, migration #62, additive): a database at migration #61
/// (AddCycle153Settings, an instance of 1.53.0) with preferences rows keeps every row and value, and the new column is null for each
/// (= the default Home order). Through the model, an order round-trips and a new row starts null.
/// </summary>
public sealed class AddHomeRowOrderMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddHomeRowOrderMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-homeroworder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    private static UserEntity User(long id) => new()
    {
        Id = id,
        PublicId = $"u{id}",
        UserName = $"user{id}",
        NormalizedUserName = $"USER{id}",
        PasswordHash = "hash",
        SecurityStamp = "stamp",
        IsActive = true,
        IsAdmin = id == 1,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Migration_AfterMigration61_AddsANullHomeRowOrder_AndKeepsEveryRow()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddHomeRowOrder", StringComparison.Ordinal));
            Assert.Equal(61, index); // migration #62: the upgrade from 1.53.0 (#61)
            Assert.EndsWith("_AddCycle153Settings", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            for (var id = 1L; id <= 3; id++)
                await LegacySchemaSeed.InsertAsync(db, User(id));
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity
            {
                UserId = 1,
                LibraryViewMode = "list",
                ShowFavoritesHomeRow = true,
                ShowRecommendationsHomeRow = false,
                HomeStacksPerLibrary = 7,
            });
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity { UserId = 2 });
        }

        string? taken = null;
        await using (var db = NewContext(dbPath))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, path =>
            {
                taken = Path.GetFileName(path);
                File.WriteAllText(path, "snapshot");
                return Task.FromResult(true);
            });
        }

        Assert.NotNull(taken); // data present + a pending migration: the pre-migration safety snapshot is taken
        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var prefs = await db.ReaderPreferences.OrderBy(p => p.UserId).ToListAsync();
            Assert.Equal(2, prefs.Count);
            Assert.All(prefs, p => Assert.Null(p.HomeRowOrder));
            Assert.Equal("list", prefs[0].LibraryViewMode);
            Assert.True(prefs[0].ShowFavoritesHomeRow);
            Assert.False(prefs[0].ShowRecommendationsHomeRow);
            Assert.Equal(7, prefs[0].HomeStacksPerLibrary);

            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = 3 });
            prefs[1].HomeRowOrder = "libraries,continue,recommended,favorites,recent";
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var orders = await db.ReaderPreferences.ToDictionaryAsync(p => p.UserId, p => p.HomeRowOrder);
            Assert.Null(orders[1]);
            Assert.Equal("libraries,continue,recommended,favorites,recent", orders[2]);
            Assert.Null(orders[3]);
        }
    }
}
