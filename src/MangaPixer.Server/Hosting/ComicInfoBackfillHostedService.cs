namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Startup kick for the ComicInfo backfill (1.24.0), the sibling of
/// <see cref="ThumbnailBackfillHostedService"/>: fire-and-forget so API startup is
/// never blocked, registered after the worker pool's hosted service. The pass
/// itself lives in <see cref="ComicInfoBackfillService"/> (single-flight,
/// saturation-aware); post-scan kicks come from the scan launcher.
/// </summary>
public sealed class ComicInfoBackfillHostedService : IHostedService
{
    private readonly ComicInfoBackfillService _backfill;
    private readonly ILogger<ComicInfoBackfillHostedService> _logger;

    public ComicInfoBackfillHostedService(
        ComicInfoBackfillService backfill,
        ILogger<ComicInfoBackfillHostedService> logger)
    {
        _backfill = backfill;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The pass observes the host's ApplicationStopping token itself.
        _logger.LogDebug(LogEvents.Metadata.ComicInfoBackfillStarted, "ComicInfo backfill: startup pass requested");
        _ = _backfill.RequestRun();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
