namespace com.lifepixer.mangaplex.Server.Features.Reading;

using System.IO.Compression;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Page and cover delivery endpoints.
/// GET /api/v1/items/{itemId}/pages/{pageIndex} — page image (original or variant)
/// GET /api/v1/items/{itemId}/pages/{pageIndex}/thumbnail — thumbnail variant
/// GET /api/v1/items/{itemId}/cover — cover image (first page)
///
/// All endpoints check authorization before serving. Source paths are never
/// exposed. Pages are extracted on-demand from ZIP archives and cached.
/// </summary>
[ApiController]
[Route("api/v1/items")]
[Authorize]
public sealed class PageController : ControllerBase
{
    private readonly MangaPlexDbContext _db;
    private readonly CacheService _cache;
    private readonly ILogger<PageController> _logger;

    public PageController(
        MangaPlexDbContext db,
        CacheService cache,
        ILogger<PageController> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    [HttpGet("{itemId}/pages/{pageIndex}")]
    public async Task<IActionResult> GetPage(string itemId, int pageIndex, CancellationToken ct)
    {
        return await GetPageInternal(itemId, pageIndex, "original", ct);
    }

    [HttpGet("{itemId}/pages/{pageIndex}/thumbnail")]
    public async Task<IActionResult> GetPageThumbnail(string itemId, int pageIndex, CancellationToken ct)
    {
        return await GetPageInternal(itemId, pageIndex, "thumbnail", ct);
    }

    [HttpGet("{itemId}/cover")]
    public async Task<IActionResult> GetCover(string itemId, CancellationToken ct)
    {
        return await GetPageInternal(itemId, 0, "cover", ct);
    }

    private async Task<IActionResult> GetPageInternal(string itemId, int pageIndex, string variant, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _db.CatalogNodes
            .Include(n => n.Library)
            .FirstOrDefaultAsync(n => n.PublicId == itemId, ct);
        if (node is null) return NotFound();

        // Check library access
        if (!await HasAccessAsync(userId.Value, node.LibraryId, ct))
            return NotFound();

        if (node.Kind != 1)
            return BadRequest(new ApiError { Error = "not_readable", Message = "Item is not a readable archive." });

        // Get the page entry
        var archiveItem = await _db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id, ct);
        if (archiveItem is null || archiveItem.AnalysisState != 0)
            return NotFound(new ApiError { Error = "not_analyzed", Message = "Item has not been analyzed yet." });

        var pageEntry = await _db.PageEntries
            .Where(p => p.ItemId == node.Id && p.Ordinal == pageIndex)
            .FirstOrDefaultAsync(ct);
        if (pageEntry is null)
            return NotFound(new ApiError { Error = "page_not_found", Message = "Page index out of range." });

        // Try cache first
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, pageEntry.EntryKey, variant);
        if (_cache.TryGet(cacheKey, out _))
        {
            var cachedStream = _cache.OpenRead(cacheKey);
            if (cachedStream is not null)
            {
                return File(cachedStream, pageEntry.MediaType);
            }
        }

        // Extract from archive directly (ZIP only for now)
        var sourcePath = Path.Combine(node.Library!.RootPath, node.RelativePath);
        if (!System.IO.File.Exists(sourcePath))
            return NotFound(new ApiError { Error = "source_missing", Message = "Source file is not accessible." });

        try
        {
            var (stream, mediaType) = await ExtractPageAsync(sourcePath, pageEntry.SourceEntryLocator, variant, ct);
            if (stream is null)
                return NotFound(new ApiError { Error = "extraction_failed", Message = "Could not extract page from archive." });

            // Cache the extracted page (for original and cover variants)
            if (variant is "original" or "cover")
            {
                await CachePageAsync(cacheKey, stream, mediaType, ct);
                // Re-open from cache to get a fresh stream
                var cachedStream = _cache.OpenRead(cacheKey);
                if (cachedStream is not null)
                    return File(cachedStream, mediaType);
            }

            return File(stream, mediaType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Page extraction failed for item {ItemId} page {PageIndex}: {Error}",
                node.Id, pageIndex, ex.GetType().Name);
            return StatusCode(500, new ApiError { Error = "extraction_error", Message = "Failed to extract page." });
        }
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

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(entryKey);
        if (entry is null)
            return (null, "application/octet-stream");

        var mediaType = GetMediaType(entryKey);

        if (variant == "thumbnail")
        {
            // For thumbnails, return the original for now.
            // Image resizing will be added in a future iteration.
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
            // Write to a temp file then publish to cache
            var tempPath = Path.Combine(Path.GetTempPath(), "mangaplex-page-" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
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
