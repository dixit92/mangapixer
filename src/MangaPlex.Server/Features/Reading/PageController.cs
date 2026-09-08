namespace com.lifepixer.mangaplex.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Catalog;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Page and cover delivery endpoints.
/// GET /api/v1/items/{itemId}/pages/{entryKey} — page image (original or variant)
/// GET /api/v1/items/{itemId}/pages/{entryKey}/thumbnail — thumbnail variant
/// GET /api/v1/items/{itemId}/cover — cover image (first page)
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
    private readonly MangaPlexDbContext _db;
    private readonly CacheService _cache;
    private readonly CatalogIdResolver _idResolver;
    private readonly MediaWorkerPool _workerPool;
    private readonly ILogger<PageController> _logger;

    // WebP transcode defaults (C13). Overridable later via preferences/config.
    private const int ThumbnailMaxDimension = 320;
    private const int WebpQuality = 82;

    public PageController(
        MangaPlexDbContext db,
        CacheService cache,
        CatalogIdResolver idResolver,
        MediaWorkerPool workerPool,
        ILogger<PageController> logger)
    {
        _db = db;
        _cache = cache;
        _idResolver = idResolver;
        _workerPool = workerPool;
        _logger = logger;
    }

    [HttpGet("{itemId}/pages/{entryKey}")]
    public async Task<IActionResult> GetPage(string itemId, string entryKey, CancellationToken ct)
    {
        return await GetPageInternal(itemId, entryKey, "original", ct);
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

    private async Task<IActionResult> GetPageInternal(string itemId, string entryKey, string variant, CancellationToken ct)
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
        // served as WebP (C13); the thumbnail endpoint gets a downscaled WebP.
        var workerVariant = variant == "thumbnail" ? "thumbnail" : "webp";

        // Try cache first — serve the stored media type (handles animated passthrough).
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, pageEntry.EntryKey, workerVariant);
        if (_cache.TryGet(cacheKey, out var hit) && hit is not null)
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                SetCacheHeaders(cacheKey, hit.MediaType);
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
            outputPath, ThumbnailMaxDimension, WebpQuality, ct);

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
            _logger.LogDebug("Cache publish failed (non-fatal): {Error}", ex.GetType().Name);
        }
        finally
        {
            try { System.IO.File.Delete(outputPath); } catch { /* best effort */ }
        }

        var published = _cache.OpenRead(cacheKey);
        if (published is not null)
        {
            SetCacheHeaders(cacheKey, outcome.MediaType!);
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
        _logger.LogWarning("Page extraction failed for item {ItemId} entry {EntryKey}: {Error}",
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
            return NotFound(new ApiError { Error = "not_analyzed", Message = "Item has not been analyzed yet." });

        // Cover = first page by ordinal
        var firstPage = await _db.PageEntries
            .Where(p => p.ItemId == node.Id)
            .OrderBy(p => p.Ordinal)
            .FirstOrDefaultAsync(ct);
        if (firstPage is null)
            return NotFound(new ApiError { Error = "no_pages", Message = "Item has no analyzed pages." });

        // Delegate to the page delivery path with the first page's entry key
        return await GetPageInternal(itemId, firstPage.EntryKey, "cover", ct);
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
    /// browser can cache and revalidate (audit defect D9 / finding A1).
    /// </summary>
    private void SetCacheHeaders(string cacheKey, string mediaType)
    {
        Response.Headers.CacheControl = $"private, max-age={PageCacheMaxAgeSeconds}, immutable";
        Response.Headers.ETag = $"\"{cacheKey}\"";
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
