namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Recommendations (1.53.0 lane C): the Home "Recommended for you" row and the series page's "More like this". Cookie auth (the default
/// scheme; never an API token). Content-based and local only - computed on this server from the viewer's own signals and the tags of the
/// series the viewer can see; no request leaves the server. The viewer's scope follows <c>X-Incognito</c> (their Private libraries leave
/// it while Incognito is on) and leaves out libraries whose series information is hidden.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class RecommendationsController : ControllerBase
{
    private readonly RecommendationService _recommendations;
    private readonly IncognitoAccessor _incognito;

    public RecommendationsController(RecommendationService recommendations, IncognitoAccessor incognito)
    {
        _recommendations = recommendations;
        _incognito = incognito;
    }

    /// <summary>
    /// The viewer's recommendations: up to <paramref name="limit"/> (1-50, default 12) series they have not rated, starred, read or started,
    /// from libraries they show on Home. Empty when they have no rating of 3.5 stars or more, favourite or finished series yet.
    /// </summary>
    [HttpGet("recommendations")]
    [ProducesResponseType<RecommendationsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecommendations([FromQuery] int limit = RecommendationService.DefaultLimit, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        return Ok(await _recommendations.ForUserAsync(userId, _incognito.IsIncognito, limit, ct));
    }

    /// <summary>
    /// Series like this node's (its own work, or the work of its nearest series folder): up to <paramref name="limit"/> (1-50, default 12),
    /// unread first. 404 for a node the viewer may not open (never 403); an empty list when it shows no series information.
    /// </summary>
    [HttpGet("nodes/{nodeId}/similar")]
    [ProducesResponseType<RecommendationsDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSimilar(string nodeId, [FromQuery] int limit = RecommendationService.DefaultLimit, CancellationToken ct = default)
    {
        if (UserId() is not { } userId)
            return Unauthorized();
        var answer = await _recommendations.SimilarAsync(userId, _incognito.IsIncognito, nodeId, limit, ct);
        return answer is null ? NotFound() : Ok(answer);
    }

    private long? UserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
