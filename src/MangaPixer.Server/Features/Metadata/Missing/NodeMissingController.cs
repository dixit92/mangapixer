namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The series page's Missing line for everyone who can open the folder (1.28.0, owner): the same stored-data row
/// as the admin report, under the node's access rules exactly like series information - no access to the node's
/// library answers 404 (never 403), so a non-member cannot learn that the node exists. Makes no request anywhere.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class NodeMissingController : ControllerBase
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly MissingReportService _report;

    public NodeMissingController(MangaPixerDbContext db, LibraryAuthorizationService libraryAuth, MissingReportService report)
    {
        _db = db;
        _libraryAuth = libraryAuth;
        _report = report;
    }

    [HttpGet("nodes/{nodeId}/missing")]
    [ProducesResponseType<MissingSeriesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return NotFound();
        if (!await _libraryAuth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return NotFound();

        return await _report.ForNodeAsync(nodeId, ct) is { } dto ? Ok(dto) : NotFound();
    }
}
