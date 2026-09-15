namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Logging;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Continuous startup backfill pass (post-1.2.0): generates durable thumbnails
/// for ALL ready archive items that lack a current thumbnail, in bounded batches,
/// until none remain. This replaces the 1.2.0 fixed 500-item cap so a large
/// library fully backfills from a single startup without repeated restarts or
/// the manual admin button.
///
/// The pass is throttled (saturation-aware): it yields when the worker pool is
/// saturated or analysis work is pending, so interactive reader page requests
/// are not starved. Each item is attempted at most once per pass; persistent
/// failures do not loop forever. The pass is idempotent and stateless — it
/// re-queries what is missing on every batch.
///
/// The primary thumbnail-generation path is analysis-time (hooked in
/// <see cref="MediaWorkerPool"/>); this backfill is a safety net for
/// pre-existing data (items analyzed before the persistent-thumbnail feature,
/// or whose source changed). It coordinates with
/// <see cref="PendingAnalysisResumeHostedService"/>: that service re-enqueues
/// pending analysis on startup, and each analysis generates its thumbnail on
/// completion — so on a fresh large library, analysis-resume + analysis-time
/// generation already produce most thumbnails. This backfill mops up
/// ready-but-missing stragglers without double-generating (the per-item
/// generator is idempotent).
/// </summary>
public sealed class ThumbnailBackfillHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ThumbnailBackfillHostedService> _logger;
    private CancellationTokenSource? _stopCts;

    public ThumbnailBackfillHostedService(
        IServiceProvider services,
        ILogger<ThumbnailBackfillHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: do not block API startup. The worker pool is
        // started by MediaWorkerHostedService (registered before this service),
        // so it is available by the time this task runs.
        _stopCts = new CancellationTokenSource();
        _ = Task.Run(() => RunBackfillAsync(_stopCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal the continuous loop to stop promptly on shutdown.
        try { _stopCts?.Cancel(); } catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var thumbnailService = scope.ServiceProvider.GetRequiredService<ThumbnailGenerationService>();

            var libraries = await db.Libraries
                .Where(l => l.State == "active")
                .Select(l => l.Id)
                .ToListAsync(ct);

            var totalAttempted = 0;
            foreach (var libraryId in libraries)
            {
                ct.ThrowIfCancellationRequested();

                var beforeCount = await ThumbnailGenerationService.CountItemsNeedingThumbnailsAsync(db, libraryId, ct);
                if (beforeCount == 0)
                    continue;

                _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillBatch,
                    "Startup thumbnail backfill: {Count} items need thumbnails in library {LibraryId}", beforeCount, libraryId);

                var attempted = await thumbnailService.RunContinuousBackfillAsync(libraryId, ct);
                totalAttempted += attempted;

                _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillBatch,
                    "Startup thumbnail backfill: processed {Attempted} items in library {LibraryId}", attempted, libraryId);
            }

            if (totalAttempted > 0)
                _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillEnqueued,
                    "Startup thumbnail backfill complete: {Count} thumbnails processed", totalAttempted);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed,
                ex, "Startup thumbnail backfill failed: {Error}", ex.GetType().Name);
        }
    }
}
