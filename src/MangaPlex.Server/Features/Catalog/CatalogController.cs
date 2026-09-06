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
    private readonly CatalogIdResolver _idResolver;
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<CatalogController> _logger;

    public CatalogController(
        CatalogBrowseService browseService,
        CatalogIdResolver idResolver,
        MangaPlexDbContext db,
        ILogger<CatalogController> logger)
    {
        _browseService = browseService;
        _idResolver = idResolver;
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

        var libraries = await query.ToListAsync(ct);

        // Compute IsScanning and ItemCount for each library — the list
        // endpoint previously hard-coded these to false/null (audit defect D30).
        var scanningLibIds = await _db.ScanRuns
            .Where(s => s.Status == 1)
            .Select(s => s.LibraryId)
            .Distinct()
            .ToListAsync(ct);
        var scanningSet = new HashSet<long>(scanningLibIds);

        var itemCounts = await _db.CatalogNodes
            .Where(n => n.Kind == 1 && n.Availability != 5)
            .GroupBy(n => n.LibraryId)
            .Select(g => new { LibraryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.LibraryId, x => x.Count, ct);

        var dtos = libraries.Select(l => new LibraryDto
        {
            Id = l.PublicId,
            Name = l.DisplayName,
            IsScanning = scanningSet.Contains(l.Id),
            ItemCount = itemCounts.TryGetValue(l.Id, out var count) ? count : 0,
            LastScanCompleted = l.LastScanCompleted,
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("libraries/{libraryId}")]
    public async Task<IActionResult> GetLibrary(string libraryId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == libraryId, ct);
        if (library is null) return NotFound();

        // Check access
        if (!user.IsAdmin)
        {
            var hasGrant = await _db.LibraryGrants
                .AnyAsync(g => g.UserId == userId && g.LibraryId == library.Id, ct);
            if (!hasGrant) return NotFound();
        }

        var itemCount = await _db.CatalogNodes
            .CountAsync(n => n.LibraryId == library.Id && n.Kind == 1 && n.Availability != 5, ct);

        var isScanning = await _db.ScanRuns
            .AnyAsync(s => s.LibraryId == library.Id && s.Status == 1, ct);

        return Ok(new LibraryDto
        {
            Id = library.PublicId,
            Name = library.DisplayName,
            IsScanning = isScanning,
            ItemCount = itemCount,
            LastScanCompleted = library.LastScanCompleted,
        });
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

        // Resolve public IDs to internal IDs (audit defect D5/D29)
        var library = await _idResolver.ResolveLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        long? parentIdLong = null;
        if (parentId is not null)
        {
            var parent = await _idResolver.ResolveNodeAsync(parentId, ct);
            if (parent is null) return NotFound();
            parentIdLong = parent.Id;
        }

        var result = await _browseService.BrowseAsync(
            userId.Value, library.Id, parentIdLong, cursor, pageSize, ct: ct);

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

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(nodeId, ct);
        if (node is null) return NotFound();

        var result = await _browseService.GetBreadcrumbsAsync(userId.Value, node.Id, ct);
        if (result is null) return NotFound();

        return Ok(result);
    }

    [HttpGet("nodes/{nodeId}/neighbors")]
    public async Task<IActionResult> GetNeighbors(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        // Resolve public ID to internal ID (audit defect D5/D29)
        var node = await _idResolver.ResolveNodeAsync(nodeId, ct);
        if (node is null) return NotFound();

        var result = await _browseService.GetNeighborsAsync(userId.Value, node.Id, ct);
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

        // Resolve public ID to internal ID (audit defect D5/D29)
        long? libId = null;
        if (libraryId is not null)
        {
            var library = await _idResolver.ResolveLibraryAsync(libraryId, ct);
            if (library is null) return NotFound();
            libId = library.Id;
        }

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
