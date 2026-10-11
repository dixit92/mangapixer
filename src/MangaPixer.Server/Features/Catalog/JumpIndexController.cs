using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace com.lifepixer.mangapixer.Server.Features.Catalog;

/// <summary>
/// Jump-index API endpoint for multilingual collation-aware jump navigation.
///
/// Exposes a per-library A–Z/script rail built from the existing SortKeys. The
/// endpoint is kept separate from <c>CatalogController</c>, which owns the main
/// catalog-browsing surface. It reuses <c>CatalogIdResolver</c> for public→internal ID
/// resolution and <c>JumpIndexService</c> for the bucket computation.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class JumpIndexController : ControllerBase
{
    private readonly JumpIndexService _jumpIndexService;
    private readonly CatalogIdResolver _idResolver;
    private readonly ILogger<JumpIndexController> _logger;

    public JumpIndexController(
        JumpIndexService jumpIndexService,
        CatalogIdResolver idResolver,
        ILogger<JumpIndexController> logger)
    {
        _jumpIndexService = jumpIndexService;
        _idResolver = idResolver;
        _logger = logger;
    }

    /// <summary>
    /// Returns the jump index of one listed level: a list of buckets (label, count, firstCursor, firstNodeId, range) computed
    /// server-side from the level's SortKeys and names. Without <paramref name="parentId"/> it covers the library root, with it that
    /// folder's children (1.41.0; the folder must belong to the library, else 404, like a node of another library). Each bucket is one
    /// contiguous run of the listing, and its firstCursor is a valid keyset cursor for the browse name sort in
    /// <paramref name="direction"/> (<c>asc</c> default, <c>desc</c>). <paramref name="group"/> is the browse Volumes-view override
    /// (<c>volumes</c> / <c>flat</c>): where the Volumes view is shown the index is empty. Authorization is enforced by the service.
    /// </summary>
    [HttpGet("libraries/{libraryId}/jump-index")]
    [ProducesResponseType(typeof(JumpIndexDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJumpIndex(
        string libraryId,
        [FromQuery] string? parentId,
        [FromQuery] string? direction,
        [FromQuery] string? group,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var library = await _idResolver.ResolveLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        long? parentIdLong = null;
        if (parentId is not null)
        {
            var parent = await _idResolver.ResolveNodeAsync(parentId, ct);
            if (parent is null || parent.LibraryId != library.Id) return NotFound();
            parentIdLong = parent.Id;
        }

        var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
        var result = await _jumpIndexService.GetJumpIndexAsync(userId.Value, library.Id, parentIdLong, descending, group, ct);

        // Re-stamp the public id so the DTO carries the caller-facing id even
        // when the level had no rows (or the Volumes view hid the rail).
        return Ok(result with { LibraryId = library.PublicId });
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}
