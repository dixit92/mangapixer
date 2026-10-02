namespace com.lifepixer.mangapixer.Server.Hosting;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Scheduling;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Periodic maintenance: hourly expired-session cleanup and the daily cache clean-up (over-budget eviction), plus a one-off
/// background deletion at startup of the cache files earlier runs left behind (1.24.1). 1.32.0: a loop on the registered
/// <see cref="TimeProvider"/>; the cache clean-up runs once a day at the admin's hour (<c>app_settings.CacheEvictionHour</c>,
/// default <see cref="ScheduledJobDefaults.CacheEvictionHour"/>:00 server time) with its last run persisted in <c>job_runs</c>, so a
/// restart no longer restarts its clock (a slot missed while the server was down is caught up once). Failures are logged as
/// warnings and do not crash the host.
/// </summary>
public sealed class MaintenanceHostedService : BackgroundService
{
    public static readonly TimeSpan SessionInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan SessionStartDelay = TimeSpan.FromMinutes(1);

    /// <summary>A caught-up cache clean-up waits this long after start-up.</summary>
    public static readonly TimeSpan CacheStartDelay = TimeSpan.FromMinutes(5);

    /// <summary>The loop re-reads the hour at least this often, so a change applies without a restart.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly ILogger<MaintenanceHostedService> _logger;
    private readonly TimeProvider _time;
    private readonly JobRunRecorder? _runs;

    public MaintenanceHostedService(
        IServiceProvider services,
        ILogger<MaintenanceHostedService> logger,
        TimeProvider? time = null,
        JobRunRecorder? runs = null)
    {
        _services = services;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _runs = runs;
    }

    /// <summary>When the next cache clean-up is due for the stored hour and last run.</summary>
    public static DateTimeOffset NextCacheEviction(DateTimeOffset nowUtc, TimeZoneInfo zone, int? storedHour, DateTimeOffset? lastStartedUtc) =>
        JobSchedule.NextDue(nowUtc, zone,
            new TimeOfDaySchedule(ScheduledJobDefaults.HourOf(storedHour, ScheduledJobDefaults.CacheEvictionHour)), lastStartedUtc);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: reading starts at once in this run's own cache folder while the old ones are deleted.
        _ = Task.Run(() => RunSafeAsync(DeleteStaleCacheRunsAsync, "stale cache cleanup"), CancellationToken.None);

        var started = _time.GetUtcNow();
        var nextSession = started + SessionStartDelay;
        var cacheEarliest = started + CacheStartDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            var wait = CheckInterval;
            try
            {
                if (now >= nextSession)
                {
                    await RunSafeAsync(CleanupSessionsAsync, "session cleanup");
                    nextSession = _time.GetUtcNow() + SessionInterval;
                }
                if (nextSession - now < wait)
                    wait = nextSession - now;

                var cacheDue = await CacheEvictionDueAsync(now, stoppingToken);
                if (cacheDue < cacheEarliest)
                    cacheDue = cacheEarliest;
                if (cacheDue <= now)
                {
                    await RunSafeAsync(RunCacheEvictionAsync, "cache eviction");
                    continue;
                }
                if (cacheDue - now < wait)
                    wait = cacheDue - now;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Backup.MaintenanceFailed, "Maintenance {Label} failed: {Error}", "schedule", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<DateTimeOffset> CacheEvictionDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        int? hour;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            hour = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
                .Select(s => s.CacheEvictionHour).FirstOrDefaultAsync(ct);
        }
        var last = _runs is null ? null : await _runs.LastStartedAsync(ScheduledJobKeys.CacheEviction, ct);
        return NextCacheEviction(now, _time.LocalTimeZone, hour, last);
    }

    private async Task RunSafeAsync(Func<Task> work, string label)
    {
        // Never throws into the host.
        try { await work(); }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Backup.MaintenanceFailed, "Maintenance {Label} failed: {Error}", label, ex.GetType().Name);
        }
    }

    private async Task CleanupSessionsAsync()
    {
        var started = _runs is null ? _time.GetUtcNow() : await _runs.StartedAsync(ScheduledJobKeys.SessionCleanup, CancellationToken.None);
        var outcome = JobOutcomes.Failed;
        try
        {
            using var scope = _services.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            await sessions.CleanupExpiredSessionsAsync();
            outcome = JobOutcomes.Ok;
        }
        finally
        {
            if (_runs is not null)
                await _runs.FinishedAsync(ScheduledJobKeys.SessionCleanup, started, outcome, null, CancellationToken.None);
        }
    }

    private Task DeleteStaleCacheRunsAsync()
    {
        var cache = _services.GetRequiredService<CacheService>();
        cache.DeleteStaleRuns();
        return Task.CompletedTask;
    }

    private async Task RunCacheEvictionAsync()
    {
        // Recorded first (the floor), so a failing clean-up is not retried until the next day.
        var started = _runs is null ? _time.GetUtcNow() : await _runs.StartedAsync(ScheduledJobKeys.CacheEviction, CancellationToken.None);
        var outcome = JobOutcomes.Failed;
        string? detail = null;
        try
        {
            using var scope = _services.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<CacheService>();
            // Routine over-budget eviction — does not log a disk-full warning
            // (audit defect D27). HandleDiskFull is reserved for real IOException
            // disk-full paths.
            var freed = cache.EvictOverBudget();
            outcome = JobOutcomes.Ok;
            detail = string.Create(CultureInfo.InvariantCulture, $"{freed / (1024 * 1024)} MB freed");
            _logger.LogInformation(LogEvents.Administration.CacheEvictionPass, "Cache clean-up: {Bytes} bytes freed", freed);
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name;
            throw;
        }
        finally
        {
            if (_runs is not null)
                await _runs.FinishedAsync(ScheduledJobKeys.CacheEviction, started, outcome, detail, CancellationToken.None);
        }
    }
}
