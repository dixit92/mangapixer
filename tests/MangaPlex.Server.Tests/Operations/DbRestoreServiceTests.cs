namespace com.lifepixer.mangaplex.Tests.Server.Operations;

using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-level tests for <see cref="DbRestoreService"/> — backup
/// validation, staging, and the startup apply/rollback path. Covers the
/// security model's REJECTION paths (bad magic, oversized, failed integrity,
/// missing schema) and the atomic apply + rollback contract.
/// </summary>
public sealed class DbRestoreServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly string _dataRoot;
    private readonly string _backupsDir;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public DbRestoreServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-restore-" + Guid.NewGuid().ToString("N")[..8]);
        _dataRoot = Path.Combine(_tempDir, "data");
        _backupsDir = Path.Combine(_dataRoot, "backups");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_backupsDir);
        _dbPath = Path.Combine(_dataRoot, "mangaplex.db");
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(_dbPath))
            .Options;
    }

    public void Dispose()
    {
        // Close all pooled connections so the temp dir can be deleted on Windows.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, BackupService backup, DbRestoreService restore)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        db.Libraries.Add(new LibraryEntity
        {
            PublicId = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(1),
            DisplayName = "Test",
            RootPath = "/private/test",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new UserEntity
        {
            PublicId = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(2),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var backup = new BackupService(db);
        var rotatingOptions = new RotatingBackupOptions { BackupDirectory = _backupsDir };
        var restore = new DbRestoreService(
            db, backup,
            new DbRestoreOptions { MaxUploadBytes = 1024 * 1024 },
            new AppRootOptions { DataRoot = _dataRoot },
            rotatingOptions);
        return (db, backup, restore);
    }

    /// <summary>Creates a genuine MangaPlex SQLite backup via VACUUM INTO.</summary>
    private async Task<string> MakeValidBackupAsync(BackupService backup)
    {
        var path = Path.Combine(_tempDir, "valid-backup.db");
        var result = await backup.BackupAsync(path);
        Assert.True(result.Succeeded);
        return path;
    }

    // --- Validation (rejection paths) ---

    [Fact]
    public async Task Validate_NonSqliteFile_RejectsBadMagic()
    {
        var path = Path.Combine(_tempDir, "not-sqlite.db");
        await File.WriteAllTextAsync(path, "this is not a sqlite database");

        var v = await DbRestoreService.ValidateBackupAsync(path);

        Assert.False(v.IsValid);
        Assert.Contains("magic", v.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_NonExistentFile_Rejects()
    {
        var v = await DbRestoreService.ValidateBackupAsync(Path.Combine(_tempDir, "nope.db"));

        Assert.False(v.IsValid);
    }

    [Fact]
    public async Task Validate_CorruptSqlite_RejectsIntegrityCheck()
    {
        // Start from a valid backup, then truncate it so integrity_check
        // fails (malformed image) but the magic header survives.
        var (db, backup, _) = await SetupAsync();
        try
        {
            var valid = await MakeValidBackupAsync(backup);
            var corrupt = Path.Combine(_tempDir, "corrupt.db");
            File.Copy(valid, corrupt, overwrite: true);
            // Truncate to just past the 100-byte file header — no complete
            // b-tree pages remain, so integrity_check rejects the image.
            using (var fs = new FileStream(corrupt, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(128);
            }

            var v = await DbRestoreService.ValidateBackupAsync(corrupt);

            Assert.False(v.IsValid);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Validate_ValidMangaPlexBackup_Passes()
    {
        var (db, backup, _) = await SetupAsync();
        try
        {
            var valid = await MakeValidBackupAsync(backup);

            var v = await DbRestoreService.ValidateBackupAsync(valid);

            Assert.True(v.IsValid, v.Error);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Validate_EmptySqlite_RejectsMissingSchema()
    {
        // A bare SQLite file with an unrelated table (no MangaPlex schema).
        var empty = Path.Combine(_tempDir, "empty.db");
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            DatabaseInitialization.BuildConnectionString(empty)))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE unrelated(x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var v = await DbRestoreService.ValidateBackupAsync(empty);

        Assert.False(v.IsValid);
        Assert.Contains("table", v.Error, StringComparison.OrdinalIgnoreCase);
    }

    // --- Staging (rejection paths) ---

    [Fact]
    public async Task Stage_NonSqliteStream_RejectsBadMagic()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            await using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("not a sqlite file"));

            var result = await restore.StageRestoreAsync(stream, "admin");

            Assert.False(result.Succeeded);
            Assert.Equal("invalid_backup", result.Error);
            // Staged file must be cleaned up.
            Assert.False(File.Exists(Path.Combine(_dataRoot, "restore-pending", "mangaplex.db.staged")));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Stage_OversizedStream_RejectsSizeCap()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            // Cap is 1 MiB; send 2 MiB of valid-looking bytes (magic header + junk).
            var bytes = new byte[2 * 1024 * 1024];
            System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 0);
            await using var stream = new MemoryStream(bytes);

            var result = await restore.StageRestoreAsync(stream, "admin");

            Assert.False(result.Succeeded);
            Assert.Equal("upload_too_large", result.Error);
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Stage_ValidBackup_StagesAndTakesPreRestoreSnapshot()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            var valid = await MakeValidBackupAsync(backup);
            await using var stream = File.OpenRead(valid);

            var result = await restore.StageRestoreAsync(stream, "admin");

            Assert.True(result.Succeeded);
            Assert.NotNull(result.PreRestoreBackupFileName);
            Assert.StartsWith("pre-restore-", result.PreRestoreBackupFileName);
            // Staged file + marker exist under the controlled data root.
            Assert.True(File.Exists(Path.Combine(_dataRoot, "restore-pending", "mangaplex.db.staged")));
            Assert.True(File.Exists(Path.Combine(_dataRoot, "restore-pending", "restore.json")));
            // Pre-restore snapshot exists in the backups folder.
            Assert.True(File.Exists(Path.Combine(_backupsDir, result.PreRestoreBackupFileName!)));
        }
        finally { await db.DisposeAsync(); }
    }

    // --- Startup apply (atomic swap + rollback) ---

    [Fact]
    public async Task Apply_NoPendingRestore_ReturnsNone()
    {
        var outcome = await DbRestoreService.ApplyPendingRestoreAsync(_dataRoot, _dbPath);

        Assert.False(outcome.Applied);
        Assert.False(outcome.Failed);
    }

    [Fact]
    public async Task Apply_PendingRestore_SwapsDbAndClearsMarker()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            // The live DB has a library named "Test". Stage a backup that has
            // a DIFFERENT library named "Restored" so we can prove the swap.
            var restoredDbPath = Path.Combine(_tempDir, "restored-source.db");
            var cs = DatabaseInitialization.BuildConnectionString(restoredDbPath);
            // Build the restored-source DB in a self-disposing block, then
            // checkpoint the WAL and clear the pool so the main file holds all
            // data (the staged copy does not include the -wal sidecar).
            await using (var restoredDb = new MangaPlexDbContext(
                new DbContextOptionsBuilder<MangaPlexDbContext>().UseSqlite(cs).Options))
            {
                await restoredDb.Database.EnsureCreatedAsync();
                await DatabaseInitialization.ConfigureDatabaseAsync(restoredDb);
                restoredDb.Libraries.Add(new LibraryEntity
                {
                    PublicId = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(99),
                    DisplayName = "Restored",
                    RootPath = "/private/restored",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                restoredDb.Users.Add(new UserEntity
                {
                    PublicId = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(2),
                    UserName = "admin",
                    NormalizedUserName = "ADMIN",
                    IsActive = true,
                    IsAdmin = true,
                    PasswordHash = "hash",
                    SecurityStamp = "stamp",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await restoredDb.SaveChangesAsync();
                // PRAGMA user_version does not support bound parameters; the
                // value is an internal constant, so concatenation is safe
                // (mirrors DatabaseInitialization.SetSchemaVersionAsync).
                var sql = "PRAGMA user_version = " + DatabaseInitialization.CurrentSchemaVersion
                    .ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
                await restoredDb.Database.ExecuteSqlRawAsync(sql);
                // Checkpoint the WAL into the main file so a plain file copy is
                // a complete, self-contained SQLite database.
                await restoredDb.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // Stage the restored-source DB as the upload.
            await using var stream = File.OpenRead(restoredDbPath);
            var stage = await restore.StageRestoreAsync(stream, "admin");
            Assert.True(stage.Succeeded, $"{stage.Error}: {stage.Message}");
            await db.DisposeAsync(); // close the live DB so the swap can move it.

            var outcome = await DbRestoreService.ApplyPendingRestoreAsync(_dataRoot, _dbPath);

            Assert.True(outcome.Applied, outcome.Error);
            Assert.False(File.Exists(Path.Combine(_dataRoot, "restore-pending", "restore.json")));
            // The new live DB must be the restored one (library "Restored").
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var verify = new MangaPlexDbContext(
                new DbContextOptionsBuilder<MangaPlexDbContext>()
                    .UseSqlite(DatabaseInitialization.BuildConnectionString(_dbPath)).Options);
            var lib = await verify.Libraries.FirstOrDefaultAsync();
            Assert.NotNull(lib);
            Assert.Equal("Restored", lib!.DisplayName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task Apply_CorruptStagedFile_RollsBackAndKeepsOriginalDb()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            var valid = await MakeValidBackupAsync(backup);
            await using var stream = File.OpenRead(valid);
            var stage = await restore.StageRestoreAsync(stream, "admin");
            Assert.True(stage.Succeeded);
            await db.DisposeAsync(); // close live DB so swap path is reachable.

            // Corrupt the staged file AFTER staging so re-validation at apply fails.
            var stagedPath = Path.Combine(_dataRoot, "restore-pending", "mangaplex.db.staged");
            using (var fs = new FileStream(stagedPath, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.WriteByte(0x00); // break the magic header
                fs.WriteByte(0x00);
            } // disposed + flushed before the apply reads the file

            var outcome = await DbRestoreService.ApplyPendingRestoreAsync(_dataRoot, _dbPath);

            Assert.True(outcome.Failed, outcome.Error);
            Assert.False(outcome.Applied);
            // Marker cleared.
            Assert.False(File.Exists(Path.Combine(_dataRoot, "restore-pending", "restore.json")));
            // Original DB intact.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var verify = new MangaPlexDbContext(
                new DbContextOptionsBuilder<MangaPlexDbContext>()
                    .UseSqlite(DatabaseInitialization.BuildConnectionString(_dbPath)).Options);
            var lib = await verify.Libraries.FirstOrDefaultAsync();
            Assert.NotNull(lib);
            Assert.Equal("Test", lib!.DisplayName);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    // --- Audit ---

    [Fact]
    public async Task AuditRestore_WritesAuditEventRow()
    {
        var (db, backup, restore) = await SetupAsync();
        try
        {
            await restore.AuditRestoreAsync("db_restore", "applied", "admin", null);

            var audit = await db.AuditEvents.ToListAsync();
            var row = Assert.Single(audit);
            Assert.Equal("db_restore", row.Action);
            Assert.Equal("applied", row.Result);
            Assert.NotNull(row.ActorUserId);
        }
        finally { await db.DisposeAsync(); }
    }
}
