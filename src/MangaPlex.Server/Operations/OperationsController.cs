namespace com.lifepixer.mangaplex.Server.Operations;

using com.lifepixer.mangaplex.Core.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog.Events;

/// <summary>
/// Operations API endpoints: diagnostics, backup, restore.
/// Admin-only.
/// </summary>
[ApiController]
[Route("api/v1/operations")]
[Authorize(Policy = "Admin")]
public sealed class OperationsController : ControllerBase
{
    private readonly DiagnosticsService _diagnostics;
    private readonly BackupService _backup;
    private readonly LogLevelSettingsService _logLevel;
    private readonly ILogger<OperationsController> _logger;

    public OperationsController(
        DiagnosticsService diagnostics,
        BackupService backup,
        LogLevelSettingsService logLevel,
        ILogger<OperationsController> logger)
    {
        _diagnostics = diagnostics;
        _backup = backup;
        _logLevel = logLevel;
        _logger = logger;
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> GetDiagnostics(CancellationToken ct)
    {
        var snapshot = await _diagnostics.GetSnapshotAsync(ct);
        return Ok(snapshot);
    }

    [HttpGet("diagnostics/export")]
    public async Task<IActionResult> ExportLog(CancellationToken ct)
    {
        var export = await _diagnostics.ExportLogAsync(ct);
        return Ok(export);
    }

    [HttpGet("logging")]
    public IActionResult GetLoggingLevel()
    {
        return Ok(new LogLevelDto { Level = _logLevel.GetCurrent().ToString() });
    }

    [HttpPut("logging")]
    public IActionResult UpdateLoggingLevel([FromBody] UpdateLogLevelRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Level))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Log level is required." });

        if (!Enum.TryParse<LogEventLevel>(request.Level, ignoreCase: true, out var level))
            return BadRequest(new ApiError { Error = "invalid_level", Message = $"'{request.Level}' is not a valid log level. Allowed: Verbose, Debug, Information, Warning, Error, Fatal." });

        var userName = User.Identity?.Name ?? "unknown";
        _logLevel.SetLevel(level, userName);

        return Ok(new LogLevelDto { Level = level.ToString() });
    }

    [HttpPost("backup")]
    public async Task<IActionResult> Backup([FromBody] BackupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Path))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Backup path is required." });

        var result = await _backup.BackupAsync(request.Path, ct);
        if (!result.Succeeded)
            return BadRequest(new ApiError { Error = "backup_failed", Message = result.Error ?? "Backup failed" });

        return Ok(new { path = result.Path });
    }
}

public sealed record BackupRequest
{
    public string? Path { get; init; }
}

public sealed record LogLevelDto
{
    public required string Level { get; init; }
}

public sealed record UpdateLogLevelRequest
{
    public string? Level { get; init; }
}
