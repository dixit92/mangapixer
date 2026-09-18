namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Page and cover delivery endpoints.
/// GET /api/v1/items/{itemId}/pages/{entryKey}[?maxDim=n][&filter=sharp|balanced|soft] — page image (full or display-sized)
/// GET /api/v1/items/{itemId}/pages/{entryKey}/thumbnail — thumbnail variant
/// GET /api/v1/items/{itemId}/cover — cover image (first page)
///
/// Every page response carries an <c>X-MangaPixer-Variant</c> header naming the
/// variant actually served ("webp", "webp@1440:balanced", "thumbnail"), so the
/// client can tell when its <c>maxDim</c> request was snapped to a bucket or
/// declined (page already smaller than the bucket, or above the ladder), and
/// which resampling filter it actually got. This header is not expressible in
/// the generated OpenAPI document, which is why it is specified here.
///
/// All endpoints check authorization before serving. Source paths are never
/// exposed. Pages are extracted on-demand from archives and cached.
///
/// Audit defect D3: routes use entry keys (opaque page identifiers) instead
/// of numeric page indices. Audit defect D9: every binary response sets
/// Cache-Control and ETag headers.
/// </summary>
[ApiController]
[Route("api/v1/items")]
[Authorize]
public sealed class PageController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly CacheService _cache;
    private readonly CatalogIdResolver _idResolver;
    private readonly MediaWorkerPool _workerPool;
    private readonly ThumbnailStore _thumbnailStore;
    private readonly PageVariantOptions _pageVariants;
    private readonly ILogger<PageController> _logger;

    // WebP transcode defaults for on-demand page delivery; not yet user-configurable.
    private const int ThumbnailMaxDimension = 320;
    private const int WebpQuality = 82;

    /// <summary>
    /// Response header naming the variant actually served, so the client can
    /// detect bucket snapping and full-size fallbacks without guessing.
    /// </summary>
    internal const string VariantHeaderName = "X-MangaPixer-Variant";

    public PageController(
        MangaPixerDbContext db,
        CacheService cache,
        CatalogIdResolver idResolver,
        MediaWorkerPool workerPool,
        ThumbnailStore thumbnailStore,
        PageVariantOptions pageVariants,
        ILogger<PageController> logger)
    {
        _db = db;
        _cache = cache;
        _idResolver = idResolver;
        _workerPool = workerPool;
        _thumbnailStore = thumbnailStore;
        _pageVariants = pageVariants;
        _logger = logger;
    }

    /// <summary>
    /// Serves one page image. Without <paramref name="maxDim"/> this is the
    /// pre-1.19.0 behaviour: the full-size WebP transcode. With a positive
    /// <paramref name="maxDim"/> (the longest edge the client will actually
    /// display, in device pixels) the request snaps UP to the smallest
    /// configured bucket that covers it and serves a Lanczos-downscaled variant
    /// instead — sharper on line art and screentones than a browser downscale,
    /// and a fraction of the bytes.
    ///
    /// The full-size variant is served anyway when the request exceeds the
    /// largest bucket, or when the page's own longest edge already fits inside
    /// the chosen bucket (the server never upscales, and a pass-through bucket
    /// entry would only waste cache budget).
    ///
    /// <paramref name="filter"/> picks the resampling kernel for that downscale
    /// (1.20.0). It is validated on every request but only has an effect when a
    /// sized variant is actually produced: on a full-size response the served
    /// variant stays plain "webp".
    /// </summary>
    /// <param name="maxDim">
    /// Optional longest edge in pixels. Absent, empty or non-positive means
    /// "full size". A value that is not an integer is rejected with 400
    /// <c>invalid_request</c>. Bound as a string so the rejection is a typed
    /// ApiError like every other bad request, not a framework ProblemDetails.
    /// </param>
    /// <param name="filter">
    /// Optional resampling filter: <c>sharp</c>, <c>balanced</c> or <c>soft</c>
    /// (case-insensitive). Absent or empty means the server default
    /// (<see cref="PageVariantOptions.DefaultFilter"/>). Any other value is
    /// rejected with 400 <c>invalid_request</c> rather than silently ignored,
    /// so a client typo is visible instead of quietly producing the wrong look.
    /// Bound as a string for the same typed-ApiError reason as maxDim.
    /// </param>
    [HttpGet("{itemId}/pages/{entryKey}")]
    public async Task<IActionResult> GetPage(
        string itemId,
        string entryKey,
        CancellationToken ct,
        [FromQuery(Name = "maxDim")] string? maxDim = null,
        [FromQuery(Name = "filter")] string? filter = null)
    {
        int requestedMaxDim = 0;
        if (!string.IsNullOrWhiteSpace(maxDim) &&
            !int.TryParse(maxDim, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out requestedMaxDim))
        {
            return BadRequest(new ApiError { Error = "invalid_request", Message = "maxDim must be an integer." });
        }

        // Validate unconditionally, even when no sized variant will be produced:
        // a client that misspells the filter should learn that now, not the
        // first time it happens to request a size.
        var resolvedFilter = _pageVariants.DefaultFilter;
        if (!string.IsNullOrWhiteSpace(filter) &&
            !PageVariantFilters.TryNormalize(filter, out resolvedFilter))
        {
            return BadRequest(new ApiError
            {
                Error = "invalid_request",
                Message = "filter must be one of " + PageVariantFilters.Vocabulary + ".",
            });
        }

        return await GetPageInternal(itemId, entryKey, "original", ct, requestedMaxDim, resolvedFilter);
    }

    [HttpGet("{itemId}/pages/{entryKey}/thumbnail")]
    public async Task<IActionResult> GetPageThumbnail(string itemId, string entryKey, CancellationToken ct)
    {
        return await GetPageInternal(itemId, entryKey, "thumbnail", ct);
    }

    [HttpGet("{itemId}/cover")]
    public async Task<IActionResult> GetCover(string itemId, CancellationToken ct)
    {
        // Cover = first page by ordinal (entry key resolved from PageEntries)
        return await GetCoverInternal(itemId, ct);
    }

    private async Task<IActionResult> GetPageInternal(
        string itemId, string entryKey, string variant, CancellationToken ct,
        int requestedMaxDim = 0, string? resizeFilter = null)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal node ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        // Check library access
        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        if (node.Kind != 1)
            return BadRequest(new ApiError { Error = "not_readable", Message = "Item is not a readable archive." });

        // Get the archive item
        var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);
        if (archiveItem is null || archiveItem.AnalysisState != 0)
            return NotFound(new ApiError { Error = "not_analyzed", Message = "Item has not been analyzed yet." });

        // Look up page by entry key (audit defect D3 — was pageIndex)
        var pageEntry = await _db.PageEntries
            .Where(p => p.ItemId == node.Id && p.EntryKey == entryKey)
            .FirstOrDefaultAsync(ct);
        if (pageEntry is null)
            return NotFound(new ApiError { Error = "page_not_found", Message = "Page entry key not found." });

        // Map the requested variant to a worker variant. Pages and covers are
        // served as WebP; the thumbnail endpoint gets a downscaled WebP; a page
        // request carrying maxDim may get a bucket-sized WebP (1.19.0).
        var workerVariant = variant == "thumbnail" ? "thumbnail" : "webp";
        var pageMaxDimension = 0;
        // Only set alongside pageMaxDimension: the filter is meaningless (and
        // must stay out of the cache key) when nothing is being resampled.
        string? pageResizeFilter = null;

        if (variant != "thumbnail" && requestedMaxDim > 0 &&
            _pageVariants.SelectBucket(requestedMaxDim) is int bucket)
        {
            // Never upscale, and never spend cache budget on a "downscale" that
            // would be a verbatim copy: when the page's own longest edge already
            // fits the bucket, the full-size variant IS the sized variant. Width
            // and height are null/0 when analysis could not determine them; in
            // that case size it and let the worker decide (it never upscales).
            var longestEdge = Math.Max(pageEntry.Width ?? 0, pageEntry.Height ?? 0);
            if (longestEdge <= 0 || longestEdge > bucket)
            {
                pageResizeFilter = resizeFilter ?? _pageVariants.DefaultFilter;
                workerVariant = PageVariantOptions.VariantName(bucket, pageResizeFilter);
                pageMaxDimension = bucket;
            }
        }

        // Try cache first — serve the stored media type (handles animated passthrough).
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, pageEntry.EntryKey, workerVariant);
        if (_cache.TryGet(cacheKey, out var hit) && hit is not null)
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                SetCacheHeaders(cacheKey, workerVariant);
                return File(cachedStream, hit.MediaType);
            }
        }

        // Cache miss — extract + encode in the WORKER (the server never opens the
        // archive itself; image decoding stays inside the worker fault boundary).
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == node.LibraryId, ct);
        if (library is null)
            return NotFound(new ApiError { Error = "source_missing", Message = "Source library is not accessible." });

        var sourcePath = Path.Combine(library.RootPath, node.RelativePath);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new ApiError { Error = "source_missing", Message = "Source file is not accessible." });

        Directory.CreateDirectory(_cache.ScratchDirectory);
        var outputPath = Path.Combine(_cache.ScratchDirectory, "page-" + Guid.NewGuid().ToString("N")[..12] + ".bin");

        var outcome = await _workerPool.ExtractPageAsync(
            sourcePath, pageEntry.SourceEntryLocator, workerVariant,
            archiveItem.ModificationTicks, archiveItem.ByteLength,
            outputPath, ThumbnailMaxDimension,
            // Sized variants get their own configurable quality; the full-size
            // and thumbnail paths keep the existing constant.
            pageMaxDimension > 0 ? _pageVariants.WebpQuality : WebpQuality,
            ct, pageMaxDimension, pageResizeFilter);

        if (!outcome.Success)
        {
            try { System.IO.File.Delete(outputPath); } catch { /* best effort */ }
            return MapExtractionFailure(outcome, node.Id, entryKey);
        }

        try
        {
            await _cache.PublishAsync(cacheKey, outcome.OutputPath!, outcome.MediaType!, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(LogEvents.Worker.PageCachePublishFailed, "Cache publish failed (non-fatal): {Error}", ex.GetType().Name);
        }
        finally
        {
            try { System.IO.File.Delete(outputPath); } catch { /* best effort */ }
        }

        var published = _cache.OpenRead(cacheKey);
        if (published is not null)
        {
            SetCacheHeaders(cacheKey, workerVariant);
            return File(published, outcome.MediaType!);
        }

        // Publishing failed but extraction succeeded — this should be rare; report.
        return StatusCode(500, new ApiError { Error = "extraction_error", Message = "Failed to serve page." });
    }

    /// <summary>
    /// Maps a worker extraction failure to an HTTP response with a stable code.
    /// </summary>
    private IActionResult MapExtractionFailure(PageExtractionOutcome outcome, long nodeId, string entryKey)
    {
        _logger.LogWarning(LogEvents.Worker.PageExtractionFailed, "Page extraction failed for item {ItemId} entry {EntryKey}: {Error}",
            nodeId, entryKey, outcome.ErrorType);
        return outcome.ErrorType switch
        {
            "page_not_found" => NotFound(new ApiError { Error = "page_not_found", Message = "Page not found in archive." }),
            "encrypted" => StatusCode(422, new ApiError { Error = "encrypted", Message = "This archive is password-protected." }),
            "unsupported_solid" => StatusCode(422, new ApiError { Error = "unsupported_solid", Message = "Solid archives are not yet supported for reading." }),
            "source_changed" => Conflict(new ApiError { Error = "source_changed", Message = "The source changed; re-analysis is needed." }),
            "source_missing" => NotFound(new ApiError { Error = "source_missing", Message = "The source file is no longer available." }),
            "timeout" or "busy" => StatusCode(503, new ApiError { Error = "try_again", Message = "The page could not be prepared in time; please retry." }),
            _ => StatusCode(500, new ApiError { Error = "extraction_error", Message = "Failed to extract page." }),
        };
    }

    private async Task<IActionResult> GetCoverInternal(string itemId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        if (node.Kind != 1)
            return BadRequest(new ApiError { Error = "not_readable", Message = "Item is not a readable archive." });

        var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);
        if (archiveItem is null || archiveItem.AnalysisState != 0)
        {
            // Not analyzed yet — the thumbnail cannot exist. Return a typed
            // pending response so the frontend shows a placeholder rather than
            // a broken-image icon (owner requirement, 2026-09-09).
            return Accepted(new ApiError { Error = "pending", Message = "Thumbnail is being generated." });
        }

        // Serve the durable thumbnail from the persistent store (1.2.0).
        // Thumbnails are pre-generated at analysis time and persisted under
        // DataRoot/thumbnails — never the evictable page cache.
        _thumbnailStore.Initialize();
        var thumbnailStream = _thumbnailStore.OpenRead(node.Id, archiveItem.ContentVersion);
        if (thumbnailStream is not null)
        {
            // Long-lived immutable cache headers keyed by content version: a
            // changed source yields a new content version (and a new thumbnail
            // file), so the old URL's bytes are safe to cache indefinitely.
            Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            Response.Headers.ETag = $"\"thumb-{node.Id}-{archiveItem.ContentVersion}\"";
            _logger.LogDebug(LogEvents.Worker.ThumbnailServedFromStore, "Cover served from durable thumbnail store (item {ItemId})", node.Id);
            return File(thumbnailStream, "image/webp");
        }

        // Thumbnail not generated yet (e.g., backfill not complete). Return a
        // typed pending response; the frontend shows a placeholder and can
        // retry. A generate-on-miss safety net is triggered below.
        _logger.LogDebug(LogEvents.Worker.ThumbnailStoreMiss, "Cover thumbnail store miss (item {ItemId}); returning pending", node.Id);

        // Safety net: trigger generation fire-and-forget so a retry will find
        // the thumbnail. The primary path is scan-time generation; this only
        // catches items that were analyzed before the thumbnail feature existed.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var thumbService = scope.ServiceProvider.GetService<ThumbnailGenerationService>();
                if (thumbService is not null)
                    await thumbService.GenerateForItemAsync(node.Id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(LogEvents.Worker.ThumbnailGenerationFailed, "Generate-on-miss failed (item {ItemId}): {Error}", node.Id, ex.GetType().Name);
            }
        }, CancellationToken.None);

        return Accepted(new ApiError { Error = "pending", Message = "Thumbnail is being generated." });
    }

    /// <summary>
    /// Page images are immutable per content version: the cache key embeds the
    /// item's <c>ContentVersion</c>, so a changed source yields a new URL rather
    /// than new bytes at the same URL. That makes long-lived private validation
    /// caching safe and correct (audit defect D9 / finding A1). The previous
    /// <c>no-store</c> value contradicted the ETag and forced a full re-download
    /// of every page on every view.
    /// </summary>
    private const int PageCacheMaxAgeSeconds = 86400; // 1 day

    /// <summary>
    /// Sets a coherent Cache-Control + ETag pair on binary page responses so the
    /// browser can cache and revalidate (audit defect D9 / finding A1), plus the
    /// <c>X-MangaPixer-Variant</c> header naming what was actually served. The
    /// cache key already embeds the variant, so per-bucket responses get distinct
    /// ETags for free.
    /// </summary>
    private void SetCacheHeaders(string cacheKey, string servedVariant)
    {
        Response.Headers.CacheControl = $"private, max-age={PageCacheMaxAgeSeconds}, immutable";
        Response.Headers.ETag = $"\"{cacheKey}\"";
        Response.Headers[VariantHeaderName] = servedVariant;
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }

    private async Task<bool> HasAccessAsync(long userId, long libraryId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        if (user.IsAdmin) return true;
        return await _db.LibraryGrants.AnyAsync(g => g.UserId == userId && g.LibraryId == libraryId, ct);
    }
}
