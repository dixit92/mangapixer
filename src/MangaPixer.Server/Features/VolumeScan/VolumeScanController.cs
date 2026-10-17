namespace com.lifepixer.mangapixer.Server.Features.VolumeScan;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin > Performance, "Find the chapters inside volume files" (1.44.0): the volume contents scan's switch (on by default) and its
/// counts. Admin-only, cookie + CSRF like every admin route.
/// </summary>
[ApiController]
[Route("api/v1/admin/volume-contents")]
[Authorize(Policy = "Admin")]
public sealed class VolumeScanController(VolumeScanSettingsService settings) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<VolumeScanStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await settings.GetAsync(ct));

    [HttpPut]
    [ProducesResponseType<VolumeScanStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Put([FromBody] UpdateVolumeScanRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });
        return Ok(await settings.SetAsync(request.Enabled, ct));
    }
}
