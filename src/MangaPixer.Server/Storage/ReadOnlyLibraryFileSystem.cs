namespace com.lifepixer.mangapixer.Server.Storage;

using System.IO;

/// <summary>
/// Read-only filesystem implementation using .NET file I/O.
/// All file handles are opened with FileShare.Read only — no write sharing.
/// Symlinks/junctions/reparse points are detected and reported, not followed
/// for directory traversal (to prevent escaping the library root).
/// </summary>
public sealed class ReadOnlyLibraryFileSystem : IReadOnlyLibraryFileSystem
{
    private readonly string _rootPath;
    private readonly StringComparer _pathComparer;

    public ReadOnlyLibraryFileSystem(string rootPath, StringComparer? pathComparer = null)
    {
        _rootPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _pathComparer = pathComparer ?? StringComparer.Ordinal;
    }

    public bool RootExists() => Directory.Exists(_rootPath);

    public DirectoryEntry? GetRoot()
    {
        if (!Directory.Exists(_rootPath))
            return null;

        var info = new DirectoryInfo(_rootPath);
        return new DirectoryEntry
        {
            RelativePath = "",
            Name = info.Name,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
            IsSymlink = IsReparsePoint(info),
        };
    }

    public IReadOnlyList<FileSystemEntry> EnumerateEntries(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (fullPath is null || !Directory.Exists(fullPath))
            return [];

        var entries = new List<FileSystemEntry>();
        var dirInfo = new DirectoryInfo(fullPath);

        foreach (var child in dirInfo.EnumerateFileSystemInfos())
        {
            // Skip reparse points (symlinks/junctions) to prevent escaping the root
            if (IsReparsePoint(child))
            {
                entries.Add(new FileSystemEntry
                {
                    RelativePath = GetRelativePath(child.FullName),
                    Name = child.Name,
                    Kind = child is DirectoryInfo ? EntryKind.Directory : EntryKind.File,
                    ByteLength = child is FileInfo fi ? fi.Length : 0,
                    LastWriteTimeUtc = child.LastWriteTimeUtc,
                    IsHidden = (child.Attributes & FileAttributes.Hidden) != 0,
                    IsSymlink = true,
                });
                continue;
            }

            entries.Add(new FileSystemEntry
            {
                RelativePath = GetRelativePath(child.FullName),
                Name = child.Name,
                Kind = child is DirectoryInfo ? EntryKind.Directory : EntryKind.File,
                ByteLength = child is FileInfo fi2 ? fi2.Length : 0,
                LastWriteTimeUtc = child.LastWriteTimeUtc,
                IsHidden = (child.Attributes & FileAttributes.Hidden) != 0,
                IsSymlink = false,
            });
        }

        return entries;
    }

    public FileSystemEntry? GetEntry(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (fullPath is null)
            return null;

        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return new FileSystemEntry
            {
                RelativePath = relativePath,
                Name = info.Name,
                Kind = EntryKind.File,
                ByteLength = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
                IsSymlink = IsReparsePoint(info),
            };
        }

        if (Directory.Exists(fullPath))
        {
            var info = new DirectoryInfo(fullPath);
            return new FileSystemEntry
            {
                RelativePath = relativePath,
                Name = info.Name,
                Kind = EntryKind.Directory,
                ByteLength = 0,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
                IsSymlink = IsReparsePoint(info),
            };
        }

        return null;
    }

    public Stream OpenRead(string relativePath)
    {
        var fullPath = ResolvePath(relativePath)
            ?? throw new FileNotFoundException($"File not found: {relativePath}");

        // Open read-only, no write sharing
        return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public SourceStamp GetSourceStamp(string relativePath)
    {
        var fullPath = ResolvePath(relativePath);
        if (fullPath is null || !File.Exists(fullPath))
            return SourceStamp.None;

        var info = new FileInfo(fullPath);
        return new SourceStamp(info.LastWriteTimeUtc.Ticks, info.Length);
    }

    public string? GetRootIdentity()
    {
        if (!Directory.Exists(_rootPath))
            return null;

        // Fallback: use root path + last write time as a weak identity
        // A stronger identity (volume serial / inode) can be added per-platform later.
        var dirInfo = new DirectoryInfo(_rootPath);
        return $"path:{_rootPath}:ticks:{dirInfo.LastWriteTimeUtc.Ticks}";
    }

    /// <summary>
    /// Resolves a relative path against the root, ensuring the result is contained within the root.
    /// Returns null if the path escapes the root (path traversal protection).
    /// </summary>
    private string? ResolvePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return _rootPath;

        // Normalize and combine
        var combined = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        var normalizedRoot = _rootPath.EndsWith(Path.DirectorySeparatorChar) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;

        // Ensure the combined path is within the root
        var comparison = _pathComparer == StringComparer.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!combined.StartsWith(normalizedRoot, comparison) && combined != _rootPath)
            return null; // Path traversal attempt

        return combined;
    }

    private string GetRelativePath(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (normalized == _rootPath)
            return "";
        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;
        return normalized.StartsWith(rootWithSep)
            ? normalized[rootWithSep.Length..]
            : normalized;
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        return (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
