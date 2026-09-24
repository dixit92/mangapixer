namespace com.lifepixer.mangapixer.Server.Scanning;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A scan started by <see cref="LibraryScanLauncher"/>: the acquired lease
/// (scan run row) and a task that completes when the background scan, including
/// its post-scan analysis enqueue, has finished. The task never faults.
/// </summary>
public sealed record LibraryScanLaunch(ScanRunEntity Run, Task Completion);

/// <summary>
/// The single path that starts a library scan (1.23.0: extracted from the
/// admin controller so the admin endpoints and the scan scheduler share it).
/// Acquires a scan lease, then runs the scan on a background task with its own
/// DI scope: maintenance, <see cref="LibraryScanCoordinator"/>, scan-run
/// counters, lease release, post-scan analysis enqueue and thumbnail backfill.
/// Cancellation goes through <see cref="ScanRunRegistry"/>.
/// </summary>
public sealed class LibraryScanLauncher
{
    private readonly ScanLeaseService _leaseService;
    private readonly LibraryScanPolicy _scanPolicy;
    private readonly ScanRunRegistry _scanRunRegistry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LibraryScanLauncher> _logger;

    public LibraryScanLauncher(
        ScanLeaseService leaseService,
        LibraryScanPolicy scanPolicy,
        ScanRunRegistry scanRunRegistry,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        ILogger<LibraryScanLauncher> logger)
    {
        _leaseService = leaseService;
        _scanPolicy = scanPolicy;
        _scanRunRegistry = scanRunRegistry;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Acquires a scan lease for a library and launches the background scan.
    /// Returns null when a scan is already running for that library
    /// (per-library guard). Returns as soon as the scan is started; the scan
    /// runs on a background task with a dedicated DI scope so scoped services
    /// (DbContext, etc.) outlive the caller's scope.
    /// </summary>
    public async Task<LibraryScanLaunch?> StartAsync(LibraryEntity library, string leaseOwner, CancellationToken ct)
    {
        var lease = await _leaseService.AcquireLeaseAsync(library.Id, leaseOwner, TimeSpan.FromMinutes(30), ct);
        if (lease is null)
            return null;

        // Register a cancellation token so CancelScan can cooperatively
        // cancel the background scan (audit defect D34).
        var scanCt = _scanRunRegistry.Register(lease.Id);

        // Run scan on a background task — the caller returns immediately.
        // Use a dedicated DI scope so scoped services (DbContext, etc.) are not
        // disposed when the caller's scope ends.
        var libraryId = library.Id;
        var libraryRootPath = library.RootPath;
        var leaseId = lease.Id;
        var scanRevision = lease.ScanRevision;
        var ownerTag = lease.LeaseOwner ?? "server";

        var completion = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var scopedMaintenance = scope.ServiceProvider.GetRequiredService<LibraryMaintenanceService>();
            var scopedLeaseService = scope.ServiceProvider.GetRequiredService<ScanLeaseService>();
            var scopedJobScheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
            try
            {
                await scopedMaintenance.EnterMaintenanceAsync(libraryId);
                var fs = new ReadOnlyLibraryFileSystem(libraryRootPath);
                var coordinator = new LibraryScanCoordinator(
                    scopedDb, fs, _scanPolicy, libraryId, scanRevision, ownerTag,
                    _loggerFactory.CreateLogger<LibraryScanCoordinator>());
                var result = await coordinator.ScanAsync(scanCt);

                // Persist scan counters to ScanRunEntity (audit defect D8).
                // Previously TriggerScan logged result.NodesAdded but never
                // wrote counts to the ScanRun, so GET /scans reported zeros.
                var scanRun = await scopedDb.ScanRuns.FirstOrDefaultAsync(s => s.Id == leaseId, scanCt);
                if (scanRun is not null)
                {
                    scanRun.NodesObserved = result.NodesObserved;
                    scanRun.NodesAdded = result.NodesAdded;
                    scanRun.NodesUpdated = result.NodesUpdated;
                    scanRun.NodesTombstoned = result.NodesTombstoned;
                    scanRun.Status = result.Success ? 2 : 3;
                    scanRun.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync(scanCt);
                }

                await scopedLeaseService.ReleaseLeaseAsync(leaseId, result.Success, result.Error);
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogInformation(LogEvents.Scanning.AdminScanCompleted, "Scan completed for library {LibraryId}: {Added} added, {Tombstoned} tombstoned",
                    libraryId, result.NodesAdded, result.NodesTombstoned);

                // Enqueue analysis for pending archive items after a successful
                // scan (audit defect D33). Previously items were only analyzed
                // when a reader opened them, so page counts never appeared
                // after a scan.
                if (result.Success)
                {
                    await EnqueueAnalysisForPendingItemsAsync(scopedDb, scopedJobScheduler, libraryId, scanCt);

                    // Post-scan thumbnail backfill (post-1.2.0): kick a continuous
                    // backfill for this library to catch ready-but-missing or
                    // stale (content-version-bumped) thumbnails. Newly-scanned
                    // items are AnalysisState==1 (pending), so the backfill query
                    // won't touch them until analyzed — at which point
                    // analysis-time generation already made their thumbnail
                    // (idempotent skip, no double-generate). Fire-and-forget;
                    // the runner throttles itself against reader demand.
                    var postScanLibraryId = libraryId;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var thumbScope = _scopeFactory.CreateScope();
                            var thumbService = thumbScope.ServiceProvider.GetRequiredService<ThumbnailGenerationService>();
                            await thumbService.RunContinuousBackfillAsync(postScanLibraryId, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed, ex, "Post-scan thumbnail backfill failed (library {LibraryId}): {Error}", postScanLibraryId, ex.GetType().Name);
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // Scan was cancelled via CancelScan (audit defect D34).
                // Use CancelScanAsync (status=cancelled, guarded on still-running)
                // rather than ReleaseLeaseAsync(success:false), which would derive
                // status purely from the boolean and mark a user-cancelled scan as
                // "failed". The Status==1 guard also leaves an already-completed
                // scan (whose only cancelled step was the post-scan analysis
                // enqueue) as "completed" instead of downgrading it.
                await scopedLeaseService.CancelScanAsync(leaseId);
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogInformation(LogEvents.Scanning.AdminScanCancelled, "Scan cancelled for library {LibraryId}", libraryId);
            }
            catch (Exception ex)
            {
                var scanRun = await scopedDb.ScanRuns.FirstOrDefaultAsync(s => s.Id == leaseId);
                if (scanRun is not null)
                {
                    scanRun.Status = 3; // failed
                    scanRun.SanitizedError = ex.GetType().Name;
                    scanRun.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync();
                }
                await scopedLeaseService.ReleaseLeaseAsync(leaseId, false, ex.GetType().Name);
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogWarning(LogEvents.Scanning.AdminScanFailed, "Scan failed for library {LibraryId}: {Error}", libraryId, ex.GetType().Name);
            }
            finally
            {
                _scanRunRegistry.Complete(leaseId);
            }
        }, CancellationToken.None);

        // Observe the task so a fault in the failure-handling path itself never
        // surfaces as an UnobservedTaskException; callers only await completion.
        var observed = completion.ContinueWith(static t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return new LibraryScanLaunch(lease, observed);
    }

    /// <summary>
    /// Enqueues analysis jobs for archive items in a library that are in the
    /// pending analysis state (audit defect D33). Bounded to parallelism 2
    /// to avoid flooding the worker pool.
    /// </summary>
    private async Task EnqueueAnalysisForPendingItemsAsync(
        MangaPixerDbContext db,
        JobScheduler scheduler,
        long libraryId,
        CancellationToken ct)
    {
        try
        {
            var pendingItems = await db.CatalogNodes
                .Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5)
                .Join(db.ArchiveItems.Where(a => a.AnalysisState == 1),
                      n => n.Id, a => a.NodeId,
                      (n, a) => new { Node = n, Item = a })
                .ToListAsync(ct);

            if (pendingItems.Count == 0)
                return;

            _logger.LogInformation(LogEvents.Scanning.AnalysisEnqueueBatch, "Enqueuing analysis for {Count} pending items in library {LibraryId}",
                pendingItems.Count, libraryId);

            // Enqueue at Background priority — scan-triggered analysis is not
            // interactive and should not block reader-triggered analysis.
            foreach (var entry in pendingItems)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Resolve the source path via the library root + relative path.
                    // The scheduler stores this for the worker; the path is never
                    // echoed in any API response.
                    var library = await db.Libraries.FirstAsync(l => l.Id == entry.Node.LibraryId, ct);
                    var sourcePath = Path.Combine(library.RootPath, entry.Node.RelativePath);
                    var fileInfo = new FileInfo(sourcePath);
                    if (!fileInfo.Exists)
                        continue;

                    // Fire-and-forget: EnqueueAsync returns a Task that only
                    // completes when the job is *analyzed*. Awaiting it here would
                    // serialize the whole loop on each job's completion (and block
                    // the background scan on the very first job). We only need the
                    // job queued; the pool drains the queue at its own pace. Observe
                    // the task so a faulted job does not raise UnobservedTaskException.
                    _ = scheduler.EnqueueAsync(
                        itemId: entry.Node.Id,
                        contentVersion: entry.Item.ContentVersion,
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
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(LogEvents.Scanning.AnalysisEnqueueItemFailed, "Failed to enqueue analysis for item {ItemId}: {Error}",
                        entry.Node.Id, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Scanning.AnalysisEnqueueFailed, "Post-scan analysis enqueue failed for library {LibraryId}: {Error}",
                libraryId, ex.GetType().Name);
        }
    }
}
