namespace com.lifepixer.mangapixer.Server.Features.Metadata.Review;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The admin review dashboard (metadata stage 2): tab lists with a library filter
/// and cursor, the per-tab summary, accepting a stored candidate, bulk actions and
/// missing-folder re-attach. Reading a tab never contacts a provider (candidates
/// are stored); accepting a candidate whose record is not stored yet makes one
/// gated GET.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataReviewController : ControllerBase
{
    private readonly MetadataReviewService _review;
    private readonly MetadataCarryOverService _carryOver;

    public MetadataReviewController(MetadataReviewService review, MetadataCarryOverService carryOver)
    {
        _review = review;
        _carryOver = carryOver;
    }

    private string? Actor => User.Identity?.Name;

    [HttpGet("review/summary")]
    [ProducesResponseType<MetadataReviewSummaryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary([FromQuery] string? library = null, CancellationToken ct = default) =>
        await _review.SummaryAsync(library, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpGet("review")]
    [ProducesResponseType<MetadataReviewPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] MetadataReviewTab tab = MetadataReviewTab.NeedsReview, [FromQuery] string? library = null,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50, [FromQuery] bool? later = null, CancellationToken ct = default)
    {
        var (error, page) = await _review.ListAsync(tab, library, cursor, limit, ct, later);
        return error switch
        {
            null => Ok(page),
            "library_not_found" => NotFound(),
            _ => BadRequest(new ApiError { Error = error, Message = "Unknown review tab." }),
        };
    }

    [HttpPost("review/{nodeId}/accept")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Accept(string nodeId, [FromBody] MetadataReviewAcceptRequest request, CancellationToken ct)
    {
        try
        {
            var (error, change) = await _review.AcceptAsync(nodeId, request.Rank, Actor, ct);
            return error switch
            {
                null => Ok(change),
                "not_found" => NotFound(),
                "no_candidate" => BadRequest(new ApiError { Error = error, Message = "This row has no stored candidate with that rank." }),
                _ => NotFound(new ApiError { Error = error, Message = "The provider has no series with that id." }),
            };
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>Sets a Needs review row aside ("Later", 1.33.0): it is listed after the others until it is decided or checked again.</summary>
    [HttpPost("review/{nodeId}/later")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetLater(string nodeId, CancellationToken ct) =>
        LaterResult(await _review.SetLaterAsync(nodeId, later: true, Actor, ct));

    /// <summary>Brings a row set aside back into the normal order.</summary>
    [HttpDelete("review/{nodeId}/later")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ClearLater(string nodeId, CancellationToken ct) =>
        LaterResult(await _review.SetLaterAsync(nodeId, later: false, Actor, ct));

    private IActionResult LaterResult(string code) => code switch
    {
        "ok" => NoContent(),
        "not_found" => NotFound(),
        _ => Conflict(new ApiError { Error = code, Message = "Only a work waiting in Needs review can be set aside for later." }),
    };

    [HttpPost("review/bulk")]
    [ProducesResponseType<MetadataReviewBulkResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Bulk([FromBody] MetadataReviewBulkRequest request, CancellationToken ct)
    {
        var (error, result) = await _review.BulkAsync(request, Actor, ct);
        return error is null
            ? Ok(result)
            : BadRequest(new ApiError { Error = error, Message = $"Give a known action and 1-{MetadataReviewService.MaxBulk} node ids." });
    }

    [HttpPost("missing/{nodeId}/reattach")]
    [ProducesResponseType<MetadataReattachResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reattach(string nodeId, [FromBody] MetadataReattachRequest request, CancellationToken ct)
    {
        var (error, result) = await _carryOver.ReattachAsync(nodeId, request.TargetNodeId, Actor, ct);
        return error switch
        {
            null => Ok(result),
            "not_found" => NotFound(),
            "target_not_found" => BadRequest(new ApiError { Error = error, Message = "The target folder does not exist." }),
            "not_a_folder" => BadRequest(new ApiError { Error = error, Message = "Metadata can only be re-attached to a folder." }),
            _ => BadRequest(new ApiError { Error = error, Message = "The target folder must be in the same library." }),
        };
    }

    /// <summary>Deletes the metadata rows left on a removed folder ("Delete" on the Missing folders tab).</summary>
    [HttpDelete("missing/{nodeId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteMissing(string nodeId, CancellationToken ct) =>
        await _carryOver.DeleteMissingAsync(nodeId, Actor, ct) ? NoContent() : NotFound();
}
