namespace com.lifepixer.mangapixer.Server.Features.Metadata.Artists;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

/// <summary>
/// "Artist folder" (1.37.0), admin only: mark a folder as an artist's folder (the works of one creator, matched one by one) from the
/// series Admin menu or the browse Series menu, remove the mark, or mark a Needs-review / Unmatched folder from the review dashboard
/// (key <c>r</c>). No network: the artist is a declared creator, compared on the server only.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataArtistFolderController : ControllerBase
{
    private readonly ArtistFolderService _artists;
    private readonly MetadataReviewService _review;

    public MetadataArtistFolderController(ArtistFolderService artists, MetadataReviewService review)
    {
        _artists = artists;
        _review = review;
    }

    private string? Actor => User.Identity?.Name;

    /// <summary>Marks a folder an artist's folder; the body (name, role) is optional - default the folder's name, "Story &amp; art".</summary>
    [HttpPut("nodes/{nodeId}/artist-folder")]
    [ProducesResponseType<ArtistFolderResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Set(string nodeId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SetArtistFolderRequest? request,
        CancellationToken ct) =>
        ToResult(await _artists.SetAsync(nodeId, request, Actor, ct));

    /// <summary>Removes only an "Artist folder" row (another link is left alone); the declared creator stays.</summary>
    [HttpDelete("nodes/{nodeId}/artist-folder")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Clear(string nodeId, CancellationToken ct)
    {
        var (code, change) = await _artists.ClearAsync(nodeId, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : NotFound();
    }

    /// <summary>Marks a folder waiting in Needs review or Unmatched an artist's folder (its review row goes, its works are queued).</summary>
    [HttpPost("review/{nodeId}/accept-artist")]
    [ProducesResponseType<ArtistFolderResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptArtist(string nodeId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SetArtistFolderRequest? request, CancellationToken ct) =>
        ToResult(await _review.AcceptArtistAsync(nodeId, request, Actor, ct));

    private IActionResult ToResult(ArtistFolderOutcome outcome) => outcome switch
    {
        { Code: MetadataLinkResultCode.Ok, Result: { } result } => Ok(result),
        { Code: MetadataLinkResultCode.NodeNotFound } => NotFound(),
        { Error: "not_a_folder" } => BadRequest(new ApiError { Error = "not_a_folder", Message = "Only a folder can be an artist's folder." }),
        { Error: "creator_role_invalid" } => BadRequest(new ApiError { Error = "creator_role_invalid", Message = "Unknown creator role." }),
        { Error: "creators_too_many" } => BadRequest(new ApiError
        {
            Error = "creators_too_many",
            Message = "This folder already declares the most creators; remove one in Declared facts first.",
        }),
        { Error: "creator_name_invalid" } => BadRequest(new ApiError { Error = "creator_name_invalid", Message = "The artist's name is not valid." }),
        _ => BadRequest(new ApiError { Error = outcome.Error ?? "invalid_request", Message = "The request is not valid." }),
    };
}
