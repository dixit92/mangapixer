namespace com.lifepixer.mangapixer.Server.Features.Trash;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin trash (1.31.0): the overview with its preview, the settings (automatic cleaning, the move window / trash retention),
/// "Empty trash now" (all libraries, or one - which can release its hold) and "Clean bundles now". Admin-only.
/// </summary>
[ApiController]
[Route("api/v1/admin/trash")]
[Authorize(Policy = "Admin")]
public sealed class TrashController(TrashService trash) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TrashOverviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await trash.GetOverviewAsync(ct));

    [HttpPut("settings")]
    [ProducesResponseType<TrashSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PutSettings([FromBody] UpdateTrashSettingsRequest request, CancellationToken ct)
    {
        var (settings, error) = await trash.UpdateSettingsAsync(request, User.Identity?.Name, ct);
        return error is null
            ? Ok(settings)
            : BadRequest(new ApiError { Error = error, Message = error == "invalid_hour" ? "Choose an hour from 0 to 23." : "Choose Daily, Weekly, Monthly, Quarterly or Yearly." });
    }

    [HttpPost("empty")]
    [ProducesResponseType<EmptyTrashResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EmptyTrash([FromBody] EmptyTrashRequest? request, CancellationToken ct)
    {
        var outcome = await trash.EmptyAsync(request?.LibraryId, request?.ReleaseHold ?? false, automatic: false, User.Identity?.Name, ct);
        return outcome.Error switch
        {
            null => Ok(outcome.Result),
            "not_found" => NotFound(new ApiError { Error = "not_found", Message = "The library does not exist." }),
            "scan_in_progress" => Conflict(new ApiError { Error = "scan_in_progress", Message = "A scan of this library is running. Empty its trash after the scan." }),
            _ => Conflict(new ApiError { Error = "trash_held", Message = $"This library's trash is held ({outcome.Hold}). Confirm to empty it anyway." }),
        };
    }

    [HttpPost("clean-bundles")]
    [ProducesResponseType<TrashFilesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CleanBundles(CancellationToken ct) =>
        Ok(await trash.CleanBundlesAsync(automatic: false, User.Identity?.Name, ct));
}
