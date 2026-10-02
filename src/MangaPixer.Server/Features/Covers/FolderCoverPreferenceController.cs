namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// A folder's cover preference (1.32.0): <c>GET / PUT / DELETE /admin/folders/{nodeId}/cover-preference</c>, admin only, shaped like
/// the folder reader-default endpoints. DELETE clears the folder's own value (it inherits again).
/// </summary>
[ApiController]
[Route("api/v1/admin/folders/{nodeId}/cover-preference")]
[Authorize(Policy = "Admin")]
public sealed class FolderCoverPreferenceController(FolderCoverPreferenceService service) : ControllerBase
{
    private string? Actor => User.Identity?.Name;

    [HttpGet]
    [ProducesResponseType<FolderCoverPreferenceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string nodeId, CancellationToken ct)
    {
        var (code, dto) = await service.GetAsync(nodeId, ct);
        return code == FolderCoverPreferenceResult.Ok ? Ok(dto) : ToResult(code);
    }

    [HttpPut]
    [ProducesResponseType<FolderCoverPreferenceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Set(string nodeId, [FromBody] SetFolderCoverPreferenceRequest request, CancellationToken ct)
    {
        var (code, dto) = await service.SetAsync(nodeId, request.Preference, Actor, ct);
        return code == FolderCoverPreferenceResult.Ok ? Ok(dto) : ToResult(code);
    }

    [HttpDelete]
    [ProducesResponseType<FolderCoverPreferenceDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Clear(string nodeId, CancellationToken ct)
    {
        var (code, dto) = await service.ClearAsync(nodeId, Actor, ct);
        return code == FolderCoverPreferenceResult.Ok ? Ok(dto) : ToResult(code);
    }

    private IActionResult ToResult(FolderCoverPreferenceResult code) => code switch
    {
        FolderCoverPreferenceResult.NodeNotFound => NotFound(),
        FolderCoverPreferenceResult.NotAFolder => BadRequest(new ApiError { Error = "not_a_folder", Message = "A cover preference can only be set on a folder." }),
        FolderCoverPreferenceResult.InvalidPreference => BadRequest(new ApiError { Error = "invalid_preference", Message = "Preference must be Web or File." }),
        _ => NoContent(),
    };
}
