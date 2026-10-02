namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Scheduling;
using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Drains the automatic-matching queue (metadata stage 2), modelled on the scan
/// scheduler: waits <see cref="MetadataAutoMatchOptions.StartupDelay"/> after boot,
/// then runs a pass on every <see cref="MetadataAutoMatchOptions.TickInterval"/>
/// tick or <see cref="MetadataAutoMatchState.Signal"/> (post-scan, bulk, re-run).
/// Single flight (one loop), one work at a time, each in its own DI scope; the
/// gateway paces automatic requests at &lt;= 1/s. When the automatic gate is closed
/// (switch off, backoff, budget spent until the next UTC day) the pass stops and
/// the loop sleeps until the next tick - leased rows are released, and a crash
/// leaves only leases that expire and are picked up again. Failures are logged by
/// type only and never crash the host. 1.29.0: after the matching pass (and the
/// refresh check) each tick also runs one time-sliced tick of the volume-cover pass.
/// 1.32.0: the id-only refresh is a daily job at the admin's hour
/// (<see cref="MetadataRefreshSchedule"/>), its last run persisted in <c>job_runs</c>, so a
/// restart neither runs it again nor skips a day.
/// </summary>
public sealed class MetadataAutoMatchHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MetadataAutoMatchState _state;
    private readonly MetadataAutoMatchOptions _options;
    private readonly ILogger<MetadataAutoMatchHostedService> _logger;
    private readonly JobRunRecorder? _runs;
    private readonly TimeProvider _time;
    private DateTimeOffset _startedAt;
    private readonly string _owner = "automatch-" + Environment.ProcessId;

    public MetadataAutoMatchHostedService(
        IServiceScopeFactory scopeFactory,
        MetadataAutoMatchState state,
        MetadataAutoMatchOptions options,
        ILogger<MetadataAutoMatchHostedService> logger,
        JobRunRecorder? runs = null,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _options = options;
        _logger = logger;
        _runs = runs;
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
            return;
        _startedAt = _time.GetUtcNow();
        try
        {
            if (_options.StartupDelay > TimeSpan.Zero)
                await Task.Delay(_options.StartupDelay, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);
                if (await RefreshDueAsync(stoppingToken))
                    await RunRefreshAsync(stoppingToken);
                await RunVolumeCoversAsync(stoppingToken);
                await _state.WaitAsync(_options.TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass: promote due retries, queue the works in review the matcher's current rules have not scored (once per revision), then process works until the queue is empty or the gate closes.</summary>
    public async Task<int> RunPassAsync(CancellationToken ct)
    {
        var processed = 0;
        try
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<MetadataAutoMatchService>();
                await queue.PromoteDueRetriesAsync(ct);
                await queue.QueueOutdatedReviewsAsync(ct);
            }

            while (!ct.IsCancellationRequested)
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MetadataAutoMatchService>();
                if (await service.CheckGlobalGateAsync(ct) is { } wait)
                {
                    if (_state.WaitingCode != wait.Code)
                        _logger.LogInformation(LogEvents.Metadata.AutoMatchWaiting, "Automatic matching waiting: {Code}", wait.Code);
                    _state.SetStatus(wait.Code, wait.Until, working: false);
                    break;
                }
                var row = await service.LeaseNextAsync(_owner, ct);
                if (row is null)
                {
                    _state.SetStatus(null, null, working: false);
                    break;
                }
                _state.SetStatus(null, null, working: true);
                try
                {
                    await service.ProcessAsync(row, ct);
                    processed++;
                }
                catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
                {
                    // Released by ProcessAsync; the gate check above reports why on the next loop.
                    _state.SetStatus(ex.Code, ex.RetryAt, working: false);
                    if (ex.Code is "library_metadata_disabled" or "provider_busy")
                        continue;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _state.SetStatus("error", null, working: false);
            _logger.LogWarning(LogEvents.Metadata.AutoMatchFailed, "Automatic matching pass failed: {Error}", ex.GetType().Name);
        }
        return processed;
    }

    /// <summary>
    /// One tick of the volume-cover / volume-list pass (1.29.0): at most <see cref="Features.Metadata.Volumes.VolumeCoverPass.SliceRequests"/>
    /// requests, so the next matching pass is never far away. Failures are logged by type only.
    /// </summary>
    public async Task RunVolumeCoversAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<Features.Metadata.Volumes.VolumeCoverPass>().RunTickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Metadata.VolumeCoverPass, "Volume cover pass failed: {Error}", ex.GetType().Name);
        }
    }

    /// <summary>True when the daily refresh is due now (the admin's hour passed since its last run, or a waiting run's retry).</summary>
    public async Task<bool> RefreshDueAsync(CancellationToken ct)
    {
        try
        {
            int? stored;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
                stored = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
                    .Select(s => s.MetadataRefreshHour).FirstOrDefaultAsync(ct);
            }
            var last = _runs is null ? null : await _runs.GetAsync(ScheduledJobKeys.MetadataRefresh, ct);
            var now = _time.GetUtcNow();
            // Never run yet (a fresh install or the upgrade): the floor is this start, so the first slot after it is not skipped.
            return MetadataRefreshSchedule.NextDue(now, _time.LocalTimeZone, MetadataRefreshSchedule.HourOf(stored), last?.LastStartedAt ?? _startedAt,
                last?.LastOutcome) <= now;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Metadata.RefreshFailed, "Metadata refresh schedule check failed: {Error}", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>One refresh pass, recorded as the <c>metadata-refresh</c> job's run. Failures are logged by type only.</summary>
    public async Task RunRefreshAsync(CancellationToken ct)
    {
        var started = _runs is null ? _time.GetUtcNow() : await _runs.StartedAsync(ScheduledJobKeys.MetadataRefresh, ct);
        string outcome = JobOutcomes.Failed;
        string? detail = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<MetadataRefreshService>().RunPassAsync(ct);
            (outcome, detail) = MetadataRefreshSchedule.Describe(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = JobOutcomes.Skipped;
            detail = "stopped";
            throw;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            _logger.LogWarning(LogEvents.Metadata.RefreshFailed, "Metadata refresh pass failed: {Error}", ex.GetType().Name);
        }
        finally
        {
            if (_runs is not null)
                await _runs.FinishedAsync(ScheduledJobKeys.MetadataRefresh, started, outcome, detail, CancellationToken.None);
        }
    }
}

/// <summary>
/// When the daily series information refresh runs (1.32.0): at the admin's hour (<c>app_settings.MetadataRefreshHour</c>, default
/// <see cref="ScheduledJobDefaults.MetadataRefreshHour"/>:00 server time) on <see cref="JobSchedule"/>, its floor the last run's start.
/// A run the gate stopped (backoff, budget spent, switch off) is tried again every <see cref="RetryAfter"/> until the day ends.
/// </summary>
public static class MetadataRefreshSchedule
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public static int HourOf(int? stored) => ScheduledJobDefaults.HourOf(stored, ScheduledJobDefaults.MetadataRefreshHour);

    public static DateTimeOffset NextDue(DateTimeOffset nowUtc, TimeZoneInfo zone, int hour, DateTimeOffset? lastStartedUtc, string? lastOutcome)
    {
        var due = JobSchedule.NextDue(nowUtc, zone, new TimeOfDaySchedule(hour), lastStartedUtc);
        if (lastOutcome == JobOutcomes.Waiting && lastStartedUtc is { } started
            && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, zone).DateTime) == DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime))
        {
            var retry = started + RetryAfter;
            if (retry < due)
                due = retry < nowUtc ? nowUtc : retry;
        }
        return due;
    }

    /// <summary>The run's outcome and its counts-only detail.</summary>
    public static (string Outcome, string Detail) Describe(RefreshPassResult result) => result.StoppedCode switch
    {
        null => (JobOutcomes.Ok, string.Create(CultureInfo.InvariantCulture, $"{result.Refreshed} refreshed")),
        "refresh_cap" => (JobOutcomes.Ok, string.Create(CultureInfo.InvariantCulture,
            $"{result.Refreshed} refreshed, {Math.Max(0, result.Due - result.Refreshed)} left for the next day")),
        var code => (JobOutcomes.Waiting, string.Create(CultureInfo.InvariantCulture, $"{result.Refreshed} refreshed, waiting: {code}")),
    };
}
