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
    private readonly MetadataAutoMatchService _autoMatch;

    public MetadataAutoMatchController(MetadataAutoMatchService autoMatch)
    {
        _autoMatch = autoMatch;
    }

    private string? Actor => User.Identity?.Name;

    [HttpGet("runs")]
    [ProducesResponseType<MetadataMatchRunsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Runs([FromQuery] string? library = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 20,
        CancellationToken ct = default) => Ok(await _autoMatch.ListRunsAsync(library, cursor, limit, ct));

    [HttpPost("runs/{runId}/cancel")]
    [ProducesResponseType<MetadataMatchRunDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(string runId, CancellationToken ct)
    {
        var (error, run) = await _autoMatch.CancelRunAsync(runId, Actor, ct);
        return error is null ? Ok(run) : NotFound();
    }

    [HttpGet("libraries/{libraryId}/match/estimate")]
    [ProducesResponseType<MetadataMatchEstimateDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Estimate(string libraryId, [FromQuery] bool retryUnmatched = false, CancellationToken ct = default) =>
        await _autoMatch.EstimateAsync(libraryId, retryUnmatched, ct) is { } dto ? Ok(dto) : NotFound();

    [HttpPost("libraries/{libraryId}/match")]
    [ProducesResponseType<MetadataMatchRunDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MatchLibrary(string libraryId, [FromBody] MetadataMatchLibraryRequest request, CancellationToken ct)
    {
        var (error, run) = await _autoMatch.StartBulkAsync(libraryId, request, Actor, ct);
        return error switch
        {
            null => Accepted(run),
            "not_found" => NotFound(),
            "nothing_to_match" => Conflict(new ApiError { Error = error, Message = "Every work in this library is already linked, in review or queued." }),
            _ => Conflict(new ApiError { Error = error, Message = "Automatic matching is not available in this build." }),
        };
    }
}
