namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Scheduled rotating database backups. Takes a consistent online snapshot
/// (VACUUM INTO) every effective interval (default daily) and prunes the
/// oldest rotating snapshots beyond the retention count (default 7).
///
/// A loop on the registered <see cref="TimeProvider"/>, not a fixed timer:
/// every iteration re-reads the effective settings, and a settings change
/// (<see cref="BackupSettingsResolver.ChangeToken"/>) wakes it, so enabling,
/// disabling, or changing the interval / location applies without a restart.
/// Next due = max(start + initial delay, last attempt + interval); the last
/// attempt is seeded from the newest snapshot name so frequent restarts do not
/// each take a backup and rotate out the daily history.
/// Failures are logged (type / code only) and never crash the host.
/// </summary>
public sealed class RotatingBackupHostedService : BackgroundService
{
    /// <summary>Let startup recovery finish before the first backup.</summary>
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly BackupSettingsResolver _settings;
    private readonly RotatingBackupState _state;
    private readonly TimeProvider _time;
    private readonly ILogger<RotatingBackupHostedService> _logger;

    public RotatingBackupHostedService(
        IServiceProvider services,
        BackupSettingsResolver settings,
        RotatingBackupState state,
        TimeProvider time,
        ILogger<RotatingBackupHostedService> logger)
    {
        _services = services;
        _settings = settings;
        _state = state;
        _time = time;
        _logger = logger;
    }

    /// <summary>Next scheduled run for the given start, last attempt, and interval.</summary>
    public static DateTimeOffset NextDue(DateTimeOffset startedUtc, DateTimeOffset? lastAttemptUtc, TimeSpan interval)
    {
        var earliest = startedUtc + InitialDelay;
        if (lastAttemptUtc is not { } last)
            return earliest;
        var due = last + interval;
        return due > earliest ? due : earliest;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = _time.GetUtcNow();
        _state.SchedulerStartedUtc = started;

        foreach (var key in _settings.Configuration.InvalidKeys)
            _logger.LogWarning(LogEvents.Backup.BackupConfigInvalid,
                "Ignoring invalid backup configuration value for {Key}", key);

        DateTimeOffset? seeded = null;
        try
        {
            await _settings.ReloadAsync(stoppingToken);
            // Startup location check so the status is right before the first run.
            using var scope = _services.CreateScope();
            var location = scope.ServiceProvider.GetRequiredService<BackupLocationService>();
            var check = await location.CheckAsync(stoppingToken, startup: true);
            if (check.IsValid && check.NormalizedLocation is not null)
                seeded = RotatingBackupService.NewestSnapshotTimestamp(check.NormalizedLocation);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Backup.ScheduledRotatingError,
                "Backup settings could not be loaded at startup: {Error}", ex.GetType().Name);
        }

        if (!_settings.Current.Enabled)
            _logger.LogInformation(LogEvents.Backup.RotatingDisabled, "Scheduled rotating database backups are disabled.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = _settings.ChangeToken;
            var settings = _settings.Current;

            var lastAttempt = Max(seeded, _state.LastAttemptUtc);
            var wait = settings.Enabled
                ? NextDue(started, lastAttempt, settings.Interval) - _time.GetUtcNow()
                : Timeout.InfiniteTimeSpan;

            if (wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.Zero)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, changed);
                try
                {
                    await Task.Delay(wait, _time, linked.Token);
                }
                catch (OperationCanceledException)
                {
                    if (stoppingToken.IsCancellationRequested)
                        return;
                    continue; // settings changed: recompute
                }
                if (_settings.ChangeToken != changed)
                    continue;
            }

            await RunOnceAsync(stoppingToken);
            seeded = _time.GetUtcNow();
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var rotating = scope.ServiceProvider.GetRequiredService<RotatingBackupService>();
            var outcome = await rotating.RunAsync(ct);
            if (!outcome.Succeeded)
                _logger.LogWarning(LogEvents.Backup.ScheduledRotatingFailed,
                    "Scheduled rotating backup failed: {Code} (retained {Count}).", outcome.FailureCode ?? "backup_failed", outcome.RetainedCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Type only: an exception message can carry an absolute path.
            _logger.LogWarning(LogEvents.Backup.ScheduledRotatingError, "Scheduled rotating backup failed: {Error}", ex.GetType().Name);
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
