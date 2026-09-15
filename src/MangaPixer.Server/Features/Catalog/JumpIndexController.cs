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
    /// Returns the per-library jump index: a list of buckets (label, count,
    /// firstCursor) computed server-side from the top-level children's SortKeys.
    /// Each bucket's firstCursor is a valid keyset cursor for the name sort of
    /// the browse endpoint. Authorization is enforced by the service.
    /// </summary>
    [HttpGet("libraries/{libraryId}/jump-index")]
    public async Task<IActionResult> GetJumpIndex(string libraryId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var library = await _idResolver.ResolveLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var result = await _jumpIndexService.GetJumpIndexAsync(userId.Value, library.Id, ct);

        // Re-stamp the public id so the DTO carries the caller-facing id even
        // when the library had no top-level children (the service resolves it
        // separately in that case, but this guarantees correctness).
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
