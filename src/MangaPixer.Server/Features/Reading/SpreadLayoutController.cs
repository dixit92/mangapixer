namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Shared per-archive double-page pairing (1.23.0).
/// PUT /api/v1/items/{itemId}/spread-layout — replace the archive's forced spread starts.
/// Readers receive the saved layout in the item manifest (<c>spreadStarts</c>), so there
/// is no separate GET. Antiforgery is enforced by the global filter.
/// </summary>
[ApiController]
[Route("api/v1/items")]
[Authorize]
public sealed class SpreadLayoutController : ControllerBase
{
    private readonly SpreadLayoutService _layouts;
    private readonly CatalogIdResolver _idResolver;

    public SpreadLayoutController(SpreadLayoutService layouts, CatalogIdResolver idResolver)
    {
        _layouts = layouts;
        _idResolver = idResolver;
    }

    [HttpPut("{itemId}/spread-layout")]
    public async Task<IActionResult> SetSpreadLayout(
        string itemId,
        [FromBody] SetSpreadLayoutRequest request,
        CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();

        var node = await _idResolver.ResolveNodeAsync(itemId, ct);
        if (node is null) return NotFound();

        var result = await _layouts.SetAsync(
            userId, node.Id, itemId, request.ExpectedContentVersion, request.SpreadStarts, ct);

        return result.Status switch
        {
            SpreadLayoutService.SetStatus.Ok => Ok(result.Layout),
            SpreadLayoutService.SetStatus.NotFound => NotFound(),
            SpreadLayoutService.SetStatus.NotReadable => BadRequest(new ApiError { Error = "not_readable", Message = result.Error ?? "Item is not a readable archive." }),
            SpreadLayoutService.SetStatus.StaleContent => Conflict(new ApiError { Error = "stale_content", Message = result.Error ?? "Content version mismatch" }),
            SpreadLayoutService.SetStatus.Invalid => BadRequest(new ApiError { Error = "invalid", Message = result.Error ?? "Invalid request" }),
            _ => StatusCode(500, new ApiError { Error = "internal_error", Message = "Unexpected error" }),
        };
    }
}
