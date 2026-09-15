namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Durable, persistent thumbnail store under <c>DataRoot/thumbnails/</c>.
///
/// Thumbnails are pre-generated at analysis time and persisted durably — they
/// are NOT held in the evictable page cache (<c>CacheRoot</c>). Files are keyed
/// by <c>(itemId, contentVersion)</c> as WebP, sharded by a two-character id
/// prefix to avoid one huge directory. A source change (new content version)
/// produces a new file; the old one is retired by <see cref="ThumbnailGenerationService"/>.
///
/// This store is completely independent of <see cref="CacheService"/>: it has no
/// budget, no eviction, and no LRU tracking. Thumbnails survive restarts and
/// cache-clears by design (owner requirement, 2026-09-09).
///
/// File names use opaque item ids + content version only — no source paths or
/// entry names (privacy invariant).
/// </summary>
public sealed class ThumbnailStore
{
    private readonly string _thumbnailsRoot;
    private readonly ILogger<ThumbnailStore>? _logger;

    public ThumbnailStore(string thumbnailsRoot, ILogger<ThumbnailStore>? logger = null)
    {
        _thumbnailsRoot = Path.GetFullPath(thumbnailsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger = logger;
    }

    /// <summary>
    /// The durable thumbnail root directory (under DataRoot).
    /// </summary>
    public string ThumbnailsRoot => _thumbnailsRoot;

    /// <summary>
    /// Initializes the thumbnail root directory.
    /// </summary>
    public void Initialize()
    {
        Directory.CreateDirectory(_thumbnailsRoot);
    }

    /// <summary>
    /// Returns the sharded file path for a thumbnail. Does not check existence.
    /// Path format: <c>thumbnails/&lt;prefix&gt;/&lt;itemId&gt;-&lt;contentVersion&gt;.webp</c>
    /// </summary>
    public string GetThumbnailPath(long itemId, long contentVersion)
    {
        var idBase36 = com.lifepixer.mangapixer.Core.Catalog.OpaqueId.Encode(itemId);
        var prefix = idBase36.Length >= 2 ? idBase36[..2] : "00";
        var fileName = $"{idBase36}-{contentVersion}.webp";
        return Path.Combine(_thumbnailsRoot, prefix, fileName);
    }

    /// <summary>
    /// Checks whether a durable thumbnail exists for the given item + content version.
    /// </summary>
    public bool HasThumbnail(long itemId, long contentVersion)
    {
        return File.Exists(GetThumbnailPath(itemId, contentVersion));
    }

    /// <summary>
    /// Opens a durable thumbnail for reading. Returns null if it does not exist.
    /// </summary>
    public Stream? OpenRead(long itemId, long contentVersion)
    {
        var path = GetThumbnailPath(itemId, contentVersion);
        try
        {
            if (!File.Exists(path))
                return null;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Atomically publishes a file into the durable thumbnail store. Writes to a
    /// temp file first, then renames within the same directory (same filesystem
    /// for atomicity). Overwrites an existing thumbnail for the same key.
    /// </summary>
    public async Task PublishAsync(long itemId, long contentVersion, string sourceFilePath, CancellationToken ct = default)
    {
        var destPath = GetThumbnailPath(itemId, contentVersion);
        var destDir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(destDir);

        var tempPath = destPath + ".tmp";
        try
        {
            using var source = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(dest, ct);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        if (File.Exists(destPath))
            File.Delete(destPath);
        File.Move(tempPath, destPath);

        _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerated, "Thumbnail published (item {ItemId}, content version {ContentVersion})",
            itemId, contentVersion);
    }

    /// <summary>
    /// Deletes a durable thumbnail for the given item + content version, if it
    /// exists. Used when a content-version bump invalidates a stale thumbnail.
    /// </summary>
    public void Delete(long itemId, long contentVersion)
    {
        var path = GetThumbnailPath(itemId, contentVersion);
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
