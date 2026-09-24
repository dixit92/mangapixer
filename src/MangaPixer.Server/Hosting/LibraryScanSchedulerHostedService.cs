namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Automatic per-library scans (1.23.0). Waits
/// <see cref="LibraryScanSchedulerOptions.StartupDelay"/> after boot so startup
/// recovery and the worker pool settle first (no scan burst at startup), then
/// runs one <see cref="LibraryScanScheduler.EvaluateAsync"/> pass per
/// <see cref="LibraryScanSchedulerOptions.TickInterval"/> on the registered
/// <see cref="TimeProvider"/>. Overdue libraries are scanned one after another
/// within a pass. Failures are logged (type only) and never crash the host.
/// </summary>
public sealed class LibraryScanSchedulerHostedService : BackgroundService
{
    private readonly LibraryScanScheduler _scheduler;
    private readonly TimeProvider _time;
    private readonly ILogger<LibraryScanSchedulerHostedService> _logger;

    public LibraryScanSchedulerHostedService(
        LibraryScanScheduler scheduler,
        TimeProvider time,
        ILogger<LibraryScanSchedulerHostedService> logger)
    {
        _scheduler = scheduler;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _scheduler.Options;
        if (!options.Enabled)
        {
            _logger.LogInformation(LogEvents.Scanning.ScheduledScanDisabled, "Scheduled library scans are disabled by configuration.");
            return;
        }

        _scheduler.FirstEvaluationUtc = _time.GetUtcNow() + options.StartupDelay;
        try
        {
            if (options.StartupDelay > TimeSpan.Zero)
                await Task.Delay(options.StartupDelay, _time, stoppingToken);

            using var timer = new PeriodicTimer(options.TickInterval, _time);
            do
            {
                try
                {
                    await _scheduler.EvaluateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Type only: an exception message can carry an absolute path.
                    _logger.LogWarning(LogEvents.Scanning.ScheduledScanEvaluationFailed,
                        "Scheduled scan evaluation failed: {Error}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
