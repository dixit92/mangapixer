namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using com.lifepixer.mangapixer.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The missing volumes / chapters report (1.28.0): the Metadata Manager's Missing tab and the series page line.
/// Admin-only; built from stored data, never a provider request.
/// </summary>
[ApiController]
[Route("api/v1/admin/metadata/missing")]
[Authorize(Policy = "Admin")]
public sealed class MissingReportController : ControllerBase
{
    private readonly MissingReportService _report;

    public MissingReportController(MissingReportService report)
    {
        _report = report;
    }

    [HttpGet]
    [ProducesResponseType<MissingReportPageDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? library = null, [FromQuery] bool onlyMissing = false,
        [FromQuery] string? cursor = null, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var (error, page) = await _report.ListAsync(library, onlyMissing, cursor, limit, ct);
        return error is null ? Ok(page) : NotFound();
    }

    [HttpGet("{nodeId}")]
    [ProducesResponseType<MissingSeriesDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ForNode(string nodeId, CancellationToken ct = default) =>
        await _report.ForNodeAsync(nodeId, ct) is { } dto ? Ok(dto) : NotFound();
}
