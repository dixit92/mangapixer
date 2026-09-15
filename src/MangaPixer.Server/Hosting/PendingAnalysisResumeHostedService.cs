namespace com.lifepixer.mangapixer.Server.Hosting;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Startup pass (1.2.0) that re-enqueues analysis for archive items left in the
/// pending state (<c>AnalysisState == 1</c>).
///
/// Why this exists: analysis jobs live in the in-memory <see cref="JobScheduler"/>
/// queue, not as persisted <c>JobEntity</c> rows, so a process restart (e.g. a
/// deploy) drops any not-yet-started analysis. On a large library mid-analysis
/// that strands thousands of items pending forever — no manifest, no cover, and
/// (since thumbnails are generated at analysis time) no thumbnail — until the next
/// scan happens to re-enqueue them. This service resumes that work on startup so a
/// restart is safe. (Discovered on the live Unraid library, 2026-09-09: a deploy
/// left ~13.6k items pending with analysis idle.)
///
/// Runs fire-and-forget after the worker pool starts, enqueues at Background
/// priority (never blocks reader-triggered analysis), and is a no-op when nothing
/// is pending. Mirrors <see cref="ThumbnailBackfillHostedService"/>.
/// </summary>
public sealed class PendingAnalysisResumeHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PendingAnalysisResumeHostedService> _logger;

    public PendingAnalysisResumeHostedService(
        IServiceProvider services,
        ILogger<PendingAnalysisResumeHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: must not block host startup. The worker pool is started
        // by MediaWorkerHostedService (registered before this), so it is available.
        _ = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var scheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();

            var libraryIds = await db.Libraries
                .Where(l => l.State == "active")
                .Select(l => l.Id)
                .ToListAsync();

            var totalResumed = 0;
            foreach (var libraryId in libraryIds)
            {
                // Archive nodes whose analysis never completed (pending).
                var pending = await db.CatalogNodes
                    .Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5) // 5 = tombstoned
                    .Join(db.ArchiveItems.Where(a => a.AnalysisState == 1),
                          n => n.Id, a => a.NodeId,
                          (n, a) => new { n.Id, n.RelativePath, a.ContentVersion })
                    .ToListAsync();

                if (pending.Count == 0)
                    continue;

                var library = await db.Libraries.FirstAsync(l => l.Id == libraryId);
                _logger.LogInformation(LogEvents.Scanning.AnalysisEnqueueBatch,
                    "Resuming analysis for {Count} pending items in library {LibraryId} after startup",
                    pending.Count, libraryId);

                foreach (var entry in pending)
                {
                    try
                    {
                        var sourcePath = Path.Combine(library.RootPath, entry.RelativePath);
                        var fileInfo = new FileInfo(sourcePath);
                        if (!fileInfo.Exists)
                            continue;

                        // Fire-and-forget at Background priority (mirrors the post-scan
                        // enqueue). The returned task completes only when the item is
                        // analyzed; we only need it queued, so observe faults and move on.
                        _ = scheduler.EnqueueAsync(
                            itemId: entry.Id,
                            contentVersion: entry.ContentVersion,
                            operation: JobOperation.Analyze,
                            priority: JobPriority.Background,
                            archivePath: sourcePath,
                            expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks,
                            expectedByteLength: fileInfo.Length,
                            callerToken: CancellationToken.None)
                            .ContinueWith(static t => { _ = t.Exception; },
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted,
                                TaskScheduler.Default);
                        totalResumed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(LogEvents.Scanning.AnalysisEnqueueItemFailed,
                            "Failed to resume analysis for item {ItemId}: {Error}", entry.Id, ex.GetType().Name);
                    }
                }
            }

            if (totalResumed > 0)
                _logger.LogInformation(LogEvents.Scanning.AnalysisEnqueueBatch,
                    "Startup analysis resume complete: {Count} item(s) re-enqueued", totalResumed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Scanning.AnalysisEnqueueFailed,
                "Startup analysis resume failed: {Error}", ex.GetType().Name);
        }
    }
}
