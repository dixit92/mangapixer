namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddSeriesInfoOnHover migration (1.27.0): applied to a
/// populated pre-1.27 database, it is purely additive - two users' existing preference
/// rows keep every value and read the new per-user option as ON (the owner's default),
/// and a new row written through the model starts ON too while OFF still round-trips.
/// </summary>
public sealed class AddSeriesInfoOnHoverMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddSeriesInfoOnHoverMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-hovermig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    private static string UserSql(long id) =>
        "INSERT INTO users (Id, PublicId, UserName, NormalizedUserName, PasswordHash, SecurityStamp, IsAdmin, IsActive, " +
        "IsPendingActivation, ActivationTokenConsumed, ForcePasswordChange, LockoutEnabled, AccessFailedCount, CreatedAt) " +
        $"VALUES ({id}, 'user{id}', 'user{id}', 'USER{id}', 'hash', 'stamp', 0, 1, 0, 0, 0, 0, 0, 0)";

    [Fact]
    public async Task Migration_OnAPopulatedDatabase_IsAdditive_AndExistingRowsReadAsOn()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddSeriesInfoOnHover (looked up
            // dynamically so an integrator re-sequence does not break the test).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddSeriesInfoOnHover", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Seeded as SQL: the current model's reader_preferences has the new column.
            foreach (var id in new long[] { 1, 2, 3 })
                await db.Database.ExecuteSqlRawAsync(UserSql(id));
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO reader_preferences (Id, UserId, DefaultReaderMode, PreferDoubleSpread, ReducedMotion, " +
                "AlwaysOpenReadFromStart, LibraryViewMode, LibraryGridDensity, LibrarySort, LibraryDirection, LibraryCardSize, " +
                "LibraryPageSize, HomeRecentWindowDays, ListColumns, ShowFavoritesHomeRow, FavoritesSearchProminence) VALUES " +
                "(1, 1, 3, 0, 0, 1, 'list', 'comfortable', 'recentlyAdded', 'desc', '180', 100, 14, 3, 1, 1), " +
                "(2, 2, 0, 0, 0, 0, 'grid', 'comfortable', 'name', '', '', 0, 0, 0, 0, 0)");
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

            var rows = await db.ReaderPreferences.OrderBy(p => p.UserId).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(r.SeriesInfoOnHover));

            // Existing values untouched.
            var first = rows[0];
            Assert.Equal(3, first.DefaultReaderMode);
            Assert.True(first.AlwaysOpenReadFromStart);
            Assert.Equal("list", first.LibraryViewMode);
            Assert.Equal("recentlyAdded", first.LibrarySort);
            Assert.Equal("desc", first.LibraryDirection);
            Assert.Equal("180", first.LibraryCardSize);
            Assert.Equal(100, first.LibraryPageSize);
            Assert.Equal(14, first.HomeRecentWindowDays);
            Assert.Equal(3, first.ListColumns);
            Assert.True(first.ShowFavoritesHomeRow);
            Assert.True(first.FavoritesSearchProminence);

            // A new row starts ON; OFF is stored as written (no DB default overriding false).
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = 3 });
            rows[1].SeriesInfoOnHover = false;
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var byUser = await db.ReaderPreferences.ToDictionaryAsync(p => p.UserId, p => p.SeriesInfoOnHover);
            Assert.True(byUser[1]);
            Assert.False(byUser[2]);
            Assert.True(byUser[3]);
        }
    }
}
