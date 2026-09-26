namespace com.lifepixer.mangapixer.Server.Features.Metadata.Flags;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// "Wrong series?" flags (metadata stage 2). Any signed-in user with access to a
/// node can flag the series information it shows (404 without access, never 403);
/// admins list and resolve flags. Notes are user content: admin-only, never logged.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class MetadataFlagsController : ControllerBase
{
    private readonly MetadataFlagService _flags;

    public MetadataFlagsController(MetadataFlagService flags)
    {
        _flags = flags;
    }

    private string? Actor => User.Identity?.Name;

    private long? UserId =>
        long.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    [HttpPost("nodes/{nodeId}/series-info/flags")]
    [ProducesResponseType<MetadataMyFlagDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(string nodeId, [FromBody] CreateMetadataFlagRequest request, CancellationToken ct)
    {
        if (UserId is not { } userId)
            return Unauthorized();
        var (error, flag) = await _flags.CreateAsync(userId, nodeId, request, ct);
        return error switch
        {
            null => StatusCode(StatusCodes.Status201Created, flag),
            "not_found" => NotFound(),
            "no_web_data" => Conflict(new ApiError { Error = error, Message = "This item shows no series information from the web." }),
            "flag_exists" => Conflict(new ApiError { Error = error, Message = "You already reported this series; an admin will review it." }),
            "flag_limit" => StatusCode(StatusCodes.Status429TooManyRequests,
                new ApiError { Error = error, Message = "You reached today's limit of reports. Try again tomorrow." }),
            "note_too_long" => BadRequest(new ApiError { Error = error, Message = $"The note can be at most {MetadataFlagService.MaxNoteLength} characters." }),
            _ => BadRequest(new ApiError { Error = error, Message = "Choose a reason." }),
        };
    }

    [HttpGet("nodes/{nodeId}/series-info/flags/mine")]
    [ProducesResponseType<MetadataMyFlagStateDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(string nodeId, CancellationToken ct)
    {
        if (UserId is not { } userId)
            return Unauthorized();
        return await _flags.MineAsync(userId, nodeId, ct) is { } dto ? Ok(dto) : NotFound();
    }

    /// <summary>Flags by <paramref name="state"/>: <c>open</c> (default, auto links first), <c>resolved</c> or <c>all</c>.</summary>
    [HttpGet("admin/metadata/flags")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<MetadataFlagPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? state = null, [FromQuery] string? library = null,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var (error, page) = await _flags.ListAsync(state, library, cursor, limit, ct);
        return error switch
        {
            null => Ok(page),
            "library_not_found" => NotFound(),
            _ => BadRequest(new ApiError { Error = error, Message = "state must be open, resolved or all." }),
        };
    }

    [HttpPost("admin/metadata/flags/{flagId}/resolve")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<MetadataFlagDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resolve(string flagId, [FromBody] ResolveMetadataFlagRequest request, CancellationToken ct)
    {
        var (error, flag) = await _flags.ResolveAsync(flagId, request.Outcome, Actor, ct);
        return error switch
        {
            null => Ok(flag),
            "not_found" => NotFound(),
            "already_resolved" => Conflict(new ApiError { Error = error, Message = "This flag is already resolved." }),
            _ => BadRequest(new ApiError { Error = error, Message = "The outcome must be Relinked, Unlinked, DontMatch or Dismissed." }),
        };
    }
}
