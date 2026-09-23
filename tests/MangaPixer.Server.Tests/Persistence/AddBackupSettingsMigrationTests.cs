namespace com.lifepixer.mangapixer.Server.Tests.Persistence;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

/// <summary>
/// Service-with-DB tests for the AddBackupSettings migration (1.22.0): it is
/// purely additive on a pre-existing (1.21-era) database, leaves every new
/// column null so behaviour is unchanged, and the pre-migration safety
/// snapshot it triggers prunes older pre-migration snapshots to the newest 3.
/// </summary>
public sealed class AddBackupSettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public AddBackupSettingsMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mangapixer-bkmig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static MangaPixerDbContext NewContext(string dbPath) => new(
        new DbContextOptionsBuilder<MangaPixerDbContext>().ConfigureSqlite(dbPath).Options);

    [Fact]
    public async Task Migration_IsAdditive_AndPrunesPreMigrationSnapshotsToThree()
    {
        var dbPath = Path.Combine(_dir, "mangapixer.db");
        await using (var db = NewContext(dbPath))
        {
            // Everything up to the migration BEFORE AddBackupSettings (looked up
            // dynamically so an integrator re-sequence does not break the test).
            var all = db.Database.GetMigrations().ToList();
            var index = all.FindIndex(m => m.EndsWith("_AddBackupSettings", StringComparison.Ordinal));
            Assert.True(index > 0);
            await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO app_settings (Id, UpdateCheckEnabled) VALUES (1, 1);");
            await db.Database.ExecuteSqlRawAsync(
                @"INSERT INTO users (PublicId, UserName, NormalizedUserName, PasswordHash, SecurityStamp,
                    IsActive, IsAdmin, ForcePasswordChange, AccessFailedCount, LockoutEnabled, CreatedAt)
                  VALUES ('u_pub_1','admin','ADMIN','hash','stamp', 1, 1, 0, 0, 1, 0)");
        }

        // Four older pre-migration snapshots plus an unrelated file.
        var backups = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(backups);
        foreach (var ts in new[] { "20250101-000000", "20250201-000000", "20250301-000000", "20250401-000000" })
            await File.WriteAllTextAsync(Path.Combine(backups, $"pre-migration-{ts}.db"), "old");
        await File.WriteAllTextAsync(Path.Combine(backups, "pre-migration-keep-me.db"), "not generated");

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

        await using (var db = NewContext(dbPath))
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var row = await db.AppSettings.SingleAsync();
            Assert.True(row.UpdateCheckEnabled);
            Assert.Null(row.BackupsEnabled);
            Assert.Null(row.BackupIntervalHours);
            Assert.Null(row.BackupRetentionCount);
            Assert.Null(row.BackupLocation);
            Assert.Null(row.BackupLocationMarkerId);

            // No behaviour change: the effective settings are the defaults.
            var effective = BackupSettingsResolver.Compute(new RotatingBackupOptions { SafetyBackupDirectory = backups }, row);
            Assert.True(effective.Enabled);
            Assert.Equal(24, effective.IntervalHours);
            Assert.Equal(7, effective.RetentionCount);
            Assert.Equal(EffectiveBackupSettings.KindDefault, effective.LocationKind);
        }

        Assert.NotNull(taken);
        var remaining = Directory.EnumerateFiles(backups, "pre-migration-*.db").Select(Path.GetFileName).ToList();
        Assert.Contains(taken, remaining);
        Assert.Contains("pre-migration-20250401-000000.db", remaining);
        Assert.Contains("pre-migration-20250301-000000.db", remaining);
        Assert.DoesNotContain("pre-migration-20250201-000000.db", remaining);
        Assert.DoesNotContain("pre-migration-20250101-000000.db", remaining);
        Assert.Contains("pre-migration-keep-me.db", remaining);
        Assert.Equal(4, remaining.Count);
    }
}
