namespace com.lifepixer.mangapixer.Server.Features.Jobs;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin Scheduled jobs (1.32.0): every job with its rhythm, last and next run on the server's clock; the hours of the daily jobs;
/// the series information refresh cadence; one linked series' cadence. Admin-only.
/// </summary>
[ApiController]
[Route("api/v1/admin/jobs")]
[Authorize(Policy = "Admin")]
public sealed class ScheduledJobsController(ScheduledJobsService jobs) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ScheduledJobsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await jobs.GetAsync(ct));

    [HttpPut("{key}")]
    [ProducesResponseType<ScheduledJobsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PutHour(string key, [FromBody] UpdateJobScheduleRequest request, CancellationToken ct)
    {
        var error = await jobs.SetHourAsync(key, request.Hour, User.Identity?.Name, ct);
        return error switch
        {
            null => Ok(await jobs.GetAsync(ct)),
            "invalid_job" => NotFound(new ApiError { Error = error, Message = "There is no scheduled job with this key." }),
            "not_configurable" => BadRequest(new ApiError { Error = error, Message = "This job runs on its own rhythm; its time cannot be chosen." }),
            "managed_by_config" => Conflict(new ApiError { Error = error, Message = "The backup hour is set by server configuration." }),
            _ => BadRequest(new ApiError { Error = error, Message = "Choose an hour from 0 to 23." }),
        };
    }

    [HttpPut("metadata-refresh/cadence")]
    [ProducesResponseType<ScheduledJobsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutCadence([FromBody] UpdateRefreshCadenceRequest request, CancellationToken ct)
    {
        var error = await jobs.SetCadenceAsync(request, User.Identity?.Name, ct);
        return error is null
            ? Ok(await jobs.GetAsync(ct))
            : BadRequest(new ApiError { Error = error, Message = "Ongoing series: every 7, 14 or 30 days; finished series: every 30, 90 or 180 days." });
    }

    [HttpGet("metadata-refresh/series/{nodeId}")]
    [ProducesResponseType<SeriesRefreshCadenceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GetSeriesCadence(string nodeId, CancellationToken ct) =>
        await jobs.SeriesCadenceAsync(nodeId, ct) is { } dto ? Ok(dto) : NoContent();
}
