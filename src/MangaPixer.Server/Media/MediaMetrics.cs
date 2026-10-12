namespace com.lifepixer.mangapixer.Server.Media;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

/// <summary>
/// Who asked the <see cref="MediaWorkerPool"/> for a worker slot (1.42.0 performance profiling). Analysis jobs do not
/// take a slot this way (they go through the <see cref="JobScheduler"/>); their wait is the queue wait.
/// </summary>
public enum MediaSlotKind
{
    /// <summary>A reader page (full size or a sized variant).</summary>
    Page = 0,

    /// <summary>The page thumbnail endpoint (the reader's page strip).</summary>
    PageThumbnail = 1,

    /// <summary>A durable cover thumbnail (after analysis, the backfill, or the cover endpoint's safety net).</summary>
    Thumbnail = 2,

    /// <summary>The ComicInfo backfill.</summary>
    ComicInfo = 3,

    /// <summary>A perceptual hash of a server-owned image (automatic cover comparison).</summary>
    ImageHash = 4,

    /// <summary>A cover render from an archive page (a cover crop).</summary>
    CoverRenderArchive = 5,

    /// <summary>A cover render from provider image bytes (MangaDex volume covers, posters).</summary>
    CoverRenderImage = 6,

    /// <summary>A reader page the web reader marked as a prefetch (1.42.0): served after current pages.</summary>
    PagePrefetch = 7,
}

/// <summary>
/// Who gets a freed worker slot first (1.42.0 reader-first hand-off): a reader's current page, then reader prefetches
/// and page thumbnails, then background work; analysis dispatch only when nobody waits.
/// </summary>
internal enum SlotClass
{
    Reader = 0,
    ReaderPrefetch = 1,
    Background = 2,
}

/// <summary>
/// What background media work may do while someone reads (1.42.0 admin setting "Background work while someone
/// reads"): <see cref="Pause"/> none, <see cref="Slow"/> one worker (default), <see cref="Normal"/> all workers but one.
/// </summary>
public enum BackgroundWhileReading
{
    Pause = 0,
    Slow = 1,
    Normal = 2,
}

/// <summary>
/// In-process media metrics (1.42.0): one <see cref="Meter"/> named <see cref="MeterName"/> (no exporter, nothing
/// leaves the process; <c>dotnet-counters</c> can attach to it) plus an in-memory aggregate the admin diagnostics
/// endpoint reads (<see cref="Snapshot"/>). Records only kinds, counts and durations - never ids, paths or names.
/// Every timing keeps cumulative fixed-bucket counts, so a caller can subtract two snapshots and get the percentiles
/// of the window in between (what the load harness in <c>scripts/perf</c> does).
/// </summary>
public sealed class MediaMetrics : IDisposable
{
    public const string MeterName = "MangaPixer.Media";

    // Timing names (milliseconds).
    public const string SlotWait = "slot_wait";
    public const string WorkerCall = "worker_call";
    public const string ExtractOpen = "extract_open";
    public const string ExtractRead = "extract_read";
    public const string ExtractEncode = "extract_encode";
    public const string AnalysisQueueWait = "analysis_queue_wait";
    public const string AnalysisRun = "analysis_run";
    public const string PageDb = "page_db";
    public const string PageTotal = "page_total";
    public const string CachePublish = "cache_publish";

    // Count names.
    public const string SlotOutcome = "slot_outcome";
    public const string SlotReleasedWhileWorkerBusy = "slot_released_while_worker_busy";
    public const string CacheLookup = "cache_lookup";
    public const string CacheEvicted = "cache_evicted";
    public const string CacheEvictedBytes = "cache_evicted_bytes";
    public const string CachePublished = "cache_published";
    public const string PageStatus = "page_status";

    /// <summary>Upper bounds (ms) of the timing buckets; the last bucket is everything above the last bound.</summary>
    public static readonly IReadOnlyList<double> BucketBoundsMs =
        [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000, 120000];

    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Counter<long>> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Name, string Kind), TimingSeries> _timings = new();
    private readonly ConcurrentDictionary<(string Name, string Kind), long[]> _counts = new();
    private readonly int[] _slotsHeld = new int[Enum.GetValues<MediaSlotKind>().Length];
    private Func<MediaPoolGauges>? _pool;
    private Func<MediaCacheGauges>? _cache;

    public MediaMetrics(TimeProvider? time = null)
    {
        Since = (time ?? TimeProvider.System).GetUtcNow();
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge("mangapixer.media.workers", () => _pool?.Invoke().Workers ?? 0, description: "Worker processes in the pool");
        _meter.CreateObservableGauge("mangapixer.media.workers.busy", () => _pool?.Invoke().Busy ?? 0, description: "Workers running a job");
        _meter.CreateObservableGauge("mangapixer.media.slot.waiting", () => _pool?.Invoke().WaitingSlotCallers ?? 0, description: "Slot requests in flight (waiting or just served)");
        _meter.CreateObservableGauge("mangapixer.media.analysis.pending", () => _pool?.Invoke().PendingAnalysis ?? 0, description: "Analysis jobs queued");
        _meter.CreateObservableGauge("mangapixer.media.cache.bytes", () => _cache?.Invoke().UsageBytes ?? 0, unit: "By", description: "Page cache usage");
    }

    /// <summary>This instance's meter (tests listen to exactly this one).</summary>
    internal Meter Meter => _meter;

    /// <summary>When this instance started counting (the process start, in practice).</summary>
    public DateTimeOffset Since { get; }

    /// <summary>Kind tag of a slot caller ("page", "cover_render_image", ...).</summary>
    public static string KindTag(MediaSlotKind kind) => kind switch
    {
        MediaSlotKind.Page => "page",
        MediaSlotKind.PageThumbnail => "page_thumbnail",
        MediaSlotKind.Thumbnail => "thumbnail",
        MediaSlotKind.ComicInfo => "comicinfo",
        MediaSlotKind.ImageHash => "image_hash",
        MediaSlotKind.CoverRenderArchive => "cover_render_archive",
        MediaSlotKind.CoverRenderImage => "cover_render_image",
        MediaSlotKind.PagePrefetch => "page_prefetch",
        _ => "other",
    };

    /// <summary>The pool's live gauges (set by the pool that owns this instance).</summary>
    internal void ObservePool(Func<MediaPoolGauges> gauges) => _pool = gauges;

    /// <summary>The page cache's live gauges (set by the cache that owns this instance).</summary>
    internal void ObserveCache(Func<MediaCacheGauges> gauges) => _cache = gauges;

    /// <summary>Records one duration under <paramref name="name"/> / <paramref name="kind"/>.</summary>
    public void RecordMs(string name, string kind, double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0)
            milliseconds = 0;
        _timings.GetOrAdd((name, kind), _ => new TimingSeries()).Add(milliseconds);
        var histogram = _histograms.GetOrAdd(name, n => _meter.CreateHistogram<double>("mangapixer.media." + n, unit: "ms"));
        histogram.Record(milliseconds, new KeyValuePair<string, object?>("kind", kind));
    }

    /// <summary>Adds <paramref name="delta"/> to the count <paramref name="name"/> / <paramref name="kind"/>.</summary>
    public void Count(string name, string kind, long delta = 1)
    {
        var cell = _counts.GetOrAdd((name, kind), _ => new long[1]);
        Interlocked.Add(ref cell[0], delta);
        var counter = _counters.GetOrAdd(name, n => _meter.CreateCounter<long>("mangapixer.media." + n));
        counter.Add(delta, new KeyValuePair<string, object?>("kind", kind));
    }

    /// <summary>A slot was handed to <paramref name="kind"/> (pair with <see cref="SlotReleased"/>).</summary>
    internal void SlotTaken(MediaSlotKind kind) => Interlocked.Increment(ref _slotsHeld[(int)kind]);

    internal void SlotReleased(MediaSlotKind kind) => Interlocked.Decrement(ref _slotsHeld[(int)kind]);

    /// <summary>The cumulative state: pool and cache gauges, every timing (with bucket counts) and every count.</summary>
    public MediaDiagnostics Snapshot()
    {
        var pool = _pool?.Invoke();
        var cache = _cache?.Invoke();
        var held = Enum.GetValues<MediaSlotKind>()
            .Select(k => new MediaCountDiagnostics { Name = "slots_held", Kind = KindTag(k), Value = Volatile.Read(ref _slotsHeld[(int)k]) })
            .Where(c => c.Value != 0)
            .ToList();

        return new MediaDiagnostics
        {
            Since = Since,
            Pool = new MediaPoolDiagnostics
            {
                MaxConcurrentJobs = pool?.MaxConcurrentJobs ?? 0,
                Workers = pool?.Workers ?? 0,
                BusyWorkers = pool?.Busy ?? 0,
                WaitingSlotCallers = pool?.WaitingSlotCallers ?? 0,
                PendingAnalysisJobs = pool?.PendingAnalysis ?? 0,
                InFlightAnalysisJobs = pool?.InFlightAnalysis ?? 0,
                SlotsHeld = held,
            },
            Cache = new MediaCacheDiagnostics
            {
                BudgetBytes = cache?.BudgetBytes ?? 0,
                UsageBytes = cache?.UsageBytes ?? 0,
                ActiveEntries = cache?.ActiveEntries ?? 0,
                TrackedEntries = cache?.TrackedEntries ?? 0,
            },
            TimingBucketBoundsMs = BucketBoundsMs,
            Timings = _timings
                .OrderBy(t => t.Key.Name, StringComparer.Ordinal).ThenBy(t => t.Key.Kind, StringComparer.Ordinal)
                .Select(t => t.Value.ToDiagnostics(t.Key.Name, t.Key.Kind))
                .ToList(),
            Counts = _counts
                .OrderBy(c => c.Key.Name, StringComparer.Ordinal).ThenBy(c => c.Key.Kind, StringComparer.Ordinal)
                .Select(c => new MediaCountDiagnostics { Name = c.Key.Name, Kind = c.Key.Kind, Value = Interlocked.Read(ref c.Value[0]) })
                .ToList(),
        };
    }

    /// <summary>One line for the periodic Debug summary: counts only.</summary>
    internal string SummaryLine()
    {
        var s = Snapshot();
        string P95(string name, string kind) =>
            s.Timings.FirstOrDefault(t => t.Name == name && t.Kind == kind) is { } t ? $"{t.Count}/p95 {t.P95Ms:F0}ms" : "0";
        long C(string name, string kind) => s.Counts.FirstOrDefault(c => c.Name == name && c.Kind == kind)?.Value ?? 0;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"workers {s.Pool.BusyWorkers}/{s.Pool.Workers} of {s.Pool.MaxConcurrentJobs}, waiting {s.Pool.WaitingSlotCallers}, analysis pending {s.Pool.PendingAnalysisJobs}; " +
            $"page slot wait {P95(SlotWait, "page")}, page worker {P95(WorkerCall, "page")}; cache hit {C(CacheLookup, "hit")} miss {C(CacheLookup, "miss")} " +
            $"evicted {C(CacheEvicted, "lru")}; 503 {C(PageStatus, "503")} 499 {C(PageStatus, "499")}");
    }

    /// <summary>Total number of recorded events (timings + counts), to skip an idle summary.</summary>
    internal long ActivityStamp() =>
        _timings.Values.Sum(t => t.CountValue) + _counts.Values.Sum(c => Interlocked.Read(ref c[0]));

    public void Dispose() => _meter.Dispose();

    /// <summary>Cumulative fixed-bucket histogram of one timing.</summary>
    private sealed class TimingSeries
    {
        private readonly object _lock = new();
        private readonly long[] _buckets = new long[BucketBoundsMs.Count + 1];
        private long _count;
        private double _total;
        private double _max;

        public long CountValue { get { lock (_lock) return _count; } }

        public void Add(double ms)
        {
            var i = 0;
            while (i < BucketBoundsMs.Count && ms > BucketBoundsMs[i])
                i++;
            lock (_lock)
            {
                _buckets[i]++;
                _count++;
                _total += ms;
                if (ms > _max)
                    _max = ms;
            }
        }

        public MediaTimingDiagnostics ToDiagnostics(string name, string kind)
        {
            long[] buckets;
            long count;
            double total, max;
            lock (_lock)
            {
                buckets = (long[])_buckets.Clone();
                count = _count;
                total = _total;
                max = _max;
            }
            return new MediaTimingDiagnostics
            {
                Name = name,
                Kind = kind,
                Count = count,
                TotalMs = Math.Round(total, 1),
                MaxMs = Math.Round(max, 1),
                P50Ms = Percentile(buckets, count, 0.50, max),
                P95Ms = Percentile(buckets, count, 0.95, max),
                BucketCounts = buckets,
            };
        }
    }

    /// <summary>
    /// The upper bound of the bucket holding the <paramref name="q"/> quantile (capped at the observed maximum), so a
    /// percentile is never reported lower than it was. 0 when nothing was recorded.
    /// </summary>
    public static double Percentile(IReadOnlyList<long> buckets, long count, double q, double max)
    {
        if (count <= 0)
            return 0;
        var rank = (long)Math.Ceiling(q * count);
        long seen = 0;
        for (var i = 0; i < buckets.Count; i++)
        {
            seen += buckets[i];
            if (seen >= rank)
                return i < BucketBoundsMs.Count ? Math.Min(BucketBoundsMs[i], Math.Round(max, 1)) : Math.Round(max, 1);
        }
        return Math.Round(max, 1);
    }
}

/// <summary>Live pool numbers for the gauges.</summary>
internal readonly record struct MediaPoolGauges(int MaxConcurrentJobs, int Workers, int Busy, int WaitingSlotCallers, int PendingAnalysis, int InFlightAnalysis);

/// <summary>Live page-cache numbers for the gauges.</summary>
internal readonly record struct MediaCacheGauges(long BudgetBytes, long UsageBytes, int ActiveEntries, int TrackedEntries);

/// <summary>Media pool, page cache and timing diagnostics (1.42.0) - kinds, counts and durations only.</summary>
public sealed record MediaDiagnostics
{
    public required DateTimeOffset Since { get; init; }
    public required MediaPoolDiagnostics Pool { get; init; }
    public required MediaCacheDiagnostics Cache { get; init; }

    /// <summary>Upper bounds (ms) shared by every timing's <see cref="MediaTimingDiagnostics.BucketCounts"/>.</summary>
    public required IReadOnlyList<double> TimingBucketBoundsMs { get; init; }

    public required IReadOnlyList<MediaTimingDiagnostics> Timings { get; init; }
    public required IReadOnlyList<MediaCountDiagnostics> Counts { get; init; }
}

public sealed record MediaPoolDiagnostics
{
    public required int MaxConcurrentJobs { get; init; }
    public required int Workers { get; init; }
    public required int BusyWorkers { get; init; }

    /// <summary>Slot requests in flight (reader pages, thumbnails, covers, hashes, ComicInfo), waiting or just served.</summary>
    public required int WaitingSlotCallers { get; init; }

    public required int PendingAnalysisJobs { get; init; }
    public required int InFlightAnalysisJobs { get; init; }

    /// <summary>Slots held right now, by caller kind (kinds holding none are left out).</summary>
    public required IReadOnlyList<MediaCountDiagnostics> SlotsHeld { get; init; }
}

public sealed record MediaCacheDiagnostics
{
    public required long BudgetBytes { get; init; }
    public required long UsageBytes { get; init; }
    public required int ActiveEntries { get; init; }

    /// <summary>Entries the in-memory index holds, evicted ones included.</summary>
    public required int TrackedEntries { get; init; }
}

public sealed record MediaTimingDiagnostics
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required long Count { get; init; }
    public required double TotalMs { get; init; }
    public required double MaxMs { get; init; }

    /// <summary>Bucket upper bound holding the median (capped at the maximum).</summary>
    public required double P50Ms { get; init; }

    public required double P95Ms { get; init; }

    /// <summary>Cumulative counts per bucket of <see cref="MediaDiagnostics.TimingBucketBoundsMs"/>, plus one overflow bucket.</summary>
    public required IReadOnlyList<long> BucketCounts { get; init; }
}

public sealed record MediaCountDiagnostics
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required long Value { get; init; }
}
