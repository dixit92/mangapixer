namespace com.lifepixer.mangapixer.Server.Features.Ratings;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The current user's own rating of a catalog node (1.52.0 lane R): <c>PUT</c> sets it (1-10, shown as 5 stars with halves), <c>DELETE</c>
/// clears it. Per user and private - read back as <c>CatalogNodeDto.MyRating</c> by browse, search, the single-node lookup and the
/// Favorites list. MangaPixer only: no tracker score is read or written.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
public sealed class RatingsController : ControllerBase
{
    private readonly RatingsService _ratings;

    public RatingsController(RatingsService ratings) => _ratings = ratings;

    /// <summary>
    /// Sets the current user's rating of a node: <c>{ "score": 1..10 }</c> (1 = half a star, 10 = five stars). Idempotent. 400 outside
    /// 1-10; 404 for a node the user cannot open (the favourite routes' check). Allowed under Incognito, as reading progress is.
    /// </summary>
    [HttpPut("nodes/{nodeId}/rating")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetRating(string nodeId, [FromBody] SetRatingRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        return await _ratings.SetRatingAsync(userId.Value, nodeId, request.Score, ct) switch
        {
            RatingsService.RatingResult.InvalidScore => BadRequest(new ApiError { Error = "invalid_score", Message = "The score must be 1 to 10." }),
            RatingsService.RatingResult.NodeNotFound => NotFound(),
            _ => NoContent(),
        };
    }

    /// <summary>Clears the current user's rating of a node. Idempotent - 204 whether or not a rating was there.</summary>
    [HttpDelete("nodes/{nodeId}/rating")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearRating(string nodeId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        await _ratings.ClearRatingAsync(userId.Value, nodeId, ct);
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }
}
