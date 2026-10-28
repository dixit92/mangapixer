namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Quick results while typing (1.49.0, lane O): <c>GET /api/v1/search/suggest?q=...</c>. Cookie authentication like the search itself
/// (the default scheme; an API token never reaches it - tokens open only the export routes), the same <c>X-Incognito</c> signal as browse
/// and search, and read-only: a GET changes nothing. It takes no library or node id, so the access sweeps list it with the other
/// discovery routes and check its answers for the viewer's libraries only.
/// </summary>
[ApiController]
[Route("api/v1/search")]
[Authorize]
public sealed class SearchSuggestController : ControllerBase
{
    private readonly SearchSuggestService _suggest;
    private readonly IncognitoAccessor _incognito;

    public SearchSuggestController(SearchSuggestService suggest, IncognitoAccessor incognito)
    {
        _suggest = suggest;
        _incognito = incognito;
    }

    /// <summary>
    /// The best few series, folders, archives, creators and tags for <paramref name="q"/> (about five each), or - when the text ends in a
    /// typed field such as <c>creator:ura</c> - the values of that field in <c>field</c>. An empty or missing <paramref name="q"/> answers
    /// empty lists. The client waits about 200 ms after the last keystroke and cancels the request it replaces.
    /// </summary>
    [HttpGet("suggest")]
    [ProducesResponseType<SearchSuggestDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Suggest([FromQuery] string? q, CancellationToken ct = default)
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var userId))
            return Unauthorized();
        return Ok(await _suggest.SuggestAsync(userId, _incognito.IsIncognito, q, ct));
    }
}
