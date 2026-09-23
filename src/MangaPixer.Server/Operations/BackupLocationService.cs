namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using System.IO;

/// <summary>
/// Runs the backup location checks against the live server state: builds the
/// protected-root context (data/cache/scratch/media/install roots + every
/// registered library root), re-checks the effective location, and records the
/// outcome in <see cref="RotatingBackupState"/>. Status TRANSITIONS (ok to
/// unavailable/invalid and back) are logged and audited once, never per run.
/// Logs carry the location KIND and a code only, never the location itself.
/// </summary>
public sealed class BackupLocationService
{
    private readonly MangaPixerDbContext _db;
    private readonly BackupSettingsResolver _settings;
    private readonly BackupLocationValidator _validator;
    private readonly IBackupLocationFileSystem _fs;
    private readonly RotatingBackupState _state;
    private readonly AppRootOptions _roots;
    private readonly MediaBrowseOptions? _media;
    private readonly AuditService? _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupLocationService>? _logger;

    public BackupLocationService(
        MangaPixerDbContext db,
        BackupSettingsResolver settings,
        BackupLocationValidator validator,
        IBackupLocationFileSystem fs,
        RotatingBackupState state,
        AppRootOptions roots,
        MediaBrowseOptions? media = null,
        AuditService? audit = null,
        TimeProvider? time = null,
        ILogger<BackupLocationService>? logger = null)
    {
        _db = db;
        _settings = settings;
        _validator = validator;
        _fs = fs;
        _state = state;
        _roots = roots;
        _media = media;
        _audit = audit;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    public string DataRoot => string.IsNullOrWhiteSpace(_roots.DataRoot)
        ? Path.Combine(AppContext.BaseDirectory, "data")
        : _roots.DataRoot;

    /// <summary>Protected roots + DB size for the validator.</summary>
    public async Task<BackupLocationContext> BuildContextAsync(CancellationToken ct = default)
    {
        var roots = new List<string>();
        void Add(string? r)
        {
            if (!string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
                roots.Add(r);
        }

        Add(_roots.CacheRoot);
        Add(_roots.ScratchRoot);
        Add(_media?.Root);
        Add(AppContext.BaseDirectory);

        List<string> libraryRoots;
        try
        {
            libraryRoots = await _db.Libraries.AsNoTracking().Select(l => l.RootPath).ToListAsync(ct);
        }
        catch (Exception)
        {
            libraryRoots = new List<string>();
        }
        foreach (var root in libraryRoots)
            Add(root);

        long dbBytes = 0;
        try
        {
            var dbFile = new FileInfo(Path.Combine(DataRoot, "mangapixer.db"));
            if (dbFile.Exists) dbBytes = dbFile.Length;
        }
        catch (Exception) { }

        return new BackupLocationContext { DataRoot = DataRoot, ProtectedRoots = roots, DatabaseBytes = dbBytes };
    }

    /// <summary>Full validation of a candidate custom location (settings PUT).</summary>
    public async Task<BackupLocationValidation> ValidateCandidateAsync(
        string? location, bool adoptExistingMarker, bool validateOnly, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(ct);
        return _validator.Validate(location, context, _settings.Current.MarkerId, adoptExistingMarker, validateOnly);
    }

    /// <summary>
    /// Re-checks the effective location and updates the status. Default mode
    /// is always <c>ok</c> (the folder is auto-created at run time). With
    /// <paramref name="startup"/> an operator-pinned (configuration) location
    /// that exists but carries no marker yet is initialized once; a UI-chosen
    /// location got its marker when it was saved.
    /// </summary>
    public async Task<BackupLocationValidation> CheckAsync(CancellationToken ct = default, bool startup = false)
    {
        var settings = _settings.Current;
        if (!settings.IsCustom)
        {
            await TransitionAsync(BackupLocationStatuses.Ok, settings, ct);
            return new BackupLocationValidation { IsValid = true, NormalizedLocation = settings.RotatingDirectory };
        }

        var context = await BuildContextAsync(ct);
        var result = _validator.CheckBeforeRun(settings.CustomLocation, context, settings.MarkerId);

        if (!result.IsValid && startup &&
            settings.LocationSource == BackupSettingSources.Configuration &&
            result.ErrorCode == BackupLocationCodes.Unavailable &&
            result.NormalizedLocation is not null &&
            _fs.DirectoryExists(result.NormalizedLocation))
        {
            result = await InitializeConfiguredMarkerAsync(result, context, settings, ct);
        }

        var status = result.IsValid
            ? BackupLocationStatuses.Ok
            : result.ErrorCode == BackupLocationCodes.Invalid
                ? BackupLocationStatuses.Invalid
                : BackupLocationStatuses.Unavailable;
        await TransitionAsync(status, settings, ct);
        return result;
    }

    /// <summary>
    /// First sight of an operator-pinned location: adopt its marker when the
    /// file carries this instance's id, or write one when there is none. A
    /// marker of ANOTHER instance stays a failure (two instances must never
    /// prune each other's snapshots); the operator resolves it on disk.
    /// </summary>
    private async Task<BackupLocationValidation> InitializeConfiguredMarkerAsync(
        BackupLocationValidation check, BackupLocationContext context, EffectiveBackupSettings settings, CancellationToken ct)
    {
        try
        {
            var row = await LoadOrCreateRowAsync(ct);
            if (check.MarkerExists)
            {
                // Only an instance that has never owned a marker may adopt one.
                if (row.BackupLocationMarkerId is not null || check.MarkerId is null)
                    return check;
                row.BackupLocationMarkerId = check.MarkerId;
            }
            else
            {
                row.BackupLocationMarkerId ??= BackupLocationValidator.NewMarkerId();
                _fs.WriteMarker(check.NormalizedLocation!, row.BackupLocationMarkerId, _time.GetUtcNow());
            }
            await _db.SaveChangesAsync(ct);
            _settings.Apply(row);
            _logger?.LogInformation(LogEvents.Backup.BackupLocationMarkerInitialized,
                "Backup location marker initialized (location kind {Kind}, source {Source})",
                settings.LocationKind, settings.LocationSource);
            return _validator.CheckBeforeRun(settings.CustomLocation, context, row.BackupLocationMarkerId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Backup.RotatingLocationUnavailable,
                "Backup location marker could not be initialized: {Error}", ex.GetType().Name);
            return check;
        }
    }

    public async Task<AppSettingsEntity> LoadOrCreateRowAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId, ct);
        if (row is not null)
            return row;
        row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
        _db.AppSettings.Add(row);
        return row;
    }

    private async Task TransitionAsync(string status, EffectiveBackupSettings settings, CancellationToken ct)
    {
        var previous = _state.SetLocationStatus(status);
        if (previous == status)
            return;

        var wasBad = previous is BackupLocationStatuses.Unavailable or BackupLocationStatuses.Invalid;
        var isBad = status is BackupLocationStatuses.Unavailable or BackupLocationStatuses.Invalid;

        if (isBad && !wasBad)
        {
            _logger?.LogWarning(LogEvents.Backup.RotatingLocationUnavailable,
                "Backup location is {Status} (location kind {Kind}); scheduled backups are paused until it is fixed",
                status, settings.LocationKind);
            await AuditAsync(AuditActions.BackupLocationUnavailable, AuditResults.Failure, ct);
        }
        else if (wasBad && !isBad)
        {
            _logger?.LogInformation(LogEvents.Backup.RotatingLocationRecovered,
                "Backup location is available again (location kind {Kind})", settings.LocationKind);
            await AuditAsync(AuditActions.BackupLocationAvailable, AuditResults.Success, ct);
        }
    }

    private async Task AuditAsync(string action, string result, CancellationToken ct)
    {
        if (_audit is null)
            return;
        try
        {
            await _audit.RecordAsync(action, result, actorUserName: null, ct: ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(LogEvents.Backup.RotatingLocationUnavailable,
                "Backup location audit could not be recorded: {Error}", ex.GetType().Name);
        }
    }
}
