namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Extensions.Hosting;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>Per-file outcome of a snapshot move.</summary>
public static class BackupSnapshotMoveOutcomes
{
    /// <summary>Copied, verified, renamed into place, source deleted.</summary>
    public const string Moved = "moved";

    /// <summary>A byte-identical file of the same name was already there; the source was deleted.</summary>
    public const string AlreadyPresent = "already_present";

    /// <summary>The source vanished before it was copied (nothing to do).</summary>
    public const string SourceMissing = "source_missing";

    /// <summary>A DIFFERENT file of the same name is in the new location; both kept, source not moved.</summary>
    public const string NameConflict = "name_conflict";

    /// <summary>The copy did not match the source (size or SHA-256); the copy was discarded.</summary>
    public const string VerifyFailed = "verify_failed";

    /// <summary>The new location could not be written (or the source read); nothing changed.</summary>
    public const string CopyFailed = "copy_failed";

    /// <summary>The verified copy is in place but the original could not be deleted: it exists in both.</summary>
    public const string SourceDeleteFailed = "source_delete_failed";

    /// <summary>The move was stopped (server shutdown) before this file was moved.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>Where a snapshot is after the move (for the UI's "which files stayed where").</summary>
public static class BackupSnapshotMoveLocations
{
    public const string New = "new";
    public const string Previous = "previous";
    public const string Both = "both";
}

/// <summary>Move job states. Kept in memory only: a restart returns to <c>idle</c>.</summary>
public static class BackupSnapshotMoveStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

/// <summary>Result of moving one snapshot file.</summary>
public sealed record BackupSnapshotFileMoveResult(string FileName, string Outcome)
{
    public bool IsIssue => Outcome is not (BackupSnapshotMoveOutcomes.Moved
        or BackupSnapshotMoveOutcomes.AlreadyPresent or BackupSnapshotMoveOutcomes.SourceMissing);

    /// <summary>Where the snapshot lives now.</summary>
    public string Location => Outcome switch
    {
        BackupSnapshotMoveOutcomes.SourceDeleteFailed => BackupSnapshotMoveLocations.Both,
        BackupSnapshotMoveOutcomes.NameConflict => BackupSnapshotMoveLocations.Previous,
        _ when IsIssue => BackupSnapshotMoveLocations.Previous,
        _ => BackupSnapshotMoveLocations.New,
    };
}

/// <summary>
/// File-level engine of the snapshot move. Moves ONE generated rotating
/// snapshot (strict <see cref="RotatingBackupService.FileNamePattern"/> names
/// only) between folders that may be on different filesystems:
/// copy to a temp name in the destination (hashing the bytes read), flush to
/// disk, re-read the copy and compare size + SHA-256, rename atomically to the
/// final name without overwriting, and only then delete the source. Any
/// failure before the rename discards the temp copy and leaves the source
/// untouched; a crash leaves at worst a temp file (never a valid snapshot
/// name, cleaned up by <see cref="DeleteStaleTemps"/>) or the same snapshot in
/// both folders, so a snapshot is never lost.
/// </summary>
public static partial class BackupSnapshotMover
{
    private const int BufferSize = 1 << 20;

    /// <summary>Temp names written during a move: never matched by the rotating name pattern.</summary>
    [GeneratedRegex(@"^rotating-move-[0-9a-f]{32}\.partial$", RegexOptions.CultureInvariant)]
    public static partial Regex TempNamePattern();

    /// <summary>The rotating snapshots a move would take from <paramref name="directory"/>, newest first.</summary>
    public static IReadOnlyList<FileInfo> ListCandidates(string directory) =>
        RotatingBackupService.EnumerateGenerated(directory);

    /// <summary>Count and total bytes of the rotating snapshots in <paramref name="directory"/>.</summary>
    public static (int Count, long Bytes) Summarize(string directory)
    {
        var files = ListCandidates(directory);
        long bytes = 0;
        foreach (var f in files)
        {
            try { bytes += f.Length; }
            catch (IOException) { }
        }
        return (files.Count, bytes);
    }

    /// <summary>
    /// Deletes leftover temp files of an interrupted move (strict temp name
    /// only). Callers hold the rotating run gate, so no move is mid-copy.
    /// </summary>
    public static int DeleteStaleTemps(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            var deleted = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("rotating-move-*.partial"))
            {
                if (!TempNamePattern().IsMatch(file.Name))
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
    /// Moves one snapshot. <paramref name="onBytes"/> reports copied bytes;
    /// <paramref name="afterTempWritten"/> is a test seam that runs between the
    /// flushed copy and its verification. Never throws for IO problems: the
    /// outcome says what happened and where the snapshot is.
    /// </summary>
    public static async Task<BackupSnapshotFileMoveResult> MoveFileAsync(
        string sourceDirectory,
        string destinationDirectory,
        string fileName,
        Action<long>? onBytes = null,
        Func<string, CancellationToken, Task>? afterTempWritten = null,
        CancellationToken ct = default)
    {
        if (!RotatingBackupService.FileNamePattern().IsMatch(fileName))
            throw new ArgumentException("Only generated rotating snapshot names can be moved.", nameof(fileName));

        BackupSnapshotFileMoveResult Result(string outcome) => new(fileName, outcome);

        var source = Path.Combine(sourceDirectory, fileName);
        var destination = Path.Combine(destinationDirectory, fileName);
        if (!File.Exists(source))
            return Result(BackupSnapshotMoveOutcomes.SourceMissing);

        // Never overwrite: an identical file means an earlier move got as far
        // as the rename (e.g. a restart before the source delete).
        if (File.Exists(destination))
        {
            try
            {
                if (!await SameContentAsync(source, destination, ct))
                    return Result(BackupSnapshotMoveOutcomes.NameConflict);
            }
            catch (OperationCanceledException) { return Result(BackupSnapshotMoveOutcomes.Cancelled); }
            catch (Exception) { return Result(BackupSnapshotMoveOutcomes.CopyFailed); }
            return Result(TryDelete(source)
                ? BackupSnapshotMoveOutcomes.AlreadyPresent
                : BackupSnapshotMoveOutcomes.SourceDeleteFailed);
        }

        var temp = Path.Combine(destinationDirectory, $"rotating-move-{Guid.NewGuid():N}.partial");
        byte[] sourceHash;
        long sourceLength;
        try
        {
            (sourceHash, sourceLength) = await CopyAsync(source, temp, onBytes, ct);
            if (afterTempWritten is not null)
                await afterTempWritten(temp, ct);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return Result(BackupSnapshotMoveOutcomes.Cancelled);
        }
        catch (Exception)
        {
            TryDelete(temp);
            return Result(BackupSnapshotMoveOutcomes.CopyFailed);
        }

        try
        {
            var (tempHash, tempLength) = await HashAsync(temp, ct);
            var verified = tempLength == sourceLength &&
                new FileInfo(source).Length == sourceLength &&
                CryptographicOperations.FixedTimeEquals(tempHash, sourceHash);
            if (!verified)
            {
                TryDelete(temp);
                return Result(BackupSnapshotMoveOutcomes.VerifyFailed);
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            return Result(BackupSnapshotMoveOutcomes.Cancelled);
        }
        catch (Exception)
        {
            TryDelete(temp);
            return Result(BackupSnapshotMoveOutcomes.VerifyFailed);
        }

        try
        {
            // Same directory, so an atomic rename; overwrite:false refuses a
            // file that appeared under the final name in the meantime.
            File.Move(temp, destination, overwrite: false);
        }
        catch (Exception)
        {
            TryDelete(temp);
            return Result(File.Exists(destination)
                ? BackupSnapshotMoveOutcomes.NameConflict
                : BackupSnapshotMoveOutcomes.CopyFailed);
        }

        return Result(TryDelete(source)
            ? BackupSnapshotMoveOutcomes.Moved
            : BackupSnapshotMoveOutcomes.SourceDeleteFailed);
    }

    private static async Task<(byte[] Hash, long Length)> CopyAsync(
        string source, string temp, Action<long>? onBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                length += read;
                onBytes?.Invoke(read);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }
        return (hash.GetHashAndReset(), length);
    }

    private static async Task<(byte[] Hash, long Length)> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(input, ct);
        return (hash, input.Length);
    }

    private static async Task<bool> SameContentAsync(string a, string b, CancellationToken ct)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        var (hashA, _) = await HashAsync(a, ct);
        var (hashB, _) = await HashAsync(b, ct);
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Background job that moves the existing rotating snapshots to the new
/// location after a backup location change (1.23.0), with progress for the
/// Backup settings card. One job at a time; state is in memory only (a restart
/// ends the job, and every snapshot not yet moved is still a valid file in
/// the previous folder). Each file is moved while holding the rotating run
/// gate, so a scheduled or manual backup (and its pruning) never interleaves
/// with a copy, yet can run between two files. After the last file, the
/// normal retention is applied in the new location. Logs and audit carry
/// counts and codes only, never a folder.
/// </summary>
public sealed class BackupSnapshotMoveService
{
    private readonly BackupSettingsResolver _settings;
    private readonly RotatingBackupState _state;
    private readonly IServiceScopeFactory? _scopes;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupSnapshotMoveService>? _logger;
    private readonly object _gate = new();

    private string _jobState = BackupSnapshotMoveStates.Idle;
    private string? _fromKind;
    private string? _toKind;
    private int _totalFiles;
    private int _filesDone;
    private long _totalBytes;
    private long _bytesDone;
    private int _movedCount;
    private int _prunedCount;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _finishedUtc;
    private List<BackupSnapshotFileMoveResult> _issues = new();
    private Task? _job;

    public BackupSnapshotMoveService(
        BackupSettingsResolver settings,
        RotatingBackupState state,
        IServiceScopeFactory? scopes = null,
        IHostApplicationLifetime? lifetime = null,
        TimeProvider? time = null,
        ILogger<BackupSnapshotMoveService>? logger = null)
    {
        _settings = settings;
        _state = state;
        _scopes = scopes;
        _lifetime = lifetime;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Test seam, see <see cref="BackupSnapshotMover.MoveFileAsync"/>.</summary>
    internal Func<string, CancellationToken, Task>? AfterTempWritten { get; set; }

    public bool IsRunning
    {
        get { lock (_gate) return _jobState == BackupSnapshotMoveStates.Running; }
    }

    /// <summary>The running (or last) job, for tests and orderly waits.</summary>
    public Task CurrentJob
    {
        get { lock (_gate) return _job ?? Task.CompletedTask; }
    }

    /// <summary>
    /// Starts moving the rotating snapshots from <paramref name="sourceDirectory"/>
    /// to <paramref name="destinationDirectory"/> in the background. Returns
    /// false when a move is already running or the folders are the same.
    /// </summary>
    public bool TryStart(string sourceDirectory, string destinationDirectory, string? actor)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(sourceDirectory),
                Path.TrimEndingDirectorySeparator(destinationDirectory), StringComparison.Ordinal))
            return false;

        var safety = _settings.Current.SafetyDirectory;
        lock (_gate)
        {
            if (_jobState == BackupSnapshotMoveStates.Running)
                return false;
            _jobState = BackupSnapshotMoveStates.Running;
            _fromKind = KindOf(sourceDirectory, safety);
            _toKind = KindOf(destinationDirectory, safety);
            _totalFiles = 0;
            _filesDone = 0;
            _totalBytes = 0;
            _bytesDone = 0;
            _movedCount = 0;
            _prunedCount = 0;
            _startedUtc = _time.GetUtcNow();
            _finishedUtc = null;
            _issues = new List<BackupSnapshotFileMoveResult>();
            var ct = _lifetime?.ApplicationStopping ?? CancellationToken.None;
            _job = Task.Run(() => RunAsync(sourceDirectory, destinationDirectory, actor, ct), CancellationToken.None);
            return true;
        }
    }

    public BackupSnapshotMoveStatusDto GetStatus()
    {
        lock (_gate)
        {
            return new BackupSnapshotMoveStatusDto
            {
                State = _jobState,
                FromKind = _fromKind,
                ToKind = _toKind,
                TotalFiles = _totalFiles,
                FilesDone = _filesDone,
                TotalBytes = _totalBytes,
                BytesDone = Interlocked.Read(ref _bytesDone),
                MovedCount = _movedCount,
                PrunedCount = _prunedCount,
                StartedUtc = _startedUtc,
                FinishedUtc = _finishedUtc,
                Issues = _issues
                    .Select(i => new BackupSnapshotMoveIssueDto { FileName = i.FileName, Code = i.Outcome, Location = i.Location })
                    .ToList(),
            };
        }
    }

    private async Task RunAsync(string source, string destination, string? actor, CancellationToken ct)
    {
        var cancelled = false;
        try
        {
            var files = BackupSnapshotMover.ListCandidates(source);
            long total = 0;
            foreach (var f in files)
            {
                try { total += f.Length; }
                catch (IOException) { }
            }
            lock (_gate)
            {
                _totalFiles = files.Count;
                _totalBytes = total;
            }
            _logger?.LogInformation(LogEvents.Backup.SnapshotMoveStarted,
                "Backup snapshot move started ({Count} file(s), {Bytes} bytes, {From} to {To})",
                files.Count, total, _fromKind, _toKind);

            if (files.Count > 0 && _toKind == EffectiveBackupSettings.KindDefault)
            {
                // The default folder is created on demand, like a backup run does;
                // a custom folder was validated (and its marker written) on save.
                try { Directory.CreateDirectory(destination); }
                catch (Exception) { /* each file then reports copy_failed */ }
            }

            for (var i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    MarkCancelled(files.Skip(i));
                    break;
                }

                BackupSnapshotFileMoveResult result;
                try
                {
                    await _state.RunGate.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    MarkCancelled(files.Skip(i));
                    break;
                }
                try
                {
                    if (i == 0)
                        BackupSnapshotMover.DeleteStaleTemps(destination);
                    result = await BackupSnapshotMover.MoveFileAsync(
                        source, destination, files[i].Name,
                        n => Interlocked.Add(ref _bytesDone, n),
                        AfterTempWritten, ct);
                }
                finally
                {
                    _state.RunGate.Release();
                }

                lock (_gate)
                {
                    _filesDone++;
                    if (result.IsIssue) _issues.Add(result);
                    else if (result.Outcome != BackupSnapshotMoveOutcomes.SourceMissing) _movedCount++;
                }
                if (result.Outcome == BackupSnapshotMoveOutcomes.Cancelled)
                {
                    cancelled = true;
                    MarkCancelled(files.Skip(i + 1));
                    break;
                }
            }

            if (!cancelled)
                await ApplyRetentionAsync(destination, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Backup.SnapshotMoveFailed,
                "Backup snapshot move failed: {Error}", ex.GetType().Name);
        }

        int moved, issues, pruned;
        lock (_gate)
        {
            moved = _movedCount;
            issues = _issues.Count;
            pruned = _prunedCount;
        }

        if (issues == 0 && !cancelled)
            _logger?.LogInformation(LogEvents.Backup.SnapshotMoveCompleted,
                "Backup snapshot move completed: {Moved} moved, {Pruned} pruned by retention", moved, pruned);
        else
            _logger?.LogWarning(LogEvents.Backup.SnapshotMoveFailed,
                "Backup snapshot move finished with issues: {Moved} moved, {Issues} kept in the previous location, cancelled {Cancelled}",
                moved, issues, cancelled);

        // Audit before the state leaves "running", so a caller that sees the
        // final state also sees the audit row.
        await AuditAsync(issues == 0 && !cancelled, actor);

        lock (_gate)
        {
            _jobState = cancelled ? BackupSnapshotMoveStates.Cancelled : BackupSnapshotMoveStates.Completed;
            _finishedUtc = _time.GetUtcNow();
        }
    }

    private async Task ApplyRetentionAsync(string destination, CancellationToken ct)
    {
        await _state.RunGate.WaitAsync(ct);
        try
        {
            var current = _settings.Current;
            // Only while the destination is still the managed folder.
            if (!string.Equals(current.RotatingDirectory, destination, StringComparison.Ordinal))
                return;
            var pruned = RotatingBackupService.Prune(destination, current.RetentionCount);
            lock (_gate) _prunedCount = pruned;
        }
        finally
        {
            _state.RunGate.Release();
        }
    }

    private void MarkCancelled(IEnumerable<FileInfo> remaining)
    {
        lock (_gate)
        {
            foreach (var f in remaining)
                _issues.Add(new BackupSnapshotFileMoveResult(f.Name, BackupSnapshotMoveOutcomes.Cancelled));
        }
    }

    private async Task AuditAsync(bool success, string? actor)
    {
        if (_scopes is null)
            return;
        try
        {
            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetService<AuditService>();
            if (audit is not null)
                await audit.RecordAsync(AuditActions.BackupSnapshotsMoved,
                    success ? AuditResults.Success : AuditResults.Failure, actor, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Backup.SnapshotMoveFailed,
                "Backup snapshot move audit could not be recorded: {Error}", ex.GetType().Name);
        }
    }

    private static string KindOf(string directory, string safetyDirectory) =>
        string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(safetyDirectory), StringComparison.Ordinal)
            ? EffectiveBackupSettings.KindDefault
            : EffectiveBackupSettings.KindCustom;
}
