namespace com.lifepixer.mangaplex.Server.Features.Home;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Home "New chapters" endpoint. Surfaces the most-recently-
/// added archives grouped by visible library, newest first, capped per
/// library. Requires authentication; visibility (Incognito/Private) is
/// enforced inside <see cref="RecentChaptersService"/> via the shared
/// <see cref="LibraryAuthorizationService"/> chokepoint, identical to the
/// other discovery surfaces.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class RecentChaptersController : ControllerBase
{
    private readonly RecentChaptersService _service;
    private readonly IncognitoAccessor _incognito;

    public RecentChaptersController(RecentChaptersService service, IncognitoAccessor incognito)
    {
        _service = service;
        _incognito = incognito;
    }

    /// <summary>
    /// Returns the caller's per-library "New chapters" rows for the home page.
    /// <paramref name="perLibrary"/> caps each library group (1-50, default 12);
    /// out-of-range values are clamped by the service.
    /// </summary>
    [HttpGet("home/recent-chapters")]
    public async Task<IActionResult> GetRecentChapters(
        [FromQuery] int? perLibrary = null,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var result = await _service.GetRecentChaptersAsync(
            userId.Value, perLibrary, _incognito.IsIncognito, ct);
        return Ok(result);
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}
