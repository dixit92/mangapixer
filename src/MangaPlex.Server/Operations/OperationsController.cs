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
    private readonly RotatingBackupService _rotating;
    private readonly RotatingBackupOptions _rotatingOptions;
    private readonly RotatingBackupState _rotatingState;
    private readonly LogLevelSettingsService _logLevel;
    private readonly ILogger<OperationsController> _logger;

    public OperationsController(
        DiagnosticsService diagnostics,
        BackupService backup,
        RotatingBackupService rotating,
        RotatingBackupOptions rotatingOptions,
        RotatingBackupState rotatingState,
        LogLevelSettingsService logLevel,
        ILogger<OperationsController> logger)
    {
        _diagnostics = diagnostics;
        _backup = backup;
        _rotating = rotating;
        _rotatingOptions = rotatingOptions;
        _rotatingState = rotatingState;
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

    /// <summary>
    /// Status of the scheduled rotating backups: configuration, last
    /// attempt/success, and the retained snapshot count. Reports generated
    /// file names only — never absolute paths.
    /// </summary>
    [HttpGet("backups")]
    public IActionResult GetRotatingBackups()
    {
        return Ok(BuildRotatingStatusDto());
    }

    /// <summary>
    /// Triggers a rotating backup immediately (same path as the scheduled
    /// run: snapshot + prune, pre-migration backups exempt from pruning).
    /// Serialized with the scheduled timer via the shared run gate.
    /// </summary>
    [HttpPost("backups/rotating")]
    public async Task<IActionResult> RunRotatingBackup(CancellationToken ct)
    {
        var outcome = await _rotating.RunAsync(ct);
        if (!outcome.Succeeded)
            return BadRequest(new ApiError { Error = "backup_failed", Message = "Rotating backup failed." });

        return Ok(BuildRotatingStatusDto());
    }

    private RotatingBackupStatusDto BuildRotatingStatusDto()
    {
        int retained;
        try { retained = _rotating.CountBackups(_rotatingOptions.BackupDirectory); }
        catch (IOException) { retained = 0; }

        return new RotatingBackupStatusDto
        {
            Enabled = _rotatingOptions.Enabled,
            IntervalHours = _rotatingOptions.Interval.TotalHours,
            RetentionCount = _rotatingOptions.RetentionCount,
            LastAttemptUtc = _rotatingState.LastAttemptUtc,
            LastSuccessUtc = _rotatingState.LastSuccessUtc,
            LastFailureUtc = _rotatingState.LastFailureUtc,
            LastBackupFileName = _rotatingState.LastBackupFileName,
            RetainedCount = retained,
        };
    }
}

public sealed record BackupRequest
{
    public string? Path { get; init; }
}

public sealed record RotatingBackupStatusDto
{
    public required bool Enabled { get; init; }
    public required double IntervalHours { get; init; }
    public required int RetentionCount { get; init; }
    public required DateTimeOffset? LastAttemptUtc { get; init; }
    public required DateTimeOffset? LastSuccessUtc { get; init; }
    public required DateTimeOffset? LastFailureUtc { get; init; }
    public required string? LastBackupFileName { get; init; }
    public required int RetainedCount { get; init; }
}

public sealed record LogLevelDto
{
    public required string Level { get; init; }
}

public sealed record UpdateLogLevelRequest
{
    public string? Level { get; init; }
}
