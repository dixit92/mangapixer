namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Series information for a node (1.24.0): one endpoint feeds both the overlay and
/// the series page. Metadata is only reachable THROUGH a node and follows the
/// node's access rules: a user without access to the node's library gets 404
/// (never 403), so a non-member cannot even learn that a node or series exists.
/// Direct access works in Incognito (like node lookup); Incognito only filters
/// discovery surfaces.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class SeriesInfoController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly SeriesInfoResolver _resolver;

    public SeriesInfoController(MangaPixerDbContext db, LibraryAuthorizationService libraryAuth, SeriesInfoResolver resolver)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _resolver = resolver;
    }

    /// <summary>
    /// Resolved series information. When "Show series information" is off for the
    /// node's library the response is 200 with <c>state: None</c> - the same shape
    /// as a node that simply has none; 404 stays reserved for "no such node / no
    /// access". <paramref name="includeItems"/> adds the per-item ComicInfo table
    /// (series page).
    /// </summary>
    [HttpGet("nodes/{nodeId}/series-info")]
    [ProducesResponseType<SeriesInfoDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSeriesInfo(string nodeId, [FromQuery] bool includeItems = false, CancellationToken ct = default)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return NotFound();

        return Ok(await _resolver.ResolveAsync(node, includeItems, ct));
    }
}
