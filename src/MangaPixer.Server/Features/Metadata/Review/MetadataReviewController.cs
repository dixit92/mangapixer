namespace com.lifepixer.mangapixer.Server.Features.Metadata.Review;

using com.lifepixer.mangapixer.Core.Api;
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
    [HttpGet("review/summary")]
    [ProducesResponseType<MetadataReviewSummaryDto>(StatusCodes.Status200OK)]
    public IActionResult Summary([FromQuery] string? library = null) => NotImplemented();

    [HttpGet("review")]
    [ProducesResponseType<MetadataReviewPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public IActionResult List([FromQuery] MetadataReviewTab tab = MetadataReviewTab.NeedsReview, [FromQuery] string? library = null,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50) => NotImplemented();

    [HttpPost("review/{nodeId}/accept")]
    [ProducesResponseType<NodeSeriesLinkChangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Accept(string nodeId, [FromBody] MetadataReviewAcceptRequest request) => NotImplemented();

    [HttpPost("review/bulk")]
    [ProducesResponseType<MetadataReviewBulkResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public IActionResult Bulk([FromBody] MetadataReviewBulkRequest request) => NotImplemented();

    [HttpPost("missing/{nodeId}/reattach")]
    [ProducesResponseType<MetadataReattachResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public IActionResult Reattach(string nodeId, [FromBody] MetadataReattachRequest request) => NotImplemented();

    /// <summary>Deletes the metadata rows left on a removed folder ("Delete" on the Missing folders tab).</summary>
    [HttpDelete("missing/{nodeId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeleteMissing(string nodeId) => NotImplemented();

    private ObjectResult NotImplemented() =>
        StatusCode(StatusCodes.Status501NotImplemented, new ApiError { Error = "not_implemented", Message = "Not implemented yet." });
}
