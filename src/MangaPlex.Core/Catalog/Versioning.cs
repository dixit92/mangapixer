namespace com.lifepixer.mangaplex.Core.Catalog;

/// <summary>
/// Source version and content version rules for catalog items.
///
/// Source version: a monotonically increasing counter per library scan.
/// Changes when the library is rescanned and the file's metadata changes
/// (size, mtime, inode/file-id).
///
/// Content version: a hash-derived version stamp for the file's actual content.
/// Changes only when the file's bytes change (not just metadata).
/// Used to invalidate manifests, cached pages, and reading progress.
///
/// Neither version exposes the file's actual path, name, or content to clients.
/// </summary>
public sealed record SourceVersion
{
    /// <summary>
    /// Library scan revision when this version was recorded.
    /// </summary>
    public required long ScanRevision { get; init; }

    /// <summary>
    /// File size in bytes at last scan.
    /// </summary>
    public required long FileSize { get; init; }

    /// <summary>
    /// File last-modified time in ticks (UTC). Not exposed to clients.
    /// </summary>
    public required long LastModifiedTicks { get; init; }

    /// <summary>
    /// Whether the source version is considered stale (file has changed since last scan).
    /// </summary>
    public bool IsStale { get; init; }
}

/// <summary>
/// Content version for an item. Derived from the source file's content hash
/// (computed lazily by the worker) or from a fast heuristic (size + mtime).
/// Used to invalidate manifests, cached pages, and reading progress.
/// </summary>
public sealed record ContentVersion
{
    /// <summary>
    /// Numeric content version. Incremented when content changes are detected.
    /// </summary>
    public required long Version { get; init; }

    /// <summary>
    /// Whether the content version is based on a full hash (true) or a heuristic (false).
    /// </summary>
    public bool IsHashBased { get; init; }

    /// <summary>
    /// Whether the content version is confirmed current (false if the source may have changed).
    /// </summary>
    public bool IsConfirmed { get; init; }

    /// <summary>
    /// Checks whether this content version is compatible with a progress entry.
    /// Progress is stale if the content version differs.
    /// </summary>
    public bool IsCompatibleWith(long progressContentVersion) => Version == progressContentVersion;
}

/// <summary>
/// Progress preconditions for reading state updates.
/// The server rejects progress updates that don't match the current content version
/// to prevent overwriting valid progress with stale data.
/// </summary>
public static class ProgressPreconditions
{
    /// <summary>
    /// Validates a progress update request against the current content version.
    /// Returns null if valid, or an error message if the precondition fails.
    /// </summary>
    public static string? Validate(
        long expectedContentVersion,
        long actualContentVersion,
        int requestedPageIndex,
        int actualPageCount)
    {
        if (expectedContentVersion != actualContentVersion)
            return "Content version mismatch: the item has changed since the client last loaded it.";

        if (requestedPageIndex < 0)
            return "Page index cannot be negative.";

        if (actualPageCount > 0 && requestedPageIndex >= actualPageCount)
            return $"Page index {requestedPageIndex} is out of range (0..{actualPageCount - 1}).";

        return null;
    }
}
