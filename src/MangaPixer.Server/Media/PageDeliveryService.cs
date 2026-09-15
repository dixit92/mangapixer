namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Page delivery service. Handles on-demand page extraction, cache lookup,
/// and authorized streaming. Returns readiness separately from binary fetches.
///
/// Rules:
/// - Check permissions before serving even an already-cached page.
/// - A cached cover cannot bypass a revoked grant.
/// - No eager full-library extraction.
/// - Repeated concurrent page requests share work (deduplication via JobScheduler).
/// - Cached pages are served without worker access when possible.
/// </summary>
public sealed class PageDeliveryService
{
    private readonly MangaPixerDbContext _db;
    private readonly CacheService _cache;
    private readonly JobScheduler _scheduler;
    private readonly WorkerPoolOptions _options;
    private readonly ILogger<PageDeliveryService>? _logger;

    public PageDeliveryService(
        MangaPixerDbContext db,
        CacheService cache,
        JobScheduler scheduler,
        WorkerPoolOptions options,
        ILogger<PageDeliveryService>? logger = null)
    {
        _db = db;
        _cache = cache;
        _scheduler = scheduler;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Gets the readiness state for an item (separate from binary fetches).
    /// Returns pending, partial, unsupported, corrupt, or complete manifest state.
    /// </summary>
    public async Task<PageReadinessResult> GetReadinessAsync(long itemId, CancellationToken ct = default)
    {
        var item = await _db.ArchiveItems
            .FirstOrDefaultAsync(a => a.NodeId == itemId, ct);

        if (item is null)
            return new PageReadinessResult { State = ItemReadinessState.Missing };

        return new PageReadinessResult
        {
            State = (ItemReadinessState)item.AnalysisState,
            ContentVersion = item.ContentVersion,
            Error = item.AnalysisError,
            PageCount = item.PageCount,
            IsAnalyzing = item.AnalysisState == 1, // pending
        };
    }

    /// <summary>
    /// Gets a page image stream. Checks authorization, looks up cache first,
    /// and falls back to on-demand extraction if not cached.
    /// </summary>
    public async Task<PageStreamResult?> GetPageStreamAsync(
        long userId,
        long itemId,
        string entryKey,
        long? contentVersion = null,
        string variant = "original",
        CancellationToken ct = default)
    {
        // 1. Check authorization — even for cached pages
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == itemId, ct);
        if (node is null)
        {
            _logger?.LogDebug(LogEvents.Worker.PageDeniedItemMissing, "Page stream denied: item {ItemId} not found", itemId);
            return null;
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
        {
            _logger?.LogDebug(LogEvents.Worker.PageDeniedUserInactive, "Page stream denied: user {UserId} inactive or not found", userId);
            return null;
        }

        if (!user.IsAdmin)
        {
            var hasGrant = await _db.LibraryGrants
                .AnyAsync(g => g.UserId == userId && g.LibraryId == node.LibraryId, ct);
            if (!hasGrant)
            {
                _logger?.LogDebug(LogEvents.Worker.PageDeniedNoGrant, "Page stream denied: user {UserId} lacks grant for library {LibraryId}", userId, node.LibraryId);
                return null; // Access denied — don't reveal existence
            }
        }

        // 2. Check item readiness
        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item is null)
        {
            _logger?.LogDebug(LogEvents.Worker.PageDeniedArchiveMissing, "Page stream denied: archive item {ItemId} not found", itemId);
            return null;
        }

        if (item.AnalysisState != 0) // not ready
        {
            _logger?.LogDebug(LogEvents.Worker.PageNotReady, "Page stream: item {ItemId} not ready (state {State})", itemId, item.AnalysisState);
            return new PageStreamResult { ReadinessState = (ItemReadinessState)item.AnalysisState };
        }

        // 3. Look up cache first
        var cacheKey = CacheService.BuildCacheKey(itemId, item.ContentVersion, entryKey, variant);
        if (_cache.TryGet(cacheKey, out _))
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                _logger?.LogDebug(LogEvents.Worker.PageServedFromCache, "Page stream served from cache (item {ItemId}, entry {EntryKey}, variant {Variant})",
                    itemId, entryKey, variant);
                return new PageStreamResult
                {
                    Stream = cachedStream,
                    MediaType = GetMediaTypeForEntry(entryKey),
                    FromCache = true,
                    ReadinessState = ItemReadinessState.Ready,
                };
            }
        }

        // 4. Cache miss — need to extract via worker
        _logger?.LogDebug(LogEvents.Worker.PageCacheMiss, "Page stream cache miss (item {ItemId}, entry {EntryKey}, variant {Variant}); extraction required",
            itemId, entryKey, variant);
        // This path does not yet dispatch to MediaWorkerPool for on-demand extraction;
        // it returns a "preparing" state if the page isn't cached.
        return new PageStreamResult
        {
            ReadinessState = ItemReadinessState.Pending,
        };
    }

    /// <summary>
    /// Gets a cover image stream. Checks authorization first.
    /// A cached cover cannot bypass a revoked grant.
    /// </summary>
    public async Task<PageStreamResult?> GetCoverStreamAsync(
        long userId,
        long itemId,
        long? contentVersion = null,
        CancellationToken ct = default)
    {
        // Check authorization
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == itemId, ct);
        if (node is null)
            return null;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return null;

        if (!user.IsAdmin)
        {
            var hasGrant = await _db.LibraryGrants
                .AnyAsync(g => g.UserId == userId && g.LibraryId == node.LibraryId, ct);
            if (!hasGrant)
                return null;
        }

        var item = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item is null)
            return null;

        // Cover is the first page
        var firstPage = await _db.PageEntries
            .Where(p => p.ItemId == itemId && p.ContentVersion == item.ContentVersion)
            .OrderBy(p => p.Ordinal)
            .FirstOrDefaultAsync(ct);

        if (firstPage is null)
            return null;

        var cacheKey = CacheService.BuildCacheKey(itemId, item.ContentVersion, firstPage.EntryKey, "cover");
        if (_cache.TryGet(cacheKey, out _))
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                _logger?.LogDebug(LogEvents.Worker.CoverServedFromCache, "Cover stream served from cache (item {ItemId})", itemId);
                return new PageStreamResult
                {
                    Stream = cachedStream,
                    MediaType = firstPage.MediaType,
                    FromCache = true,
                    ReadinessState = ItemReadinessState.Ready,
                };
            }
        }

        _logger?.LogDebug(LogEvents.Worker.CoverCacheMiss, "Cover stream cache miss (item {ItemId}); extraction required", itemId);
        return new PageStreamResult
        {
            ReadinessState = ItemReadinessState.Pending,
        };
    }

    /// <summary>
    /// Publishes a page to the cache after worker extraction.
    /// </summary>
    public async Task PublishPageToCacheAsync(
        long itemId,
        long contentVersion,
        string entryKey,
        string variant,
        string sourceFilePath,
        string mediaType,
        CancellationToken ct = default)
    {
        var cacheKey = CacheService.BuildCacheKey(itemId, contentVersion, entryKey, variant);
        await _cache.PublishAsync(cacheKey, sourceFilePath, mediaType, ct);
    }

    private static string GetMediaTypeForEntry(string entryKey)
    {
        var ext = Path.GetExtension(entryKey).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => ImageMediaTypes.Jpeg,
            ".png" => ImageMediaTypes.Png,
            ".webp" => ImageMediaTypes.WebP,
            ".avif" => ImageMediaTypes.Avif,
            ".gif" => ImageMediaTypes.Gif,
            ".bmp" => ImageMediaTypes.Bmp,
            ".tif" or ".tiff" => ImageMediaTypes.Tiff,
            _ => "application/octet-stream",
        };
    }
}

/// <summary>
/// Result of a page readiness check.
/// </summary>
public sealed record PageReadinessResult
{
    public required ItemReadinessState State { get; init; }
    public long ContentVersion { get; init; }
    public string? Error { get; init; }
    public int? PageCount { get; init; }
    public bool IsAnalyzing { get; init; }
}

/// <summary>
/// Result of a page stream request.
/// </summary>
public sealed class PageStreamResult
{
    public Stream? Stream { get; init; }
    public string? MediaType { get; init; }
    public bool FromCache { get; init; }
    public ItemReadinessState ReadinessState { get; init; }
    public bool IsReady => Stream is not null;
}
