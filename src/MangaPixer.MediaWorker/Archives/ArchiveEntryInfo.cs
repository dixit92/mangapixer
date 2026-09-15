namespace com.lifepixer.mangapixer.MediaWorker.Archives;

using com.lifepixer.mangapixer.Core.Media;

/// <summary>
/// Represents a single entry in an archive, enumerated without extraction.
/// </summary>
public sealed record ArchiveEntryInfo
{
    /// <summary>
    /// Zero-based ordinal position in the archive's natural entry order.
    /// </summary>
    public required int Ordinal { get; init; }

    /// <summary>
    /// Entry path as stored in the archive (may contain directory separators).
    /// </summary>
    public required string EntryPath { get; init; }

    /// <summary>
    /// Whether this entry represents a directory (not a file).
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// Uncompressed size in bytes, if known.
    /// </summary>
    public long UncompressedSize { get; init; }

    /// <summary>
    /// Whether the entry is encrypted.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Whether the entry path looks like an image file (by extension).
    /// </summary>
    public bool IsImageCandidate => !IsDirectory && ImageExtensions.IsImageExtension(EntryPath);
}

/// <summary>
/// Result of enumerating an archive's entries.
/// </summary>
public sealed record ArchiveEnumerationResult
{
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyList<ArchiveEntryInfo> Entries { get; init; }
    public bool IsSolid { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsMultipart { get; init; }
    public string? Error { get; init; }

    public int ImageCount => Entries.Count(e => e.IsImageCandidate);
    public int DirectoryCount => Entries.Count(e => e.IsDirectory);
    public int TotalEntries => Entries.Count;
}

/// <summary>
/// Supported image file extensions for archive entry filtering.
/// </summary>
public static class ImageExtensions
{
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif", ".bmp", ".tif", ".tiff"
    };

    /// <summary>
    /// Directories and files to ignore during enumeration.
    /// </summary>
    private static readonly HashSet<string> s_ignoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__MACOSX", "Thumbs.db", ".DS_Store", "ComicInfo.xml"
    };

    public static bool IsImageExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return s_extensions.Contains(ext);
    }

    public static bool ShouldIgnore(string entryPath)
    {
        if (string.IsNullOrEmpty(entryPath)) return true;

        // Check for AppleDouble/MacOSX entries
        if (entryPath.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) return true;
        if (entryPath.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase)) return true;

        // Check for AppleDouble files (._ prefix)
        var fileName = Path.GetFileName(entryPath);
        if (fileName.StartsWith("._", StringComparison.Ordinal)) return true;

        // Check for known bookkeeping files
        if (s_ignoredNames.Contains(fileName)) return true;

        // Check for thumbnail databases
        if (fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
