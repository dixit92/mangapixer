namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The Info panel's tags (1.45.0, lane Q). Cookie auth (the default scheme; never an API token). Follows series-info's access rule: a
/// node or library the viewer may not open answers 404 (never 403), so a non-member cannot learn that it exists. Direct access works
/// in Incognito; only the counts leave Private libraries out.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class InfoPanelController : ControllerBase
{
    private readonly InfoPanelService _panels;
    private readonly IncognitoAccessor _incognito;

    public InfoPanelController(InfoPanelService panels, IncognitoAccessor incognito)
    {
        _panels = panels;
        _incognito = incognito;
    }

    /// <summary>The tags of a node: its own work's values, or (a container) the values of the works below, top 12 per bucket.</summary>
    [HttpGet("nodes/{nodeId}/info")]
    [ProducesResponseType<InfoPanelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetNodeInfo(string nodeId, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var panel = await _panels.ForNodeAsync(userId, _incognito.IsIncognito, nodeId, ct);
        return panel is null ? NotFound() : Ok(panel);
    }

    /// <summary>The tags of a library root (the values of every work in it, top 12 per bucket).</summary>
    [HttpGet("libraries/{libraryId}/info")]
    [ProducesResponseType<InfoPanelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLibraryInfo(string libraryId, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var panel = await _panels.ForLibraryAsync(userId, _incognito.IsIncognito, libraryId, ct);
        return panel is null ? NotFound() : Ok(panel);
    }

    private long? UserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
