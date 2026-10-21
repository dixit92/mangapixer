namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// The tag index's dirty-library queue (1.45.0 lane F) - the app's <see cref="IFacetIndexNudge"/>, replacing seam I0's no-op. A nudge only
/// marks a library (or every library) dirty with the time and wakes the builder: no database, no network, never throws (the seam's
/// contract). In memory: the builder's periodic inputs-key sweep finds every change again, so a restart loses nothing.
/// </summary>
public sealed class FacetIndexQueue : IFacetIndexNudge
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _dirty = new();
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _signal = new(0);
    private long _allTicks = long.MinValue;

    public FacetIndexQueue(TimeProvider time) => _time = time;

    /// <summary>
    /// Marks the library dirty (its debounce restarts). 1.45.1 (B2): while an "every library" nudge waits, a library nudge is absorbed by it -
    /// that pass starts after this nudge's write was committed, so it rebuilds the library once instead of twice.
    /// </summary>
    public void LibraryChanged(long libraryId, FacetNudgeReason reason)
    {
        if (Interlocked.Read(ref _allTicks) == long.MinValue)
            _dirty[libraryId] = _time.GetUtcNow();
        Wake();
    }

    /// <summary>Marks every library dirty; 1.45.1 (B2): the libraries already waiting are taken into it (one rebuild each, not two).</summary>
    public void AllChanged(FacetNudgeReason reason)
    {
        Interlocked.Exchange(ref _allTicks, _time.GetUtcNow().UtcTicks);
        foreach (var waiting in _dirty)
            _dirty.TryRemove(waiting);
        Wake();
    }

    /// <summary>How many libraries wait (the "all" mark counts as one).</summary>
    public int Pending => _dirty.Count + (Interlocked.Read(ref _allTicks) == long.MinValue ? 0 : 1);

    /// <summary>
    /// The libraries whose last nudge is at least <paramref name="debounce"/> old (taken off the queue), and whether an "every library" nudge
    /// is due; <paramref name="nextDue"/> is when the next waiting nudge becomes due (null when none waits).
    /// </summary>
    internal (IReadOnlyList<long> Libraries, bool All) TakeDue(TimeSpan debounce, out DateTimeOffset? nextDue)
    {
        var now = _time.GetUtcNow();
        var due = new List<long>();
        nextDue = null;
        foreach (var (library, at) in _dirty)
        {
            if (now - at >= debounce)
            {
                if (_dirty.TryRemove(new KeyValuePair<long, DateTimeOffset>(library, at)))
                    due.Add(library);
            }
            else if (nextDue is null || at + debounce < nextDue)
            {
                nextDue = at + debounce;
            }
        }
        var all = false;
        var allTicks = Interlocked.Read(ref _allTicks);
        if (allTicks != long.MinValue)
        {
            var at = new DateTimeOffset(allTicks, TimeSpan.Zero);
            if (now - at >= debounce)
                all = Interlocked.CompareExchange(ref _allTicks, long.MinValue, allTicks) == allTicks;
            else if (nextDue is null || at + debounce < nextDue)
                nextDue = at + debounce;
        }
        return (due, all);
    }

    /// <summary>Puts a library back (a scan ran when its turn came): it becomes due again one debounce from now.</summary>
    internal void Requeue(long libraryId) => _dirty[libraryId] = _time.GetUtcNow();

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try { await _signal.WaitAsync(timeout, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (Exception)
        {
            // A nudge never throws (the caller's write is already saved).
        }
    }
}

/// <summary>
/// The tag index builder in the background (1.45.0 lane F, D1 2.3; the cover-decision pattern). Nudged libraries are rebuilt once their
/// last nudge is <c>Facets:Index:DebounceSeconds</c> old (30 s); every <c>Facets:Index:SweepSeconds</c> (5 min) each library's inputs key is
/// recomputed and a changed one is rebuilt; a daily full pass (<c>Facets:Index:FullPassHours</c>, 24) rebuilds every library. A library
/// whose scan runs is skipped and tried again later; between write batches the builder waits while reader work has the media worker
/// pool (the thumbnail backfill's rule) - reading is never interrupted (owner decision 6). <c>Facets:Index:Enabled=false</c> stops it
/// (the queue still takes nudges). No network.
/// </summary>
public sealed class FacetIndexHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly FacetIndexQueue _queue;
    private readonly MediaWorkerPool? _pool;
    private readonly TimeProvider _time;
    private readonly ILogger<FacetIndexHostedService> _logger;

    public FacetIndexHostedService(IServiceScopeFactory scopes, FacetIndexQueue queue, IConfiguration configuration, TimeProvider time,
        ILogger<FacetIndexHostedService> logger, MediaWorkerPool? pool = null)
    {
        _scopes = scopes;
        _queue = queue;
        _pool = pool;
        _time = time;
        _logger = logger;
        Enabled = !(bool.TryParse(configuration["Facets:Index:Enabled"], out var enabled) && !enabled);
        Debounce = TimeSpan.FromSeconds(Read(configuration, "Facets:Index:DebounceSeconds", 30));
        SweepInterval = TimeSpan.FromSeconds(Math.Max(1, Read(configuration, "Facets:Index:SweepSeconds", 300)));
        StartDelay = TimeSpan.FromSeconds(Read(configuration, "Facets:Index:StartDelaySeconds", 60));
        // 1.46.0 lane M (measurement only, unset by default): Facets:Index:FullPassSeconds replaces the daily interval, so a measured
        // instance can keep the builder rebuilding every library back to back (the tag-index load of a rebuild round).
        var fullPassSeconds = Read(configuration, "Facets:Index:FullPassSeconds", 0);
        FullPassInterval = fullPassSeconds > 0
            ? TimeSpan.FromSeconds(fullPassSeconds)
            : TimeSpan.FromHours(Math.Max(1, Read(configuration, "Facets:Index:FullPassHours", 24)));
    }

    public bool Enabled { get; }
    public TimeSpan Debounce { get; }
    public TimeSpan SweepInterval { get; }
    public TimeSpan StartDelay { get; }
    public TimeSpan FullPassInterval { get; }

    private static int Read(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out var v) && v >= 0 ? v : fallback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;
        var started = _time.GetUtcNow();
        var nextSweep = started + StartDelay;
        var nextFull = started + StartDelay + FullPassInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (libraries, all) = _queue.TakeDue(Debounce, out var nextDue);
                var now = _time.GetUtcNow();
                if (all)
                    libraries = await AllLibrariesAsync(stoppingToken);
                foreach (var library in libraries.Distinct())
                    await RebuildAsync(library, force: true, stoppingToken);

                if (now >= nextFull)
                {
                    foreach (var library in await AllLibrariesAsync(stoppingToken))
                        await RebuildAsync(library, force: true, stoppingToken);
                    nextFull = _time.GetUtcNow() + FullPassInterval;
                    nextSweep = _time.GetUtcNow() + SweepInterval;
                }
                else if (now >= nextSweep)
                {
                    await SweepAsync(stoppingToken);
                    nextSweep = _time.GetUtcNow() + SweepInterval;
                }

                var wakeAt = nextDue is { } due && due < nextSweep ? due : nextSweep;
                var wait = wakeAt - _time.GetUtcNow();
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
                if (libraries.Count == 0 || wait > TimeSpan.Zero)
                    await _queue.WaitAsync(wait, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Metadata.FacetIndexFailed, "Tag index pass failed: {Error}", ex.GetType().Name);
                await _queue.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    /// <summary>Every library whose inputs key changed since its last build is rebuilt (internal for tests).</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var rebuilt = 0;
        var libraries = await AllLibrariesAsync(ct);
        foreach (var library in libraries)
        {
            if (await RebuildAsync(library, force: false, ct) == FacetRebuildOutcome.Rebuilt)
                rebuilt++;
        }
        _logger.LogDebug(LogEvents.Metadata.FacetIndexSweep, "Tag index sweep: {Libraries} library(ies) checked, {Rebuilt} rebuilt", libraries.Count, rebuilt);
        return rebuilt;
    }

    private async Task<IReadOnlyList<long>> AllLibrariesAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.Libraries.AsNoTracking().OrderBy(l => l.Id).Select(l => l.Id).ToListAsync(ct);
    }

    private async Task<FacetRebuildOutcome?> RebuildAsync(long libraryId, bool force, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<FacetIndexService>();
        try
        {
            var result = await index.RebuildLibraryAsync(libraryId, ct, force, YieldToReaderAsync);
            if (result.Outcome == FacetRebuildOutcome.SkippedScanRunning)
                _queue.Requeue(libraryId);
            return result.Outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(LogEvents.Metadata.FacetIndexFailed, "Tag index of library {LibraryId} failed: {Error}", libraryId, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Waits while reader work has the worker pool (the thumbnail backfill's rule), at most about five minutes.</summary>
    private async Task YieldToReaderAsync(CancellationToken ct)
    {
        if (_pool is null)
            return;
        var guard = 0;
        while ((_pool.IsSaturated || _pool.SchedulerPendingCount > 0) && guard++ < 600)
            await Task.Delay(500, ct);
    }
}
