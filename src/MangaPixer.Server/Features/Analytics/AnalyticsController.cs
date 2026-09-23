namespace com.lifepixer.mangapixer.Server.Features.Analytics;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin-only analytics endpoints (1.22.0 lane E): a Dashboard v1 section on
/// the existing admin page, backed by on-demand aggregation (no rollup/
/// snapshot table — see the feature note for the query-time measurement that
/// justified this).
/// </summary>
[ApiController]
[Route("api/v1/admin/analytics")]
[Authorize(Policy = "Admin")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
    {
        var overview = await _analytics.GetOverviewAsync(ct);
        return Ok(overview);
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUserAnalytics(CancellationToken ct)
    {
        var rows = await _analytics.GetUserAnalyticsAsync(ct);
        return Ok(rows);
    }
}
