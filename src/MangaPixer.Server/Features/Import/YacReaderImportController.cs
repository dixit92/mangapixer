namespace com.lifepixer.mangapixer.Server.Features.Import;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Features.Import.YacReader;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Admin-only endpoints for importing reading progress from external
/// libraries. Currently supports YACReader <c>library.ydb</c> databases.
///
/// All endpoints require the Admin role. Source paths supplied in requests
/// are private server locators and are never echoed in responses (source-path
/// privacy invariant).
/// </summary>
[ApiController]
[Route("api/v1/admin/import")]
[Authorize(Policy = "Admin")]
public sealed class YacReaderImportController : ControllerBase
{
    private readonly YacReaderImportService _importService;
    private readonly ILogger<YacReaderImportController> _logger;

    public YacReaderImportController(
        YacReaderImportService importService,
        ILogger<YacReaderImportController> logger)
    {
        _importService = importService;
        _logger = logger;
    }

    /// <summary>
    /// Detects whether a YACReader library is present inside a MangaPixer library's
    /// root, so the admin UI can offer the import only when one exists. Read-only;
    /// the source path is resolved server-side and never returned.
    /// </summary>
    [HttpGet("yacreader/detect")]
    public async Task<IActionResult> Detect([FromQuery] string libraryId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "libraryId is required." });

        var result = await _importService.DetectAsync(libraryId, ct);
        if (!result.Success)
            return BadRequest(new ApiError { Error = result.Error ?? "detect_failed", Message = result.Message ?? "Detection failed." });

        return Ok(result.Value);
    }

    /// <summary>
    /// Previews (dry-run) a YACReader progress import. Reads the source
    /// database read-only (or from a scratch snapshot) and reports the
    /// mapping, conflicts, and a bounded sample — without writing any state.
    /// </summary>
    [HttpPost("yacreader/preview")]
    public async Task<IActionResult> Preview(
        [FromBody] YacReaderImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryId)
            || string.IsNullOrWhiteSpace(request.TargetUserId))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "LibraryId and TargetUserId are required." });

        var result = await _importService.PreviewAsync(request, ct);
        if (!result.Success)
            return BadRequest(new ApiError { Error = result.Error ?? "import_failed", Message = result.Message ?? "Import preview failed." });

        return Ok(result.Value);
    }

    /// <summary>
    /// Applies a YACReader progress import, writing reading progress and
    /// sticky read-marks for the target user. Existing MangaPixer progress is
    /// skipped unless <see cref="YacReaderImportRequest.Overwrite"/> is set.
    /// </summary>
    [HttpPost("yacreader/apply")]
    public async Task<IActionResult> Apply(
        [FromBody] YacReaderImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.LibraryId)
            || string.IsNullOrWhiteSpace(request.TargetUserId))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "LibraryId and TargetUserId are required." });

        var result = await _importService.ApplyAsync(request, ct);
        if (!result.Success)
            return BadRequest(new ApiError { Error = result.Error ?? "import_failed", Message = result.Message ?? "Import apply failed." });

        return Ok(result.Value);
    }
}
