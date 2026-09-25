namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin identify endpoints (1.24.0, lane B2): the ONLY HTTP surface that can make
/// MangaPixer contact a metadata provider, and only on an explicit admin action.
/// Every call passes the <see cref="MetadataGateway"/> gates for the node's
/// library; refusals are typed <see cref="ApiError"/>s (409 switched off, 429
/// budget/busy, 503 backoff with <c>Retry-After</c>) and make no call.
/// Link-with-fetch is <c>PUT nodes/{id}/link</c> on <see cref="MetadataAdminController"/>.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataIdentifyController : ControllerBase
{
    private readonly MetadataIdentifyService _identify;

    public MetadataIdentifyController(MetadataIdentifyService identify)
    {
        _identify = identify;
    }

    private string? Actor => User.Identity?.Name;

    /// <summary>What the identify dialog needs first: availability, suggestions, ComicInfo hint, budget. No network.</summary>
    [HttpGet("nodes/{nodeId}/identify")]
    [ProducesResponseType<IdentifyContextDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Context(string nodeId, CancellationToken ct) =>
        await _identify.GetContextAsync(nodeId, ct) is { } dto ? Ok(dto) : NotFound();

    /// <summary>Searches the provider with the text the admin confirmed (the only library-derived text ever sent).</summary>
    [HttpPost("nodes/{nodeId}/search")]
    [ProducesResponseType<IdentifySearchResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Search(string nodeId, [FromBody] IdentifySearchRequest request, CancellationToken ct) =>
        Run(async () => await _identify.SearchAsync(nodeId, request, ct));

    /// <summary>Parses a pasted URL / shortcode locally and previews that record (only the id is sent).</summary>
    [HttpPost("nodes/{nodeId}/lookup")]
    [ProducesResponseType<IdentifyPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Lookup(string nodeId, [FromBody] IdentifyLookupRequest request, CancellationToken ct) =>
        Run(async () => await _identify.LookupAsync(nodeId, request, ct));

    [HttpPost("nodes/{nodeId}/preview")]
    [ProducesResponseType<IdentifyPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Preview(string nodeId, [FromBody] IdentifyPreviewRequest request, CancellationToken ct) =>
        Run(async () => await _identify.PreviewAsync(nodeId, request, ct));

    /// <summary>Re-fetches the web record that applies to the node (one GET; images follow when changed).</summary>
    [HttpPost("nodes/{nodeId}/refresh")]
    [ProducesResponseType<MetadataRefreshResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Refresh(string nodeId, CancellationToken ct) =>
        Run(async () => await _identify.RefreshAsync(nodeId, Actor, ct));

    /// <summary>A search/preview candidate's image, by short-lived server-side token (no provider URL comes from the client).</summary>
    [HttpGet("candidates/{token}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CandidateImage(string token, CancellationToken ct)
    {
        try
        {
            if (await _identify.GetCandidateImageAsync(token, ct) is not { } image)
                return NotFound();
            Response.Headers.CacheControl = "private, max-age=3600";
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(image.Bytes, image.ContentType);
        }
        catch (MetadataGatewayException ex)
        {
            return Error(this, ex);
        }
    }

    private async Task<IActionResult> Run<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            return await action() is { } result ? Ok(result) : NotFound();
        }
        catch (MetadataGatewayException ex)
        {
            return Error(this, ex);
        }
    }

    /// <summary>Maps a gateway refusal / provider failure to its typed <see cref="ApiError"/> (+ Retry-After on backoff).</summary>
    internal static IActionResult Error(ControllerBase controller, MetadataGatewayException ex)
    {
        if (ex.RetryAt is { } retryAt)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((retryAt - DateTimeOffset.UtcNow).TotalSeconds));
            controller.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }
        return controller.StatusCode(ex.HttpStatus, new ApiError
        {
            Error = ex.Code,
            Message = ex.Message,
            Detail = ex.RetryAt?.ToString("o", CultureInfo.InvariantCulture),
        });
    }
}
