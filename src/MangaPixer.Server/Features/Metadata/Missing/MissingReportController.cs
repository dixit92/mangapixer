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
    private readonly MissingConversionService _conversion;

    public MissingReportController(MissingReportService report, MissingConversionService conversion)
    {
        _report = report;
        _conversion = conversion;
    }

    private string? Actor => User.Identity?.Name;

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

    /// <summary>
    /// 1.28.0: asks AniList (one gated request) for the entry matching this folder's linked MangaUpdates record and
    /// stores its volume / chapter totals for the chapters-per-volume conversion.
    /// </summary>
    [HttpPost("{nodeId}/conversion")]
    [ProducesResponseType<MissingConversionResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiError>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ApiError>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ApiError>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> LookupConversion(string nodeId, CancellationToken ct)
    {
        try
        {
            var (error, result) = await _conversion.LookupAsync(nodeId, Actor, ct);
            return error is null ? Ok(result) : NotFound();
        }
        catch (MetadataGatewayException ex)
        {
            return MetadataIdentifyController.Error(this, ex);
        }
    }

    /// <summary>1.28.0: the same for up to 20 linked series without a stored source; stops at the first refusal.</summary>
    [HttpPost("conversions")]
    [ProducesResponseType<MissingConversionBatchResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> LookupConversions([FromBody] MissingConversionBatchRequest request, CancellationToken ct)
    {
        var (error, result) = await _conversion.LookupBatchAsync(request.Library, Actor, ct);
        return error is null ? Ok(result) : NotFound();
    }
}
