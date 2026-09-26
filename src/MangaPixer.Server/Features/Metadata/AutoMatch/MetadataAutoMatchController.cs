namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Automatic matching runs (metadata stage 2): the run list with the worker's
/// status, cancelling a run, and "Match this library now" with its estimate. The
/// estimate is local only (no network); starting a run only queues work - the
/// background worker sends the requests within the automatic gate and budget.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata")]
[Authorize(Policy = "Admin")]
public sealed class MetadataAutoMatchController : ControllerBase
{
    [HttpGet("runs")]
    [ProducesResponseType<MetadataMatchRunsDto>(StatusCodes.Status200OK)]
    public IActionResult Runs([FromQuery] string? library = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20) => NotImplemented();

    [HttpPost("runs/{runId}/cancel")]
    [ProducesResponseType<MetadataMatchRunDto>(StatusCodes.Status200OK)]
    public IActionResult Cancel(string runId) => NotImplemented();

    [HttpGet("libraries/{libraryId}/match/estimate")]
    [ProducesResponseType<MetadataMatchEstimateDto>(StatusCodes.Status200OK)]
    public IActionResult Estimate(string libraryId, [FromQuery] bool retryUnmatched = false) => NotImplemented();

    [HttpPost("libraries/{libraryId}/match")]
    [ProducesResponseType<MetadataMatchRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public IActionResult MatchLibrary(string libraryId, [FromBody] MetadataMatchLibraryRequest request) => NotImplemented();

    private ObjectResult NotImplemented() =>
        StatusCode(StatusCodes.Status501NotImplemented, new ApiError { Error = "not_implemented", Message = "Not implemented yet." });
}
