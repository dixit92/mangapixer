namespace com.lifepixer.mangapixer.Server.Media;

using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Tells the pending-archive sweep (1.50.0 lane X) that some archive may be left at <c>AnalysisState == 1</c> with no job queued:
/// a persist that failed after its job ran, or a scan that stopped before it queued its archives. Cheap and non-blocking; the sweep
/// itself waits until the pool is idle.
/// </summary>
public interface IPendingAnalysisSweepTrigger
{
    /// <summary>Asks for one sweep (coalesced; many calls before the sweep runs are one sweep).</summary>
    void Request();
}

/// <summary>
/// Queues analysis jobs for the archives of one library that are still pending (<c>AnalysisState == 1</c>). The one implementation
/// behind the start-up resume, the post-scan enqueue and the sweep, so a pending archive is re-queued the same way from each.
/// </summary>
internal static class PendingAnalysisEnqueue
{
    /// <summary>
    /// Queues Background-priority analysis for every non-tombstoned pending archive of <paramref name="libraryId"/> whose file exists.
    /// One query lists the archives and one reads the library root (it was one root query per archive). <paramref name="admit"/>,
    /// when given, is asked per archive (id, content version) before its file is looked at; false skips it (the sweep's attempt cap).
    /// </summary>
    /// <returns>The pending archives found and how many were queued.</returns>
    public static async Task<(int Found, int Queued)> EnqueueLibraryAsync(
        MangaPixerDbContext db, JobScheduler scheduler, ILogger logger, long libraryId, string reason,
        Func<long, long, bool>? admit = null, CancellationToken ct = default)
    {
        var pending = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5) // 5 = tombstoned
            .Join(db.ArchiveItems.AsNoTracking().Where(a => a.AnalysisState == 1),
                  n => n.Id, a => a.NodeId,
                  (n, a) => new { n.Id, n.RelativePath, a.ContentVersion })
            .ToListAsync(ct);
        if (pending.Count == 0)
            return (0, 0);

        var rootPath = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.RootPath).FirstOrDefaultAsync(ct);
        if (rootPath is null)
            return (pending.Count, 0);

        logger.LogInformation(LogEvents.Scanning.AnalysisEnqueueBatch,
            "Enqueuing analysis for {Count} pending items in library {LibraryId} ({Reason})", pending.Count, libraryId, reason);

        var queued = 0;
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (admit is not null && !admit(entry.Id, entry.ContentVersion))
                    continue;
                // The scheduler stores the path for the worker; it is never echoed in any API response.
                var sourcePath = Path.Combine(rootPath, entry.RelativePath);
                var fileInfo = new FileInfo(sourcePath);
                if (!fileInfo.Exists)
                    continue;

                // Fire-and-forget: EnqueueAsync completes when the job is *analyzed*; awaiting it would serialize the loop on each
                // job. The job only needs to be queued - observe faults so none goes unobserved.
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
                queued++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(LogEvents.Scanning.AnalysisEnqueueItemFailed, "Failed to enqueue analysis for item {ItemId}: {Error}",
                    entry.Id, ex.GetType().Name);
            }
        }
        return (pending.Count, queued);
    }
}
