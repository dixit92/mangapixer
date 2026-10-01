namespace com.lifepixer.mangapixer.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Move conflicts (1.31.0), admin-only: items moved to another library whose old and new copies both had their own state.
/// List (open / resolved), count (the admin link badge) and resolve (Overwrite with the old state, or Keep the new one;
/// listed ids or every open conflict).
/// </summary>
[ApiController]
[Route("api/v1/admin/move-conflicts")]
[Authorize(Policy = "Admin")]
public sealed class MoveConflictsController : ControllerBase
{
    private readonly MoveConflictService _conflicts;

    public MoveConflictsController(MoveConflictService conflicts) => _conflicts = conflicts;

    [HttpGet]
    [ProducesResponseType<MoveConflictPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? state = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        bool resolved;
        switch (state)
        {
            case null or "" or "open": resolved = false; break;
            case "resolved": resolved = true; break;
            default: return BadRequest(new ApiError { Error = "invalid_state", Message = "State must be open or resolved." });
        }
        return Ok(await _conflicts.ListAsync(resolved, cursor, limit, ct));
    }

    [HttpGet("count")]
    [ProducesResponseType<MoveConflictCountDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Count(CancellationToken ct) =>
        Ok(new MoveConflictCountDto { Open = await _conflicts.CountOpenAsync(ct) });

    [HttpPost("resolve")]
    [ProducesResponseType<MoveConflictResolveResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Resolve([FromBody] MoveConflictResolveRequest request, CancellationToken ct)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        long? userId = claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
        var (error, result) = await _conflicts.ResolveAsync(request, userId, User.Identity?.Name, ct);
        return error switch
        {
            null => Ok(result),
            "too_many_ids" => BadRequest(new ApiError { Error = error, Message = $"At most {MoveConflictService.MaxIds} ids per request." }),
            _ => BadRequest(new ApiError { Error = error, Message = "Select conflicts, or resolve all." }),
        };
    }
}
