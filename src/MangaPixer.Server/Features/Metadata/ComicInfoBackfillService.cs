namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Background ComicInfo.xml backfill (1.24.0) for archives analysed before
/// ComicInfo was read at analysis time, or whose stored row is stale (content
/// version changed). Modelled on the thumbnail backfill:
/// - fire-and-forget at startup (<see cref="Hosting.ComicInfoBackfillHostedService"/>)
///   and after every successful scan (<see cref="Scanning.LibraryScanLauncher"/>);
/// - saturation-aware: before each item it yields while the worker pool is full or
///   analysis is queued, so reading and analysis always win;
/// - single-flight: a kick while a pass runs only requests one more pass;
/// - each item is attempted at most once per pass (keyset over node ids), and every
///   outcome including "absent" is stored, so each archive is read once per content
///   version.
/// Nothing runs in the scanner's reconcile path. Logs carry counts only.
/// </summary>
public sealed class ComicInfoBackfillService
{
    /// <summary>Items selected per batch.</summary>
    public const int BatchSize = 50;

    /// <summary>Delay between saturation polls.</summary>
    public const int BackoffMs = 200;

    /// <summary>Retries of a single item when no worker slot was free.</summary>
    public const int BusyRetries = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MediaWorkerPool _workerPool;
    private readonly ILogger<ComicInfoBackfillService> _logger;
    private readonly CancellationToken _stopping;
    private readonly Facets.IFacetIndexNudge _facetNudge;
    private readonly object _gate = new();
    private Task? _running;
    private bool _rerunRequested;

    public ComicInfoBackfillService(
        IServiceScopeFactory scopeFactory,
        MediaWorkerPool workerPool,
        ILogger<ComicInfoBackfillService> logger,
        IHostApplicationLifetime? lifetime = null,
        Facets.IFacetIndexNudge? facetNudge = null)
    {
        _scopeFactory = scopeFactory;
        _workerPool = workerPool;
        _logger = logger;
        _facetNudge = facetNudge ?? Facets.NoFacetIndexNudge.Instance;
        // Every pass (startup or post-scan kick) stops with the host.
        _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;
    }

    /// <summary>True while a pass is running (diagnostics / tests).</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _running is { IsCompleted: false }; }
    }

    /// <summary>
    /// Starts a pass if none is running, otherwise requests one more pass after the
    /// current one. Returns the task of the running loop (tests await it; production
    /// callers fire and forget).
    /// </summary>
    public Task RequestRun()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                _rerunRequested = true;
                return _running;
            }
            _rerunRequested = false;
            _running = Task.Run(() => RunLoopAsync(_stopping), CancellationToken.None);
            return _running;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunPassAsync(ct);
                lock (_gate)
                {
                    if (!_rerunRequested)
                        return;
                    _rerunRequested = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Metadata.ComicInfoBackfillFailed, "ComicInfo backfill failed: {Error}", ex.GetType().Name);
        }
    }

    /// <summary>One pass over every archive that needs a (re-)read. Returns the counts.</summary>
    public async Task<ComicInfoBackfillCounts> RunPassAsync(CancellationToken ct)
    {
        var counts = await RunPassCoreAsync(
            (afterNodeId, limit, token) => SelectBatchAsync(afterNodeId, limit, token),
            ReadAndStoreAsync,
            () => _workerPool.IsSaturated,
            () => _workerPool.SchedulerPendingCount,
            BackoffMs,
            ct,
            libraries =>
            {
                // 1.45.1 (B2): only the libraries of the archives this batch stored (was: every library).
                foreach (var library in libraries)
                    _facetNudge.LibraryChanged(library, Facets.FacetNudgeReason.ComicInfoStored);
            });

        if (counts.Attempted > 0)
            _logger.LogInformation(LogEvents.Metadata.ComicInfoBackfillCompleted,
                "ComicInfo backfill pass: {Attempted} archives read, {Stored} stored, {Found} with ComicInfo, {Skipped} skipped",
                counts.Attempted, counts.Stored, counts.Found, counts.Skipped);
        return counts;
    }

    /// <summary>
    /// Delegate-driven pass (unit-testable without a pool, DB or filesystem): keyset
    /// batches by node id, yield before every item while the pool is saturated or
    /// analysis is pending, attempt each item once. <paramref name="batchStored"/> (1.45.0: the tag index nudge) runs after each batch
    /// that stored at least one row, with the libraries of the archives it stored (1.45.1).
    /// </summary>
    internal static async Task<ComicInfoBackfillCounts> RunPassCoreAsync(
        Func<long, int, CancellationToken, Task<IReadOnlyList<ComicInfoBackfillCandidate>>> selectBatchAsync,
        Func<ComicInfoBackfillCandidate, CancellationToken, Task<ComicInfoItemResult>> readAndStoreAsync,
        Func<bool> isSaturated,
        Func<int> schedulerPendingCount,
        int backoffMs,
        CancellationToken ct,
        Action<IReadOnlyCollection<long>>? batchStored = null)
    {
        var counts = new ComicInfoBackfillCounts();
        long afterNodeId = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await selectBatchAsync(afterNodeId, BatchSize, ct);
            if (batch.Count == 0)
                break;
            var storedLibraries = new HashSet<long>();

            foreach (var candidate in batch)
            {
                ct.ThrowIfCancellationRequested();
                afterNodeId = Math.Max(afterNodeId, candidate.NodeId);

                while (isSaturated() || schedulerPendingCount() > 0)
                    await Task.Delay(backoffMs, ct);

                ComicInfoItemResult result;
                try
                {
                    result = await readAndStoreAsync(candidate, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    result = ComicInfoItemResult.Skipped;
                }

                counts.Attempted++;
                switch (result)
                {
                    case ComicInfoItemResult.StoredWithComicInfo:
                        counts.Stored++;
                        counts.Found++;
                        storedLibraries.Add(candidate.LibraryId);
                        break;
                    case ComicInfoItemResult.Stored:
                        counts.Stored++;
                        storedLibraries.Add(candidate.LibraryId);
                        break;
                    default:
                        counts.Skipped++;
                        break;
                }
            }
            if (storedLibraries.Count > 0)
                batchStored?.Invoke(storedLibraries);
        }

        return counts;
    }

    /// <summary>
    /// Ready archives (non-tombstoned, active library) with no embedded_metadata row
    /// for their CURRENT content version, ordered by node id after
    /// <paramref name="afterNodeId"/>.
    /// </summary>
    internal async Task<IReadOnlyList<ComicInfoBackfillCandidate>> SelectBatchAsync(long afterNodeId, int limit, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await SelectBatchAsync(db, afterNodeId, limit, ct);
    }

    internal static async Task<IReadOnlyList<ComicInfoBackfillCandidate>> SelectBatchAsync(
        MangaPixerDbContext db, long afterNodeId, int limit, CancellationToken ct)
    {
        return await (
            from a in db.ArchiveItems
            join n in db.CatalogNodes on a.NodeId equals n.Id
            join l in db.Libraries on n.LibraryId equals l.Id
            where a.AnalysisState == 0
                && a.NodeId > afterNodeId
                && n.Availability != 5
                && l.State == "active"
                && !db.EmbeddedMetadata.Any(e => e.NodeId == a.NodeId && e.ContentVersion == a.ContentVersion)
            orderby a.NodeId
            select new ComicInfoBackfillCandidate(
                a.NodeId, a.ContentVersion, a.ModificationTicks, a.ByteLength, l.RootPath, n.RelativePath, l.Id))
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>Counts for the admin card: ready archives, read ones (current version), ones with ComicInfo.</summary>
    public static async Task<(int Total, int Read, int WithComicInfo)> CountAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var ready = from a in db.ArchiveItems
                    join n in db.CatalogNodes on a.NodeId equals n.Id
                    where a.AnalysisState == 0 && n.Availability != 5
                    select a;
        var total = await ready.CountAsync(ct);
        var read = await ready.CountAsync(a => db.EmbeddedMetadata.Any(e => e.NodeId == a.NodeId && e.ContentVersion == a.ContentVersion), ct);
        var found = await ready.CountAsync(a => db.EmbeddedMetadata.Any(e => e.NodeId == a.NodeId && e.ContentVersion == a.ContentVersion && e.State == 1), ct);
        return (total, read, found);
    }

    private async Task<ComicInfoItemResult> ReadAndStoreAsync(ComicInfoBackfillCandidate candidate, CancellationToken ct)
    {
        var sourcePath = Path.Combine(candidate.RootPath, candidate.RelativePath);
        if (!File.Exists(sourcePath))
            return ComicInfoItemResult.Skipped;

        ComicInfoReadOutcome read = ComicInfoReadOutcome.Failed("busy");
        for (var attempt = 0; attempt <= BusyRetries; attempt++)
        {
            read = await _workerPool.ReadComicInfoAsync(sourcePath, candidate.ModificationTicks, candidate.ByteLength, ct);
            if (read.Success || read.ErrorType != "busy")
                break;
            await Task.Delay(BackoffMs * (attempt + 1), ct);
        }

        if (!read.Success || read.Outcome is null)
        {
            // Source changed / missing / busy / timeout: nothing is stored, so a later
            // pass (the next scan or restart) tries again.
            _logger.LogDebug(LogEvents.Metadata.ComicInfoReadFailed, "ComicInfo read skipped (item {ItemId}): {Error}", candidate.NodeId, read.ErrorType);
            return ComicInfoItemResult.Skipped;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await ComicInfoPersister.StageAsync(db, candidate.NodeId, candidate.ContentVersion, read.Outcome, DateTimeOffset.UtcNow, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Typically the node vanished (library removed) between select and save.
            _logger.LogDebug(LogEvents.Metadata.ComicInfoPersistFailed, "ComicInfo persist failed (item {ItemId}): {Error}", candidate.NodeId, ex.GetType().Name);
            return ComicInfoItemResult.Skipped;
        }

        return read.Outcome.Status == ComicInfoStatus.Parsed
            ? ComicInfoItemResult.StoredWithComicInfo
            : ComicInfoItemResult.Stored;
    }
}

/// <summary>An archive the backfill will read. The root path never leaves the server.</summary>
public sealed record ComicInfoBackfillCandidate(
    long NodeId,
    long ContentVersion,
    long ModificationTicks,
    long ByteLength,
    string RootPath,
    string RelativePath,
    long LibraryId = 0);

public enum ComicInfoItemResult
{
    Skipped = 0,
    Stored = 1,
    StoredWithComicInfo = 2,
}

public sealed class ComicInfoBackfillCounts
{
    public int Attempted { get; set; }
    public int Stored { get; set; }
    public int Found { get; set; }
    public int Skipped { get; set; }
}
