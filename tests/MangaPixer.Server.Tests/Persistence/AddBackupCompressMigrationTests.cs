namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB test for the AddBackupCompress migration (1.50.0, migration #59): a database that already applied the migration
/// before it (migration 58, an instance of 1.49.0) keeps its settings row and its backup settings, the new nullable column reads as
/// "not set" (so compression stays on, from the default), and the model reads and writes it. The pre-migration safety snapshot is taken.
/// </summary>
public sealed class AddBackupCompressMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddBackupCompressMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-bkcompress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_AfterMigration58_AddsTheNullableColumn_AndKeepsTheBackupSettings()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddBackupCompress", StringComparison.Ordinal));
            Assert.True(index > 0);
            Assert.Equal(58, index); // the previous migration is #58: this is the upgrade from 1.49.0
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO app_settings (Id, UpdateCheckEnabled, BackupsEnabled, BackupIntervalHours, BackupRetentionCount, BackupHour) " +
                "VALUES (1, 1, 0, 12.0, 3, 4);");
        }

        string? taken = null;
        await using (var db = NewContext(dbPath))
        {
            await DatabaseInitialization.MigrateToLatestAsync(db, _dir, async path =>
            {
                taken = Path.GetFileName(path);
                await File.WriteAllTextAsync(path, "snapshot");
                return true;
            });
        }

        Assert.NotNull(taken);
        Assert.Matches(@"^pre-migration-\d{8}-\d{6}(-[0-9a-f]{4})?\.db$", taken!);
        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var row = await db.AppSettings.SingleAsync();
            Assert.True(row.UpdateCheckEnabled);
            Assert.False(row.BackupsEnabled);
            Assert.Equal(12.0, row.BackupIntervalHours);
            Assert.Equal(3, row.BackupRetentionCount);
            Assert.Equal(4, row.BackupHour);
            Assert.Null(row.BackupCompress);

            // No behaviour change: compression stays on, and says it comes from the default.
            var effective = BackupSettingsResolver.Compute(new RotatingBackupOptions { SafetyBackupDirectory = Path.Combine(_dir, "backups") }, row);
            Assert.True(effective.Compress);
            Assert.Equal(BackupSettingSources.Default, effective.CompressSource);
            Assert.Equal(3, effective.RetentionCount);

            row.BackupCompress = false;
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbPath))
        {
            var row = await db.AppSettings.SingleAsync();
            Assert.False(row.BackupCompress);
            var effective = BackupSettingsResolver.Compute(new RotatingBackupOptions { SafetyBackupDirectory = Path.Combine(_dir, "backups") }, row);
            Assert.False(effective.Compress);
            Assert.Equal(BackupSettingSources.Settings, effective.CompressSource);
        }
    }

    [Fact]
    public async Task MigrationToLatest_TwiceInOneSecond_TakesTwoPreMigrationSnapshots_NotAFailure()
    {
        // The pre-migration snapshot is named by the second; a second one in the same second used to refuse (VACUUM INTO needs a
        // new file) and abort the upgrade. Pre-seed the plain name for "now" is racy, so seed the next call's name directly.
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        var now = DateTime.UtcNow;
        var plain = SafetySnapshotPruner.UniqueName(backups, "pre-migration", now);
        await File.WriteAllTextAsync(Path.Combine(backups, plain), "first");

        var second = SafetySnapshotPruner.UniqueName(backups, "pre-migration", now);

        Assert.NotEqual(plain, second);
        Assert.Matches(SafetySnapshotPruner.PreMigrationPattern(), second);
    }
}
