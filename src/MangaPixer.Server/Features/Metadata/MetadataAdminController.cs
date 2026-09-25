namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin-only series-metadata endpoints (1.24.0, lane B1 - no network). Settings
/// (both toggles, budget), per-library toggles and precedence, node links (link
/// to an EXISTING record / Don't match / unlink), folder precedence, and purge.
/// Every change is audited with ids only. Lane B2 adds its identify endpoints in
/// its own controller and extends <c>PUT nodes/{id}/link</c> to fetch-and-store.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataAdminController : ControllerBase
{
    private readonly MetadataSettingsService _settings;
    private readonly MetadataLinkService _links;

    public MetadataAdminController(MetadataSettingsService settings, MetadataLinkService links)
    {
        _settings = settings;
        _links = links;
    }

    private string? Actor => User.Identity?.Name;

    // --- Settings ---

    [HttpGet("settings")]
    [ProducesResponseType<MetadataSettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings(CancellationToken ct) => Ok(await _settings.GetAsync(ct));

    [HttpPut("settings")]
    [ProducesResponseType<MetadataSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateMetadataSettingsRequest request, CancellationToken ct)
    {
        var error = await _settings.UpdateAsync(request, Actor, ct);
        return error switch
        {
            null => Ok(await _settings.GetAsync(ct)),
            "consent_required" => BadRequest(new ApiError
            {
                Error = error,
                Message = "Turning on web metadata requires accepting the current consent version.",
            }),
            _ => BadRequest(new ApiError { Error = error, Message = "DailyBudget must be a positive whole number." }),
        };
    }

    [HttpPut("libraries/{libraryId}")]
    [ProducesResponseType<MetadataSettingsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateLibrary(string libraryId, [FromBody] UpdateMetadataLibraryRequest request, CancellationToken ct)
    {
        if (!await _settings.UpdateLibraryAsync(libraryId, request, Actor, ct))
            return NotFound();
        return Ok(await _settings.GetAsync(ct));
    }

    [HttpPut("libraries/{libraryId}/precedence")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetLibraryPrecedence(string libraryId, [FromBody] SetMetadataPrecedenceRequest request, CancellationToken ct)
        => ToResult(await _links.SetLibraryPrecedenceAsync(libraryId, request.Precedence, Actor, ct));

    [HttpPost("purge")]
    [ProducesResponseType<MetadataPurgeResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Purge([FromBody] MetadataPurgeRequest request, CancellationToken ct)
    {
        var (code, result) = await _links.PurgeAsync(request.LibraryId, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(result) : ToResult(code);
    }

    // --- Node links ---

    [HttpPut("nodes/{nodeId}/link")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Link(string nodeId, [FromBody] LinkSeriesRequest request, CancellationToken ct)
    {
        var (code, change) = await _links.LinkAsync(nodeId, request, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : ToResult(code);
    }

    /// <summary>Removes the node's own link row (a confirmed link or a Don't match); inheritance resumes.</summary>
    [HttpDelete("nodes/{nodeId}/link")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Unlink(string nodeId, CancellationToken ct)
    {
        var (code, change) = await _links.RemoveAsync(nodeId, onlyDontMatch: false, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : ToResult(code);
    }

    [HttpPut("nodes/{nodeId}/dont-match")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DontMatch(string nodeId, CancellationToken ct)
    {
        var (code, change) = await _links.SetDontMatchAsync(nodeId, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : ToResult(code);
    }

    /// <summary>Clears only a Don't match row (a confirmed link is left alone).</summary>
    [HttpDelete("nodes/{nodeId}/dont-match")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ClearDontMatch(string nodeId, CancellationToken ct)
    {
        var (code, change) = await _links.RemoveAsync(nodeId, onlyDontMatch: true, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : ToResult(code);
    }

    // --- Folder precedence ---

    [HttpPut("folders/{nodeId}/precedence")]
    [ProducesResponseType<FolderMetadataPrecedenceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetFolderPrecedence(string nodeId, [FromBody] SetMetadataPrecedenceRequest request, CancellationToken ct)
    {
        if (request.Precedence is not { } precedence)
            return BadRequest(new ApiError { Error = "precedence_required", Message = "Precedence is required; use DELETE to clear the override." });
        var code = await _links.SetFolderPrecedenceAsync(nodeId, precedence, Actor, ct);
        return code == MetadataLinkResultCode.Ok
            ? Ok(new FolderMetadataPrecedenceDto { NodeId = nodeId, Precedence = precedence })
            : ToResult(code);
    }

    [HttpDelete("folders/{nodeId}/precedence")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearFolderPrecedence(string nodeId, CancellationToken ct)
        => ToResult(await _links.ClearFolderPrecedenceAsync(nodeId, Actor, ct));

    private IActionResult ToResult(MetadataLinkResultCode code) => code switch
    {
        MetadataLinkResultCode.Ok => NoContent(),
        MetadataLinkResultCode.NodeNotFound or MetadataLinkResultCode.LibraryNotFound => NotFound(),
        MetadataLinkResultCode.RecordNotFound => NotFound(new ApiError
        {
            Error = "record_not_found",
            Message = "No stored record with that provider and id. Records are fetched by Identify.",
        }),
        MetadataLinkResultCode.NotAFolder => BadRequest(new ApiError
        {
            Error = "not_a_folder",
            Message = "A source-precedence override can only be set on a folder.",
        }),
        _ => BadRequest(new ApiError { Error = "invalid_request", Message = "The request is not valid." }),
    };
}
