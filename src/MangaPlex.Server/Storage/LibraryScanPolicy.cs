namespace com.lifepixer.mangaplex.Server.Storage;

using System.IO;

/// <summary>
/// Defines which file extensions are approved archive candidates and which
/// directories are ignored during scanning.
/// </summary>
public sealed class LibraryScanPolicy
{
    /// <summary>
    /// Approved archive extensions (lowercase, with dot).
    /// </summary>
    public IReadOnlySet<string> ApprovedArchiveExtensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz",
        ".cbr",
        ".cb7",
        ".zip",
        ".rar",
        ".7z",
    };

    /// <summary>
    /// Directory names that are always ignored (case-insensitive).
    /// Includes YACReader bookkeeping, OS recycle/system directories, and sync state.
    /// </summary>
    public IReadOnlySet<string> IgnoredDirectoryNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".yacreader",
        "$recycle.bin",
        "system volume information",
        ".ds_store",
        "thumbs.db",
        ".git",
        ".svn",
        ".hg",
        "@eadir",
        ".@__thumb",  // Synology thumbnail cache
        ".synology_dir",  // Synology metadata
        "#recycle",
    };

    /// <summary>
    /// File names that are always ignored (case-insensitive).
    /// Includes OS metadata files and bookkeeping.
    /// </summary>
    public IReadOnlySet<string> IgnoredFileNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ds_store",
        "thumbs.db",
        "comicinfo.xml",
        "yacreader.ini",
        "yacreaderlibrary.ini",
        "desktop.ini",
    };

    /// <summary>
    /// Returns true if the file extension is an approved archive format.
    /// </summary>
    public bool IsApprovedArchive(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && ApprovedArchiveExtensions.Contains(ext);
    }

    /// <summary>
    /// Returns true if the directory name should be ignored: any hidden
    /// (dot-prefixed) directory, plus the known bookkeeping names. Hidden
    /// directories (e.g. <c>.yacreaderlibrary</c>, <c>.git</c>) are never shown
    /// as manga folders.
    /// </summary>
    public bool IsIgnoredDirectory(string directoryName)
    {
        if (string.IsNullOrEmpty(directoryName)) return true;
        if (directoryName.StartsWith('.')) return true;
        return IgnoredDirectoryNames.Contains(directoryName);
    }

    /// <summary>
    /// Returns true if the file name should be ignored.
    /// </summary>
    public bool IsIgnoredFile(string fileName)
    {
        return IgnoredFileNames.Contains(fileName);
    }

    /// <summary>
    /// Returns true if the entry is a candidate archive (file with approved extension, not ignored).
    /// </summary>
    public bool IsArchiveCandidate(FileSystemEntry entry)
    {
        if (entry.Kind != EntryKind.File) return false;
        if (IsIgnoredFile(entry.Name)) return false;
        return IsApprovedArchive(entry.Name);
    }

    /// <summary>
    /// Returns true if the directory should be traversed (not ignored, not a symlink).
    /// </summary>
    public bool ShouldTraverseDirectory(FileSystemEntry entry)
    {
        if (entry.Kind != EntryKind.Directory) return false;
        if (entry.IsSymlink) return false; // Don't follow symlinks
        return !IsIgnoredDirectory(entry.Name);
    }
}
