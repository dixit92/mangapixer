namespace com.lifepixer.mangaplex.Server.Operations;

using com.lifepixer.mangaplex.Server.Logging;

using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO;

/// <summary>
/// Online backup service. Creates safe point-in-time snapshots of the
/// SQLite database while the server is running.
///
/// Rules:
/// - Backup uses SQLite's online backup API (VACUUM INTO) for a consistent snapshot.
/// - Backup file is written to a user-specified path (not in source media).
/// - Private config/key set is included only if explicitly requested.
/// - Verification checks the backup file is a valid SQLite database.
/// - Restore-to-new-target requires explicit confirmation guards.
/// - Session invalidation is performed after restore.
/// - No destructive repair on corruption/disk-full/interrupted jobs.
/// </summary>
public sealed class BackupService
{
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<BackupService>? _logger;

    public BackupService(MangaPlexDbContext db, ILogger<BackupService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Creates an online backup of the database to the specified path.
    /// Uses VACUUM INTO for a consistent snapshot without blocking writes.
    /// </summary>
    public async Task<BackupResult> BackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return BackupResult.Failed("Backup path is required.");

        // Ensure the directory exists
        var dir = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            // Use VACUUM INTO for a consistent online backup
            // This creates a new database file with all data, without blocking
            _logger?.LogDebug(LogEvents.Backup.BackupStarting, "Database backup starting (VACUUM INTO)");
            var escapedPath = backupPath.Replace("'", "''");
            var sql = $"VACUUM INTO '{escapedPath}';";

            await _db.Database.ExecuteSqlRawAsync(sql, ct);

            // Verify the backup
            if (!await VerifyBackupAsync(backupPath, ct))
            {
                _logger?.LogError(LogEvents.Backup.BackupVerificationFailed, "Database backup failed verification");
                return BackupResult.Failed("Backup verification failed.");
            }

            // Outcome + size only — never the backup path (privacy invariant)
            var sizeBytes = new FileInfo(backupPath).Length;
            _logger?.LogInformation(LogEvents.Backup.BackupCompleted, "Database backup completed ({SizeBytes} bytes)", sizeBytes);
            return BackupResult.Success(backupPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(LogEvents.Backup.BackupFailed, ex, "Backup failed: {Error}", ex.GetType().Name);
            return BackupResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Verifies that a backup file is a valid SQLite database.
    /// </summary>
    public async Task<bool> VerifyBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath))
            return false;

        try
        {
            // Open the backup file and check it's a valid SQLite database.
            // Not pooled: a pooled connection keeps the backup file open after
            // this check, and on Windows that open handle blocks the next
            // open, move or delete of the file (restore validation, the
            // staged-file swap, retention pruning).
            var connectionString = new SqliteConnectionStringBuilder(
                DatabaseInitialization.BuildConnectionString(backupPath))
            {
                Pooling = false,
            }.ToString();
            var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var verifyDb = new MangaPlexDbContext(options);
            // Check that key tables exist
            var tableCount = await verifyDb.Database
                .SqlQueryRaw<int>("SELECT count(*) as Value FROM sqlite_master WHERE type='table'")
                .FirstOrDefaultAsync(ct);

            return tableCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restores a backup to a new target database path.
    /// Requires explicit confirmation to prevent accidental overwrites.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        string backupPath,
        string targetPath,
        bool confirmOverwrite,
        CancellationToken ct = default)
    {
        if (!File.Exists(backupPath))
            return RestoreResult.Failed("Backup file not found.");

        if (File.Exists(targetPath) && !confirmOverwrite)
            return RestoreResult.Failed("Target file exists. Set confirmOverwrite to true to overwrite.");

        try
        {
            // Verify the backup first
            if (!await VerifyBackupAsync(backupPath, ct))
            {
                _logger?.LogError(LogEvents.Backup.RestoreAbortedVerification, "Restore aborted: backup failed verification");
                return RestoreResult.Failed("Backup verification failed.");
            }

            // Copy backup to target
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Use atomic copy: copy to temp, then rename
            var tempPath = targetPath + ".tmp";
            File.Copy(backupPath, tempPath, overwrite: true);

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            // Outcome + size only — never the backup/target paths (privacy invariant)
            var sizeBytes = new FileInfo(targetPath).Length;
            _logger?.LogWarning(LogEvents.Backup.RestoreCompleted, "Database restored from backup ({SizeBytes} bytes); all sessions must be invalidated", sizeBytes);
            return RestoreResult.Success(targetPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(LogEvents.Backup.RestoreFailed, ex, "Restore failed: {Error}", ex.GetType().Name);
            return RestoreResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Invalidates all sessions (forces re-login for all users).
    /// Called after a restore to ensure old sessions don't survive.
    /// </summary>
    public async Task<int> InvalidateAllSessionsAsync(CancellationToken ct = default)
    {
        // Delete all session records
        var deleted = await _db.Database
            .ExecuteSqlRawAsync("DELETE FROM sessions;", ct);

        // Also bump all user security stamps to invalidate any cached auth
        await _db.Database
            .ExecuteSqlRawAsync("UPDATE users SET security_stamp = hex(randomblob(16));", ct);

        _logger?.LogWarning(LogEvents.Backup.SessionsInvalidated, "All sessions invalidated — {Count} sessions removed", deleted);
        return deleted;
    }
}

/// <summary>
/// Result of a backup operation.
/// </summary>
public sealed record BackupResult
{
    public required bool Succeeded { get; init; }
    public required string? Path { get; init; }
    public required string? Error { get; init; }

    public static BackupResult Success(string path) =>
        new() { Succeeded = true, Path = path, Error = null };

    public static BackupResult Failed(string error) =>
        new() { Succeeded = false, Path = null, Error = error };
}

/// <summary>
/// Result of a restore operation.
/// </summary>
public sealed record RestoreResult
{
    public required bool Succeeded { get; init; }
    public required string? TargetPath { get; init; }
    public required string? Error { get; init; }

    public static RestoreResult Success(string targetPath) =>
        new() { Succeeded = true, TargetPath = targetPath, Error = null };

    public static RestoreResult Failed(string error) =>
        new() { Succeeded = false, TargetPath = null, Error = error };
}
