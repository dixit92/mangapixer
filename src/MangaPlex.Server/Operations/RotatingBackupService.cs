namespace com.lifepixer.mangaplex.Server.Operations;

using com.lifepixer.mangaplex.Server.Persistence;
using System.IO;

/// <summary>
/// Configuration for the scheduled rotating database backups.
/// Interval and retention are admin-configurable via configuration
/// (MangaPlex:Backups:IntervalHours / RetentionCount / Enabled); defaults are
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

    private readonly MangaPlexDbContext _db;
    private readonly BackupService _backup;
    private readonly RotatingBackupOptions _options;
    private readonly RotatingBackupState _state;
    private readonly ILogger<RotatingBackupService>? _logger;

    public RotatingBackupService(
        MangaPlexDbContext db,
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
        while (File.Exists(path))
            fileName = $"{FileNamePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.db";

        var result = await _backup.BackupAsync(path, ct);

        if (!result.Succeeded)
        {
            _state.RecordFailure(DateTimeOffset.UtcNow);
            _logger?.LogWarning("Rotating database backup failed: {Error}", result.Error ?? "unknown");
            return new RotatingBackupOutcome { Succeeded = false, FileName = fileName, RetainedCount = CountBackups(dir) };
        }

        _state.RecordSuccess(DateTimeOffset.UtcNow, fileName);
        var pruned = Prune(dir);
        if (pruned > 0)
            _logger?.LogInformation("Rotating backup pruning removed {Count} old snapshot(s).", pruned);

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
