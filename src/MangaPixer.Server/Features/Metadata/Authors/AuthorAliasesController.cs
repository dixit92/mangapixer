namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Start / status / cancel of the look-up of artists' other names (1.38.0), for the admin endpoints.</summary>
public sealed class AuthorAliasAdminService
{
    private readonly AuthorAliasLookupService _lookup;
    private readonly AuthorAliasLookupRunner _runner;
    private readonly AuditService _audit;
    private readonly ILogger<AuthorAliasAdminService> _logger;

    public AuthorAliasAdminService(AuthorAliasLookupService lookup, AuthorAliasLookupRunner runner, AuditService audit, ILogger<AuthorAliasAdminService> logger)
    {
        _lookup = lookup;
        _runner = runner;
        _audit = audit;
        _logger = logger;
    }

    public async Task<AuthorAliasStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var plan = await _lookup.PlanAsync(ct);
        return await StatusAsync(plan, ct);
    }

    /// <summary>
    /// Starts a look-up of the ids not fetched yet (or older than the refresh age). Null error = started, already running, or nothing to
    /// do (the status says which); otherwise the refusal (a switch, the backoff or the budget) and nothing was started.
    /// </summary>
    public async Task<(AuthorAliasStatusDto Status, MetadataGatewayException? Refusal, bool Started)> StartAsync(string? actor, CancellationToken ct)
    {
        var plan = await _lookup.PlanAsync(ct);
        var started = false;
        if (_runner.Running is null && plan.ToFetch.Count > 0)
        {
            if (await _lookup.BlockedAsync(plan, ct) is { } blocked)
            {
                _logger.LogInformation(LogEvents.Metadata.AuthorLookupRefused, "Author look-up refused: {Code}", blocked.Code);
                return (await StatusAsync(plan, ct), Refusal(blocked.Code, blocked.RetryAt), false);
            }
            started = _runner.TryStart(plan.ToFetch);
            if (started)
                await _audit.RecordAsync(AuditActions.MetadataAuthorsFetch, $"ids_{plan.ToFetch.Count}", actor, ct: ct);
        }
        return (await StatusAsync(plan, ct), null, started);
    }

    public async Task<AuthorAliasStatusDto> CancelAsync(string? actor, CancellationToken ct)
    {
        if (_runner.Cancel())
        {
            await _audit.RecordAsync(AuditActions.MetadataAuthorsFetchCancel, AuditResults.Success, actor, ct: ct);
            // Give the run a moment to stop after its current request, so the answer usually shows it stopped.
            await Task.WhenAny(_runner.Current, Task.Delay(TimeSpan.FromSeconds(2), ct));
        }
        return await GetStatusAsync(ct);
    }

    private async Task<AuthorAliasStatusDto> StatusAsync(AuthorAliasPlan plan, CancellationToken ct)
    {
        var running = _runner.Running;
        var blocked = running is null ? await _lookup.BlockedAsync(plan, ct) : null;
        return new AuthorAliasStatusDto
        {
            Eligible = plan.Eligible,
            Fetched = plan.Fetched,
            ToFetch = plan.ToFetch.Count,
            WithOtherNames = await _lookup.CountWithOtherNamesAsync(ct),
            RefreshAfterDays = AuthorAliasLookupService.RefreshAfterDays,
            SecondsPerRequest = (int)AuthorAliasLookupService.Interval.TotalSeconds,
            BlockedReason = blocked?.Code,
            Running = running,
            LastRun = _runner.LastRun,
        };
    }

    private static MetadataGatewayException Refusal(string code, DateTimeOffset? retryAt) => code switch
    {
        "provider_backoff" => new(StatusCodes.Status503ServiceUnavailable, code, "MangaUpdates asked us to slow down. Try again later.", retryAt),
        "budget_exhausted" => new(StatusCodes.Status429TooManyRequests, code,
            "Today's metadata request budget is used up. It resets at midnight server time; an admin can raise it in Metadata Manager."),
        "metadata_network_disabled" => new(StatusCodes.Status409Conflict, code,
            "Web metadata is disabled in the server configuration (Metadata:NetworkDisabled)."),
        "provider_not_allowed" => new(StatusCodes.Status409Conflict, code,
            "MangaUpdates is not on the provider allowlist. An admin can add it back in Metadata Manager > Settings."),
        "library_metadata_disabled" => new(StatusCodes.Status409Conflict, code,
            "Fetching series information from the web is off for every library that links these series."),
        _ => new(StatusCodes.Status409Conflict, code,
            "Fetching series information from the web is off. An admin can turn it on in Metadata Manager."),
    };
}

/// <summary>
/// Artists' other names (1.38.0): the admin-run look-up of MangaUpdates author records. Admin policy (cookie only - never reachable
/// with an API token), CSRF on every POST by the global anti-forgery filter. A start is audited with the number of ids only.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/authors")]
[Authorize(Policy = "Admin")]
public sealed class AuthorAliasesController : ControllerBase
{
    private readonly AuthorAliasAdminService _service;

    public AuthorAliasesController(AuthorAliasAdminService service) => _service = service;

    /// <summary>Known authors, how many are fetched, how many a look-up would request, the running / last look-up. No request.</summary>
    [HttpGet]
    [ProducesResponseType<AuthorAliasStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await _service.GetStatusAsync(ct));

    /// <summary>
    /// "Look up the rest": starts the look-up in the background (202). 200 when one already runs or nothing is left to look up;
    /// 409 / 429 / 503 with the gateway's code when a switch, the budget or a backoff refuses (nothing is sent).
    /// </summary>
    [HttpPost("lookup")]
    [ProducesResponseType<AuthorAliasStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<AuthorAliasStatusDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var (status, refusal, started) = await _service.StartAsync(User.Identity?.Name, ct);
        if (refusal is not null)
            return MetadataIdentifyController.Error(this, refusal);
        return started ? StatusCode(StatusCodes.Status202Accepted, status) : Ok(status);
    }

    /// <summary>Stops the running look-up after its current request (the ids not reached stay unfetched).</summary>
    [HttpPost("lookup/cancel")]
    [ProducesResponseType<AuthorAliasStatusDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(CancellationToken ct) => Ok(await _service.CancelAsync(User.Identity?.Name, ct));
}
