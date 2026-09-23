namespace com.lifepixer.mangapixer.Server.Operations;

using System.IO;
using System.Text.Json;

/// <summary>Path rules a backup location is judged by (injectable so Windows rules are testable on Linux).</summary>
public enum BackupPathFlavor
{
    Unix,
    Windows,
}

/// <summary>
/// Error / warning codes of the backup location validator. Coarse by design:
/// they never echo a resolved path (privacy invariant, and they must not turn
/// the endpoint into a detailed filesystem oracle).
/// </summary>
public static class BackupLocationCodes
{
    public const string NotAbsolute = "location_not_absolute";
    public const string Invalid = "location_invalid";
    public const string OverlapsProtected = "location_overlaps_protected";
    public const string Forbidden = "location_forbidden";
    public const string ParentMissing = "location_parent_missing";
    public const string NotWritable = "location_not_writable";
    public const string InUse = "location_in_use";
    public const string Unavailable = "location_unavailable";
    public const string LowFreeSpace = "low_free_space";
}

/// <summary>
/// The directories a custom backup location must never overlap, plus the
/// current database size (for the free-space warning). Built per check by
/// <see cref="BackupLocationService"/> from the storage roots and the
/// registered library roots.
/// </summary>
public sealed record BackupLocationContext
{
    public required string DataRoot { get; init; }
    public IReadOnlyList<string> ProtectedRoots { get; init; } = Array.Empty<string>();
    public long DatabaseBytes { get; init; }
}

/// <summary>Outcome of validating a candidate location (PUT) or re-checking the effective one (before a run).</summary>
public sealed record BackupLocationValidation
{
    public required bool IsValid { get; init; }
    public string? ErrorCode { get; init; }
    public string? NormalizedLocation { get; init; }

    /// <summary>The leaf folder did not exist and is (or, for validateOnly, would be) created.</summary>
    public bool WillCreate { get; init; }

    /// <summary>The marker id the location carries (existing, adopted) or should carry (new).</summary>
    public string? MarkerId { get; init; }

    /// <summary>The location already carries a marker file.</summary>
    public bool MarkerExists { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static BackupLocationValidation Fail(string code, string? normalized = null) =>
        new() { IsValid = false, ErrorCode = code, NormalizedLocation = normalized };
}

/// <summary>What the marker file of a location says.</summary>
public readonly record struct BackupMarkerRead(bool Exists, string? MarkerId);

/// <summary>
/// Filesystem side of the location checks, behind an interface so the pure
/// path rules can be exercised for both path flavors without touching disk.
/// </summary>
public interface IBackupLocationFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>Resolves symlinks / junctions along the existing part of <paramref name="path"/>.</summary>
    string ResolveLinks(string path);

    void CreateDirectory(string path);

    /// <summary>Removes a directory the probe created, only if it is empty.</summary>
    void DeleteEmptyDirectory(string path);

    /// <summary>Writes, flushes, reads back and deletes a probe file. Throws on any failure.</summary>
    void WriteProbe(string directory);

    BackupMarkerRead ReadMarker(string directory);

    void WriteMarker(string directory, string markerId, DateTimeOffset createdUtc);

    long? GetAvailableFreeBytes(string directory);
}

/// <summary>
/// Server-side validation of an admin- or operator-chosen rotating backup
/// directory. Checks run in order and the first failure wins:
/// 1 syntax, 2 normalize, 3 resolve links, 4 protected-root overlap (both
/// directions, lexical and resolved), 5 system / ephemeral deny-list,
/// 6 parent exists, 7 write probe, 8 marker ownership, 9 free space (warning).
/// <see cref="CheckBeforeRun"/> repeats the cheap subset before every backup
/// run and never creates anything, so an unmounted mount point is caught
/// instead of silently filling the local disk.
/// </summary>
public sealed class BackupLocationValidator
{
    public const string MarkerFileName = ".mangapixer-backups.json";
    public const string ProbeFilePrefix = ".mangapixer-probe-";
    public const int MaxLength = 1024;

    private static readonly string[] UnixSystemRoots =
    {
        "/proc", "/sys", "/dev", "/run", "/tmp", "/var/tmp", "/dev/shm",
        "/etc", "/usr", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/boot",
    };

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IBackupLocationFileSystem _fs;
    private readonly BackupPathFlavor _flavor;
    private readonly IReadOnlyList<string> _systemRoots;

    public BackupLocationValidator(
        IBackupLocationFileSystem fs,
        BackupPathFlavor flavor,
        IReadOnlyList<string>? systemRoots = null)
    {
        _fs = fs;
        _flavor = flavor;
        _systemRoots = systemRoots ?? DefaultSystemRoots(flavor);
    }

    /// <summary>The validator for the running OS, on the real filesystem.</summary>
    public static BackupLocationValidator ForCurrentPlatform() => new(
        new PhysicalBackupLocationFileSystem(),
        OperatingSystem.IsWindows() ? BackupPathFlavor.Windows : BackupPathFlavor.Unix);

    public BackupPathFlavor Flavor => _flavor;

    private StringComparison Comparison =>
        _flavor == BackupPathFlavor.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private char Separator => _flavor == BackupPathFlavor.Windows ? '\\' : '/';

    /// <summary>
    /// Full validation of a candidate location (settings PUT). With
    /// <paramref name="validateOnly"/> a leaf created only for the write probe
    /// is removed again. Never writes the marker (the caller does, after the
    /// settings are accepted).
    /// </summary>
    public BackupLocationValidation Validate(
        string? location,
        BackupLocationContext context,
        string? expectedMarkerId,
        bool adoptExistingMarker,
        bool validateOnly)
    {
        var staticResult = CheckStatic(location, context);
        if (staticResult.ErrorCode is not null)
            return BackupLocationValidation.Fail(staticResult.ErrorCode);
        var normalized = staticResult.Normalized!;

        // 6. Only the leaf may be created: a typo cannot build a deep tree on
        //    the wrong disk, and an unmounted mount point's parent check fails early.
        var parent = ParentOf(normalized);
        if (parent is null || !SafeDirectoryExists(parent))
            return BackupLocationValidation.Fail(BackupLocationCodes.ParentMissing, normalized);

        // 7. Write probe (creates the leaf when missing).
        var existed = SafeDirectoryExists(normalized);
        try
        {
            if (!existed)
                _fs.CreateDirectory(normalized);
            _fs.WriteProbe(normalized);
        }
        catch (Exception)
        {
            if (!existed)
                TryDeleteEmpty(normalized);
            return BackupLocationValidation.Fail(BackupLocationCodes.NotWritable, normalized);
        }

        // 8. Marker ownership: another instance (or a DB restored from one)
        //    already uses this folder unless the admin explicitly takes it over.
        BackupMarkerRead marker;
        try { marker = _fs.ReadMarker(normalized); }
        catch (Exception) { marker = new BackupMarkerRead(true, null); }

        string markerId;
        if (!marker.Exists)
        {
            markerId = expectedMarkerId ?? NewMarkerId();
        }
        else if (marker.MarkerId is not null && string.Equals(marker.MarkerId, expectedMarkerId, StringComparison.Ordinal))
        {
            markerId = marker.MarkerId;
        }
        else if (adoptExistingMarker)
        {
            markerId = expectedMarkerId ?? marker.MarkerId ?? NewMarkerId();
        }
        else
        {
            if (!existed)
                TryDeleteEmpty(normalized);
            return BackupLocationValidation.Fail(BackupLocationCodes.InUse, normalized);
        }

        // 9. Free space: warning only.
        var warnings = new List<string>();
        try
        {
            var free = _fs.GetAvailableFreeBytes(normalized);
            if (free is { } bytes && context.DatabaseBytes > 0 && bytes < 2 * context.DatabaseBytes)
                warnings.Add(BackupLocationCodes.LowFreeSpace);
        }
        catch (Exception)
        {
            // Free space is advisory; an unknown value is not an error.
        }

        if (validateOnly && !existed)
            TryDeleteEmpty(normalized);

        return new BackupLocationValidation
        {
            IsValid = true,
            NormalizedLocation = normalized,
            WillCreate = !existed,
            MarkerId = markerId,
            MarkerExists = marker.Exists,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Re-check before every run (scheduled or manual) and at startup. Never
    /// creates the directory. Returns <c>location_invalid</c> when the location
    /// now fails the static rules (syntax, protected overlap, deny-list) and
    /// <c>location_unavailable</c> when the folder or its marker is missing or
    /// foreign.
    /// </summary>
    public BackupLocationValidation CheckBeforeRun(
        string? location,
        BackupLocationContext context,
        string? expectedMarkerId)
    {
        var staticResult = CheckStatic(location, context);
        if (staticResult.ErrorCode is not null)
            return BackupLocationValidation.Fail(BackupLocationCodes.Invalid);
        var normalized = staticResult.Normalized!;

        if (!SafeDirectoryExists(normalized))
            return BackupLocationValidation.Fail(BackupLocationCodes.Unavailable, normalized);

        BackupMarkerRead marker;
        try { marker = _fs.ReadMarker(normalized); }
        catch (Exception) { return BackupLocationValidation.Fail(BackupLocationCodes.Unavailable, normalized); }

        if (!marker.Exists || expectedMarkerId is null ||
            !string.Equals(marker.MarkerId, expectedMarkerId, StringComparison.Ordinal))
        {
            return new BackupLocationValidation
            {
                IsValid = false,
                ErrorCode = BackupLocationCodes.Unavailable,
                NormalizedLocation = normalized,
                MarkerExists = marker.Exists,
                MarkerId = marker.MarkerId,
            };
        }

        return new BackupLocationValidation
        {
            IsValid = true,
            NormalizedLocation = normalized,
            MarkerId = marker.MarkerId,
            MarkerExists = true,
        };
    }

    /// <summary>Checks 1-5 (no filesystem writes). Returns the normalized path or an error code.</summary>
    public (string? Normalized, string? ErrorCode) CheckStatic(string? location, BackupLocationContext context)
    {
        // 1 + 2. Syntax and lexical normalization.
        var (normalized, error) = Normalize(location);
        if (error is not null)
            return (null, error);

        // 4 + 5 on the lexical path.
        var lexicalError = CheckRoots(normalized!, context);
        if (lexicalError is not null)
            return (null, lexicalError);

        // 3. Resolve links, then 4 + 5 again on the resolved path, so a
        //    symlink / junction cannot smuggle the location into a protected root.
        string resolved;
        try { resolved = _fs.ResolveLinks(normalized!); }
        catch (Exception) { resolved = normalized!; }

        if (!string.Equals(resolved, normalized, Comparison))
        {
            var (resolvedNormalized, resolvedError) = Normalize(resolved);
            if (resolvedError is not null)
                return (null, BackupLocationCodes.Invalid);
            var err = CheckRoots(resolvedNormalized!, context, resolveProtected: true);
            if (err is not null)
                return (null, err);
        }
        else
        {
            var err = CheckRoots(normalized!, context, resolveProtected: true);
            if (err is not null)
                return (null, err);
        }

        return (normalized, null);
    }

    /// <summary>
    /// Syntax check + lexical normalization. Rejects rather than normalizes
    /// anything an admin never needs (<c>..</c>, device prefixes, reserved
    /// names, alternate data streams).
    /// </summary>
    public (string? Normalized, string? ErrorCode) Normalize(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) || location.Length > MaxLength)
            return (null, BackupLocationCodes.Invalid);
        foreach (var c in location)
        {
            if (char.IsControl(c))
                return (null, BackupLocationCodes.Invalid);
        }

        return _flavor == BackupPathFlavor.Windows ? NormalizeWindows(location) : NormalizeUnix(location);
    }

    private static (string?, string?) NormalizeUnix(string location)
    {
        if (!location.StartsWith('/'))
            return (null, BackupLocationCodes.NotAbsolute);

        var segments = new List<string>();
        foreach (var segment in location.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                return (null, BackupLocationCodes.Invalid);
            segments.Add(segment);
        }

        return ("/" + string.Join('/', segments), null);
    }

    private static (string?, string?) NormalizeWindows(string location)
    {
        var path = location.Replace('/', '\\');

        // Device namespaces are never a valid backup folder.
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return (null, BackupLocationCodes.Invalid);

        string root;
        string rest;
        var minSegments = 0;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC: \\server\share\folder (at least one folder below the share).
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return (null, BackupLocationCodes.Invalid);
            if (!IsValidWindowsSegment(parts[0]) || !IsValidWindowsSegment(parts[1]))
                return (null, BackupLocationCodes.Invalid);
            root = @"\\" + parts[0] + "\\" + parts[1] + "\\";
            rest = string.Join('\\', parts.Skip(2));
            minSegments = 1;
        }
        else if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            // Drive-relative "C:foo" is not fully qualified.
            if (path.Length < 3 || path[2] != '\\')
                return (null, BackupLocationCodes.NotAbsolute);
            root = char.ToUpperInvariant(path[0]) + @":\";
            rest = path[3..];
        }
        else
        {
            // Rooted but driveless ("\foo") or relative.
            return (null, BackupLocationCodes.NotAbsolute);
        }

        var segments = new List<string>();
        foreach (var segment in rest.Split('\\'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                return (null, BackupLocationCodes.Invalid);
            if (!IsValidWindowsSegment(segment))
                return (null, BackupLocationCodes.Invalid);
            segments.Add(segment);
        }

        if (segments.Count < minSegments)
            return (null, BackupLocationCodes.Invalid);

        return (root + string.Join('\\', segments), null);
    }

    private static bool IsValidWindowsSegment(string segment)
    {
        if (segment.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', ':' }) >= 0)
            return false; // ':' = alternate data stream
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            return false;
        var dot = segment.IndexOf('.');
        var baseName = (dot >= 0 ? segment[..dot] : segment).TrimEnd(' ');
        return !WindowsReservedNames.Contains(baseName);
    }

    private string? CheckRoots(string normalized, BackupLocationContext context, bool resolveProtected = false)
    {
        // 4. Protected roots, both directions: never equal to, inside, or an
        //    ancestor of the data/cache/scratch/media/library/install roots.
        foreach (var raw in context.ProtectedRoots.Append(context.DataRoot))
        {
            foreach (var root in ProtectedVariants(raw, resolveProtected))
            {
                if (IsSameOrInside(normalized, root) || IsSameOrInside(root, normalized))
                    return BackupLocationCodes.OverlapsProtected;
            }
        }

        // 5. System / ephemeral locations. The filesystem root (drive root on
        //    Windows) itself is forbidden; everything else is "equal or inside".
        if (IsFilesystemRoot(normalized))
            return BackupLocationCodes.Forbidden;

        var dataRoot = NormalizeOrNull(context.DataRoot);
        foreach (var raw in _systemRoots)
        {
            var root = NormalizeOrNull(raw);
            if (root is null)
                continue;
            // When the data root itself lives in that "ephemeral" root, the
            // backups are no more ephemeral than the data they protect.
            if (dataRoot is not null && IsSameOrInside(dataRoot, root))
                continue;
            if (IsSameOrInside(normalized, root))
                return BackupLocationCodes.Forbidden;
        }

        return null;
    }

    private IEnumerable<string> ProtectedVariants(string? raw, bool resolve)
    {
        var lexical = NormalizeOrNull(raw);
        if (lexical is null)
            yield break;
        yield return lexical;
        if (!resolve)
            yield break;

        string? resolved = null;
        try { resolved = NormalizeOrNull(_fs.ResolveLinks(lexical)); }
        catch (Exception) { }
        if (resolved is not null && !string.Equals(resolved, lexical, Comparison))
            yield return resolved;
    }

    private string? NormalizeOrNull(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var (normalized, error) = Normalize(raw);
        return error is null ? normalized : null;
    }

    private bool IsFilesystemRoot(string normalized) => _flavor == BackupPathFlavor.Windows
        ? normalized.Length == 3 && normalized.EndsWith(@":\", StringComparison.Ordinal)
        : normalized == "/";

    /// <summary>Separator-aware containment: <c>/data2</c> is not inside <c>/data</c>.</summary>
    public bool IsSameOrInside(string candidate, string root)
    {
        var c = TrimSeparator(candidate);
        var r = TrimSeparator(root);
        if (r.Length == 0)
            return true; // "/" contains everything
        if (string.Equals(c, r, Comparison))
            return true;
        return c.StartsWith(r + Separator, Comparison);
    }

    private static string TrimSeparator(string path) => path.TrimEnd('/', '\\');

    private string? ParentOf(string normalized)
    {
        var idx = normalized.LastIndexOf(Separator);
        if (idx < 0)
            return null;
        if (_flavor == BackupPathFlavor.Unix)
            return idx == 0 ? "/" : normalized[..idx];
        // Windows: keep the trailing separator of a drive root / UNC share root.
        var parent = normalized[..idx];
        if (parent.Length == 2 && parent[1] == ':')
            return parent + "\\";
        return parent;
    }

    private bool SafeDirectoryExists(string path)
    {
        try { return _fs.DirectoryExists(path); }
        catch (Exception) { return false; }
    }

    private void TryDeleteEmpty(string path)
    {
        try { _fs.DeleteEmptyDirectory(path); }
        catch (Exception) { }
    }

    public static string NewMarkerId() => Guid.NewGuid().ToString("N");

    private static IReadOnlyList<string> DefaultSystemRoots(BackupPathFlavor flavor)
    {
        if (flavor == BackupPathFlavor.Unix)
            return UnixSystemRoots;

        var roots = new List<string>();
        foreach (var name in new[] { "WINDIR", "SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "TEMP", "TMP" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                roots.Add(value);
        }
        if (OperatingSystem.IsWindows())
            roots.Add(Path.GetTempPath());
        return roots;
    }
}

/// <summary>The real filesystem behind <see cref="BackupLocationValidator"/>.</summary>
public sealed class PhysicalBackupLocationFileSystem : IBackupLocationFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ResolveLinks(string path)
    {
        // Walk the existing prefix segment by segment, replacing every link
        // with its final target (a parent link is resolved too, not just the leaf).
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var segments = full[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        var i = 0;
        for (; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var info = new DirectoryInfo(next);
            if (!info.Exists)
                break;
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                next = target is null ? next : Path.GetFullPath(target.FullName);
            }
            current = next;
        }

        for (; i < segments.Length; i++)
            current = Path.Combine(current, segments[i]);
        return current;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteEmptyDirectory(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            Directory.Delete(path);
    }

    public void WriteProbe(string directory)
    {
        var probe = Path.Combine(directory, BackupLocationValidator.ProbeFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        var payload = Guid.NewGuid().ToByteArray();
        try
        {
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(payload);
                fs.Flush(flushToDisk: true);
            }
            var readBack = File.ReadAllBytes(probe);
            if (!readBack.AsSpan().SequenceEqual(payload))
                throw new IOException("Probe read-back mismatch.");
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    public BackupMarkerRead ReadMarker(string directory)
    {
        var path = Path.Combine(directory, BackupLocationValidator.MarkerFileName);
        if (!File.Exists(path))
            return new BackupMarkerRead(false, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("markerId", out var id) &&
                id.ValueKind == JsonValueKind.String)
            {
                return new BackupMarkerRead(true, id.GetString());
            }
        }
        catch (JsonException) { }
        return new BackupMarkerRead(true, null);
    }

    public void WriteMarker(string directory, string markerId, DateTimeOffset createdUtc)
    {
        // Only a random id and a timestamp: no hostname, no instance data.
        var path = Path.Combine(directory, BackupLocationValidator.MarkerFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { markerId, createdUtc }));
        File.Move(temp, path, overwrite: true);
    }

    public long? GetAvailableFreeBytes(string directory)
    {
        try { return new DriveInfo(directory).AvailableFreeSpace; }
        catch (Exception) { return null; }
    }
}
