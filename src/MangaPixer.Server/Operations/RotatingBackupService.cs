namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Persistence;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

/// <summary>Location health of the rotating backups, as reported to the UI and readiness check.</summary>
public static class BackupLocationStatuses
{
    public const string Ok = "ok";
    public const string Unavailable = "unavailable";
    public const string Invalid = "invalid";
    public const string Unknown = "unknown";
}

/// <summary>
/// Singleton status + concurrency guard for rotating backups. Survives across
/// requests so the operations API can report the last scheduled or manual run.
/// Only counts, timestamps, codes, and generated file names are recorded — never paths.
/// </summary>
public sealed class RotatingBackupState
{
    private readonly object _gate = new();

    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public DateTimeOffset? LastFailureUtc { get; private set; }
    public string? LastBackupFileName { get; private set; }

    /// <summary><c>location_unavailable</c>, <c>location_invalid</c>, <c>backup_failed</c>, or null.</summary>
    public string? LastFailureCode { get; private set; }

    /// <summary>One of <see cref="BackupLocationStatuses"/>.</summary>
    public string LocationStatus { get; private set; } = BackupLocationStatuses.Unknown;

    /// <summary>When the scheduler started (drives the readiness staleness check).</summary>
    public DateTimeOffset? SchedulerStartedUtc { get; set; }

    /// <summary>
    /// Serializes backup runs between the scheduled timer and manual triggers
    /// (VACUUM INTO requires a non-existent target file).
    /// </summary>
    public SemaphoreSlim RunGate { get; } = new(1, 1);

    public void RecordSuccess(DateTimeOffset atUtc, string fileName)
    {
        lock (_gate)
        {
            LastAttemptUtc = atUtc;
            LastSuccessUtc = atUtc;
            LastFailureUtc = null;
            LastFailureCode = null;
            LastBackupFileName = fileName;
        }
    }

    public void RecordFailure(DateTimeOffset atUtc, string code = "backup_failed")
    {
        lock (_gate)
        {
            LastAttemptUtc = atUtc;
            LastFailureUtc = atUtc;
            LastFailureCode = code;
        }
    }

    /// <summary>Sets the location status and returns the previous one (for transition audits).</summary>
    public string SetLocationStatus(string status)
    {
        lock (_gate)
        {
            var previous = LocationStatus;
            LocationStatus = status;
            return previous;
        }
    }
}

/// <summary>
/// Rotating scheduled backups: writes a consistent online snapshot
/// (via <see cref="BackupService"/>, VACUUM INTO) into the effective rotating
/// directory (default <c>&lt;dataRoot&gt;/backups</c>, or an admin/operator
/// custom location) and prunes the oldest rotating snapshots beyond retention.
///
/// A custom location is re-checked before every run and is never created at
/// run time: a missing folder or marker skips the run loudly (no fallback to
/// the data disk). Rotation only ever lists / prunes files matching the strict
/// generated name <see cref="FileNamePattern"/>, never anything else a shared
/// archival folder might contain.
/// </summary>
public sealed partial class RotatingBackupService
{
    public const string FileNamePrefix = "rotating-";

    /// <summary>
    /// Generated rotating snapshot names: <c>rotating-yyyyMMdd-HHmmss[-xxxx].db</c> (plain, every version before 1.49.0)
    /// or <c>.db.gz</c> (gzip, 1.49.0 on). Both kinds are listed, pruned and restorable side by side.
    /// </summary>
    [GeneratedRegex(@"^rotating-(\d{8}-\d{6})(-[0-9a-f]{4})?\.db(\.gz)?$", RegexOptions.CultureInvariant)]
    public static partial Regex FileNamePattern();

    /// <summary>True for a generated compressed snapshot name (<c>.db.gz</c>).</summary>
    public static bool IsCompressedName(string fileName) => fileName.EndsWith(".db.gz", StringComparison.Ordinal);

    /// <summary>Temporary plain copy written in the data root while a compressed snapshot is made.</summary>
    public const string BuildTempPrefix = "rotating-build-";
    public const string BuildTempSuffix = ".tmp";

    /// <summary>Leftover <c>.db.gz.partial</c> of an interrupted compression (never a valid snapshot name).</summary>
    [GeneratedRegex(@"^rotating-\d{8}-\d{6}(-[0-9a-f]{4})?\.db\.gz\.partial$", RegexOptions.CultureInvariant)]
    private static partial Regex GzipPartialPattern();

    [GeneratedRegex(@"^rotating-build-[0-9a-f]{32}\.tmp(-wal|-shm|-journal)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildTempPattern();

    private readonly BackupService _backup;
    private readonly BackupSettingsResolver _settings;
    private readonly RotatingBackupState _state;
    private readonly BackupLocationService? _location;
    private readonly TimeProvider _time;
    private readonly ILogger<RotatingBackupService>? _logger;

    public RotatingBackupService(
        BackupService backup,
        BackupSettingsResolver settings,
        RotatingBackupState state,
        BackupLocationService? location = null,
        TimeProvider? time = null,
        ILogger<RotatingBackupService>? logger = null)
    {
        _backup = backup;
        _settings = settings;
        _state = state;
        _location = location;
        _time = time ?? TimeProvider.System;
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
        var settings = _settings.Current;
        string dir;
        if (settings.IsCustom)
        {
            // Fail loud: never create or fall back for a custom location.
            var check = _location is null
                ? null
                : await _location.CheckAsync(ct);
            if (check is null || !check.IsValid || check.NormalizedLocation is null)
            {
                var code = check?.ErrorCode == BackupLocationCodes.Invalid
                    ? BackupLocationCodes.Invalid
                    : BackupLocationCodes.Unavailable;
                _state.RecordFailure(_time.GetUtcNow(), code);
                _logger?.LogWarning(LogEvents.Backup.RotatingLocationUnavailable,
                    "Rotating backup skipped: {Code} (location kind {Kind})", code, settings.LocationKind);
                return new RotatingBackupOutcome { Succeeded = false, FileName = null, RetainedCount = 0, FailureCode = code };
            }
            dir = check.NormalizedLocation;
        }
        else
        {
            dir = settings.RotatingDirectory;
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                _state.RecordFailure(_time.GetUtcNow());
                _logger?.LogWarning(LogEvents.Backup.RotatingRunFailed,
                    "Rotating database backup failed: {Error}", ex.GetType().Name);
                return new RotatingBackupOutcome { Succeeded = false, FileName = null, RetainedCount = 0, FailureCode = "backup_failed" };
            }
            if (_location is not null)
                await _location.CheckAsync(ct);
        }

        // Leftovers of an interrupted snapshot move (never a valid snapshot name).
        // Safe here: the run gate is held, so no move is mid-copy.
        BackupSnapshotMover.DeleteStaleTemps(dir);

        var compress = settings.Compress;
        // The plain working copy lives in the data root's safety folder (same disk as the database, never a tmpfs).
        var workDir = settings.SafetyDirectory;
        if (compress)
        {
            var stale = DeleteStaleCompressionTemps(dir) + (PathsEqual(dir, workDir) ? 0 : DeleteStaleCompressionTemps(workDir));
            if (stale > 0)
                _logger?.LogInformation(LogEvents.Backup.StaleBackupTempsRemoved,
                    "Removed {Count} leftover temporary backup file(s) of an interrupted run", stale);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var extension = compress ? ".db.gz" : ".db";
        var stem = $"{FileNamePrefix}{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var fileName = stem + extension;
        var path = Path.Combine(dir, fileName);
        // Same-second collision (a manual trigger racing the scheduled run, or a very
        // fast DB): append a short suffix and re-derive the path. Both fileName AND
        // path must advance together, or the guard below never clears (VACUUM INTO
        // refuses a pre-existing target, so this branch must terminate). A plain and a
        // compressed snapshot of the same second count as the same name.
        while (File.Exists(path) || File.Exists(Path.Combine(dir, stem + ".db")) || File.Exists(Path.Combine(dir, stem + ".db.gz")))
        {
            stem = $"{FileNamePrefix}{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..4]}";
            fileName = stem + extension;
            path = Path.Combine(dir, fileName);
        }

        var result = compress
            ? await _backup.BackupCompressedAsync(path, workDir, CompressionLevel.Optimal, ct)
            : await _backup.BackupAsync(path, ct);

        if (!result.Succeeded)
        {
            _state.RecordFailure(_time.GetUtcNow());
            _logger?.LogWarning(LogEvents.Backup.RotatingRunFailed, "Rotating database backup failed: {Error}", result.Error ?? "unknown");
            return new RotatingBackupOutcome { Succeeded = false, FileName = fileName, RetainedCount = CountBackups(dir), FailureCode = "backup_failed" };
        }

        _state.RecordSuccess(_time.GetUtcNow(), fileName);
        var pruned = Prune(dir, settings.RetentionCount);
        _logger?.LogInformation(
            LogEvents.Backup.RotatingRunCompleted, "Rotating backup completed ({FileName}, retained {Count}, location kind {Kind}); pruning removed {Pruned} old snapshot(s).",
            fileName, CountBackups(dir), settings.LocationKind, pruned);

        return new RotatingBackupOutcome { Succeeded = true, FileName = fileName, RetainedCount = CountBackups(dir) };
    }

    /// <summary>
    /// Deletes the oldest generated rotating snapshots beyond the retention count.
    /// File names embed a UTC timestamp, so ordinal name order is chronological.
    /// Never touches a file that does not match <see cref="FileNamePattern"/>
    /// (pre-migration-*, pre-restore-*, the marker, anything an archival share holds).
    /// </summary>
    public int Prune(string directory) => Prune(directory, _settings.Current.RetentionCount);

    internal static int Prune(string directory, int retentionCount)
    {
        var keep = Math.Max(1, retentionCount);
        var deleted = 0;
        foreach (var old in EnumerateGenerated(directory).Skip(keep))
        {
            try { File.Delete(old.FullName); deleted++; }
            catch (IOException) { /* in use — retried on the next run */ }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    public int CountBackups(string directory) => EnumerateGenerated(directory).Count();

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Deletes the leftovers of an interrupted compression (strict names only: the <c>.db.gz.partial</c> next to the
    /// snapshots and the <c>rotating-build-*.tmp</c> plain copy in the work folder). Callers hold the run gate.
    /// </summary>
    internal static int DeleteStaleCompressionTemps(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            var deleted = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(FileNamePrefix + "*"))
            {
                if (!GzipPartialPattern().IsMatch(file.Name) && !BuildTempPattern().IsMatch(file.Name))
                    continue;
                try { file.Delete(); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Enumerates the on-disk rotating snapshots newest-first, exposing only the
    /// generated file name, byte size, and last-write timestamp — NEVER an
    /// absolute path (privacy invariant for API-visible data). File names embed
    /// a UTC timestamp, so ordinal name order is chronological; sorting on the
    /// name (not the mtime) keeps the list stable and matches the pruning order.
    /// </summary>
    public IReadOnlyList<RotatingBackupFileInfo> ListBackups(string directory) =>
        EnumerateGenerated(directory)
            .Select(fi => new RotatingBackupFileInfo
            {
                FileName = fi.Name,
                ByteSize = fi.Length,
                TimestampUtc = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero),
            })
            .ToList();

    /// <summary>
    /// The UTC timestamp embedded in the newest generated snapshot name, or
    /// null. Seeds the scheduler so frequent restarts do not each produce a
    /// backup and silently rotate out the daily history.
    /// </summary>
    public static DateTimeOffset? NewestSnapshotTimestamp(string directory)
    {
        var newest = EnumerateGenerated(directory).FirstOrDefault();
        return newest is null ? null : ParseTimestamp(newest.Name);
    }

    public static DateTimeOffset? ParseTimestamp(string fileName)
    {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
            return null;
        return DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;
    }

    /// <summary>Generated rotating snapshots in <paramref name="directory"/>, newest first (strict name match).</summary>
    internal static List<FileInfo> EnumerateGenerated(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return new List<FileInfo>();
            // Newest first by the UTC timestamp in the name (plain .db and .db.gz mix), then the full name for a
            // same-second pair. Ordinal name order alone would put "-xxxx.db" and ".db.gz" in the wrong order.
            return new DirectoryInfo(directory)
                .EnumerateFiles(FileNamePrefix + "*")
                .Select(fi => (File: fi, Match: FileNamePattern().Match(fi.Name)))
                .Where(x => x.Match.Success)
                .OrderByDescending(x => x.Match.Groups[1].Value, StringComparer.Ordinal)
                .ThenByDescending(x => x.File.Name, StringComparer.Ordinal)
                .Select(x => x.File)
                .ToList();
        }
        catch (IOException) { return new List<FileInfo>(); }
        catch (UnauthorizedAccessException) { return new List<FileInfo>(); }
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

    /// <summary><c>location_unavailable</c>, <c>location_invalid</c>, <c>backup_failed</c>, or null on success.</summary>
    public string? FailureCode { get; init; }
}

/// <summary>
/// Retention for the local safety snapshots in <c>&lt;dataRoot&gt;/backups</c>:
/// keeps the newest <see cref="KeepPerKind"/> of each kind, pruning only after
/// a new snapshot of the SAME kind succeeded, and only files matching the
/// strict generated name of that kind.
/// </summary>
public static partial class SafetySnapshotPruner
{
    public const int KeepPerKind = 3;

    /// <summary><c>pre-migration-yyyyMMdd-HHmmss[-xxxx].db</c>: the suffix (1.50.0) separates two snapshots of the same second.</summary>
    [GeneratedRegex(@"^pre-migration-(\d{8}-\d{6})(-[0-9a-f]{4})?\.db$", RegexOptions.CultureInvariant)]
    public static partial Regex PreMigrationPattern();

    /// <summary><c>pre-restore-yyyyMMdd-HHmmss[-xxxx].db</c>: the suffix (1.50.0) separates two snapshots of the same second.</summary>
    [GeneratedRegex(@"^pre-restore-(\d{8}-\d{6})(-[0-9a-f]{4})?\.db$", RegexOptions.CultureInvariant)]
    public static partial Regex PreRestorePattern();

    /// <summary>
    /// The file name for a new safety snapshot of <paramref name="kind"/> (<c>pre-restore</c> / <c>pre-migration</c>) taken at
    /// <paramref name="utcNow"/>: <c>{kind}-yyyyMMdd-HHmmss.db</c>, or with a <c>-xxxx</c> suffix when that name is already taken
    /// (<c>VACUUM INTO</c> refuses an existing target, so a second snapshot within the same second used to fail).
    /// </summary>
    public static string UniqueName(string directory, string kind, DateTime utcNow)
    {
        var stem = $"{kind}-{utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var name = stem + ".db";
        while (File.Exists(Path.Combine(directory, name)))
            name = $"{stem}-{Guid.NewGuid().ToString("N")[..4]}.db";
        return name;
    }

    public static int PrunePreMigration(string directory) => Prune(directory, "pre-migration-*.db", PreMigrationPattern());

    public static int PrunePreRestore(string directory) => Prune(directory, "pre-restore-*.db", PreRestorePattern());

    private static int Prune(string directory, string glob, Regex pattern)
    {
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            var deleted = 0;
            var old = new DirectoryInfo(directory).EnumerateFiles(glob)
                .Select(fi => (File: fi, Match: pattern.Match(fi.Name)))
                .Where(x => x.Match.Success)
                // Newest first by the UTC timestamp in the name. Within one second the suffixed name is the LATER snapshot (the
                // suffix is added only when the plain name existed), although ordinal name order ranks it older ('-' < '.');
                // then the write time, then the name, so the order is total and a fresh snapshot is never the one pruned.
                .OrderByDescending(x => x.Match.Groups[1].Value, StringComparer.Ordinal)
                .ThenByDescending(x => x.Match.Groups[2].Success)
                .ThenByDescending(x => x.File.LastWriteTimeUtc)
                .ThenByDescending(x => x.File.Name, StringComparer.Ordinal)
                .Select(x => x.File)
                .Skip(KeepPerKind)
                .ToList();
            foreach (var file in old)
            {
                try { file.Delete(); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
