namespace com.lifepixer.mangapixer.Server.Features.Metadata.FolderMatch;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// "Match folders by name" (1.38.0), admin only: preview and apply marking several selected folders as artist folders or as collections
/// about a series, by their names against names stored on this server. Stored data only - neither route sends a request.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/folder-match")]
[Authorize(Policy = "Admin")]
public sealed class MetadataFolderMatchController : ControllerBase
{
    private readonly FolderMatchService _match;

    public MetadataFolderMatchController(FolderMatchService match) => _match = match;

    private string? Actor => User.Identity?.Name;

    /// <summary>What each selected node would be marked as (nothing changes).</summary>
    [HttpPost("preview")]
    [ProducesResponseType<FolderMatchPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Preview([FromBody] FolderMatchPreviewRequest request, CancellationToken ct)
    {
        var (error, result) = await _match.PreviewAsync(request, ct);
        return error is null
            ? Ok(result)
            : BadRequest(new ApiError { Error = error, Message = $"Give a kind and 1-{FolderMatchService.MaxNodes} node ids." });
    }

    /// <summary>Marks the ticked folders, each through the single action; per-folder results.</summary>
    [HttpPost("apply")]
    [ProducesResponseType<FolderMatchApplyResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Apply([FromBody] FolderMatchApplyRequest request, CancellationToken ct)
    {
        var (error, result) = await _match.ApplyAsync(request, Actor, ct);
        return error is null
            ? Ok(result)
            : BadRequest(new ApiError { Error = error, Message = $"Give a kind and 1-{FolderMatchService.MaxNodes} folders." });
    }
}
