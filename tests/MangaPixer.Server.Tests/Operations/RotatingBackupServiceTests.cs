namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for RotatingBackupService — snapshot creation,
/// retention pruning, and the pre-migration exemption.
/// </summary>
public sealed class RotatingBackupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _backupsDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public RotatingBackupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-rotbackup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _backupsDir = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(_backupsDir);
        var dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, RotatingBackupService service)> SetupAsync(
        int retentionCount = 3)
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        db.Libraries.Add(new LibraryEntity
        {
            PublicId = OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new RotatingBackupService(
            db,
            new BackupService(db),
            new RotatingBackupOptions { BackupDirectory = _backupsDir, RetentionCount = retentionCount },
            new RotatingBackupState());
        return (db, service);
    }

    [Fact]
    public async Task Run_CreatesRotatingBackupFile()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var outcome = await service.RunAsync();

            Assert.True(outcome.Succeeded);
            Assert.NotNull(outcome.FileName);
            Assert.StartsWith(RotatingBackupService.FileNamePrefix, outcome.FileName);
            Assert.EndsWith(".db", outcome.FileName);
            Assert.True(File.Exists(Path.Combine(_backupsDir, outcome.FileName)));
            Assert.Equal(1, outcome.RetainedCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Prune_KeepsNewestRetentionCount()
    {
        var (db, service) = await SetupAsync(retentionCount: 3);
        try
        {
            // File names embed a UTC timestamp, so ordinal name order is
            // chronological — five distinct names, oldest first.
            for (var day = 1; day <= 5; day++)
            {
                var name = $"{RotatingBackupService.FileNamePrefix}2026090{day}000000.db";
                await File.WriteAllTextAsync(Path.Combine(_backupsDir, name), "snapshot");
            }

            var deleted = service.Prune(_backupsDir);

            Assert.Equal(2, deleted);
            var remaining = Directory.EnumerateFiles(_backupsDir, "*.db").Select(Path.GetFileName).ToList();
            Assert.Equal(3, remaining.Count);
            Assert.Contains("rotating-20260905000000.db", remaining);
            Assert.Contains("rotating-20260904000000.db", remaining);
            Assert.Contains("rotating-20260903000000.db", remaining);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Prune_NeverTouchesPreMigrationBackups()
    {
        var (db, service) = await SetupAsync(retentionCount: 1);
        try
        {
            var preMigration = Path.Combine(_backupsDir, "pre-migration-1.0.0-20260907.db");
            await File.WriteAllTextAsync(preMigration, "protected");
            for (var day = 1; day <= 4; day++)
            {
                var name = $"{RotatingBackupService.FileNamePrefix}2026090{day}000000.db";
                await File.WriteAllTextAsync(Path.Combine(_backupsDir, name), "snapshot");
            }

            service.Prune(_backupsDir);

            Assert.True(File.Exists(preMigration),
                "Pre-migration backups must never be pruned by rotation.");
            Assert.Single(Directory.EnumerateFiles(_backupsDir, RotatingBackupService.FileNamePrefix + "*.db"));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Prune_LeavesUnrelatedFilesAlone()
    {
        var (db, service) = await SetupAsync(retentionCount: 1);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, "rotating-20260901000000.db"), "a");
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, "rotating-20260902000000.db"), "b");
            await File.WriteAllTextAsync(Path.Combine(_backupsDir, "unrelated.db"), "keep me");

            service.Prune(_backupsDir);

            Assert.True(File.Exists(Path.Combine(_backupsDir, "unrelated.db")));
            Assert.False(File.Exists(Path.Combine(_backupsDir, "rotating-20260901000000.db")));
            Assert.True(File.Exists(Path.Combine(_backupsDir, "rotating-20260902000000.db")));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact(Timeout = 30000)]
    public async Task Run_SameSecondCollision_ResolvesToDistinctFileWithoutHanging()
    {
        var (db, service) = await SetupAsync(retentionCount: 5);
        try
        {
            // Occupy the base file names the service would generate this second and
            // the next, so whichever second RunAsync lands in, the un-suffixed name
            // already exists and it must take the collision branch. Before the fix
            // this hung (the loop regenerated the name but not the path); the Timeout
            // turns any regression into a failure instead of a hang.
            var now = DateTime.UtcNow;
            var occupiedNames = new[] { now, now.AddSeconds(1) }
                .Select(t => $"{RotatingBackupService.FileNamePrefix}{t:yyyyMMdd-HHmmss}.db")
                .ToHashSet();
            foreach (var occupied in occupiedNames)
                await File.WriteAllTextAsync(Path.Combine(_backupsDir, occupied), "occupied");

            var outcome = await service.RunAsync();

            Assert.True(outcome.Succeeded);
            Assert.NotNull(outcome.FileName);
            // It must have taken the collision branch (a suffixed name), not reused an
            // occupied base name, and the reported name must match the file written.
            Assert.DoesNotContain(outcome.FileName!, occupiedNames);
            Assert.True(File.Exists(Path.Combine(_backupsDir, outcome.FileName!)),
                "The reported file name must match the file written.");
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Run_RepeatedRuns_PruneToRetention()
    {
        var (db, service) = await SetupAsync(retentionCount: 2);
        try
        {
            await service.RunAsync();
            await Task.Delay(1100); // generated file names have 1-second resolution
            await service.RunAsync();
            await Task.Delay(1100);
            var outcome = await service.RunAsync();

            Assert.True(outcome.Succeeded);
            Assert.Equal(2, outcome.RetainedCount);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ListBackups_ReturnsSnapshotsNewestFirst_WithSizeAndTimestamp()
    {
        var (db, service) = await SetupAsync(retentionCount: 5);
        try
        {
            await service.RunAsync();
            await Task.Delay(1100); // generated file names have 1-second resolution
            await service.RunAsync();

            var files = service.ListBackups(_backupsDir);

            Assert.Equal(2, files.Count);
            // Newest-first: names embed a UTC timestamp, so descending ordinal
            // name order is chronological.
            Assert.True(string.CompareOrdinal(files[0].FileName, files[1].FileName) > 0);
            Assert.All(files, f =>
            {
                Assert.StartsWith("rotating-", f.FileName);
                Assert.True(f.ByteSize > 0);
                Assert.DoesNotContain(Path.DirectorySeparatorChar, f.FileName);
            });
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public void ListBackups_MissingDirectory_ReturnsEmpty()
    {
        var service = new RotatingBackupService(
            new MangaPixerDbContext(_options),
            new BackupService(new MangaPixerDbContext(_options)),
            new RotatingBackupOptions { BackupDirectory = _backupsDir },
            new RotatingBackupState());

        var files = service.ListBackups(Path.Combine(_tempDir, "does-not-exist"));

        Assert.Empty(files);
    }
}
