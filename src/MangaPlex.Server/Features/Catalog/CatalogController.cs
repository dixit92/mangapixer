namespace com.lifepixer.mangaplex.Server.Features.Catalog;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Catalog API endpoints: libraries, browse, node lookup, breadcrumbs, neighbors, search.
/// All endpoints require authentication. Authorization is enforced by the service layer.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class CatalogController : ControllerBase
{
    private readonly CatalogBrowseService _browseService;
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<CatalogController> _logger;

    public CatalogController(
        CatalogBrowseService browseService,
        MangaPlexDbContext db,
        ILogger<CatalogController> logger)
    {
        _browseService = browseService;
        _db = db;
        _logger = logger;
    }

    [HttpGet("libraries")]
    public async Task<IActionResult> GetLibraries(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Get libraries the user can access
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        IQueryable<LibraryEntity> query = _db.Libraries;
        if (!user.IsAdmin)
        {
            var grantedLibIds = await _db.LibraryGrants
                .Where(g => g.UserId == userId)
                .Select(g => g.LibraryId)
                .ToListAsync(ct);
            query = query.Where(l => grantedLibIds.Contains(l.Id));
        }

        var libraries = await query
            .Select(l => new LibraryDto
            {
                Id = l.PublicId,
                Name = l.DisplayName,
                IsScanning = false,
                ItemCount = null,
                LastScanCompleted = null,
            })
            .ToListAsync(ct);

        return Ok(libraries);
    }

    [HttpGet("libraries/{libraryId}/browse")]
    public async Task<IActionResult> Browse(
        string libraryId,
        [FromQuery] string? parentId,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var libId = OpaqueId.Decode(libraryId);
        long? parentIdLong = parentId is not null ? OpaqueId.Decode(parentId) : null;

        var result = await _browseService.BrowseAsync(
            userId.Value, libId, parentIdLong, cursor, pageSize, ct: ct);

        return Ok(result);
    }

    [HttpGet("nodes/{nodeId}")]
    public async Task<IActionResult> GetNode(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var node = await _browseService.GetNodeAsync(userId.Value, nodeId, ct);
        if (node is null) return NotFound();

        return Ok(node);
    }

    [HttpGet("nodes/{nodeId}/breadcrumbs")]
    public async Task<IActionResult> GetBreadcrumbs(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var nodeIdLong = OpaqueId.Decode(nodeId);
        var result = await _browseService.GetBreadcrumbsAsync(userId.Value, nodeIdLong, ct);
        if (result is null) return NotFound();

        return Ok(result);
    }

    [HttpGet("nodes/{nodeId}/neighbors")]
    public async Task<IActionResult> GetNeighbors(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var nodeIdLong = OpaqueId.Decode(nodeId);
        var result = await _browseService.GetNeighborsAsync(userId.Value, nodeIdLong, ct);
        if (result is null) return NotFound();

        return Ok(result);
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q,
        [FromQuery] string? libraryId,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        long? libId = libraryId is not null ? OpaqueId.Decode(libraryId) : null;

        var result = await _browseService.SearchAsync(userId.Value, q, libId, ct: ct);
        return Ok(result);
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}
