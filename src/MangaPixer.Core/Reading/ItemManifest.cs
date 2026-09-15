namespace com.lifepixer.mangaplex.Core.Reading;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Media;

/// <summary>
/// Manifest for a readable item. Describes the page structure without
/// exposing source paths or archive entry names.
/// All page indices are zero-based.
/// </summary>
public sealed record ItemManifest
{
    /// <summary>
    /// Opaque item ID.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Content version of the source archive. Changes when the file is replaced or modified.
    /// Used to invalidate cached manifests and progress state.
    /// </summary>
    public required long ContentVersion { get; init; }

    /// <summary>
    /// Manifest format version. Incremented when the manifest schema changes.
    /// </summary>
    public required int ManifestVersion { get; init; }

    /// <summary>
    /// Detected archive format.
    /// </summary>
    public required ArchiveFormat ArchiveFormat { get; init; }

    /// <summary>
    /// Total number of readable pages (zero-based: pages 0..PageCount-1).
    /// </summary>
    public required int PageCount { get; init; }

    /// <summary>
    /// Page entries in reading order. Index in this list == page index.
    /// </summary>
    public required IReadOnlyList<ManifestPageEntry> Pages { get; init; }

    /// <summary>
    /// Whether the archive is solid (sequential extraction required).
    /// </summary>
    public bool IsSolid { get; init; }

    /// <summary>
    /// Whether any pages are animated.
    /// </summary>
    public bool HasAnimatedPages { get; init; }
}

/// <summary>
/// A single page entry in a manifest. Contains no source path information.
/// </summary>
public sealed record ManifestPageEntry
{
    /// <summary>
    /// Opaque page entry key (used in page fetch URLs).
    /// </summary>
    public required string EntryKey { get; init; }

    /// <summary>
    /// Zero-based page index.
    /// </summary>
    public required int PageIndex { get; init; }

    /// <summary>
    /// Image media type (e.g., "image/png").
    /// </summary>
    public required string MediaType { get; init; }

    /// <summary>
    /// Pixel width of the page, if known.
    /// </summary>
    public int Width { get; init; }

    /// <summary>
    /// Pixel height of the page, if known.
    /// </summary>
    public int Height { get; init; }

    /// <summary>
    /// Animation state of the page.
    /// </summary>
    public AnimationState AnimationState { get; init; }

    /// <summary>
    /// File size in bytes of the source entry, if known.
    /// </summary>
    public long ByteSize { get; init; }
}

/// <summary>
/// Readiness state of an item. Separated from the manifest because
/// analysis may be pending, failed, or require user action.
/// </summary>
public sealed record ItemReadiness
{
    public required string ItemId { get; init; }
    public required ItemReadinessState State { get; init; }
    public required long ContentVersion { get; init; }

    /// <summary>
    /// Human-readable error message for error states. Never contains source paths.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// When analysis was last attempted, if ever.
    /// </summary>
    public DateTimeOffset? LastAttempt { get; init; }

    /// <summary>
    /// Whether the item is currently being analyzed by a worker.
    /// </summary>
    public bool IsAnalyzing { get; init; }
}

/// <summary>
/// Readiness state for an item.
/// </summary>
public enum ItemReadinessState
{
    /// <summary>
    /// Manifest is available and item can be read.
    /// </summary>
    Ready = 0,

    /// <summary>
    /// Analysis is in progress.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// Analysis failed due to a corrupt or unsupported archive.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// Archive format is not supported.
    /// </summary>
    Unsupported = 3,

    /// <summary>
    /// Archive is encrypted and cannot be read.
    /// </summary>
    Encrypted = 4,

    /// <summary>
    /// Source file is missing or inaccessible.
    /// </summary>
    Missing = 5
}
