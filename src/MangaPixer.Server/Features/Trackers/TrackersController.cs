namespace com.lifepixer.mangapixer.Server.Features.Trackers;

using System.Globalization;
using System.Security.Claims;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Trackers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// A user's own tracker sync (1.43.0): the trackers this server allows and the caller's connections, connecting (OAuth sign-in or a
/// MangaUpdates password used once), disconnecting, settings, the caller's series list and its per-series actions, the first-connect
/// preview, and the caller's sync switches on folders / series. Cookie + CSRF only (the default scheme; an API token reaches none of these
/// routes - <c>ApiTokenHttpTests.Sweep_*</c>). Every route acts on the caller's own rows only.
/// </summary>
[ApiController]
[Route("api/v1/trackers")]
[Authorize]
public sealed class TrackersController(
    TrackerAccountService accounts,
    TrackerEntriesService entries,
    TrackerListService list,
    TrackerPullService pull) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TrackersOverviewDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(CancellationToken ct) =>
        UserId() is { } userId ? Ok(await accounts.OverviewAsync(userId, ct)) : Forbid();

    /// <summary>Starts a sign-in on the tracker's site; the answer is the URL the browser opens.</summary>
    [HttpPost("{slug}/connect/start")]
    [ProducesResponseType<TrackerConnectStartResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConnectStart(string slug, [FromBody] TrackerConnectStartRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId || await SessionKeyAsync() is not { } session)
            return Forbid();
        var (response, problem) = await accounts.StartAsync(userId, session, slug, request, ct);
        return problem is null ? Ok(response) : Problem(problem);
    }

    /// <summary>Finishes a sign-in: the callback page posts the code and the state the tracker handed back.</summary>
    [HttpPost("{slug}/connect")]
    [ProducesResponseType<TrackerAccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConnectFinish(string slug, [FromBody] TrackerConnectFinishRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId || await SessionKeyAsync() is not { } session)
            return Forbid();
        var (account, problem) = await accounts.FinishAsync(userId, User.Identity?.Name, session, slug, request, ct);
        return problem is null ? Ok(account) : Problem(problem);
    }

    /// <summary>Connects with a username and password (MangaUpdates), used once and never stored.</summary>
    [HttpPost("{slug}/connect/password")]
    [ProducesResponseType<TrackerAccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConnectPassword(string slug, [FromBody] TrackerConnectPasswordRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (account, problem) = await accounts.ConnectWithPasswordAsync(userId, User.Identity?.Name, slug, request, ct);
        return problem is null ? Ok(account) : Problem(problem);
    }

    /// <summary>Disconnects: the account, its tokens and its list are deleted here first; then the tracker is asked to revoke the sign-in.</summary>
    [HttpPost("{slug}/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(string slug, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        if (!TrackerFacts.IsKnown(slug))
            return Problem(TrackerProblem.UnknownTracker);
        return await accounts.DisconnectAsync(userId, User.Identity?.Name, slug, ct) ? NoContent() : Problem(TrackerProblem.NotConnected);
    }

    [HttpPut("{slug}/settings")]
    [ProducesResponseType<TrackerAccountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSettings(string slug, [FromBody] UpdateTrackerUserSettingsRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (account, problem) = await accounts.UpdateSettingsAsync(userId, slug, request, ct);
        return problem is null ? Ok(account) : Problem(problem);
    }

    /// <summary>The caller's series on this tracker with their states.</summary>
    [HttpGet("{slug}/entries")]
    [ProducesResponseType<IReadOnlyList<TrackerEntryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Entries(string slug, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (list, problem) = await entries.ListAsync(userId, slug, ct);
        return problem is null ? Ok(list) : Problem(problem);
    }

    /// <summary>"Sync now" for one series: read the tracker, write when MangaPixer is ahead (a held jump stays held).</summary>
    [HttpPost("{slug}/entries/{seriesId}/sync")]
    [ProducesResponseType<TrackerEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> SyncNow(string slug, string seriesId, CancellationToken ct) =>
        ActAsync(slug, seriesId, TrackerSyncMode.Automatic, null, ct);

    /// <summary>
    /// "Send" - in the 1.43.0 Tracking page "Set progress from MangaPixer to &lt;tracker&gt;": every mark counts (a held jump from folder
    /// mark-reads and imports too) - still never lowers the tracker. The route keeps its name.
    /// </summary>
    [HttpPost("{slug}/entries/{seriesId}/send")]
    [ProducesResponseType<TrackerEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Send(string slug, string seriesId, CancellationToken ct) =>
        ActAsync(slug, seriesId, TrackerSyncMode.SendAll, null, ct);

    /// <summary>"Set the tracker to N": the caller's explicit numbers, which may lower the tracker (the client asks first).</summary>
    [HttpPost("{slug}/entries/{seriesId}/set")]
    [ProducesResponseType<TrackerEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> SetProgress(string slug, string seriesId, [FromBody] TrackerSetProgressRequest request, CancellationToken ct) =>
        ActAsync(slug, seriesId, TrackerSyncMode.SetTo, new TrackerNumbers(request.Chapters, request.Volumes), ct);

    /// <summary>"Don't sync this series" on this tracker (or sync it again).</summary>
    [HttpPut("{slug}/entries/{seriesId}/dont-sync")]
    [ProducesResponseType<TrackerEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DontSync(string slug, string seriesId, [FromBody] TrackerDontSyncRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (entry, problem) = await entries.SetDontSyncAsync(userId, slug, seriesId, request.DontSync, ct);
        return problem is null ? Ok(entry) : Problem(problem);
    }

    /// <summary>The first-connect preview: reads the tracker for a batch of the caller's series; writes nothing.</summary>
    [HttpPost("{slug}/preview")]
    [ProducesResponseType<TrackerPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Preview(string slug, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (preview, problem) = await entries.PreviewAsync(userId, slug, ct);
        return problem is null ? Ok(preview) : Problem(problem);
    }

    /// <summary>"Send all": every held jump of the caller's list, until the daily cap.</summary>
    [HttpPost("{slug}/entries/send-all")]
    [ProducesResponseType<TrackerSendAllResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendAll(string slug, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (result, problem) = await entries.SendAllAsync(userId, slug, ct);
        return problem is null ? Ok(result) : Problem(problem);
    }

    /// <summary>
    /// 1.43.0 Tracking page: one page (50) of the caller's series on this tracker, by title; <c>filter</c> = a chip
    /// (<see cref="TrackerListBucket"/> name) or <c>Attention</c>; <c>q</c> = title search. Stored state only - no request to the tracker.
    /// </summary>
    [HttpGet("{slug}/series")]
    [ProducesResponseType<TrackerSeriesPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Series(string slug, [FromQuery] int? page, [FromQuery] string? filter, [FromQuery] string? q, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (result, problem) = await list.PageAsync(userId, slug, page, filter, q, ct);
        return problem is null ? Ok(result) : Problem(problem);
    }

    /// <summary>1.43.0: the account menu badge - the caller's series that need them (a held jump, a tracker ahead), per tracker.</summary>
    [HttpGet("attention")]
    [ProducesResponseType<TrackerAttentionDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Attention(CancellationToken ct) =>
        UserId() is { } userId ? Ok(await list.AttentionAsync(userId, ct)) : Forbid();

    /// <summary>1.43.0: the series header popover - the caller's tracking of the series a folder belongs to (display only, no request).</summary>
    [HttpGet("nodes/{nodeId}/summary")]
    [ProducesResponseType<TrackerSeriesSummaryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Summary(string nodeId, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (summary, problem) = await list.SummaryAsync(userId, nodeId, ct);
        return problem is null ? Ok(summary) : Problem(problem);
    }

    /// <summary>
    /// 1.43.0 "Set progress from &lt;tracker&gt; to MangaPixer": reads the caller's entry, then marks read here every archive of the series at or
    /// below the tracker's numbers. Never unmarks; nothing is sent to a tracker for these marks.
    /// </summary>
    [HttpPost("{slug}/entries/{seriesId}/from-tracker")]
    [ProducesResponseType<TrackerFromTrackerResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> FromTracker(string slug, string seriesId, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (result, problem) = await pull.FromTrackerAsync(userId, slug, seriesId, ct);
        return problem is null ? Ok(result) : Problem(problem);
    }

    /// <summary>1.43.0 (owner A1): the caller's start / finish dates of the series on this tracker (null = automatic); sent on the next write.</summary>
    [HttpPut("{slug}/entries/{seriesId}/dates")]
    [ProducesResponseType<TrackerSeriesRowDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDates(string slug, string seriesId, [FromBody] TrackerDatesRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (row, problem) = await pull.SetDatesAsync(userId, slug, seriesId, request, ct);
        return problem is null ? Ok(row) : Problem(problem);
    }

    /// <summary>The caller's sync switches on folders / series (owner Q3).</summary>
    [HttpGet("sync-overrides")]
    [ProducesResponseType<IReadOnlyList<TrackerNodeOverrideDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Overrides(CancellationToken ct) =>
        UserId() is { } userId ? Ok(await entries.ListOverridesAsync(userId, ct)) : Forbid();

    /// <summary>Sets (on / off) or clears the caller's switch on the selected folders / series.</summary>
    [HttpPut("sync-overrides")]
    [ProducesResponseType<IReadOnlyList<TrackerNodeOverrideDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiError>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOverrides([FromBody] UpdateTrackerNodeOverridesRequest request, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (list, problem) = await entries.SetOverridesAsync(userId, request, ct);
        return problem is null ? Ok(list) : Problem(problem);
    }

    private async Task<IActionResult> ActAsync(string slug, string seriesId, TrackerSyncMode mode, TrackerNumbers? setTo, CancellationToken ct)
    {
        if (UserId() is not { } userId)
            return Forbid();
        var (entry, problem) = await entries.ActAsync(userId, slug, seriesId, mode, setTo, ct);
        return problem is null ? Ok(entry) : Problem(problem);
    }

    private ObjectResult Problem(TrackerProblem problem) =>
        StatusCode(problem.Status, new ApiError { Error = problem.Code, Message = problem.Message });

    private long? UserId() =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The caller's session ticket (the key an OAuth <c>state</c> is bound to), from the cookie's auth properties.</summary>
    private async Task<string?> SessionKeyAsync()
    {
        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return result.Succeeded && result.Properties?.Items.TryGetValue(".MangaPixer.ticket", out var ticket) == true && !string.IsNullOrEmpty(ticket)
            ? ticket
            : null;
    }
}
