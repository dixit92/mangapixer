namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin Performance settings (1.42.0): media workers, page cache size and background work while someone reads, each
/// with its source (configuration pins a value). Changes apply live. Admin-only.
/// </summary>
[ApiController]
[Route("api/v1/admin/performance")]
[Authorize(Policy = "Admin")]
public sealed class PerformanceSettingsController(PerformanceSettingsService settings) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PerformanceSettingsDto>(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(settings.Get());

    [HttpPut]
    [ProducesResponseType<PerformanceSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Put([FromBody] UpdatePerformanceSettingsRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });
        var (result, status, error, message) = await settings.UpdateAsync(request, ct);
        return result is not null ? Ok(result) : StatusCode(status, new ApiError { Error = error!, Message = message! });
    }
}
