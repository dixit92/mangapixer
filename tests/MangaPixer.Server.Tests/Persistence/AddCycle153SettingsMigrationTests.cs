namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddCycle153Settings migration (1.53.0 step 0, migration #61, additive): a database at migration #60
/// (AddUserRatings, an instance of 1.52.0) with rows in <c>users</c>, <c>reader_preferences</c>, <c>app_settings</c> and
/// <c>tracker_accounts</c> keeps every row and value, and the new columns land with their defaults - <c>ShareRatings</c> and
/// <c>ShowRecommendationsHomeRow</c> ON for every existing preferences row, <c>RatingSharingEnabled</c> and <c>SyncScores</c> off,
/// <c>LastSeenVersion</c> null. Through the model, a new row starts with the same defaults and every value round-trips (an OFF stays
/// OFF - no database default overrides what is written).
/// </summary>
public sealed class AddCycle153SettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddCycle153SettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-cycle153-" + Guid.NewGuid().ToString("N"));
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
    public async Task Migration_AfterMigration60_AddsTheFiveColumnsWithTheirDefaults_AndKeepsEveryRow()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddCycle153Settings", StringComparison.Ordinal));
            Assert.Equal(60, index); // migration #61: the upgrade from 1.52.0 (#60)
            Assert.EndsWith("_AddUserRatings", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Seeded with only the columns the #60 schema has (LegacySchemaSeed): the current model names the new columns.
            for (var id = 1L; id <= 3; id++)
                await LegacySchemaSeed.InsertAsync(db, User(id));
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity
            {
                UserId = 1,
                DefaultReaderMode = 3,
                LibraryViewMode = "list",
                LibrarySort = "recentlyAdded",
                ShowFavoritesHomeRow = true,
                SeriesInfoOnHover = false,
                Theme = "sepia",
            });
            await LegacySchemaSeed.InsertAsync(db, new ReaderPreferencesEntity { UserId = 2 });
            await LegacySchemaSeed.InsertAsync(db, new AppSettingsEntity { UpdateCheckEnabled = true, BackupRetentionCount = 4 });
            await LegacySchemaSeed.InsertAsync(db, new TrackerAccountEntity
            {
                UserId = 1,
                Tracker = "mangaupdates",
                DisplayName = "Reader One",
                State = 1,
                ConsentVersion = 1,
                ConsentAt = DateTimeOffset.UnixEpoch,
                AddStartedSeries = false,
                CreatedAt = DateTimeOffset.UnixEpoch,
                RequestsUsed = 12,
            });
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

            // users: every row kept, LastSeenVersion null.
            var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
            Assert.Equal(new long[] { 1, 2, 3 }, users.Select(u => u.Id));
            Assert.All(users, u => Assert.Null(u.LastSeenVersion));
            Assert.True(users[0].IsAdmin);

            // reader_preferences: both new options ON for every existing row; the old values untouched.
            var prefs = await db.ReaderPreferences.OrderBy(p => p.UserId).ToListAsync();
            Assert.Equal(2, prefs.Count);
            Assert.All(prefs, p =>
            {
                Assert.True(p.ShareRatings);
                Assert.True(p.ShowRecommendationsHomeRow);
            });
            Assert.Equal(3, prefs[0].DefaultReaderMode);
            Assert.Equal("list", prefs[0].LibraryViewMode);
            Assert.Equal("recentlyAdded", prefs[0].LibrarySort);
            Assert.True(prefs[0].ShowFavoritesHomeRow);
            Assert.False(prefs[0].SeriesInfoOnHover);
            Assert.Equal("sepia", prefs[0].Theme);

            // app_settings: the server-wide sharing switch is off; the row's settings kept.
            var settings = await db.AppSettings.SingleAsync();
            Assert.False(settings.RatingSharingEnabled);
            Assert.True(settings.UpdateCheckEnabled);
            Assert.Equal(4, settings.BackupRetentionCount);

            // tracker_accounts: score sync off; the account kept.
            var account = await db.TrackerAccounts.SingleAsync();
            Assert.False(account.SyncScores);
            Assert.Equal("mangaupdates", account.Tracker);
            Assert.Equal("Reader One", account.DisplayName);
            Assert.False(account.AddStartedSeries);
            Assert.Equal(12, account.RequestsUsed);

            // Writes through the model: a new preferences row starts with both ON; OFF / ON / a version round-trip as written.
            db.ReaderPreferences.Add(new ReaderPreferencesEntity { UserId = 3 });
            prefs[1].ShareRatings = false;
            prefs[1].ShowRecommendationsHomeRow = false;
            settings.RatingSharingEnabled = true;
            account.SyncScores = true;
            users[1].LastSeenVersion = "1.53.0";
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var prefs = await db.ReaderPreferences.ToDictionaryAsync(p => p.UserId, p => (p.ShareRatings, p.ShowRecommendationsHomeRow));
            Assert.Equal((true, true), prefs[1]);
            Assert.Equal((false, false), prefs[2]);
            Assert.Equal((true, true), prefs[3]);
            Assert.True((await db.AppSettings.SingleAsync()).RatingSharingEnabled);
            Assert.True((await db.TrackerAccounts.SingleAsync()).SyncScores);
            Assert.Equal("1.53.0", (await db.Users.SingleAsync(u => u.Id == 2)).LastSeenVersion);
            Assert.Null((await db.Users.SingleAsync(u => u.Id == 1)).LastSeenVersion);
        }
    }
}
