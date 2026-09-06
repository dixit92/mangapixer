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
    private readonly ILogger<PageController> _logger;

    public PageController(
        MangaPlexDbContext db,
        CacheService cache,
        CatalogIdResolver idResolver,
        ILogger<PageController> logger)
    {
        _db = db;
        _cache = cache;
        _idResolver = idResolver;
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

        // Try cache first
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, pageEntry.EntryKey, variant);
        if (_cache.TryGet(cacheKey, out _))
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                SetCacheHeaders(cacheKey, pageEntry.MediaType);
                return File(cachedStream, pageEntry.MediaType);
            }
        }

        // Extract from archive directly (ZIP only for now; worker handles other formats)
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == node.LibraryId, ct);
        if (library is null)
            return NotFound(new ApiError { Error = "source_missing", Message = "Source library is not accessible." });

        var sourcePath = Path.Combine(library.RootPath, node.RelativePath);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new ApiError { Error = "source_missing", Message = "Source file is not accessible." });

        try
        {
            var (stream, mediaType) = await ExtractPageAsync(sourcePath, pageEntry.SourceEntryLocator, variant, ct);
            if (stream is null)
                return NotFound(new ApiError { Error = "extraction_failed", Message = "Could not extract page from archive." });

            // Cache the extracted page
            if (variant is "original" or "cover")
            {
                await CachePageAsync(cacheKey, stream, mediaType, ct);
                var cachedStream = _cache.OpenRead(cacheKey);
                if (cachedStream is not null)
                {
                    SetCacheHeaders(cacheKey, mediaType);
                    return File(cachedStream, mediaType);
                }
            }

            SetCacheHeaders(cacheKey, mediaType);
            return File(stream, mediaType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Page extraction failed for item {ItemId} entry {EntryKey}: {Error}",
                node.Id, entryKey, ex.GetType().Name);
            return StatusCode(500, new ApiError { Error = "extraction_error", Message = "Failed to extract page." });
        }
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

    private static async Task<(Stream? Stream, string MediaType)> ExtractPageAsync(
        string archivePath, string entryKey, string variant, CancellationToken ct)
    {
        // Only ZIP is supported for direct server-side extraction.
        // Other formats (7z, RAR) require the worker process.
        if (!archivePath.EndsWith(".cbz", StringComparison.OrdinalIgnoreCase) &&
            !archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "application/octet-stream");
        }

        using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(entryKey);
        if (entry is null)
            return (null, "application/octet-stream");

        var mediaType = GetMediaType(entryKey);

        if (variant == "thumbnail")
        {
            // For thumbnails, return the original for now.
            // Image resizing via the worker will be added in a future iteration.
            var thumbStream = new MemoryStream();
            using var entryStream = entry.Open();
            await entryStream.CopyToAsync(thumbStream, ct);
            thumbStream.Position = 0;
            return (thumbStream, mediaType);
        }

        // Original or cover — extract to memory
        var ms = new MemoryStream();
        using var es = entry.Open();
        await es.CopyToAsync(ms, ct);
        ms.Position = 0;
        return (ms, mediaType);
    }

    private async Task CachePageAsync(string cacheKey, Stream stream, string mediaType, CancellationToken ct)
    {
        try
        {
            // Write to a temp file under the app-managed cache scratch dir, not
            // the system temp dir (audit finding A2 — temp bytes must stay under
            // the scratch/cache boundary).
            var tempDir = _cache.ScratchDirectory;
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, "page-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: false))
            {
                await stream.CopyToAsync(fs, ct);
            }
            stream.Position = 0;
            await _cache.PublishAsync(cacheKey, tempPath, mediaType, ct);
            try { System.IO.File.Delete(tempPath); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Cache publish failed (non-fatal): {Error}", ex.GetType().Name);
        }
    }

    private static string GetMediaType(string entryKey)
    {
        var ext = Path.GetExtension(entryKey).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
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
