namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Persistence;
using System.IO;

/// <summary>
/// Configuration for the scheduled rotating database backups.
/// Interval and retention are admin-configurable via configuration
/// (MangaPixer:Backups:IntervalHours / RetentionCount / Enabled); defaults are
/// daily with the last 7 snapshots retained.
/// </summary>
public sealed class RotatingBackupOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    public int RetentionCount { get; set; } = 7;

    /// <summary>
    /// Directory that holds rotating and pre-migration backups. Defaults to
    /// the "backups" folder under the private data root — the same folder the
    /// migration orchestrator writes pre-migration backups into.
    /// </summary>
    public string BackupDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Singleton status + concurrency guard for rotating backups. Survives across
/// requests so the operations API can report the last scheduled or manual run.
/// Only counts, timestamps, and generated file names are recorded — never paths.
/// </summary>
public sealed class RotatingBackupState
{
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public DateTimeOffset? LastFailureUtc { get; private set; }
    public string? LastBackupFileName { get; private set; }

    /// <summary>
    /// Serializes backup runs between the scheduled timer and manual triggers
    /// (VACUUM INTO requires a non-existent target file).
    /// </summary>
    public SemaphoreSlim RunGate { get; } = new(1, 1);

    public void RecordSuccess(DateTimeOffset atUtc, string fileName)
    {
        LastAttemptUtc = atUtc;
        LastSuccessUtc = atUtc;
        LastFailureUtc = null;
        LastBackupFileName = fileName;
    }

    public void RecordFailure(DateTimeOffset atUtc)
    {
        LastAttemptUtc = atUtc;
        LastFailureUtc = atUtc;
    }
}

/// <summary>
/// Rotating scheduled backups: writes a consistent online snapshot
/// (via <see cref="BackupService"/>, VACUUM INTO) into the backups folder and
/// prunes the oldest rotating snapshots beyond the retention count.
///
/// Rotation policy: only files matching the "rotating-*.db" prefix are ever
/// pruned. Pre-migration backups ("pre-migration-*.db") live in the same
/// folder and are never touched by rotation.
/// </summary>
public sealed class RotatingBackupService
{
    public const string FileNamePrefix = "rotating-";

    private readonly MangaPixerDbContext _db;
    private readonly BackupService _backup;
    private readonly RotatingBackupOptions _options;
    private readonly RotatingBackupState _state;
    private readonly ILogger<RotatingBackupService>? _logger;

    public RotatingBackupService(
        MangaPixerDbContext db,
        BackupService backup,
        RotatingBackupOptions options,
        RotatingBackupState state,
        ILogger<RotatingBackupService>? logger = null)
    {
        _db = db;
        _backup = backup;
        _options = options;
        _state = state;
        _logger = logger;
    }

    /// <summary>
    /// Takes one rotating backup and prunes beyond retention. Serialized
    /// against concurrent runs (scheduled timer + manual trigger) via the
    /// shared run gate, since VACUUM INTO requires a non-existent target.
    /// </summary>
    public async Task<RotatingBackupOutcome> RunAsync(CancellationToken ct = default)
    {
        await _state.RunGate.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            _state.RunGate.Release();
        }
    }

    private async Task<RotatingBackupOutcome> RunCoreAsync(CancellationToken ct)
    {
        var dir = _options.BackupDirectory;
        Directory.CreateDirectory(dir);

        var fileName = $"{FileNamePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
        var path = Path.Combine(dir, fileName);
        // Same-second collision (a manual trigger racing the scheduled run, or a very
        // fast DB): append a short suffix and re-derive the path. Both fileName AND
        // path must advance together, or the guard below never clears (VACUUM INTO
        // refuses a pre-existing target, so this branch must terminate).
        while (File.Exists(path))
        {
            fileName = $"{FileNamePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.db";
            path = Path.Combine(dir, fileName);
        }

        var result = await _backup.BackupAsync(path, ct);

        if (!result.Succeeded)
        {
            _state.RecordFailure(DateTimeOffset.UtcNow);
            _logger?.LogWarning(LogEvents.Backup.RotatingRunFailed, "Rotating database backup failed: {Error}", result.Error ?? "unknown");
            return new RotatingBackupOutcome { Succeeded = false, FileName = fileName, RetainedCount = CountBackups(dir) };
        }

        _state.RecordSuccess(DateTimeOffset.UtcNow, fileName);
        var pruned = Prune(dir);
        _logger?.LogInformation(
            LogEvents.Backup.RotatingRunCompleted, "Rotating backup completed ({FileName}, retained {Count}); pruning removed {Pruned} old snapshot(s).",
            fileName, CountBackups(dir), pruned);

        return new RotatingBackupOutcome { Succeeded = true, FileName = fileName, RetainedCount = CountBackups(dir) };
    }

    /// <summary>
    /// Deletes the oldest rotating-*.db snapshots beyond the retention count.
    /// File names embed a UTC timestamp, so ordinal name order is chronological.
    /// Never touches files outside the rotating- prefix (pre-migration-* and
    /// anything else in the folder is left alone).
    /// </summary>
    public int Prune(string directory)
    {
        var keep = Math.Max(1, _options.RetentionCount);
        var files = Directory.EnumerateFiles(directory, FileNamePrefix + "*.db")
            .OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        var deleted = 0;
        foreach (var old in files.Skip(keep))
        {
            try { File.Delete(old); deleted++; }
            catch (IOException) { /* in use — retried on the next run */ }
        }
        return deleted;
    }

    public int CountBackups(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, FileNamePrefix + "*.db").Count()
            : 0;

    /// <summary>
    /// Enumerates the on-disk rotating snapshots newest-first, exposing only the
    /// generated file name, byte size, and last-write timestamp — NEVER an
    /// absolute path (privacy invariant for API-visible data). File names embed
    /// a UTC timestamp, so ordinal name order is chronological; sorting on the
    /// name (not the mtime) keeps the list stable and matches the pruning order.
    /// </summary>
    public IReadOnlyList<RotatingBackupFileInfo> ListBackups(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<RotatingBackupFileInfo>();

        return Directory.EnumerateFiles(directory, FileNamePrefix + "*.db")
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.Name, StringComparer.Ordinal)
            .Select(fi => new RotatingBackupFileInfo
            {
                FileName = fi.Name,
                ByteSize = fi.Length,
                TimestampUtc = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
            })
            .ToList();
    }
}

/// <summary>
/// One on-disk rotating snapshot, safe for API exposure: a generated file name,
/// its byte size, and its last-write timestamp — never an absolute path.
/// </summary>
public sealed record RotatingBackupFileInfo
{
    public required string FileName { get; init; }
    public required long ByteSize { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>
/// Result of one rotating-backup run. Carries a generated file name only —
/// never an absolute path (privacy invariant for API-visible data).
/// </summary>
public sealed record RotatingBackupOutcome
{
    public required bool Succeeded { get; init; }
    public required string? FileName { get; init; }
    public required int RetainedCount { get; init; }
}
