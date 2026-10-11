namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddHomeStacksPerLibrary migration (1.41.0): applied to a populated 1.40 database it is purely
/// additive - existing preference rows keep every value and read 0 stacks (unset), which the home row turns into the old fixed 12.
/// </summary>
public sealed class AddHomeStacksPerLibraryMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddHomeStacksPerLibraryMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-stacksmig-" + Guid.NewGuid().ToString("N"));
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
    public async Task Migration_OnAPopulatedDatabase_IsAdditive_AndExistingUsersKeepTwelveStacks()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddHomeStacksPerLibrary (looked up dynamically so a re-sequence does not break it).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddHomeStacksPerLibrary", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            foreach (var id in new long[] { 1, 2 })
                await db.Database.ExecuteSqlRawAsync(UserSql(id));
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity
            {
                UserId = 1,
                LibraryViewMode = "list",
                HomeRecentWindowDays = 14,
                ListColumns = 3,
                Theme = "sepia",
                Accent = "teal",
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

            var row = Assert.Single(await db.ReaderPreferences.ToListAsync());
            Assert.Equal(0, row.HomeStacksPerLibrary);
            Assert.Equal("list", row.LibraryViewMode);
            Assert.Equal(14, row.HomeRecentWindowDays);
            Assert.Equal(3, row.ListColumns);
            Assert.Equal(("sepia", "teal"), (row.Theme, row.Accent));

            // Through the service: the stored 0 reads back as unset, a preset is stored and read back, and a user who never
            // had a row gets one with only the library preferences set.
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            var before = await service.GetLibraryPreferencesAsync(1);
            Assert.Equal(0, before.HomeStacksPerLibrary);
            await service.SetLibraryPreferencesAsync(1, before with { HomeStacksPerLibrary = 24 });
            await service.SetLibraryPreferencesAsync(2, new() { HomeStacksPerLibrary = 6 });
        }

        await using (var db = NewContext(dbPath))
        {
            var byUser = await db.ReaderPreferences.ToDictionaryAsync(p => p.UserId, p => p.HomeStacksPerLibrary);
            Assert.Equal(24, byUser[1]);
            Assert.Equal(6, byUser[2]);
            var first = await db.ReaderPreferences.SingleAsync(p => p.UserId == 1);
            Assert.Equal(("sepia", "teal", 14), (first.Theme, first.Accent, first.HomeRecentWindowDays)); // the library-prefs write kept them
        }
    }
}
