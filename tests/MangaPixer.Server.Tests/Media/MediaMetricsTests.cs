namespace com.lifepixer.mangapixer.Tests.Server.Media;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using com.lifepixer.mangapixer.Server.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>The in-process media metrics aggregate (1.42.0 profiling): buckets, percentiles, counts, the Meter.</summary>
public sealed class MediaMetricsTests
{
    [Fact]
    public void Timings_KeepCumulativeBuckets_AndReportBucketPercentilesCappedAtTheMax()
    {
        using var metrics = new MediaMetrics();
        foreach (var ms in new[] { 3.0, 4, 4, 7, 40, 40, 40, 40, 40, 900 })
            metrics.RecordMs(MediaMetrics.SlotWait, "page", ms);

        var t = Assert.Single(metrics.Snapshot().Timings);
        Assert.Equal("slot_wait", t.Name);
        Assert.Equal("page", t.Kind);
        Assert.Equal(10, t.Count);
        Assert.Equal(1118, t.TotalMs);
        Assert.Equal(900, t.MaxMs);
        // Ranks: the 5th value (40) sits in the <=50 bucket; the 10th (900) in the <=1000 bucket, capped at the max.
        Assert.Equal(50, t.P50Ms);
        Assert.Equal(900, t.P95Ms);
        Assert.Equal(MediaMetrics.BucketBoundsMs.Count + 1, t.BucketCounts.Count);
        Assert.Equal(10, t.BucketCounts.Sum());
        Assert.Equal(3, t.BucketCounts[2]); // <=5 ms: 3, 4, 4
    }

    /// <summary>
    /// 1.47.0 lane T2: the admin diagnostics snapshot (and the Debug summary line) sorts the timings and counts while the media pool
    /// records them. A LINQ sort straight over a <c>ConcurrentDictionary</c> copies it by <c>Count</c> + <c>CopyTo</c>, which throws
    /// <see cref="ArgumentException"/> when the first value of a new name / kind arrives in between - a 500 on the diagnostics GET.
    /// Writers add new kinds the whole time; a fresh instance replaces one with many keys so each snapshot stays quick.
    /// </summary>
    [Fact]
    public void Snapshot_WhileNewKindsArrive_NeverThrows()
    {
        var current = new StrongBox<MediaMetrics>(new MediaMetrics());
        var all = new ConcurrentQueue<MediaMetrics>([current.Value!]);
        var stop = 0;
        var writers = new[]
        {
            new Thread(() =>
            {
                for (var i = 0; Volatile.Read(ref stop) == 0; i++)
                    Volatile.Read(ref current.Value)!.RecordMs(MediaMetrics.SlotWait, $"k{i}", 1);
            }),
            new Thread(() =>
            {
                for (var i = 0; Volatile.Read(ref stop) == 0; i++)
                    Volatile.Read(ref current.Value)!.Count(MediaMetrics.SlotOutcome, $"k{i}");
            }),
        };
        foreach (var writer in writers)
            writer.Start();
        try
        {
            var snapshots = 0;
            for (var watch = Stopwatch.StartNew(); watch.Elapsed < TimeSpan.FromSeconds(2); snapshots++)
            {
                var snapshot = Volatile.Read(ref current.Value)!.Snapshot();
                if (snapshot.Timings.Count + snapshot.Counts.Count >= 3000)
                {
                    var next = new MediaMetrics();
                    all.Enqueue(next);
                    Volatile.Write(ref current.Value, next);
                }
            }
            Assert.True(snapshots > 10, $"only {snapshots} snapshots");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            foreach (var writer in writers)
                writer.Join();
            foreach (var metrics in all)
                metrics.Dispose();
        }
    }

    [Fact]
    public void Percentile_OfAnEmptySeriesIsZero_AndTheOverflowBucketReportsTheMax()
    {
        var buckets = new long[MediaMetrics.BucketBoundsMs.Count + 1];
        Assert.Equal(0, MediaMetrics.Percentile(buckets, 0, 0.95, 0));
        buckets[^1] = 2;
        Assert.Equal(250000, MediaMetrics.Percentile(buckets, 2, 0.5, 250000));
    }

    [Fact]
    public void Counts_AddUp_PerNameAndKind_AndNegativeOrNaNDurationsCountAsZero()
    {
        using var metrics = new MediaMetrics();
        metrics.Count(MediaMetrics.CacheLookup, "hit");
        metrics.Count(MediaMetrics.CacheLookup, "hit", 2);
        metrics.Count(MediaMetrics.CacheLookup, "miss");
        metrics.RecordMs(MediaMetrics.PageDb, "page", double.NaN);
        metrics.RecordMs(MediaMetrics.PageDb, "page", -5);

        var s = metrics.Snapshot();
        Assert.Equal(3, s.Counts.Single(c => c.Kind == "hit").Value);
        Assert.Equal(1, s.Counts.Single(c => c.Kind == "miss").Value);
        var db = s.Timings.Single();
        Assert.Equal(2, db.Count);
        Assert.Equal(0, db.MaxMs);
    }

    [Fact]
    public void KindTags_AreStableAndDistinct()
    {
        var tags = Enum.GetValues<MediaSlotKind>().Select(MediaMetrics.KindTag).ToList();
        Assert.Equal(tags.Count, tags.Distinct().Count());
        Assert.DoesNotContain("other", tags);
        Assert.Equal("cover_render_image", MediaMetrics.KindTag(MediaSlotKind.CoverRenderImage));
    }

    [Fact]
    public void TheMeter_PublishesTheSameMeasurements_ToAnInProcessListener()
    {
        using var metrics = new MediaMetrics();
        var seen = new List<(string Instrument, double Value, string? Kind)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "kind").Value as string)));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            seen.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "kind").Value as string)));
        listener.Start();

        metrics.RecordMs(MediaMetrics.WorkerCall, "thumbnail", 12.5);
        metrics.Count(MediaMetrics.PageStatus, "503");

        Assert.Contains(("mangapixer.media.worker_call", 12.5, "thumbnail"), seen);
        Assert.Contains(("mangapixer.media.page_status", 1.0, "503"), seen);
    }

    [Fact]
    public void SummaryService_WritesOneDebugLine_OnlyWhenSomethingHappened()
    {
        using var metrics = new MediaMetrics();
        var logger = new ListLogger();
        var service = new MediaMetricsSummaryService(metrics, logger);

        var stamp = service.WriteIfActive(-1);
        Assert.Single(logger.Lines);
        Assert.Equal(stamp, service.WriteIfActive(stamp));
        Assert.Single(logger.Lines);

        metrics.Count(MediaMetrics.CacheLookup, "miss");
        service.WriteIfActive(stamp);
        Assert.Equal(2, logger.Lines.Count);
        Assert.Contains("miss 1", logger.Lines[1]);

        // Debug off: nothing is computed or written.
        var quiet = new MediaMetricsSummaryService(metrics, NullLogger<MediaMetricsSummaryService>.Instance);
        Assert.Equal(-1, quiet.WriteIfActive(-1));
    }

    private sealed class ListLogger : ILogger<MediaMetricsSummaryService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
