namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Admin per-folder view overrides (1.29.0): the folder's Volumes view switch.</summary>
[ApiController]
[Route("api/v1/admin/folders/{nodeId}/view-settings")]
[Authorize(Policy = "Admin")]
public sealed class FolderViewSettingsController(FolderViewSettingsService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<FolderViewSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct)
    {
        var (result, dto) = await service.GetAsync(nodeId, ct);
        return ToResult(result, dto);
    }

    /// <summary>Replaces the folder's overrides; a null field inherits the library again.</summary>
    [HttpPut]
    [ProducesResponseType<FolderViewSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Put(string nodeId, [FromBody] UpdateFolderViewSettingsRequest request, CancellationToken ct)
    {
        var (result, dto) = await service.SetAsync(nodeId, request, User.Identity?.Name, ct);
        return ToResult(result, dto);
    }

    private IActionResult ToResult(FolderViewSettingsResult result, FolderViewSettingsDto? dto) => result switch
    {
        FolderViewSettingsResult.Ok => Ok(dto),
        FolderViewSettingsResult.NotFound => NotFound(),
        FolderViewSettingsResult.NotAFolder => BadRequest(new ApiError { Error = "not_a_folder", Message = "This setting can only be set on a folder." }),
        _ => BadRequest(new ApiError { Error = "invalid_request", Message = "The request is not valid." }),
    };
}
