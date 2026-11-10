namespace com.lifepixer.mangapixer.Server.Features.Ratings;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The current user's "Share my ratings" choice (1.53.0 shared ratings). A dedicated route, never a field of the <c>library-preferences</c>
/// blob: that PUT echoes the whole blob, so a stale client could flip a privacy choice it never showed. On by default; it takes effect only
/// while the administrator's "Shared ratings" switch is on (<see cref="RatingSharingDto.ServerEnabled"/>).
/// </summary>
[ApiController]
[Route("api/v1/ratings")]
[Authorize]
public sealed class RatingSharingController : ControllerBase
{
    private readonly SharedRatingsService _shared;

    public RatingSharingController(SharedRatingsService shared) => _shared = shared;

    /// <summary>The user's choice and whether the server switch is on.</summary>
    [HttpGet("sharing")]
    [ProducesResponseType(typeof(RatingSharingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        return Ok(await _shared.GetUserAsync(userId.Value, ct));
    }

    /// <summary>Sets the user's choice: <c>{ "share": bool }</c>. Off withdraws the user's ratings from every average on the next request.</summary>
    [HttpPut("sharing")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Set([FromBody] SetRatingSharingRequest? request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });
        await _shared.SetUserAsync(userId.Value, request.Share, ct);
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}

/// <summary>
/// The server-wide "Shared ratings" switch (1.53.0, Administration; owner decision 3b): off until an admin turns it on. While it is off no
/// average is returned anywhere and the browse sort "Server rating" is not offered; turning it off deletes nothing. Each change is written to
/// the audit trail. Admins have no view of who rated what - this switch is their only control.
/// </summary>
[ApiController]
[Route("api/v1/admin/ratings")]
[Authorize(Policy = "Admin")]
public sealed class AdminRatingSharingController : ControllerBase
{
    private readonly SharedRatingsService _shared;

    public AdminRatingSharingController(SharedRatingsService shared) => _shared = shared;

    [HttpGet("sharing")]
    [ProducesResponseType(typeof(AdminRatingSharingDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await _shared.GetServerAsync(ct));

    /// <summary>Turns the switch on or off: <c>{ "enabled": bool }</c>; answers the new state.</summary>
    [HttpPut("sharing")]
    [ProducesResponseType(typeof(AdminRatingSharingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Set([FromBody] AdminRatingSharingDto? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });
        return Ok(await _shared.SetServerAsync(request.Enabled, User.Identity?.Name, ct));
    }
}

/// <summary>DI registrations of shared ratings (1.53.0) - one line in <c>Program.cs</c>.</summary>
public static class SharedRatingsRegistration
{
    public static IServiceCollection AddSharedRatings(this IServiceCollection services)
    {
        services.AddSingleton<RatingsGeneration>();
        services.AddScoped<SharedRatingsService>();
        return services;
    }
}
