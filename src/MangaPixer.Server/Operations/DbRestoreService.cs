namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.IO;
using System.Text.Json;

/// <summary>
/// Configuration for the DB backup import/restore feature (1.7.0).
/// </summary>
public sealed class DbRestoreOptions
{
    /// <summary>
    /// Hard ceiling on an uploaded restore payload, enforced while the
    /// stream is written to the controlled staging location. Defaults to
    /// 512 MiB; override via <c>MangaPixer:Backups:MaxRestoreUploadBytes</c>.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 512 * 1024 * 1024;
}

/// <summary>
/// Imports and restores a MangaPixer SQLite database backup (1.7.0).
///
/// Security model (non-negotiable):
/// 1. Admin-only — the endpoint is guarded by <c>[Authorize(Policy = "Admin")]</c>.
/// 2. Validate before trusting — an uploaded stream is accepted ONLY if it is a
///    genuine MangaPixer SQLite backup: SQLite magic header, enforced size cap,
///    <c>PRAGMA integrity_check = ok</c>, and the expected MangaPixer schema
///    (core tables + a recognised <c>user_version</c> marker). A caller-supplied
///    filesystem path is NEVER accepted; the payload is written to a controlled
///    staging location under the app's own data root.
/// 3. Pre-restore snapshot + atomic apply + rollback — before replacing
///    anything, a consistent snapshot of the current DB is taken (VACUUM INTO,
///    via <see cref="BackupService"/>). The validated upload is staged and
///    applied ATOMICALLY on a controlled restart (no connections open), so the
///    live DB is never overwritten while in use. On any failure the system is
///    rolled back to the original DB; it is never left half-written or
///    unopenable.
/// 4. State only — restore replaces app state (the SQLite DB). It NEVER touches
///    anything under a source library directory (read-only invariant holds).
/// 5. Audit + safe logging — an audit event is emitted (who, when, outcome);
///    logs carry IDs, counts, and outcomes only — never paths, secrets, or DB
///    contents.
/// </summary>
public sealed class DbRestoreService
{
    /// <summary>SQLite magic header: "SQLite format 3\0" (16 bytes).</summary>
    private static readonly byte[] SqliteMagic =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    private const string PendingDirName = "restore-pending";
    private const string StagedFileName = "mangapixer.db.staged";
    private const string MarkerFileName = "restore.json";

    private readonly MangaPixerDbContext _db;
    private readonly BackupService _backup;
    private readonly DbRestoreOptions _options;
    private readonly string _dataRoot;
    private readonly string _backupsDir;
    private readonly ILogger<DbRestoreService>? _logger;

    public DbRestoreService(
        MangaPixerDbContext db,
        BackupService backup,
        DbRestoreOptions options,
        AppRootOptions appRoot,
        RotatingBackupOptions rotatingOptions,
        ILogger<DbRestoreService>? logger = null)
    {
        _db = db;
        _backup = backup;
        _options = options;
        _dataRoot = string.IsNullOrWhiteSpace(appRoot.DataRoot)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : appRoot.DataRoot;
        _backupsDir = rotatingOptions.BackupDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Validates an uploaded backup stream, takes a pre-restore snapshot of
    /// the current DB, and stages the validated upload for apply-on-restart.
    /// Returns a staging outcome with the generated file names (never absolute
    /// paths). The actual DB swap happens at the next startup via
    /// <see cref="ApplyPendingRestoreAsync"/> — the live DB is never touched
    /// while connections are open.
    /// </summary>
    public async Task<RestoreStageResult> StageRestoreAsync(
        Stream upload,
        string actorUserName,
        CancellationToken ct = default)
    {
        if (upload is null)
            return RestoreStageResult.Failed("invalid_request", "Upload stream is required.");

        var pendingDir = Path.Combine(_dataRoot, PendingDirName);
        Directory.CreateDirectory(pendingDir);
        var stagedPath = Path.Combine(pendingDir, StagedFileName);

        // 1. Write the upload to the controlled staging location with an
        //    enforced size cap. Never trust a caller-supplied path.
        try
        {
            await using (var fs = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var written = 0L;
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await upload.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > _options.MaxUploadBytes)
                    {
                        _logger?.LogWarning(LogEvents.Backup.RestoreUploadRejected,
                            "Restore upload rejected: exceeds size cap ({Cap} bytes)", _options.MaxUploadBytes);
                        return RestoreStageResult.Failed("upload_too_large",
                            $"Upload exceeds the maximum allowed size of {_options.MaxUploadBytes} bytes.");
                    }
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }
        catch (Exception ex)
        {
            TryDelete(stagedPath);
            _logger?.LogWarning(LogEvents.Backup.RestoreUploadRejected, "Restore upload rejected: {Error}", ex.GetType().Name);
            return RestoreStageResult.Failed("upload_failed", "Could not read the uploaded file.");
        }

        // 2. Validate the staged file before trusting it.
        var validation = await ValidateBackupAsync(stagedPath, ct);
        if (!validation.IsValid)
        {
            TryDelete(stagedPath);
            _logger?.LogWarning(LogEvents.Backup.RestoreUploadRejected,
                "Restore upload rejected: {Reason}", validation.Error ?? "validation failed");
            return RestoreStageResult.Failed("invalid_backup", validation.Error ?? "Backup validation failed.");
        }

        // 3. Pre-restore snapshot of the CURRENT DB (rollback point). Reuse the
        //    online VACUUM INTO backup — safe while the server is running.
        var preRestoreName = $"pre-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
        var preRestorePath = Path.Combine(_backupsDir, preRestoreName);
        var backupResult = await _backup.BackupAsync(preRestorePath, ct);
        if (!backupResult.Succeeded)
        {
            TryDelete(stagedPath);
            _logger?.LogError(LogEvents.Backup.RestorePreSnapshotFailed,
                "Pre-restore snapshot failed: {Error}", backupResult.Error ?? "unknown");
            return RestoreStageResult.Failed("pre_restore_failed",
                "Pre-restore snapshot of the current database failed; restore aborted to protect existing data.");
        }

        // 4. Write the marker. Carries generated file names (never absolute
        //    paths) + the requesting admin, so the startup apply step can
        //    complete the swap and audit it.
        var marker = new RestoreMarker
        {
            StagedFileName = StagedFileName,
            PreRestoreBackupFileName = preRestoreName,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            RequestedBy = actorUserName,
        };
        await File.WriteAllTextAsync(Path.Combine(pendingDir, MarkerFileName),
            JsonSerializer.Serialize(marker), ct);

        _logger?.LogWarning(LogEvents.Backup.RestoreStaged,
            "DB restore staged (pre-restore snapshot {PreRestore}); restart required to apply", preRestoreName);

        return RestoreStageResult.Staged(preRestoreName);
    }

    /// <summary>
    /// Validates a staged/candidate backup file: SQLite magic header, a
    /// passing <c>PRAGMA integrity_check</c>, the expected MangaPixer core
    /// tables, and a recognised schema version marker (not newer than this
    /// server supports). Opens the file READ-ONLY so a crafted payload cannot
    /// mutate anything.
    /// </summary>
    public static async Task<BackupValidation> ValidateBackupAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return BackupValidation.Invalid("Backup file not found.");

        // Magic header
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[SqliteMagic.Length];
            var n = await fs.ReadAsync(header, ct);
            if (n < SqliteMagic.Length || !header.AsSpan().SequenceEqual(SqliteMagic))
                return BackupValidation.Invalid("File is not a SQLite database (magic header mismatch).");
        }
        catch (Exception ex)
        {
            return BackupValidation.Invalid($"Could not read backup header: {ex.GetType().Name}");
        }

        // Open read-only and run integrity_check + schema/version checks.
        // Not pooled, so disposing the connection closes the file: on Windows
        // a pooled handle would block the staged-file move that follows.
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        try
        {
            await using var conn = new SqliteConnection(cs.ConnectionString);
            await conn.OpenAsync(ct);

            // integrity_check
            var integrity = await ExecuteScalarAsync(conn, "PRAGMA integrity_check;", ct);
            if (integrity is not "ok")
                return BackupValidation.Invalid("Backup failed integrity check (database may be corrupt).");

            // Core MangaPixer tables
            foreach (var table in new[] { "users", "libraries", "catalog_nodes" })
            {
                if (!await TableExistsAsync(conn, table, ct))
                    return BackupValidation.Invalid($"Backup is missing expected MangaPixer table '{table}'.");
            }

            // Schema version marker — reject a newer-than-supported schema.
            var version = await ExecuteScalarAsync(conn, "PRAGMA user_version;", ct);
            if (version is long lv)
            {
                if (lv > DatabaseInitialization.CurrentSchemaVersion)
                    return BackupValidation.Invalid(
                        $"Backup schema version {lv} is newer than this server supports ({DatabaseInitialization.CurrentSchemaVersion}).");
            }

            return BackupValidation.Valid();
        }
        catch (Exception ex)
        {
            return BackupValidation.Invalid($"Could not validate backup: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Applies a pending restore at startup, BEFORE any connection to the live
    /// DB is opened. If no pending restore marker exists, returns
    /// <see cref="RestoreApplyOutcome.None"/>. Otherwise re-validates the
    /// staged file, atomically swaps it in for the live DB (the old DB is moved
    /// aside as a second rollback copy), and clears the marker. On any failure
    /// the system is rolled back to the original DB and the marker cleared.
    ///
    /// This method performs filesystem operations only — it must be called
    /// before the DbContext is opened against <paramref name="dbPath"/>.
    /// </summary>
    public static async Task<RestoreApplyOutcome> ApplyPendingRestoreAsync(
        string dataRoot,
        string dbPath,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        var pendingDir = Path.Combine(dataRoot, PendingDirName);
        var markerPath = Path.Combine(pendingDir, MarkerFileName);
        if (!File.Exists(markerPath))
            return RestoreApplyOutcome.None;

        RestoreMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<RestoreMarker>(
                await File.ReadAllTextAsync(markerPath, ct)) ?? new RestoreMarker();
        }
        catch (Exception ex)
        {
            logger?.LogError(LogEvents.Backup.RestoreApplyFailed, "Could not read restore marker: {Error}", ex.GetType().Name);
            ClearPending(pendingDir);
            return RestoreApplyOutcome.FailedApply("Could not read the restore marker; pending restore cleared.");
        }

        var stagedPath = Path.Combine(pendingDir, marker.StagedFileName.Length > 0 ? marker.StagedFileName : StagedFileName);

        logger?.LogInformation(LogEvents.Backup.RestoreApplyPending,
            "Pending DB restore found (requested by {Actor}); applying", marker.RequestedBy ?? "unknown");

        // Re-validate the staged file (paranoia: could have been corrupted on disk).
        var validation = await ValidateBackupAsync(stagedPath, ct);
        if (!validation.IsValid)
        {
            logger?.LogError(LogEvents.Backup.RestoreApplyFailed,
                "Pending restore staged file failed re-validation: {Reason}", validation.Error ?? "unknown");
            ClearPending(pendingDir);
            return RestoreApplyOutcome.FailedApply(validation.Error ?? "Staged backup failed re-validation.");
        }

        // Atomic swap. The old live DB is moved aside as an immediate rollback
        // copy (the VACUUM INTO pre-restore snapshot is the durable rollback
        // point in the backups folder).
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var replacedPath = Path.Combine(dataRoot, "mangapixer.db.replaced-" + ts);
        var walPath = dbPath + "-wal";
        var shmPath = dbPath + "-shm";

        try
        {
            // Remove stale WAL/SHM from the previous run (they belong to the old DB).
            TryDelete(walPath);
            TryDelete(shmPath);

            // Move the current DB aside (atomic rename on the same filesystem).
            if (File.Exists(dbPath))
                File.Move(dbPath, replacedPath);

            // Move the staged file into place.
            File.Move(stagedPath, dbPath);

            // Success — clear the marker and the moved-aside staged file. The
            // swap is now durable (the new DB is live at dbPath), so the
            // moved-aside copy is no longer needed for rollback and would
            // otherwise accumulate one-per-restore; the VACUUM INTO
            // pre-restore snapshot in the backups folder remains as the
            // durable rollback point.
            ClearPending(pendingDir);
            TryDelete(replacedPath);

            logger?.LogWarning(LogEvents.Backup.RestoreApplied,
                "DB restore applied (pre-restore snapshot {PreRestore}); sessions invalidated on next open",
                marker.PreRestoreBackupFileName ?? "none");

            return RestoreApplyOutcome.Succeeded(
                marker.RequestedBy,
                marker.RequestedAtUtc,
                marker.PreRestoreBackupFileName,
                "mangapixer.db.replaced-" + ts);
        }
        catch (Exception ex)
        {
            logger?.LogError(LogEvents.Backup.RestoreApplyFailed, ex, "DB restore apply failed: {Error}", ex.GetType().Name);
            // Rollback: restore the original DB from the moved-aside copy.
            try
            {
                if (File.Exists(replacedPath))
                {
                    if (File.Exists(dbPath)) TryDelete(dbPath);
                    File.Move(replacedPath, dbPath);
                }
            }
            catch (Exception rbEx)
            {
                logger?.LogError(LogEvents.Backup.RestoreRolledBack, rbEx,
                    "Rollback could not restore the original DB from the moved-aside copy: {Error}", rbEx.GetType().Name);
            }

            logger?.LogWarning(LogEvents.Backup.RestoreRolledBack,
                "DB restore rolled back to the original DB (pre-restore snapshot {PreRestore})",
                marker.PreRestoreBackupFileName ?? "none");
            ClearPending(pendingDir);
            return RestoreApplyOutcome.FailedApply("Restore apply failed; system rolled back to the original database.");
        }
    }

    /// <summary>
    /// Records an audit event for a restore outcome. Called after the new DB
    /// is opened (post-migrate) so the audit row lands in the restored DB.
    /// </summary>
    public async Task AuditRestoreAsync(
        string action,
        string result,
        string? actorUserName,
        string? correlationId,
        CancellationToken ct = default)
    {
        long? actorId = null;
        if (!string.IsNullOrWhiteSpace(actorUserName))
        {
            var user = await _db.Users.FirstOrDefaultAsync(
                u => u.NormalizedUserName == actorUserName.ToUpperInvariant(), ct);
            actorId = user?.Id;
        }

        _db.AuditEvents.Add(new AuditEventEntity
        {
            Action = action,
            Result = result,
            ActorUserId = actorId,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
        });
        await _db.SaveChangesAsync(ct);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name = $name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l && l > 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
    }

    private static void ClearPending(string pendingDir)
    {
        try
        {
            if (Directory.Exists(pendingDir))
                Directory.Delete(pendingDir, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>Result of validating a candidate backup.</summary>
public sealed record BackupValidation
{
    public required bool IsValid { get; init; }
    public required string? Error { get; init; }

    public static BackupValidation Valid() => new() { IsValid = true, Error = null };
    public static BackupValidation Invalid(string error) => new() { IsValid = false, Error = error };
}

/// <summary>Result of staging a restore (the apply happens on restart).</summary>
public sealed record RestoreStageResult
{
    public required bool Succeeded { get; init; }
    public required string? Error { get; init; }
    public required string? Message { get; init; }
    public required string? PreRestoreBackupFileName { get; init; }

    public static RestoreStageResult Staged(string preRestoreBackupFileName) => new()
    {
        Succeeded = true,
        Error = null,
        Message = "Restore staged. Restart the server to complete the restore.",
        PreRestoreBackupFileName = preRestoreBackupFileName,
    };

    public static RestoreStageResult Failed(string error, string message) => new()
    {
        Succeeded = false,
        Error = error,
        Message = message,
        PreRestoreBackupFileName = null,
    };
}

/// <summary>Outcome of a startup pending-restore apply.</summary>
public sealed record RestoreApplyOutcome
{
    public required bool Applied { get; init; }
    public required bool Failed { get; init; }
    public required string? ActorUserName { get; init; }
    public required DateTimeOffset? RequestedAtUtc { get; init; }
    public required string? PreRestoreBackupFileName { get; init; }
    public required string? ReplacedFileName { get; init; }
    public required string? Error { get; init; }

    public static RestoreApplyOutcome None => new()
    {
        Applied = false,
        Failed = false,
        ActorUserName = null,
        RequestedAtUtc = null,
        PreRestoreBackupFileName = null,
        ReplacedFileName = null,
        Error = null,
    };

    public static RestoreApplyOutcome Succeeded(
        string? actor, DateTimeOffset requestedAt, string? preRestore, string replaced) => new()
        {
            Applied = true,
            Failed = false,
            ActorUserName = actor,
            RequestedAtUtc = requestedAt,
            PreRestoreBackupFileName = preRestore,
            ReplacedFileName = replaced,
            Error = null,
        };

    public static RestoreApplyOutcome FailedApply(string error) => new()
    {
        Applied = false,
        Failed = true,
        ActorUserName = null,
        RequestedAtUtc = null,
        PreRestoreBackupFileName = null,
        ReplacedFileName = null,
        Error = error,
    };
}

/// <summary>Marker file contents (generated file names + actor, never paths).</summary>
public sealed class RestoreMarker
{
    public string StagedFileName { get; set; } = string.Empty;
    public string PreRestoreBackupFileName { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
}
