namespace com.lifepixer.mangaplex.Server.Media;

using com.lifepixer.mangaplex.Server.Logging;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Generates durable cover thumbnails for archive items and persists them into
/// <see cref="ThumbnailStore"/>. Called from the analysis-completion path
/// (<see cref="MediaWorkerPool"/>) and the admin backfill endpoint.
///
/// The thumbnail is the first page of the archive, downscaled to WebP by the
/// media worker (variant "thumbnail"). The server never opens the archive
/// itself — extraction/encoding stays inside the worker fault boundary.
///
/// State is tracked on <see cref="ArchiveItemEntity"/>:
/// <c>ThumbnailState</c> (0 none / 1 ready / 2 failed) and
/// <c>ThumbnailContentVersion</c> (the content version the thumbnail was
/// generated from, so a source change invalidates it).
/// </summary>
public sealed class ThumbnailGenerationService
{
    /// <summary>Longest edge (px) for the durable cover thumbnail.</summary>
    public const int ThumbnailMaxDimension = 400;

    /// <summary>WebP quality for the durable cover thumbnail.</summary>
    public const int WebpQuality = 80;

    private readonly MediaWorkerPool _workerPool;
    private readonly ThumbnailStore _thumbnailStore;
    private readonly CacheService _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThumbnailGenerationService>? _logger;

    public ThumbnailGenerationService(
        MediaWorkerPool workerPool,
        ThumbnailStore thumbnailStore,
        CacheService cache,
        IServiceScopeFactory scopeFactory,
        ILogger<ThumbnailGenerationService>? logger = null)
    {
        _workerPool = workerPool;
        _thumbnailStore = thumbnailStore;
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates (or regenerates) the durable thumbnail for a single archive
    /// item, if one is needed. "Needed" means no current thumbnail exists for
    /// the item's content version (ThumbnailState != ready or
    /// ThumbnailContentVersion != ContentVersion). Returns true if a thumbnail
    /// is now ready (or was already current).
    /// </summary>
    public async Task<bool> GenerateForItemAsync(long nodeId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();

        var archiveItem = await db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == nodeId, ct);
        if (archiveItem is null)
        {
            _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerationSkipped, "Thumbnail generation skipped: archive item {ItemId} not found", nodeId);
            return false;
        }

        // Only ready items have pages to thumbnail.
        if (archiveItem.AnalysisState != 0)
        {
            _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerationSkipped, "Thumbnail generation skipped: item {ItemId} not ready (state {State})", nodeId, archiveItem.AnalysisState);
            return false;
        }

        // Already current — nothing to do.
        if (archiveItem.ThumbnailState == 1
            && archiveItem.ThumbnailContentVersion == archiveItem.ContentVersion
            && _thumbnailStore.HasThumbnail(nodeId, archiveItem.ContentVersion))
        {
            return true;
        }

        // Invalidate any stale thumbnail from a previous content version.
        if (archiveItem.ThumbnailContentVersion is { } oldVersion && oldVersion != archiveItem.ContentVersion)
        {
            _thumbnailStore.Delete(nodeId, oldVersion);
            _logger?.LogDebug(LogEvents.Worker.ThumbnailStaleInvalidated, "Stale thumbnail invalidated (item {ItemId}, old content version {OldVersion})", nodeId, oldVersion);
        }

        // Resolve the first page entry (cover = first page by ordinal).
        var firstPage = await db.PageEntries
            .Where(p => p.ItemId == nodeId && p.ContentVersion == archiveItem.ContentVersion)
            .OrderBy(p => p.Ordinal)
            .FirstOrDefaultAsync(ct);
        if (firstPage is null)
        {
            _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerationSkipped, "Thumbnail generation skipped: item {ItemId} has no pages", nodeId);
            return false;
        }

        // Resolve the source path (library root + relative path).
        var node = await db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null)
            return false;

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == node.LibraryId, ct);
        if (library is null)
            return false;

        var sourcePath = Path.Combine(library.RootPath, node.RelativePath);
        if (!File.Exists(sourcePath))
        {
            _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerationSkipped, "Thumbnail generation skipped: source missing (item {ItemId})", nodeId);
            return false;
        }

        // Extract + encode the thumbnail in the worker.
        Directory.CreateDirectory(_cache.ScratchDirectory);
        var outputPath = Path.Combine(_cache.ScratchDirectory, "thumb-" + Guid.NewGuid().ToString("N")[..12] + ".bin");

        try
        {
            var outcome = await _workerPool.ExtractPageAsync(
                sourcePath, firstPage.SourceEntryLocator, "thumbnail",
                archiveItem.ModificationTicks, archiveItem.ByteLength,
                outputPath, ThumbnailMaxDimension, WebpQuality, ct);

            if (!outcome.Success)
            {
                await MarkThumbnailStateAsync(nodeId, 2, archiveItem.ContentVersion, ct);
                _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerationFailed, "Thumbnail generation failed (item {ItemId}): {Error}", nodeId, outcome.ErrorType);
                return false;
            }

            // Persist into the durable store.
            await _thumbnailStore.PublishAsync(nodeId, archiveItem.ContentVersion, outcome.OutputPath!, ct);
            await MarkThumbnailStateAsync(nodeId, 1, archiveItem.ContentVersion, ct);
            _logger?.LogDebug(LogEvents.Worker.ThumbnailGenerated, "Thumbnail generated (item {ItemId}, content version {ContentVersion})", nodeId, archiveItem.ContentVersion);
            return true;
        }
        catch (Exception ex)
        {
            await MarkThumbnailStateAsync(nodeId, 2, archiveItem.ContentVersion, ct);
            _logger?.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed, ex, "Thumbnail generation threw (item {ItemId}): {Error}", nodeId, ex.GetType().Name);
            return false;
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Updates the thumbnail state on the archive item entity using a fresh
    /// scope (safe to call from the worker pool's post-job path).
    /// </summary>
    private async Task MarkThumbnailStateAsync(long nodeId, int state, long contentVersion, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var item = await db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == nodeId, ct);
            if (item is null)
                return;
            item.ThumbnailState = state;
            item.ThumbnailContentVersion = state == 1 ? contentVersion : (long?)null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed, ex, "Failed to record thumbnail state (item {ItemId}): {Error}", nodeId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Returns the internal node ids of ready archive items in a library that
    /// lack a current durable thumbnail (ThumbnailState != ready or
    /// ThumbnailContentVersion != ContentVersion). Used by the backfill paths.
    /// </summary>
    public static async Task<List<long>> GetItemsNeedingThumbnailsAsync(
        MangaPlexDbContext db,
        long libraryId,
        int limit,
        CancellationToken ct = default)
    {
        return await db.CatalogNodes
            .Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5)
            .Join(db.ArchiveItems.Where(a => a.AnalysisState == 0
                && (a.ThumbnailState != 1 || a.ThumbnailContentVersion != a.ContentVersion)),
                  n => n.Id, a => a.NodeId,
                  (n, a) => n.Id)
            .Take(limit)
            .ToListAsync(ct);
    }
}
