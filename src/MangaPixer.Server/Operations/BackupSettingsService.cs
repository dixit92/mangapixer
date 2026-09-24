namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>Outcome of a backup settings request: a result DTO or an HTTP error.</summary>
public sealed record BackupSettingsUpdateOutcome
{
    public BackupSettingsUpdateResultDto? Result { get; init; }
    public int StatusCode { get; init; } = 200;
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public bool Succeeded => Result is not null;

    public static BackupSettingsUpdateOutcome Error(int status, string code, string message) =>
        new() { StatusCode = status, ErrorCode = code, Message = message };
}

/// <summary>
/// Admin read/write of the backup settings (1.22.0): enabled / interval /
/// retention / rotating location, persisted in the AppSettings row and applied
/// live through <see cref="BackupSettingsResolver"/>. Configuration-managed
/// fields are refused (409). The current-password re-authentication for a
/// location change happens in the controller, before <see cref="ApplyAsync"/>.
/// A location change moves the existing rotating snapshots only when the
/// request asks for it, as a background <see cref="BackupSnapshotMoveService"/> job.
/// </summary>
public sealed class BackupSettingsService
{
    public const double MinIntervalHours = 1;
    public const double MaxIntervalHours = 720;
    public const int MinRetention = 1;
    public const int MaxRetention = 100;

    private readonly MangaPixerDbContext _db;
    private readonly BackupSettingsResolver _settings;
    private readonly BackupLocationService _location;
    private readonly IBackupLocationFileSystem _fs;
    private readonly RotatingBackupState _state;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly BackupSnapshotMoveService? _mover;
    private readonly ILogger<BackupSettingsService>? _logger;

    public BackupSettingsService(
        MangaPixerDbContext db,
        BackupSettingsResolver settings,
        BackupLocationService location,
        IBackupLocationFileSystem fs,
        RotatingBackupState state,
        AuditService audit,
        TimeProvider time,
        ILogger<BackupSettingsService>? logger = null,
        BackupSnapshotMoveService? mover = null)
    {
        _db = db;
        _settings = settings;
        _location = location;
        _fs = fs;
        _state = state;
        _audit = audit;
        _time = time;
        _logger = logger;
        _mover = mover;
    }

    public BackupSettingsDto GetSettings() => ToDto(_settings.Current);

    /// <summary>The signed-in user, for the current-password re-authentication.</summary>
    public Task<Persistence.Entities.UserEntity?> FindUserAsync(long userId, CancellationToken ct = default) =>
        Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(_db.Users, u => u.Id == userId, ct);

    public BackupSettingsDto ToDto(EffectiveBackupSettings s)
    {
        // A custom location that failed its last check is not read from.
        var readable = !s.IsCustom ||
            _state.LocationStatus is BackupLocationStatuses.Ok or BackupLocationStatuses.Unknown;
        var (count, bytes) = readable ? BackupSnapshotMover.Summarize(s.RotatingDirectory) : (0, 0L);
        return ToDto(s, count, bytes);
    }

    private BackupSettingsDto ToDto(EffectiveBackupSettings s, int snapshotCount, long snapshotBytes) => new()
    {
        Enabled = s.Enabled,
        EnabledSource = s.EnabledSource,
        IntervalHours = s.IntervalHours,
        IntervalHoursSource = s.IntervalHoursSource,
        RetentionCount = s.RetentionCount,
        RetentionCountSource = s.RetentionCountSource,
        LocationKind = s.LocationKind,
        LocationSource = s.LocationSource,
        CustomLocation = s.CustomLocation,
        LocationChangeAllowed = s.LocationChangeAllowed,
        LocationStatus = _state.LocationStatus,
        Platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : null,
        RotatingSnapshotCount = snapshotCount,
        RotatingSnapshotBytes = snapshotBytes,
    };

    /// <summary>
    /// Cheap request checks that need no password and touch no filesystem:
    /// configuration-managed fields (409), ranges (400), location mode (400).
    /// Returns null when the request may proceed.
    /// </summary>
    public BackupSettingsUpdateOutcome? CheckRequest(UpdateBackupSettingsRequest request)
    {
        var s = _settings.Current;

        if (request.Enabled is not null && s.EnabledSource == BackupSettingSources.Configuration)
            return Managed("enabled");
        if (request.IntervalHours is { } hours)
        {
            if (s.IntervalHoursSource == BackupSettingSources.Configuration)
                return Managed("intervalHours");
            if (double.IsNaN(hours) || hours < MinIntervalHours || hours > MaxIntervalHours)
                return BackupSettingsUpdateOutcome.Error(400, "invalid_interval",
                    $"The backup interval must be between {MinIntervalHours:0} and {MaxIntervalHours:0} hours.");
        }
        if (request.RetentionCount is { } retention)
        {
            if (s.RetentionCountSource == BackupSettingSources.Configuration)
                return Managed("retentionCount");
            if (retention < MinRetention || retention > MaxRetention)
                return BackupSettingsUpdateOutcome.Error(400, "invalid_retention",
                    $"The number of kept backups must be between {MinRetention} and {MaxRetention}.");
        }
        if (request.Location is { } location)
        {
            if (!s.LocationChangeAllowed)
                return Managed("location");
            if (_mover?.IsRunning == true)
                return BackupSettingsUpdateOutcome.Error(409, "snapshot_move_in_progress",
                    "Existing snapshots are still being moved. Change the location again when the move has finished.");
            if (location.Mode is not (EffectiveBackupSettings.KindDefault or EffectiveBackupSettings.KindCustom))
                return BackupSettingsUpdateOutcome.Error(400, BackupLocationCodes.Invalid,
                    "The location mode must be 'default' or 'custom'.");
            if (location.Mode == EffectiveBackupSettings.KindCustom && string.IsNullOrWhiteSpace(location.CustomLocation))
                return BackupSettingsUpdateOutcome.Error(400, BackupLocationCodes.Invalid, "A custom backup folder is required.");
        }

        return null;
    }

    /// <summary>
    /// Validates the location (if any) and persists the change, or, with
    /// <see cref="UpdateBackupSettingsRequest.ValidateOnly"/>, reports what would
    /// happen. The caller has already run <see cref="CheckRequest"/> and the
    /// password re-authentication.
    /// </summary>
    public async Task<BackupSettingsUpdateOutcome> ApplyAsync(
        UpdateBackupSettingsRequest request, string? actor, CancellationToken ct = default)
    {
        var before = _settings.Current;
        BackupLocationValidation? validation = null;

        if (request.Location is { Mode: EffectiveBackupSettings.KindCustom } location)
        {
            validation = await _location.ValidateCandidateAsync(
                location.CustomLocation, request.AdoptExistingMarker, request.ValidateOnly, ct);
            if (!validation.IsValid)
            {
                if (!request.ValidateOnly)
                    await AuditAsync(request, actor, success: false, ct);
                return BackupSettingsUpdateOutcome.Error(400, validation.ErrorCode ?? BackupLocationCodes.Invalid,
                    MessageFor(validation.ErrorCode));
            }
        }

        var newLocation = request.Location is null
            ? before.CustomLocation
            : validation?.NormalizedLocation;
        var locationChanged = request.Location is not null &&
            !string.Equals(before.CustomLocation, newLocation, StringComparison.Ordinal);

        if (request.ValidateOnly)
        {
            return new BackupSettingsUpdateOutcome
            {
                Result = new BackupSettingsUpdateResultDto
                {
                    Settings = ToDto(before),
                    ValidateOnly = true,
                    WillCreate = validation?.WillCreate ?? false,
                    LocationChanged = locationChanged,
                    Warnings = validation?.Warnings ?? Array.Empty<string>(),
                },
            };
        }

        var row = await _location.LoadOrCreateRowAsync(ct);
        if (request.Enabled is { } enabled) row.BackupsEnabled = enabled;
        if (request.IntervalHours is { } hours) row.BackupIntervalHours = hours;
        if (request.RetentionCount is { } retention) row.BackupRetentionCount = retention;

        if (request.Location is not null)
        {
            if (validation is null)
            {
                row.BackupLocation = null;
            }
            else
            {
                // The marker id is stable per instance, so switching back to a
                // folder this instance used before needs no takeover.
                row.BackupLocationMarkerId = validation.MarkerId;
                try
                {
                    _fs.WriteMarker(validation.NormalizedLocation!, validation.MarkerId!, _time.GetUtcNow());
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(LogEvents.Backup.BackupSettingsChanged,
                        "Backup location marker could not be written: {Error}", ex.GetType().Name);
                    _db.ChangeTracker.Clear();
                    await AuditAsync(request, actor, success: false, ct);
                    return BackupSettingsUpdateOutcome.Error(400, BackupLocationCodes.NotWritable, MessageFor(BackupLocationCodes.NotWritable));
                }
                row.BackupLocation = validation.NormalizedLocation;
            }
        }

        await _db.SaveChangesAsync(ct);
        _settings.Apply(row);
        await _location.CheckAsync(ct);
        await AuditAsync(request, actor, success: true, ct);

        var after = _settings.Current;
        var moveStarted = locationChanged && request.MoveExistingSnapshots && _mover is not null &&
            _mover.TryStart(before.RotatingDirectory, after.RotatingDirectory, actor);
        _logger?.LogInformation(LogEvents.Backup.BackupSettingsChanged,
            "Backup settings changed by {Actor}: enabled {Enabled}, interval {IntervalHours} h, retention {Retention}, location kind {Kind}",
            actor ?? "unknown", after.Enabled, after.IntervalHours, after.RetentionCount, after.LocationKind);

        return new BackupSettingsUpdateOutcome
        {
            Result = new BackupSettingsUpdateResultDto
            {
                Settings = ToDto(after),
                ValidateOnly = false,
                WillCreate = validation?.WillCreate ?? false,
                LocationChanged = locationChanged,
                SnapshotMoveStarted = moveStarted,
                Warnings = validation?.Warnings ?? Array.Empty<string>(),
            },
        };
    }

    /// <summary>
    /// Audits the request: <c>backup.settings.change</c> when it touches
    /// enabled/interval/retention, <c>backup.location.change</c> when it touches
    /// the location. Action + actor + result only (no values, no paths).
    /// </summary>
    public async Task AuditAsync(UpdateBackupSettingsRequest request, string? actor, bool success, CancellationToken ct = default)
    {
        var result = success ? AuditResults.Success : AuditResults.Failure;
        if (request.Enabled is not null || request.IntervalHours is not null || request.RetentionCount is not null)
            await _audit.RecordAsync(AuditActions.BackupSettingsChanged, result, actor, ct: ct);
        if (request.Location is not null)
            await _audit.RecordAsync(AuditActions.BackupLocationChanged, result, actor, ct: ct);
    }

    private static BackupSettingsUpdateOutcome Managed(string field) => BackupSettingsUpdateOutcome.Error(409,
        "managed_by_configuration", $"The backup setting '{field}' is managed by server configuration.");

    /// <summary>Generic messages: they never echo the location or a resolved path.</summary>
    public static string MessageFor(string? code) => code switch
    {
        BackupLocationCodes.NotAbsolute => "The backup folder must be an absolute path.",
        BackupLocationCodes.OverlapsProtected => "The backup folder must be outside the data, cache, scratch, media, library and program folders.",
        BackupLocationCodes.Forbidden => "The backup folder cannot be a system or temporary folder.",
        BackupLocationCodes.ParentMissing => "The parent folder does not exist. Only the last folder is created automatically.",
        BackupLocationCodes.NotWritable => "The server cannot write to the backup folder.",
        BackupLocationCodes.InUse => "The backup folder is already used by another MangaPixer instance.",
        _ => "The backup folder is not valid.",
    };
}
