namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin endpoints of the Wikipedia companion of a linked series (1.32.0). Every action that contacts Wikipedia sends its requests only
/// through the gateway (typed <see cref="ApiError"/> refusals, as the identify endpoints).
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class WikipediaListController : ControllerBase
{
    private readonly WikipediaListAdminService _admin;

    public WikipediaListController(WikipediaListAdminService admin)
    {
        _admin = admin;
    }

    private string? Actor => User.Identity?.Name;

    /// <summary>The Wikipedia companion of the series the node is linked to: state, pages, per-volume English dates and ISBNs. No network.</summary>
    [HttpGet("nodes/{nodeId}/wikipedia")]
    [ProducesResponseType<WikipediaListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct) =>
        await _admin.GetAsync(nodeId, ct) is { } dto ? Ok(dto) : NotFound();

    /// <summary>"Use this Wikipedia page": a page title or an en.wikipedia.org address (parsed locally), read now.</summary>
    [HttpPut("nodes/{nodeId}/wikipedia")]
    [ProducesResponseType<WikipediaListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetPage(string nodeId, [FromBody] WikipediaPageRequest request, CancellationToken ct)
    {
        try
        {
            var (error, result) = await _admin.SetPageAsync(nodeId, request.Page, Actor, ct);
            return error switch
            {
                null => Ok(result),
                "not_found" => NotFound(),
                _ => BadRequest(new ApiError { Error = error, Message = "Enter the page title or its en.wikipedia.org address." }),
            };
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>"No Wikipedia list for this series": never asked again. No network.</summary>
    [HttpDelete("nodes/{nodeId}/wikipedia")]
    [ProducesResponseType<WikipediaListDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clear(string nodeId, CancellationToken ct) =>
        await _admin.ClearAsync(nodeId, Actor, ct) is { } dto ? Ok(dto) : NotFound();

    /// <summary>"Check Wikipedia again": now, as an admin request.</summary>
    [HttpPost("nodes/{nodeId}/wikipedia/recheck")]
    [ProducesResponseType<WikipediaListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Recheck(string nodeId, CancellationToken ct)
    {
        try
        {
            return await _admin.RecheckAsync(nodeId, Actor, ct) is { } dto ? Ok(dto) : NotFound();
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }
}
