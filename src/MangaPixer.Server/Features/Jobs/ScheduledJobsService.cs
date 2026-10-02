namespace com.lifepixer.mangapixer.Server.Features.Jobs;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Features.Trash;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// The admin Scheduled jobs section (1.32.0): reads every job's rhythm, last run (<see cref="JobRunRecorder"/>, the scan / trash /
/// update-check columns) and next run on the server's clock, and saves the hours of the daily jobs and the refresh cadence. Trash
/// hours go through <see cref="TrashService"/> (same validation and audit as its card); everything else is audited here. Job keys,
/// hours and counts only in audit rows and logs.
/// </summary>
public sealed class ScheduledJobsService
{
    /// <summary>Daily jobs whose hour this section saves.</summary>
    public static IReadOnlyList<string> HourJobs { get; } =
        [ScheduledJobKeys.MetadataRefresh, ScheduledJobKeys.CacheEviction, ScheduledJobKeys.Trash, ScheduledJobKeys.Backup];

    private readonly MangaPixerDbContext _db;
    private readonly JobRunRecorder _runs;
    private readonly TimeProvider _time;
    private readonly IServiceProvider _services;
    private readonly AuditService _audit;
    private readonly ILogger<ScheduledJobsService> _logger;

    public ScheduledJobsService(MangaPixerDbContext db, JobRunRecorder runs, TimeProvider time, IServiceProvider services, AuditService audit,
        ILogger<ScheduledJobsService> logger)
    {
        _db = db;
        _runs = runs;
        _time = time;
        _services = services;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>The zone's IANA id (Windows ids converted), <c>UTC</c> for the UTC zone.</summary>
    public static string ZoneId(TimeZoneInfo zone)
    {
        if (zone == TimeZoneInfo.Utc || zone.Id is "UTC" or "Etc/UTC" or "Coordinated Universal Time")
            return "UTC";
        if (zone.HasIanaId)
            return zone.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;
    }

    public async Task<ScheduledJobsDto> GetAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var runs = await _runs.GetAllAsync(ct);
        var settings = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        var jobs = new List<ScheduledJobDto>();

        ScheduledJobDto WithRun(ScheduledJobDto dto)
        {
            if (!runs.TryGetValue(dto.Key, out var run))
                return dto with { Running = _runs.IsRunning(dto.Key) };
            return dto with
            {
                LastStartedAt = run.LastStartedAt,
                LastFinishedAt = run.LastFinishedAt,
                LastOutcome = run.LastOutcome,
                LastDetail = run.LastDetail,
                Running = _runs.IsRunning(dto.Key),
            };
        }

        // Library scans: one row per library.
        var scheduler = _services.GetService<LibraryScanScheduler>();
        var libraries = await _db.Libraries.AsNoTracking().OrderBy(l => l.DisplayName).ToListAsync(ct);
        foreach (var library in libraries)
        {
            var token = LibraryScanSchedules.Resolve(library.ScanSchedule);
            var time = LibraryScanSchedules.TimeOf(token, library.ScanHour, library.ScanWeekday);
            jobs.Add(new ScheduledJobDto
            {
                Key = ScheduledJobKeys.LibraryScan,
                LibraryId = library.PublicId,
                LibraryName = library.DisplayName,
                Kind = time is null ? "interval" : token == LibraryScanSchedules.Weekly ? "weekly" : "daily",
                Enabled = token != LibraryScanSchedules.Off && (scheduler?.Options.Enabled ?? false),
                Configurable = true,
                ScanSchedule = token,
                Hour = time?.Hour,
                Weekday = time?.Weekday is { } d ? (int)d : null,
                IntervalHours = LibraryScanSchedules.IntervalOf(token)?.TotalHours,
                LastFinishedAt = library.LastScanCompleted,
                NextRunAt = scheduler?.EstimateNextScan(library.ScanSchedule, library.LastScanCompleted, library.ScanHour, library.ScanWeekday),
            });
        }

        // Series information refresh.
        var refreshHour = MetadataRefreshSchedule.HourOf(settings?.MetadataRefreshHour);
        var refreshWait = await RefreshWaitAsync(ct);
        runs.TryGetValue(ScheduledJobKeys.MetadataRefresh, out var refreshRun);
        var refreshEnabled = refreshWait is null or "budget_exhausted" or "provider_backoff" or "provider_busy" or "matcher_unavailable";
        jobs.Add(WithRun(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.MetadataRefresh,
            Kind = "daily",
            Enabled = refreshEnabled,
            Configurable = true,
            Hour = refreshHour,
            DefaultHour = ScheduledJobDefaults.MetadataRefreshHour,
            NextRunAt = MetadataRefreshSchedule.NextDue(now, zone, refreshHour, refreshRun?.LastStartedAt, refreshRun?.LastOutcome),
            WaitingCode = refreshWait is "matcher_unavailable" ? null : refreshWait,
        }));

        // Database backup.
        var backups = _services.GetService<BackupSettingsResolver>()?.Current;
        if (backups is not null)
        {
            var state = _services.GetService<RotatingBackupState>();
            runs.TryGetValue(ScheduledJobKeys.Backup, out var backupRun);
            var lastAttempt = Max(state?.LastAttemptUtc, backupRun?.LastStartedAt);
            var hourApplies = backups.Hour is not null && backups.WholeDays is not null;
            jobs.Add(WithRun(new ScheduledJobDto
            {
                Key = ScheduledJobKeys.Backup,
                Kind = hourApplies ? "daily" : "interval",
                Enabled = backups.Enabled,
                Configurable = true,
                ManagedByConfig = backups.HourSource == BackupSettingSources.Configuration,
                Hour = backups.Hour,
                IntervalHours = backups.IntervalHours,
                NextRunAt = backups.Enabled
                    ? RotatingBackupHostedService.NextDue(state?.SchedulerStartedUtc ?? now, now, zone, lastAttempt, backups)
                    : null,
            }));
        }

        // Empty trash + clean bundles.
        var trashHour = TrashSchedule.HourOf(settings?.TrashAutomaticHour);
        var trashOn = settings?.TrashAutoCleanEnabled ?? false;
        jobs.Add(WithRun(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.Trash,
            Kind = "daily",
            Enabled = trashOn,
            Configurable = true,
            Hour = trashHour,
            DefaultHour = TrashSchedule.RunHour,
            NextRunAt = trashOn ? TrashSchedule.NextDue(now, zone, settings!.TrashLastAutoRunAt, settings.TrashAutoCleanEnabledAt, trashHour) : null,
        }));

        // Cache clean-up.
        runs.TryGetValue(ScheduledJobKeys.CacheEviction, out var cacheRun);
        jobs.Add(WithRun(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.CacheEviction,
            Kind = "daily",
            Enabled = true,
            Configurable = true,
            Hour = ScheduledJobDefaults.HourOf(settings?.CacheEvictionHour, ScheduledJobDefaults.CacheEvictionHour),
            DefaultHour = ScheduledJobDefaults.CacheEvictionHour,
            NextRunAt = MaintenanceHostedService.NextCacheEviction(now, zone, settings?.CacheEvictionHour, cacheRun?.LastStartedAt),
        }));

        // Read-only rows.
        var autoState = _services.GetService<MetadataAutoMatchState>();
        var automaticOn = refreshEnabled;
        jobs.Add(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.AutoMatch,
            Kind = "continuous",
            Enabled = automaticOn,
            Configurable = false,
            Running = autoState?.Working ?? false,
            WaitingCode = automaticOn ? autoState?.WaitingCode : refreshWait,
        });
        var volumeState = _services.GetService<VolumeCoverPassState>();
        jobs.Add(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.VolumeCovers,
            Kind = "continuous",
            Enabled = automaticOn && (settings?.MetadataVolumeCoversEnabled ?? true),
            Configurable = false,
            LastStartedAt = volumeState?.LastRunAt,
            WaitingCode = volumeState?.WaitingCode,
        });
        var covers = _services.GetServices<IHostedService>().OfType<CoverDecisionHostedService>().FirstOrDefault();
        jobs.Add(Interval(ScheduledJobKeys.CoverDecisions, covers?.SweepInterval ?? TimeSpan.FromMinutes(10), covers?.SweepEnabled ?? false));
        jobs.Add(Interval(ScheduledJobKeys.ContentSignatures, ContentSignatureBackfillHostedService.PassInterval, true));
        jobs.Add(Interval(ScheduledJobKeys.SessionCleanup, MaintenanceHostedService.SessionInterval, true));
        jobs.Add(WithRun(new ScheduledJobDto { Key = ScheduledJobKeys.Thumbnails, Kind = "startup", Enabled = true, Configurable = false }));
        jobs.Add(new ScheduledJobDto
        {
            Key = ScheduledJobKeys.UpdateCheck,
            Kind = "onDemand",
            Enabled = settings?.UpdateCheckEnabled ?? false,
            Configurable = false,
            LastFinishedAt = settings?.UpdateLastCheckedAt,
        });

        ScheduledJobDto Interval(string key, TimeSpan every, bool enabled)
        {
            runs.TryGetValue(key, out var run);
            return WithRun(new ScheduledJobDto
            {
                Key = key,
                Kind = "interval",
                Enabled = enabled,
                Configurable = false,
                IntervalHours = every.TotalHours,
                NextRunAt = enabled && run?.LastStartedAt is { } last ? last + every : null,
            });
        }

        return new ScheduledJobsDto
        {
            ServerTime = now,
            TimeZone = ZoneId(zone),
            UtcOffsetMinutes = (int)zone.GetUtcOffset(now).TotalMinutes,
            Jobs = jobs,
            Refresh = await RefreshCadenceAsync(ct),
        };
    }

    private async Task<string?> RefreshWaitAsync(CancellationToken ct)
    {
        var autoMatch = _services.GetService<MetadataAutoMatchService>();
        if (autoMatch is null)
            return "automatic_off";
        return (await autoMatch.CheckGlobalGateAsync(ct))?.Code;
    }

    private async Task<RefreshCadenceDto> RefreshCadenceAsync(CancellationToken ct)
    {
        var refresh = _services.GetService<MetadataRefreshService>();
        var policy = refresh is null ? RefreshCadencePolicy.Default : await refresh.PolicyAsync(ct);
        var (overdue, byDays) = refresh is null ? (0, new Dictionary<int, int>()) : await refresh.SummaryAsync(ct);
        return new RefreshCadenceDto
        {
            OngoingDays = policy.OngoingDays,
            FinishedDays = policy.FinishedDays,
            FollowPace = policy.FollowPace,
            AllowedOngoingDays = RefreshCadencePolicy.AllowedOngoingDays,
            AllowedFinishedDays = RefreshCadencePolicy.AllowedFinishedDays,
            UsedToday = refresh is null ? 0 : await refresh.UsedTodayAsync(ct),
            MaxPerDay = MetadataRefreshService.MaxPerDay,
            Overdue = overdue,
            ByDays = byDays.OrderBy(p => p.Key).Select(p => new RefreshCadenceCountDto { Days = p.Key, Count = p.Value }).ToList(),
        };
    }

    /// <summary>
    /// Saves the hour of a daily job; null = its default (backups: any time). Returns an error code: <c>invalid_job</c>,
    /// <c>not_configurable</c>, <c>invalid_hour</c>, <c>managed_by_config</c>.
    /// </summary>
    public async Task<string?> SetHourAsync(string key, int? hour, string? actor, CancellationToken ct)
    {
        if (!ScheduledJobKeys.Ordered.Contains(key, StringComparer.Ordinal))
            return "invalid_job";
        if (!HourJobs.Contains(key, StringComparer.Ordinal))
            return "not_configurable";
        if (hour is { } h && !Core.Scheduling.JobSchedule.IsValidHour(h))
            return "invalid_hour";

        if (key == ScheduledJobKeys.Trash)
        {
            var trash = _services.GetRequiredService<TrashService>();
            var (_, error) = await trash.UpdateSettingsAsync(new UpdateTrashSettingsRequest { AutomaticHour = hour ?? TrashSchedule.RunHour }, actor, ct);
            return error;
        }

        var backups = _services.GetService<BackupSettingsResolver>();
        if (key == ScheduledJobKeys.Backup && backups?.Configuration.Hour is not null)
            return "managed_by_config";

        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            _db.AppSettings.Add(row);
        }
        var before = key switch
        {
            ScheduledJobKeys.MetadataRefresh => row.MetadataRefreshHour,
            ScheduledJobKeys.CacheEviction => row.CacheEvictionHour,
            _ => row.BackupHour,
        };
        if (before == hour)
            return null;
        switch (key)
        {
            case ScheduledJobKeys.MetadataRefresh: row.MetadataRefreshHour = hour; break;
            case ScheduledJobKeys.CacheEviction: row.CacheEvictionHour = hour; break;
            default: row.BackupHour = hour; break;
        }
        await _db.SaveChangesAsync(ct);
        if (key == ScheduledJobKeys.Backup && backups is not null)
            await backups.ReloadAsync(_db, ct); // wakes the backup scheduler
        await _audit.RecordAsync(AuditActions.JobScheduleChange,
            string.Create(CultureInfo.InvariantCulture, $"{key}_h{(hour is { } v ? v.ToString(CultureInfo.InvariantCulture) : "default")}"), actor, ct: ct);
        _logger.LogInformation(LogEvents.Administration.JobScheduleChanged, "Scheduled job {Job} hour set to {Hour}", key, hour);
        return null;
    }

    /// <summary>Saves the refresh cadence and recomputes every linked series' cadence. Error code <c>invalid_cadence</c>.</summary>
    public async Task<string?> SetCadenceAsync(UpdateRefreshCadenceRequest request, string? actor, CancellationToken ct)
    {
        if (request.OngoingDays is { } o && !RefreshCadencePolicy.AllowedOngoingDays.Contains(o))
            return "invalid_cadence";
        if (request.FinishedDays is { } f && !RefreshCadencePolicy.AllowedFinishedDays.Contains(f))
            return "invalid_cadence";

        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            _db.AppSettings.Add(row);
        }
        var before = RefreshCadencePolicy.FromStored(row.MetadataRefreshOngoingDays, row.MetadataRefreshFinishedDays, row.MetadataRefreshFollowPace);
        if (request.OngoingDays is { } ongoing)
            row.MetadataRefreshOngoingDays = ongoing;
        if (request.FinishedDays is { } finished)
            row.MetadataRefreshFinishedDays = finished;
        if (request.FollowPace is { } pace)
            row.MetadataRefreshFollowPace = pace;
        var after = RefreshCadencePolicy.FromStored(row.MetadataRefreshOngoingDays, row.MetadataRefreshFinishedDays, row.MetadataRefreshFollowPace);
        await _db.SaveChangesAsync(ct);
        if (before == after)
            return null;

        if (_services.GetService<MetadataRefreshService>() is { } refresh)
            await refresh.RecomputeCadencesAsync(ct);
        await _audit.RecordAsync(AuditActions.MetadataRefreshCadenceChange,
            string.Create(CultureInfo.InvariantCulture, $"o{after.OngoingDays}_f{after.FinishedDays}_{(after.FollowPace ? "pace" : "fixed")}"), actor, ct: ct);
        _logger.LogInformation(LogEvents.Administration.JobScheduleChanged, "Refresh cadence set: ongoing {Ongoing} days, finished {Finished} days, pace {Pace}",
            after.OngoingDays, after.FinishedDays, after.FollowPace);
        return null;
    }

    /// <summary>The refresh cadence of the series linked on <paramref name="linkNodePublicId"/> (Confirmed / Auto), or null.</summary>
    public async Task<SeriesRefreshCadenceDto?> SeriesCadenceAsync(string linkNodePublicId, CancellationToken ct)
    {
        var linked = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };
        var record = await (
            from n in _db.CatalogNodes.AsNoTracking()
            join l in _db.NodeSeriesLinks.AsNoTracking() on n.Id equals l.NodeId
            join r in _db.MetadataRecords on l.RecordId equals r.Id
            where n.PublicId == linkNodePublicId && linked.Contains(l.State)
            select r).AsNoTracking().FirstOrDefaultAsync(ct);
        if (record is null || _services.GetService<MetadataRefreshService>() is not { } refresh)
            return null;
        var result = RefreshCadence.For(await refresh.PolicyAsync(ct), await refresh.EvidenceAsync(record, ct), _time.GetUtcNow());
        return new SeriesRefreshCadenceDto
        {
            Days = result.Days,
            Reason = result.Reason switch
            {
                RefreshCadenceReason.Finished => "finished",
                RefreshCadenceReason.Pace => "pace",
                RefreshCadenceReason.Paused => "paused",
                _ => "choice",
            },
            VolumeIntervalDays = result.VolumeIntervalDays is { } v ? Math.Round(v, 1) : null,
            FetchedAt = record.FetchedAt,
            NextCheckAt = record.FetchedAt + TimeSpan.FromDays(result.Days),
        };
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : (a > b ? a : b);
}
