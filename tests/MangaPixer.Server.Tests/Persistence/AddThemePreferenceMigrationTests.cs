namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddThemePreference migration (1.40.0): applied to a populated pre-1.40 database it is purely
/// additive - existing preference rows keep every value and get no theme / accent (null = the default, so every existing user
/// keeps Dark / violet), and the new columns round-trip what a user chooses.
/// </summary>
public sealed class AddThemePreferenceMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddThemePreferenceMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-thememig-" + Guid.NewGuid().ToString("N"));
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
    public async Task Migration_OnAPopulatedDatabase_IsAdditive_AndExistingUsersKeepTheDefaults()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddThemePreference (looked up dynamically so a re-sequence does not break it).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddThemePreference", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            foreach (var id in new long[] { 1, 2, 3 })
                await db.Database.ExecuteSqlRawAsync(UserSql(id));
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity
            {
                UserId = 1,
                DefaultReaderMode = 1,
                AlwaysOpenReadFromStart = true,
                PreferredBackground = "black",
                LibraryViewMode = "list",
                LibrarySort = "recentlyAdded",
                LibraryCardSize = "180",
                HomeRecentWindowDays = 14,
                SeriesInfoOnHover = false,
                StackViewMode = "card",
            });
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity { UserId = 2 });
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
            Assert.All(rows, r =>
            {
                Assert.Null(r.Theme);
                Assert.Null(r.Accent);
            });

            // Existing values untouched.
            var first = rows[0];
            Assert.Equal(1, first.DefaultReaderMode);
            Assert.True(first.AlwaysOpenReadFromStart);
            Assert.Equal("black", first.PreferredBackground);
            Assert.Equal("list", first.LibraryViewMode);
            Assert.Equal("recentlyAdded", first.LibrarySort);
            Assert.Equal("180", first.LibraryCardSize);
            Assert.Equal(14, first.HomeRecentWindowDays);
            Assert.False(first.SeriesInfoOnHover);
            Assert.Equal("card", first.StackViewMode);

            // Through the service: an existing user reads Dark / violet; a choice is stored and read back; a third user who
            // never had a row gets a new row with only the appearance set.
            var service = new ReadingStateService(db, new LibraryAuthorizationService(db));
            var before = await service.GetPreferencesAsync(1);
            Assert.Equal(("dark", "violet"), (before.Theme, before.Accent));
            Assert.Equal(ReaderModeOf(1), before.DefaultReaderMode);

            Assert.True(await service.SetAppearanceAsync(2, new AppearancePreferencesDto { Theme = "sepia", Accent = "teal" }));
            Assert.True(await service.SetAppearanceAsync(3, new AppearancePreferencesDto { Theme = "light" }));
            Assert.False(await service.SetAppearanceAsync(1, new AppearancePreferencesDto { Theme = "neon" }));
        }

        await using (var db = NewContext(dbPath))
        {
            var byUser = await db.ReaderPreferences.ToDictionaryAsync(p => p.UserId, p => (p.Theme, p.Accent));
            Assert.Equal((null, null), byUser[1]);
            Assert.Equal(("sepia", "teal"), byUser[2]);
            Assert.Equal(("light", null), byUser[3]);
        }
    }

    private static com.lifepixer.mangapixer.Core.Reading.ReaderMode ReaderModeOf(int value) =>
        (com.lifepixer.mangapixer.Core.Reading.ReaderMode)value;
}
