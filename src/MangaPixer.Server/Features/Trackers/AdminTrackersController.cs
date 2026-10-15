namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Administration > Trackers (1.43.0): allow a tracker for this server after its admin consent text, enter the instance's own app
/// registration (the client secret is write-only), see which users connected (never their remote account or a token), disconnect a
/// user's account (an admin can never connect one), and fix a series' tracker id for everyone (owner Q6). Admin cookie + CSRF only.
/// </summary>
[ApiController]
[Route("api/v1/admin/trackers")]
[Authorize(Policy = "Admin")]
public sealed class AdminTrackersController(
    TrackerSettingsService settings,
    TrackerAccountService accounts,
    TrackerAdminService admin,
    MangaPixerDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<AdminTrackerSettingsDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await settings.ListAsync(ct));

    /// <summary>Every user's connection: who, which tracker, the state - nothing about the remote account.</summary>
    [HttpGet("accounts")]
    [ProducesResponseType<IReadOnlyList<AdminTrackerAccountDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Accounts(CancellationToken ct) => Ok(await accounts.AdminListAsync(ct));

    [HttpGet("{slug}")]
    [ProducesResponseType<AdminTrackerSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string slug, CancellationToken ct) =>
        TrackerFacts.IsKnown(slug) ? Ok(await settings.GetAsync(slug, ct)) : Problem(TrackerProblem.UnknownTracker);

    /// <summary>Turns the tracker on (with the current admin consent version) or off, and stores the app registration.</summary>
    [HttpPut("{slug}")]
    [ProducesResponseType<AdminTrackerSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(string slug, [FromBody] UpdateTrackerSettingsRequest request, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(slug))
            return Problem(TrackerProblem.UnknownTracker);
        var error = await settings.UpdateAsync(slug, request, User.Identity?.Name, ct);
        if (error is not null)
            return BadRequest(new ApiError { Error = error, Message = MessageOf(error) });
        return Ok(await settings.GetAsync(slug, ct));
    }

    /// <summary>Disconnects a user's account on the tracker: its tokens are deleted; the user sees "disconnected by an admin".</summary>
    [HttpPost("{slug}/accounts/{userId}/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisconnectUser(string slug, string userId, CancellationToken ct)
    {
        if (!TrackerFacts.IsKnown(slug))
            return Problem(TrackerProblem.UnknownTracker);
        var id = await db.Users.AsNoTracking().Where(u => u.PublicId == userId).Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);
        if (id is null || !await accounts.AdminDisconnectAsync(id.Value, User.Identity?.Name, slug, ct))
            return Problem(TrackerProblem.NotFound("not_connected", "This user has no account on this tracker."));
        return NoContent();
    }

    /// <summary>The tracker ids of the series a folder belongs to, with where each comes from and the admin's own.</summary>
    [HttpGet("nodes/{nodeId}/ids")]
    [ProducesResponseType<TrackerSeriesIdsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SeriesIds(string nodeId, CancellationToken ct) =>
        await admin.SeriesIdsAsync(nodeId, ct) is { } ids ? Ok(ids) : Problem(TrackerProblem.NotFound("unknown_node", "There is no such folder."));

    /// <summary>Sets (or with an empty id removes) the admin's tracker id of the folder's series, for every user.</summary>
    [HttpPut("nodes/{nodeId}/ids/{slug}")]
    [ProducesResponseType<TrackerSeriesIdsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetSeriesId(string nodeId, string slug, [FromBody] UpdateTrackerSeriesIdRequest request, CancellationToken ct)
    {
        var (ids, problem) = await admin.SetSeriesIdAsync(nodeId, slug, request, User.Identity?.Name, ct);
        return problem is null ? Ok(ids) : Problem(problem);
    }

    private ObjectResult Problem(TrackerProblem problem) =>
        StatusCode(problem.Status, new ApiError { Error = problem.Code, Message = problem.Message });

    private static string MessageOf(string error) => error switch
    {
        "consent_required" => "Accept the current text to allow this tracker.",
        "invalid_client_id" => "The Client ID is too long or not valid.",
        "invalid_client_secret" => "The client secret is too long or not valid.",
        "invalid_redirect_uri" => "Enter the full redirect URL (http or https) exactly as registered.",
        "invalid_daily_cap" => "The daily request cap must be from 100 to 20000.",
        _ => "The settings are not valid.",
    };
}
