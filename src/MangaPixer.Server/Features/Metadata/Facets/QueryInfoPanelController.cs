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
    public async Task<IActionResult> GetLibraryInfo(string libraryId, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var panel = await _panels.ForLibraryAsync(userId, _incognito.IsIncognito, libraryId, ct);
        return panel is null ? NotFound() : Ok(panel);
    }

    /// <summary>
    /// 1.47.0 (lane P, "+N more"): one bucket of a node's panel, paged (<paramref name="offset"/> from 0, <paramref name="limit"/> 1 -
    /// <see cref="InfoPanelService.MaxBucketPage"/>, default 100). <paramref name="bucket"/> is a filter token (<c>creator</c>,
    /// <c>genre</c>, ...) or a bucket name; anything else is 400 <c>invalid_bucket</c>. The node's access rule is the panel's (404).
    /// </summary>
    [HttpGet("nodes/{nodeId}/info/buckets/{bucket}")]
    [ProducesResponseType<FacetBucketPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNodeBucket(
        string nodeId, string bucket, [FromQuery] int offset = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        if (!FacetBucketTokens.TryParse(bucket, out var parsed))
            return BadRequest(InvalidBucket());
        var page = await _panels.BucketForNodeAsync(userId, _incognito.IsIncognito, nodeId, parsed, offset, limit, ct);
        return page is null ? NotFound() : Ok(page);
    }

    /// <summary>1.47.0 (lane P): one bucket of a library root's panel, paged like <see cref="GetNodeBucket"/>.</summary>
    [HttpGet("libraries/{libraryId}/info/buckets/{bucket}")]
    [ProducesResponseType<FacetBucketPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLibraryBucket(
        string libraryId, string bucket, [FromQuery] int offset = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        if (!FacetBucketTokens.TryParse(bucket, out var parsed))
            return BadRequest(InvalidBucket());
        var page = await _panels.BucketForLibraryAsync(userId, _incognito.IsIncognito, libraryId, parsed, offset, limit, ct);
        return page is null ? NotFound() : Ok(page);
    }

    private static ApiError InvalidBucket() =>
        new() { Error = "invalid_bucket", Message = "A bucket is a filter token such as creator, genre or publisher." };

    private long? UserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
