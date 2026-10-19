namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Author search by name (1.45.0), admin only, cookie + CSRF (never reachable with an API token): searches MangaUpdates for an artist by
/// the text the admin confirms, for one folder (its library's switches apply). Called by "Match folders by name" as artist folders (one
/// per ticked folder, paced by the dialog) and by the Artist folder dialog's "Find on MangaUpdates". The answer is only offered; marking
/// the folder is <c>PUT nodes/{nodeId}/artist-folder</c> with the picked author's id, which sends nothing.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class AuthorSearchController : ControllerBase
{
    private readonly AuthorSearchService _search;

    public AuthorSearchController(AuthorSearchService search) => _search = search;

    /// <summary>
    /// One author search: 400 <c>invalid_query</c> (1-200 characters) or <c>not_a_folder</c>; 404 no such folder; 409 web lookups off
    /// (<c>metadata_disabled</c>, <c>library_metadata_disabled</c>, <c>provider_not_allowed</c>, <c>metadata_network_disabled</c>); 429
    /// <c>budget_exhausted</c> / <c>provider_busy</c>; 503 <c>provider_backoff</c> (with Retry-After); 502 / 504 when MangaUpdates fails.
    /// </summary>
    [HttpPost("nodes/{nodeId}/author-search")]
    [ProducesResponseType<AuthorSearchResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Search(string nodeId, [FromBody] AuthorSearchRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await _search.SearchAsync(nodeId, request?.Text, ct);
            if (outcome.NotFound)
                return NotFound();
            if (outcome.Error is { } error)
                return BadRequest(new ApiError { Error = error, Message = "Only a folder can be searched for as an artist's folder." });
            return Ok(outcome.Result);
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }
}
