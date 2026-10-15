namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddTrackingPage migration (1.43.0): a database that already applied AddTrackerSync (an instance that
/// ran an earlier 1.43.0 build and connected a tracker) gains the Tracking page columns - the stored tracker settings and the user's
/// entry keep every value, the new columns read as automatic / none (DailyRequestCap null = the default, DatesPending false), and the
/// model reads and writes both tables. An applied migration never re-runs, so these columns cannot live inside AddTrackerSync.
/// </summary>
public sealed class AddTrackingPageMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddTrackingPageMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-trackingmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AfterAnAppliedAddTrackerSync_AddsTheTrackingColumns_AndKeepsEveryRow()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddTrackingPage - AddTrackerSync as an earlier 1.43.0 build applied it.
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddTrackingPage", StringComparison.Ordinal));
            Assert.True(index > 0);
            Assert.EndsWith("_AddTrackerSync", all[index - 1], StringComparison.Ordinal);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

            // Seeded as SQL in the old shape (one statement batch, so the pragma covers the insert: the entry's user and record are not
            // what this test is about).
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO tracker_settings (Id, Tracker, Enabled, ConsentVersion, ConsentAt, ClientId, ClientSecretProtected, RedirectUri, UpdatedAt) " +
                "VALUES (1, 'mal', 1, 1, 100, 'client', 'protected', 'https://review.test/trackers/callback/mal', 200)");
            await db.Database.ExecuteSqlRawAsync(
                "PRAGMA foreign_keys = OFF; " +
                "INSERT INTO tracker_entries (Id, UserId, Tracker, RecordId, RemoteId, RemoteIdSource, State, DontSync, LocalChapters, LocalVolumes, " +
                "RemoteChapters, RemoteVolumes, RemoteStatus, RemoteMissing, RemoteSeenAt, UpdatedAt) " +
                "VALUES (1, 7, 'mal', 42, '1234', 1, 1, 0, 51, 10, 51, 10, 2, 0, 300, 400)");
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

            var settings = await db.TrackerSettings.SingleAsync();
            Assert.Equal("mal", settings.Tracker);
            Assert.True(settings.Enabled);
            Assert.Equal("client", settings.ClientId);
            Assert.Equal("protected", settings.ClientSecretProtected);
            Assert.Null(settings.DailyRequestCap);

            var entry = await db.TrackerEntries.SingleAsync();
            Assert.Equal(7, entry.UserId);
            Assert.Equal("1234", entry.RemoteId);
            Assert.Equal(51, entry.RemoteChapters);
            Assert.Equal(10, entry.RemoteVolumes);
            Assert.False(entry.DatesPending);
            Assert.Null(entry.AllChapters);
            Assert.Null(entry.RemoteTotalChapters);
            Assert.Null(entry.StartedOnOverride);
            Assert.Null(entry.RemoteFinishedOn);

            settings.DailyRequestCap = 5000;
            entry.DatesPending = true;
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            Assert.Equal(5000, (await db.TrackerSettings.SingleAsync()).DailyRequestCap);
            Assert.True((await db.TrackerEntries.SingleAsync()).DatesPending);
        }
    }
}
