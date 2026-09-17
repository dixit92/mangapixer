namespace com.lifepixer.mangapixer.Server.Features.Home;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
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
    /// out-of-range values are clamped by the service. <paramref name="readState"/>
    /// (1.17.0) optionally restricts stacks to Reading/Read/Unread, mirroring the
    /// browse view's filter; omitted or unrecognized → no filter (unchanged behavior).
    /// </summary>
    [HttpGet("home/recent-chapters")]
    public async Task<IActionResult> GetRecentChapters(
        [FromQuery] int? perLibrary = null,
        [FromQuery] string? readState = null,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        var result = await _service.GetRecentChaptersAsync(
            userId.Value, perLibrary, _incognito.IsIncognito, ParseReadState(readState), ct);
        return Ok(result);
    }

    /// <summary>
    /// Parses the read-state filter query param (1.17.0). Tolerant of the wire values
    /// "reading"/"read"/"unread" (case-insensitive); anything else — including null and
    /// "all" — disables the filter, matching <c>CatalogController.ParseReadState</c>.
    /// </summary>
    private static HomeReadStateFilter ParseReadState(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return HomeReadStateFilter.All;
        if (value.Equals("reading", StringComparison.OrdinalIgnoreCase))
            return HomeReadStateFilter.Reading;
        if (value.Equals("read", StringComparison.OrdinalIgnoreCase))
            return HomeReadStateFilter.Read;
        if (value.Equals("unread", StringComparison.OrdinalIgnoreCase))
            return HomeReadStateFilter.Unread;
        return HomeReadStateFilter.All;
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}
