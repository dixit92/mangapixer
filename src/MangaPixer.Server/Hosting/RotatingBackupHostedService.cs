namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Operations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Scheduled rotating database backups. Takes a consistent online snapshot
/// (VACUUM INTO) every configured interval (default daily) and prunes the
/// oldest rotating snapshots beyond the retention count (default 7).
/// Pre-migration backups are never pruned (see RotatingBackupService).
/// Failures are logged as warnings and do not crash the host.
/// </summary>
public sealed class RotatingBackupHostedService : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly RotatingBackupOptions _options;
    private readonly ILogger<RotatingBackupHostedService> _logger;
    private Timer? _timer;

    public RotatingBackupHostedService(
        IServiceProvider services,
        RotatingBackupOptions options,
        ILogger<RotatingBackupHostedService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(LogEvents.Backup.RotatingDisabled, "Rotating database backups are disabled by configuration.");
            return Task.CompletedTask;
        }

        // First run shortly after startup (let startup recovery finish), then
        // on the configured interval.
        _timer = new Timer(_ => RunSafe(), null,
            TimeSpan.FromMinutes(2), _options.Interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RunSafe()
    {
        // Timer callbacks must never throw unhandled into the host.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var rotating = scope.ServiceProvider.GetRequiredService<RotatingBackupService>();
                var outcome = await rotating.RunAsync();
                if (!outcome.Succeeded)
                    _logger.LogWarning(LogEvents.Backup.ScheduledRotatingFailed, "Scheduled rotating backup failed (retained {Count}).", outcome.RetainedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Backup.ScheduledRotatingError, ex, "Scheduled rotating backup failed: {Error}", ex.GetType().Name);
            }
        });
    }
}
