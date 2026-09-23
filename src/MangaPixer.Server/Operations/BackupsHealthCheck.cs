namespace com.lifepixer.mangapixer.Server.Operations;

using com.lifepixer.mangapixer.Server.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Readiness check "backups" (tag <c>ready</c>). Reports <b>Degraded</b> (HTTP
/// 200 under the default status mapping, so an orchestrator does not restart
/// the container over it) while the backup location is unavailable / invalid,
/// or while scheduled backups are enabled but the last success is older than
/// twice the interval. Reads in-memory state only: no I/O on the health path.
/// </summary>
public sealed class BackupsHealthCheck : IHealthCheck
{
    private readonly BackupSettingsResolver _settings;
    private readonly RotatingBackupState _state;
    private readonly TimeProvider _time;

    public BackupsHealthCheck(BackupSettingsResolver settings, RotatingBackupState state, TimeProvider time)
    {
        _settings = settings;
        _state = state;
        _time = time;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = _state.LocationStatus;
        if (status is BackupLocationStatuses.Unavailable or BackupLocationStatuses.Invalid)
            return Task.FromResult(HealthCheckResult.Degraded($"Backup location {status}"));

        var settings = _settings.Current;
        if (settings.Enabled && _state.SchedulerStartedUtc is { } started)
        {
            var reference = _state.LastSuccessUtc ?? started + RotatingBackupHostedService.InitialDelay;
            if (_time.GetUtcNow() - reference > 2 * settings.Interval)
                return Task.FromResult(HealthCheckResult.Degraded("Last successful backup is overdue"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Backups ok"));
    }
}
