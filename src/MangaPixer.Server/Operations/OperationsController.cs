namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Core.Api;
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
    private readonly DbRestoreService _dbRestore;
    private readonly DbRestoreOptions _dbRestoreOptions;
    private readonly com.lifepixer.mangapixer.Server.Features.Updates.UpdateCheckService _updateCheck;
    private readonly com.lifepixer.mangapixer.Server.Features.Admin.AuditService _audit;
    private readonly ILogger<OperationsController> _logger;

    public OperationsController(
        DiagnosticsService diagnostics,
        BackupService backup,
        RotatingBackupService rotating,
        RotatingBackupOptions rotatingOptions,
        RotatingBackupState rotatingState,
        LogLevelSettingsService logLevel,
        DbRestoreService dbRestore,
        DbRestoreOptions dbRestoreOptions,
        com.lifepixer.mangapixer.Server.Features.Updates.UpdateCheckService updateCheck,
        com.lifepixer.mangapixer.Server.Features.Admin.AuditService audit,
        ILogger<OperationsController> logger)
    {
        _diagnostics = diagnostics;
        _backup = backup;
        _rotating = rotating;
        _rotatingOptions = rotatingOptions;
        _rotatingState = rotatingState;
        _logLevel = logLevel;
        _dbRestore = dbRestore;
        _dbRestoreOptions = dbRestoreOptions;
        _updateCheck = updateCheck;
        _audit = audit;
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
        var categories = _logLevel.GetCategoryLevels()
            .Select(c => new LogCategoryLevelDto
            {
                Name = c.Name,
                Level = c.Level.ToString(),
                Inherited = c.Inherited,
            })
            .ToList();

        return Ok(new LogLevelDto
        {
            Level = _logLevel.GetCurrent().ToString(),
            Categories = categories,
        });
    }

    [HttpPut("logging")]
    public async Task<IActionResult> UpdateLoggingLevel([FromBody] UpdateLogLevelRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });

        var userName = User.Identity?.Name ?? "unknown";
        var hasGlobalLevel = !string.IsNullOrWhiteSpace(request.Level);
        var hasCategories = request.Categories is { Count: > 0 };

        if (!hasGlobalLevel && !hasCategories)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Either 'level' or 'categories' is required." });

        // Validate the global level first (if provided).
        LogEventLevel? globalLevel = null;
        if (hasGlobalLevel)
        {
            if (!Enum.TryParse<LogEventLevel>(request.Level, ignoreCase: true, out var level))
                return BadRequest(new ApiError { Error = "invalid_level", Message = $"'{request.Level}' is not a valid log level. Allowed: Verbose, Debug, Information, Warning, Error, Fatal." });
            globalLevel = level;
        }

        // Validate per-category overrides (if provided).
        if (hasCategories)
        {
            foreach (var cat in request.Categories!)
            {
                if (string.IsNullOrWhiteSpace(cat.Name))
                    return BadRequest(new ApiError { Error = "invalid_request", Message = "Category name is required." });

                if (!com.lifepixer.mangapixer.Server.Logging.DebugCategories.IsValid(cat.Name!))
                    return BadRequest(new ApiError { Error = "invalid_category", Message = $"'{cat.Name}' is not a known debug category." });

                // A null/empty level means "clear/inherit" — valid. A non-empty
                // level must parse.
                if (!string.IsNullOrWhiteSpace(cat.Level))
                {
                    if (!Enum.TryParse<LogEventLevel>(cat.Level, ignoreCase: true, out _))
                        return BadRequest(new ApiError { Error = "invalid_level", Message = $"'{cat.Level}' is not a valid log level. Allowed: Verbose, Debug, Information, Warning, Error, Fatal." });
                }
            }
        }

        // Apply the global level (if provided).
        if (globalLevel is { } gl)
            _logLevel.SetLevel(gl, userName);

        // Apply per-category overrides (if provided).
        if (hasCategories)
        {
            foreach (var cat in request.Categories!)
            {
                if (string.IsNullOrWhiteSpace(cat.Level))
                    _logLevel.ClearCategoryLevel(cat.Name!, userName);
                else
                {
                    var catLevel = Enum.Parse<LogEventLevel>(cat.Level, ignoreCase: true);
                    _logLevel.SetCategoryLevel(cat.Name!, catLevel, userName);
                }
            }
        }

        // Build the response from the live service state.
        var categories = _logLevel.GetCategoryLevels()
            .Select(c => new LogCategoryLevelDto
            {
                Name = c.Name,
                Level = c.Level.ToString(),
                Inherited = c.Inherited,
            })
            .ToList();

        // Audit the change (action + actor only — the audit vocabulary is
        // deliberately terse; the concrete levels are not recorded).
        await _audit.RecordAsync(
            com.lifepixer.mangapixer.Server.Features.Admin.AuditActions.LoggingChanged,
            com.lifepixer.mangapixer.Server.Features.Admin.AuditResults.Success,
            userName, targetUserId: null, correlationId: null, ct);

        return Ok(new LogLevelDto
        {
            Level = _logLevel.GetCurrent().ToString(),
            Categories = categories,
        });
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
    /// Lists the on-disk rotating snapshots available to restore from — the
    /// generated file name, byte size, and timestamp of each, newest first.
    /// Reports generated file names only — NEVER absolute paths (privacy
    /// invariant). This is the read side that makes the rotating snapshots
    /// (previously exposed only as a count) actually selectable for a restore.
    /// </summary>
    [HttpGet("backups/files")]
    public IActionResult ListRotatingBackups()
    {
        IReadOnlyList<RotatingBackupFileInfo> files;
        try { files = _rotating.ListBackups(_rotatingOptions.BackupDirectory); }
        catch (IOException) { files = Array.Empty<RotatingBackupFileInfo>(); }

        var items = files
            .Select(f => new RotatingBackupFileDto
            {
                FileName = f.FileName,
                ByteSize = f.ByteSize,
                TimestampUtc = f.TimestampUtc,
            })
            .ToList();

        return Ok(new RotatingBackupListDto { Files = items });
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

    /// <summary>
    /// Update Checker status (1.21.0). Returns the opt-in flag, the running and
    /// latest-known versions, whether an update is available, and when the last
    /// check ran. When enabled and past the 24h cadence (or with
    /// <paramref name="force"/> set, the "Check now" action), performs the one
    /// sanctioned GitHub Releases call first; failures degrade gracefully and
    /// never block this response. Admin-only.
    /// </summary>
    [HttpGet("update-check")]
    public async Task<IActionResult> GetUpdateCheck([FromQuery] bool force, CancellationToken ct)
    {
        var status = await _updateCheck.GetStatusAsync(force, ct);
        return Ok(status);
    }

    /// <summary>
    /// Sets the Update Checker opt-in. Enabling triggers an immediate first check.
    /// Admin-only; the checker is OFF by default (no outbound call ships enabled).
    /// </summary>
    [HttpPut("update-check/settings")]
    public async Task<IActionResult> UpdateCheckSettings([FromBody] UpdateCheckSettingsRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Request body is required." });

        var status = await _updateCheck.SetEnabledAsync(request.Enabled, ct);
        return Ok(status);
    }

    /// <summary>
    /// Imports a MangaPixer SQLite backup and stages it for restore. The
    /// uploaded file is validated (SQLite magic, size cap, integrity check,
    /// expected schema), a pre-restore snapshot of the current DB is taken, and
    /// the validated upload is staged under the app's private data root. The
    /// actual atomic swap is applied on the next server restart (no live DB
    /// overwrite while connections are open). Admin-only.
    ///
    /// Accepts multipart/form-data with a single "file" field. A caller-
    /// supplied filesystem path is NEVER accepted.
    /// </summary>
    [HttpPost("restore")]
    [RequestSizeLimit(1073741824)]
    [TypeFilter(typeof(RestoreUploadFormLimitsFilter))]
    public async Task<IActionResult> Restore([FromForm] RestoreUploadRequest? request, CancellationToken ct)
    {
        if (request?.File is null || request.File.Length == 0)
            return BadRequest(new ApiError { Error = "invalid_request", Message = "A backup file is required." });

        var actor = User.Identity?.Name ?? "unknown";

        await using var stream = request.File.OpenReadStream();
        var result = await _dbRestore.StageRestoreAsync(stream, actor, ct);

        if (!result.Succeeded)
        {
            await AuditRestoreStageAsync(actor, false, ct);
            return BadRequest(new ApiError { Error = result.Error ?? "restore_failed", Message = result.Message ?? "Restore failed." });
        }

        await AuditRestoreStageAsync(actor, true, ct);
        return Accepted(new RestoreStageResponseDto
        {
            PreRestoreBackupFileName = result.PreRestoreBackupFileName,
            Message = result.Message,
        });
    }

    /// <summary>
    /// Stages a restore from one of the on-disk rotating snapshots, chosen by
    /// file NAME (from <see cref="ListRotatingBackups"/>). The name is validated
    /// to resolve strictly inside the app's backups directory — a caller-
    /// supplied filesystem path is NEVER accepted. Reuses the exact
    /// validate + pre-restore-snapshot + apply-on-restart + rollback path as
    /// the upload restore, so the security model is identical. Admin-only.
    /// </summary>
    [HttpPost("backups/restore")]
    public async Task<IActionResult> RestoreFromBackup([FromBody] RestoreFromBackupRequest? request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "A backup file name is required." });

        var actor = User.Identity?.Name ?? "unknown";
        var result = await _dbRestore.StageRestoreFromBackupAsync(request.FileName, actor, ct);

        if (!result.Succeeded)
        {
            await AuditRestoreStageAsync(actor, false, ct);
            return BadRequest(new ApiError { Error = result.Error ?? "restore_failed", Message = result.Message ?? "Restore failed." });
        }

        await AuditRestoreStageAsync(actor, true, ct);
        return Accepted(new RestoreStageResponseDto
        {
            PreRestoreBackupFileName = result.PreRestoreBackupFileName,
            Message = result.Message,
        });
    }

    private Task AuditRestoreStageAsync(string actor, bool succeeded, CancellationToken ct) =>
        _audit.RecordAsync(
            com.lifepixer.mangapixer.Server.Features.Admin.AuditActions.BackupRestoreStaged,
            succeeded
                ? com.lifepixer.mangapixer.Server.Features.Admin.AuditResults.Success
                : com.lifepixer.mangapixer.Server.Features.Admin.AuditResults.Failure,
            actor, targetUserId: null, correlationId: null, ct);
}

public sealed record BackupRequest
{
    public string? Path { get; init; }
}

/// <summary>One on-disk rotating snapshot, exposed for the restore picker.</summary>
public sealed record RotatingBackupFileDto
{
    public required string FileName { get; init; }
    public required long ByteSize { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
}

/// <summary>Listing of the on-disk rotating snapshots (newest first).</summary>
public sealed record RotatingBackupListDto
{
    public required IReadOnlyList<RotatingBackupFileDto> Files { get; init; }
}

/// <summary>Request to restore from a chosen on-disk rotating snapshot.</summary>
public sealed record RestoreFromBackupRequest
{
    public string? FileName { get; init; }
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
    public required IReadOnlyList<LogCategoryLevelDto> Categories { get; init; }
}

public sealed record LogCategoryLevelDto
{
    public required string Name { get; init; }
    public required string Level { get; init; }
    public required bool Inherited { get; init; }
}

public sealed record UpdateLogLevelRequest
{
    public string? Level { get; init; }
    public IReadOnlyList<LogCategoryOverride>? Categories { get; init; }
}

public sealed record LogCategoryOverride
{
    public string? Name { get; init; }
    public string? Level { get; init; }
}

/// <summary>Upload request for a DB restore (multipart/form-data).</summary>
public sealed class RestoreUploadRequest
{
    public Microsoft.AspNetCore.Http.IFormFile? File { get; init; }
}

/// <summary>Response for a staged restore (202 Accepted).</summary>
public sealed record RestoreStageResponseDto
{
    public required string? PreRestoreBackupFileName { get; init; }
    public required string? Message { get; init; }
}

/// <summary>
/// Raises the multipart form-parsing body limit for the restore upload to
/// match <see cref="DbRestoreOptions.MaxUploadBytes"/>. Without this, ASP.NET
/// Core's 128 MiB default <c>FormOptions.MultipartBodyLengthLimit</c> rejects
/// the form before the app's own (larger, configurable) cap ever applies —
/// the <see cref="RequestSizeLimitAttribute"/> on the action only raises the
/// overall request body limit, not this separate multipart-parsing limit.
/// </summary>
public sealed class RestoreUploadFormLimitsFilter : Microsoft.AspNetCore.Mvc.Filters.IResourceFilter
{
    private readonly DbRestoreOptions _options;

    public RestoreUploadFormLimitsFilter(DbRestoreOptions options)
    {
        _options = options;
    }

    public void OnResourceExecuting(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext context)
    {
        context.HttpContext.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(
            new Microsoft.AspNetCore.Http.Features.FormFeature(
                context.HttpContext.Request,
                new Microsoft.AspNetCore.Http.Features.FormOptions
                {
                    MultipartBodyLengthLimit = _options.MaxUploadBytes,
                }));
    }

    public void OnResourceExecuted(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutedContext context)
    {
    }
}
