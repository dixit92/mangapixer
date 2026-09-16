namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for BackupService — online backup, verification, restore, and
/// session invalidation.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public BackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-backup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, BackupService service)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        // Add a library and user so the backup has data
        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new UserEntity
        {
            PublicId = OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new BackupService(db);
        return (db, service);
    }

    [Fact]
    public async Task Backup_CreatesValidBackupFile()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var backupPath = Path.Combine(_tempDir, "backup.db");
            var result = await service.BackupAsync(backupPath);

            Assert.True(result.Succeeded);
            Assert.True(File.Exists(backupPath));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task VerifyBackup_ReturnsTrueForValidBackup()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var backupPath = Path.Combine(_tempDir, "backup.db");
            await service.BackupAsync(backupPath);

            var valid = await service.VerifyBackupAsync(backupPath);
            Assert.True(valid);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task VerifyBackup_ReturnsFalseForNonExistentFile()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var valid = await service.VerifyBackupAsync(Path.Combine(_tempDir, "nonexistent.db"));
            Assert.False(valid);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Restore_ToNewTarget_Succeeds()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var backupPath = Path.Combine(_tempDir, "backup.db");
            await service.BackupAsync(backupPath);

            var targetPath = Path.Combine(_tempDir, "restored.db");
            var result = await service.RestoreAsync(backupPath, targetPath, confirmOverwrite: false);

            Assert.True(result.Succeeded);
            Assert.True(File.Exists(targetPath));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Restore_ToExistingTarget_RequiresConfirmation()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var backupPath = Path.Combine(_tempDir, "backup.db");
            await service.BackupAsync(backupPath);

            var targetPath = Path.Combine(_tempDir, "existing.db");
            File.WriteAllText(targetPath, "existing data");

            var result = await service.RestoreAsync(backupPath, targetPath, confirmOverwrite: false);
            Assert.False(result.Succeeded);
            Assert.Contains("overwrite", result.Error!.ToLower());
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Restore_ToExistingTarget_WithConfirmation_Succeeds()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var backupPath = Path.Combine(_tempDir, "backup.db");
            await service.BackupAsync(backupPath);

            var targetPath = Path.Combine(_tempDir, "existing.db");
            File.WriteAllText(targetPath, "existing data");

            var result = await service.RestoreAsync(backupPath, targetPath, confirmOverwrite: true);
            Assert.True(result.Succeeded);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Restore_NonExistentBackup_Fails()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var result = await service.RestoreAsync(
                Path.Combine(_tempDir, "nonexistent.db"),
                Path.Combine(_tempDir, "target.db"),
                confirmOverwrite: false);

            Assert.False(result.Succeeded);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Backup_EmptyPath_Fails()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var result = await service.BackupAsync("");
            Assert.False(result.Succeeded);
        }
        finally { await db.DisposeAsync(); }
    }
}
