namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddPerformanceSettings migration (1.42.0): purely additive - the existing settings row keeps
/// its values and reads every performance setting as unset (configuration, then the built-in defaults apply).
/// </summary>
public sealed class AddPerformanceSettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddPerformanceSettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-perfmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_OnAnExistingSettingsRow_IsAdditive_AndLeavesThePerformanceSettingsUnset()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddPerformanceSettings", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO app_settings (Id, UpdateCheckEnabled, TrashAutomaticHour) VALUES (1, 1, 2);");
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
            Assert.True(row.UpdateCheckEnabled);
            Assert.Equal(2, row.TrashAutomaticHour);
            Assert.Null(row.PerformanceMediaWorkers);
            Assert.Null(row.PerformancePageCacheBytes);
            Assert.Null(row.PerformanceBackgroundWhileReading);
        }
    }
}
