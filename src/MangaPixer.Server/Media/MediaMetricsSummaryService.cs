namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Writes one Debug line with the media metrics (1.42.0 profiling) every <see cref="Interval"/> while something
/// happened since the last line: worker use, slot waits, cache hits / misses / evictions, 503 / 499 counts. Kinds,
/// counts and durations only. Nothing is written while the Debug level is off or the server is idle.
/// </summary>
public sealed class MediaMetricsSummaryService(MediaMetrics metrics, ILogger<MediaMetricsSummaryService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long lastStamp = -1;
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                lastStamp = WriteIfActive(lastStamp);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Writes the line when the activity stamp moved; returns the stamp to compare with next time.</summary>
    internal long WriteIfActive(long lastStamp)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return lastStamp;
        try
        {
            var stamp = metrics.ActivityStamp();
            if (stamp == lastStamp)
                return lastStamp;
            logger.LogDebug(LogEvents.Worker.MediaMetricsSummary, "Media metrics: {Summary}", metrics.SummaryLine());
            return stamp;
        }
        catch (Exception ex)
        {
            logger.LogDebug(LogEvents.Worker.MediaMetricsSummaryFailed, "Media metrics summary failed: {Error}", ex.GetType().Name);
            return lastStamp;
        }
    }
}
