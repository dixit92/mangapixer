namespace com.lifepixer.mangaplex.Server.Hosting;

using com.lifepixer.mangaplex.Server.Logging;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// One-time startup backfill pass (1.2.0): generates durable thumbnails for
/// ready archive items that lack a current thumbnail. This catches items that
/// were analyzed before the persistent-thumbnail feature existed (e.g., an
/// existing install upgrading to 1.2.0). The pass is bounded and runs in the
/// background at low priority — it must not block API startup or starve
/// interactive reader page requests.
///
/// The primary thumbnail-generation path is analysis-time (hooked in
/// <see cref="MediaWorkerPool"/>); this backfill is a safety net for
/// pre-existing data only.
/// </summary>
public sealed class ThumbnailBackfillHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ThumbnailBackfillHostedService> _logger;

    /// <summary>
    /// Maximum items to backfill per startup pass. Bounded so a very large
    /// library does not flood the worker pool on restart; the admin
    /// regenerate endpoint handles the rest on demand.
    /// </summary>
    internal const int StartupBackfillLimit = 500;

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
        _ = Task.Run(() => RunBackfillAsync(), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunBackfillAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var thumbnailService = scope.ServiceProvider.GetRequiredService<ThumbnailGenerationService>();

            var libraries = await db.Libraries
                .Where(l => l.State == "active")
                .Select(l => l.Id)
                .ToListAsync();

            var totalQueued = 0;
            foreach (var libraryId in libraries)
            {
                var items = await ThumbnailGenerationService.GetItemsNeedingThumbnailsAsync(
                    db, libraryId, limit: StartupBackfillLimit, CancellationToken.None);
                if (items.Count == 0)
                    continue;

                _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillBatch,
                    "Startup thumbnail backfill: {Count} items in library {LibraryId}", items.Count, libraryId);

                foreach (var nodeId in items)
                {
                    try
                    {
                        await thumbnailService.GenerateForItemAsync(nodeId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed,
                            ex, "Startup thumbnail backfill failed (item {ItemId}): {Error}", nodeId, ex.GetType().Name);
                    }
                }

                totalQueued += items.Count;
            }

            if (totalQueued > 0)
                _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillEnqueued,
                    "Startup thumbnail backfill complete: {Count} thumbnails generated", totalQueued);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed,
                ex, "Startup thumbnail backfill failed: {Error}", ex.GetType().Name);
        }
    }
}
