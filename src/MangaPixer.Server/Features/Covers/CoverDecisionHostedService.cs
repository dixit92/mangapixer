namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Work for the cover decisions (1.29.0): nodes to decide now (a fresh thumbnail, an admin action), folders to decide as a subtree
/// (1.32.0, a changed folder cover preference) and a "sweep soon"
/// signal (new web covers stored, a link or a setting changed). In-memory; everything is also found again by the
/// periodic sweep, so a restart loses nothing.
/// </summary>
public sealed class CoverDecisionQueue
{
    private readonly ConcurrentQueue<long> _nodes = new();
    private readonly ConcurrentQueue<long> _subtrees = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _sweepRequested;

    /// <summary>Decide these nodes soon (their nearest linked folder follows).</summary>
    public void Enqueue(params long[] nodeIds)
    {
        foreach (var id in nodeIds)
            _nodes.Enqueue(id);
        Wake();
    }

    /// <summary>
    /// Decide this folder and everything below it soon (1.32.0: a folder's cover preference changed, so the web decisions below it
    /// are dropped or made again).
    /// </summary>
    public void EnqueueSubtree(long folderId)
    {
        _subtrees.Enqueue(folderId);
        Wake();
    }

    /// <summary>Run the series sweep soon (e.g. after the volume-cover download stored covers).</summary>
    public void RequestSweep()
    {
        Interlocked.Exchange(ref _sweepRequested, 1);
        Wake();
    }

    internal bool TryDequeue(out long nodeId) => _nodes.TryDequeue(out nodeId);

    internal bool TryDequeueSubtree(out long folderId) => _subtrees.TryDequeue(out folderId);

    internal bool TakeSweepRequest() => Interlocked.Exchange(ref _sweepRequested, 0) == 1;

    internal int Pending => _nodes.Count + _subtrees.Count;

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void Wake()
    {
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }
}

/// <summary>
/// Runs the automatic cover decisions in the background (1.29.0 cover layer). Each tick: (1) the queued nodes (fresh
/// thumbnails) and their nearest linked folder; (2) the series sweep - every linked node whose link, record, companion or
/// stored covers changed since the last sweep (all of them after a start or a cover-setting change), decided as a subtree
/// with the inputs-key short cut; (3) the spread backfill - ready archives whose page 1 is spread-shaped and that have no
/// decision yet; (4) the clean-up of web decisions whose node lost its link. Yields to the reader whenever the media
/// worker pool is saturated or analysis is pending. No network: web covers are only READ from the data root.
/// <c>Covers:SweepEnabled=false</c> keeps only the queue (tests drive decisions directly).
/// </summary>
public sealed class CoverDecisionHostedService : BackgroundService
{
    /// <summary>At most this many spread archives per tick.</summary>
    public const int SpreadBatch = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly CoverDecisionQueue _queue;
    private readonly MediaWorkerPool? _pool;
    private readonly ILogger<CoverDecisionHostedService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _startDelay;
    private readonly bool _sweepEnabled;
    private readonly HashSet<(long NodeId, long Version)> _spreadAttempted = [];
    private DateTimeOffset _watermark = DateTimeOffset.MinValue;
    private string? _settingsStamp;
    private readonly Jobs.JobRunRecorder? _runs;

    /// <summary>How often the sweep runs (<c>Covers:SweepIntervalSeconds</c>), for the Scheduled jobs section.</summary>
    public TimeSpan SweepInterval => _interval;

    public bool SweepEnabled => _sweepEnabled;

    public CoverDecisionHostedService(IServiceScopeFactory scopes, CoverDecisionQueue queue, IConfiguration configuration,
        ILogger<CoverDecisionHostedService> logger, MediaWorkerPool? pool = null, Jobs.JobRunRecorder? runs = null)
    {
        _runs = runs;
        _scopes = scopes;
        _queue = queue;
        _pool = pool;
        _logger = logger;
        _sweepEnabled = !(bool.TryParse(configuration["Covers:SweepEnabled"], out var enabled) && !enabled);
        _interval = TimeSpan.FromSeconds(ReadSeconds(configuration, "Covers:SweepIntervalSeconds", 600));
        _startDelay = TimeSpan.FromSeconds(ReadSeconds(configuration, "Covers:SweepStartDelaySeconds", 60));
    }

    private static int ReadSeconds(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var v) && v >= 0 ? v : fallback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextSweep = DateTimeOffset.UtcNow + _startDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = _sweepEnabled ? nextSweep - DateTimeOffset.UtcNow : Timeout.InfiniteTimeSpan;
            await _queue.WaitAsync(wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
                break;
            try
            {
                await DrainQueueAsync(stoppingToken);
                if ((_queue.TakeSweepRequest() || DateTimeOffset.UtcNow >= nextSweep) && _sweepEnabled)
                {
                    // 1.32.0: recorded as the cover-decisions job's run (read-only in the Scheduled jobs section).
                    if (_runs is null)
                        await SweepAsync(stoppingToken);
                    else
                        await _runs.RunAsync(Jobs.ScheduledJobKeys.CoverDecisions, async () =>
                        {
                            await SweepAsync(stoppingToken);
                            return (true, Jobs.JobOutcomes.Ok, (string?)null);
                        }, stoppingToken);
                    nextSweep = DateTimeOffset.UtcNow + _interval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Metadata.CoverDecisionFailed, "Cover decision pass failed: {Error}", ex.GetType().Name);
                nextSweep = DateTimeOffset.UtcNow + _interval;
            }
        }
    }

    /// <summary>Decides the queued nodes and, for each, its nearest linked folder (its series card may change with it).</summary>
    internal async Task DrainQueueAsync(CancellationToken ct)
    {
        var seenSubtrees = new HashSet<long>();
        while (_queue.TryDequeueSubtree(out var folderId))
        {
            if (!seenSubtrees.Add(folderId))
                continue;
            await YieldToReaderAsync(ct);
            using var scope = _scopes.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<CoverDecisionService>().DecideSubtreeAsync(folderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(LogEvents.Metadata.CoverDecisionFailed, "Cover decision failed (folder {NodeId}): {Error}", folderId, ex.GetType().Name);
            }
        }

        var seen = new HashSet<long>();
        while (_queue.TryDequeue(out var nodeId))
        {
            if (!seen.Add(nodeId))
                continue;
            await YieldToReaderAsync(ct);
            using var scope = _scopes.CreateScope();
            var decisions = scope.ServiceProvider.GetRequiredService<CoverDecisionService>();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            await DecideSafelyAsync(decisions, nodeId, ct);
            var links = await CoverLinks.NearestAsync(db, [nodeId], ct);
            if (links.TryGetValue(nodeId, out var link) && link.IsLinked && link.LinkNodeId != nodeId && seen.Add(link.LinkNodeId))
                await DecideSafelyAsync(decisions, link.LinkNodeId, ct);
        }
    }

    /// <summary>One full sweep (series, spreads, clean-up). Internal for tests.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var decisions = scope.ServiceProvider.GetRequiredService<CoverDecisionService>();

        var settings = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataCoverLanguage, s.CoverSpreadCropEnabled }).FirstOrDefaultAsync(ct);
        var stamp = $"{settings?.MetadataCoverLanguage}:{settings?.CoverSpreadCropEnabled}";
        if (!string.Equals(stamp, _settingsStamp, StringComparison.Ordinal))
        {
            _watermark = DateTimeOffset.MinValue;
            _settingsStamp = stamp;
        }

        // (2) Series whose inputs may have changed since the watermark.
        var since = _watermark;
        var seriesNodes = await ChangedSeriesAsync(db, since, ct);
        var series = 0;
        foreach (var nodeId in seriesNodes)
        {
            await YieldToReaderAsync(ct);
            try
            {
                await decisions.DecideSubtreeAsync(nodeId, ct);
                series++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(LogEvents.Metadata.CoverDecisionFailed, "Cover decision failed (node {NodeId}): {Error}", nodeId, ex.GetType().Name);
            }
        }
        _watermark = started;

        // (3) Spread-shaped archives without a decision (the crop backfill of existing libraries).
        var spreads = 0;
        if (settings?.CoverSpreadCropEnabled ?? true)
        {
            foreach (var (nodeId, version) in await UndecidedSpreadsAsync(db, ct))
            {
                if (!_spreadAttempted.Add((nodeId, version)))
                    continue;
                await YieldToReaderAsync(ct);
                await DecideSafelyAsync(decisions, nodeId, ct);
                spreads++;
            }
        }

        // (4) Web decisions whose node is no longer linked (unlinked, or now under Don't match).
        var orphans = 0;
        var webRows = await db.NodeAutoCovers.AsNoTracking()
            .Where(a => a.Source == (int)AutoCoverSource.WebVolume || a.Source == (int)AutoCoverSource.WebMain || a.Source == (int)AutoCoverSource.Poster)
            .Select(a => a.NodeId).ToListAsync(ct);
        if (webRows.Count > 0)
        {
            var links = await CoverLinks.NearestAsync(db, webRows, ct);
            foreach (var nodeId in webRows.Where(id => !links.TryGetValue(id, out var l) || !l.IsLinked))
            {
                await DecideSafelyAsync(decisions, nodeId, ct);
                orphans++;
            }
        }

        _logger.LogDebug(LogEvents.Metadata.CoverSweep, "Cover sweep: {Series} series, {Spreads} spreads, {Orphans} unlinked, {Ms} ms",
            series, spreads, orphans, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
    }

    /// <summary>
    /// Nodes with their OWN link (any state but needs-review, so a new Don't match clears its subtree) whose link, record,
    /// MangaDex companion or stored covers changed after <paramref name="since"/>.
    /// </summary>
    internal static async Task<List<long>> ChangedSeriesAsync(MangaPixerDbContext db, DateTimeOffset since, CancellationToken ct)
    {
        var all = since == DateTimeOffset.MinValue;
        var query =
            from l in db.NodeSeriesLinks.AsNoTracking()
            where l.State != (int)SeriesLinkState.NeedsReview
            join r in db.MetadataRecords.AsNoTracking() on l.RecordId equals (long?)r.Id into rs
            from r in rs.DefaultIfEmpty()
            join c in db.MetadataCompanions.AsNoTracking().Where(c => c.Provider == CoverSeries.CompanionProvider) on l.RecordId equals (long?)c.RecordId into cs
            from c in cs.DefaultIfEmpty()
            where all
                || l.UpdatedAt > since
                || (r != null && r.FetchedAt > since)
                || (c != null && c.CheckedAt > since)
                || (c != null && db.VolumeCovers.Any(v => v.ProviderRecordId == c.CompanionRecordId && (v.ListedAt > since || v.StoredAt > since)))
            select l.NodeId;
        // Order after Distinct: an ORDER BY before it is dropped by the query (EF warning 10114).
        return await query.Distinct().OrderBy(id => id).ToListAsync(ct);
    }

    /// <summary>Ready archives whose page 1 is spread-shaped (width / height &gt;= 1.2) and that have no decision.</summary>
    internal static async Task<List<(long NodeId, long Version)>> UndecidedSpreadsAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var rows = await (
            from n in db.CatalogNodes.AsNoTracking()
            where n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            join a in db.ArchiveItems.AsNoTracking() on n.Id equals a.NodeId
            where a.AnalysisState == 0
            join p in db.PageEntries.AsNoTracking() on n.Id equals p.ItemId
            where p.Ordinal == 0 && p.ContentVersion == a.ContentVersion && p.Width != null && p.Height != null && p.Height > 0
                && p.Width * 10 >= p.Height * 12
                && !db.NodeAutoCovers.Any(x => x.NodeId == n.Id)
            orderby n.Id
            select new { n.Id, a.ContentVersion })
            .Take(SpreadBatch * 4)
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.ContentVersion)).ToList();
    }

    private async Task DecideSafelyAsync(CoverDecisionService decisions, long nodeId, CancellationToken ct)
    {
        try
        {
            await decisions.DecideAsync(nodeId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(LogEvents.Metadata.CoverDecisionFailed, "Cover decision failed (node {NodeId}): {Error}", nodeId, ex.GetType().Name);
        }
    }

    /// <summary>Waits while reader work has the worker pool (the thumbnail backfill's rule).</summary>
    private async Task YieldToReaderAsync(CancellationToken ct)
    {
        if (_pool is null)
            return;
        var guard = 0;
        while ((_pool.IsSaturated || _pool.SchedulerPendingCount > 0) && guard++ < 600)
            await Task.Delay(500, ct);
    }
}
