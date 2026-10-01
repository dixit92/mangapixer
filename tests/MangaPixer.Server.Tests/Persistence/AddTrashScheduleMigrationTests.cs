namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddTrashSchedule migration (1.31.0): on an existing settings row, automatic cleaning starts
/// OFF (the first automatic purge after the upgrade needs the admin's approval), no run is recorded, and the retention chosen
/// before is kept.
/// </summary>
public sealed class AddTrashScheduleMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddTrashScheduleMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-trashmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_LeavesAutomaticCleaningOff_AndKeepsTheRetention()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddTrashSchedule", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            // Pre-migration shape, seeded as SQL (the current model's app_settings has the new columns).
            await db.Database.ExecuteSqlRawAsync("INSERT INTO app_settings (Id, UpdateCheckEnabled, TrashRetentionDays) VALUES (1, 0, 90);");
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
            var row = await db.AppSettings.AsNoTracking().SingleAsync();
            Assert.False(row.TrashAutoCleanEnabled);
            Assert.Null(row.TrashAutoCleanEnabledAt);
            Assert.Null(row.TrashLastAutoRunAt);
            Assert.Null(row.TrashLastEmptiedAt);
            Assert.Null(row.BundlesLastCleanedAt);
            Assert.Equal(90, row.TrashRetentionDays);
        }
    }
}
