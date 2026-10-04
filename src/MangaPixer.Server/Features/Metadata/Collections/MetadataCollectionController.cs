namespace com.lifepixer.mangapixer.Server.Features.Metadata.Collections;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Metadata.Review;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// "Collection about" (1.34.0), admin only: mark a folder as a collection of works about a series (fan works) - from Identify's
/// "pick the series" mode or the browse Series menu - clear it, or accept a Needs-review folder as one from its suggestion.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataCollectionController : ControllerBase
{
    private readonly CollectionAboutService _collections;
    private readonly MetadataReviewService _review;

    public MetadataCollectionController(CollectionAboutService collections, MetadataReviewService review)
    {
        _collections = collections;
        _review = review;
    }

    private string? Actor => User.Identity?.Name;

    /// <summary>Marks a folder "Collection about" a provider record (fetched and stored first when needed).</summary>
    [HttpPut("nodes/{nodeId}/collection")]
    [ProducesResponseType<CollectionAboutResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Set(string nodeId, [FromBody] SetCollectionAboutRequest request, CancellationToken ct)
    {
        try
        {
            var (code, result) = await _collections.SetAsync(nodeId, request, Actor, ct);
            return code == MetadataLinkResultCode.Ok ? Ok(result) : Error(code);
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>Clears only a "Collection about" row (another link is left alone); the folder's Content stays.</summary>
    [HttpDelete("nodes/{nodeId}/collection")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clear(string nodeId, CancellationToken ct)
    {
        var (code, change) = await _collections.ClearAsync(nodeId, Actor, ct);
        return code == MetadataLinkResultCode.Ok ? Ok(change) : NotFound();
    }

    /// <summary>Accepts a Needs-review folder as a collection about one of its stored candidates.</summary>
    [HttpPost("review/{nodeId}/accept-collection")]
    [ProducesResponseType<CollectionAboutResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AcceptCollection(string nodeId, [FromBody] MetadataReviewAcceptCollectionRequest request, CancellationToken ct)
    {
        try
        {
            var (error, result) = await _review.AcceptCollectionAsync(nodeId, request.Rank, Actor, ct);
            return error switch
            {
                null => Ok(result),
                "not_found" => NotFound(),
                "not_a_folder" => BadRequest(new ApiError { Error = error, Message = "Only a folder can be a collection." }),
                "no_candidate" => BadRequest(new ApiError { Error = error, Message = "This row has no stored candidate with that rank." }),
                _ => NotFound(new ApiError { Error = error, Message = "The provider has no series with that id." }),
            };
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    private IActionResult Error(MetadataLinkResultCode code) => code switch
    {
        MetadataLinkResultCode.NodeNotFound => NotFound(),
        MetadataLinkResultCode.RecordNotFound => NotFound(new ApiError
        {
            Error = "record_not_found",
            Message = "The provider has no series with that id.",
        }),
        MetadataLinkResultCode.NotAFolder => BadRequest(new ApiError { Error = "not_a_folder", Message = "Only a folder can be a collection." }),
        _ => BadRequest(new ApiError { Error = "invalid_request", Message = "The request is not valid." }),
    };
}
