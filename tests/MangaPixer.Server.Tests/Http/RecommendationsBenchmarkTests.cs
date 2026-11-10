namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Diagnostics;
using System.Net;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Runs only with <c>MANGAPIXER_BENCH=1</c> (a benchmark harness, not a gate: timings on a shared box are noisy).</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MANGAPIXER_BENCH") != "1")
            Skip = "benchmark: set MANGAPIXER_BENCH=1";
    }
}

/// <summary>
/// 1.53.0 lane C: the recommendation rows at LIVE SIZE (the cycle note's census: ~3.1k works, ~50k tag entries, one heavy reader), through
/// the HTTP surface. Synthetic, deterministic (seeded RNG): 3000 series folders of 3 archives each in 3 libraries; values drawn with a
/// popularity skew - 1800 creators, 85 genres, 2900 tags, 300 magazines, 6 types, 200 publishers, years; 600 linked records naming 2
/// related records each. The admin is the measured user: 300 ratings, 100 favourites, 100 finished series, 1500 started series (one
/// archive read) and 2000 progress rows - about 500 seeds. Measures p50 / p95 of <c>GET recommendations</c> and <c>GET nodes/{id}/similar</c>,
/// cold (the tag index generation bumped before each call: index rebuilt, answer recomputed) and warm, n = 30 each, and writes them to
/// <c>$TMPDIR/rec-bench.txt</c>. Budget (design note): p95 &lt; 150 ms.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class RecommendationsBenchmarkTests
{
    private const int Works = 3000;
    private const int Runs = 30;

    [BenchFact]
    public async Task HomeAndSimilar_AtLiveSize_StayInsideTheBudget()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var seedClock = Stopwatch.StartNew();
        var adminId = await s.WithDbAsync(db => db.Users.Where(u => u.IsAdmin).Select(u => u.Id).FirstAsync());
        var folders = await SeedAsync(s, adminId);
        var seeded = seedClock.Elapsed;

        var generation = factory.Services.GetRequiredService<IFacetIndexGeneration>();
        async Task<double> TimeAsync(string url)
        {
            var clock = Stopwatch.StartNew();
            using var response = await admin.GetAsync(url);
            var ms = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return ms;
        }

        // Warm-up (JIT, connection pool), then measure.
        await TimeAsync("/api/v1/recommendations?limit=12");
        var homeCold = new List<double>();
        var homeWarm = new List<double>();
        var similarCold = new List<double>();
        var similarWarm = new List<double>();
        for (var i = 0; i < Runs; i++)
        {
            generation.Bump();
            homeCold.Add(await TimeAsync("/api/v1/recommendations?limit=12"));
            homeWarm.Add(await TimeAsync("/api/v1/recommendations?limit=12"));
        }
        for (var i = 0; i < Runs; i++)
        {
            var node = folders[(i * 97) % folders.Count];
            generation.Bump();
            similarCold.Add(await TimeAsync($"/api/v1/nodes/{node}/similar?limit=12"));
            similarWarm.Add(await TimeAsync($"/api/v1/nodes/{node}/similar?limit=12"));
        }
        var home = await (await admin.GetAsync("/api/v1/recommendations?limit=12")).Content.ReadAsStringAsync();

        var counts = await s.WithDbAsync(async db => new
        {
            Works = await db.FacetWorks.CountAsync(),
            Entries = await db.FacetEntries.CountAsync(),
            Ratings = await db.UserRatings.CountAsync(r => r.UserId == adminId),
            Favorites = await db.Favorites.CountAsync(f => f.UserId == adminId),
            Marks = await db.ReadMarks.CountAsync(m => m.UserId == adminId),
            Progress = await db.ReadingProgress.CountAsync(p => p.UserId == adminId),
        });
        static string P(List<double> xs)
        {
            var sorted = xs.Order().ToList();
            double At(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"p50 {At(0.50):F1} ms, p95 {At(0.95):F1} ms, max {sorted[^1]:F1} ms (n {sorted.Count})");
        }
        var report = string.Join("\n",
            $"seeded in {seeded.TotalSeconds:F1} s: works {counts.Works}, entries {counts.Entries}, ratings {counts.Ratings}, favorites {counts.Favorites}, read marks {counts.Marks}, progress {counts.Progress}",
            $"home cold:    {P(homeCold)}",
            $"home warm:    {P(homeWarm)}",
            $"similar cold: {P(similarCold)}",
            $"similar warm: {P(similarWarm)}",
            $"home picks: {System.Text.RegularExpressions.Regex.Matches(home, "\"reason\"").Count}");
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "rec-bench.txt"), report + "\n");

        Assert.Contains("\"reason\"", home, StringComparison.Ordinal);
        double P95(List<double> xs) => xs.Order().ElementAt((int)Math.Ceiling(0.95 * xs.Count) - 1);
        Assert.True(P95(homeWarm) < 150 && P95(similarWarm) < 150 && P95(homeCold) < 150 && P95(similarCold) < 150, report);
    }

    /// <summary>The live-size library; returns the series folders' public ids.</summary>
    private static async Task<List<string>> SeedAsync(FacetQueryTestSupport s, long userId)
    {
        var rng = new Random(20261110);
        var libraries = new[] { await s.LibraryAsync("rbA", "Bench A"), await s.LibraryAsync("rbB", "Bench B"), await s.LibraryAsync("rbC", "Bench C") };
        var now = DateTimeOffset.UtcNow;

        return await s.WithDbAsync(async db =>
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            // Nodes: a folder per series and three archives in it.
            var folderNodes = new List<CatalogNodeEntity>(Works);
            for (var w = 0; w < Works; w++)
            {
                var name = "Bench Series " + w.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
                folderNodes.Add(new CatalogNodeEntity
                {
                    PublicId = "rbf" + w.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    LibraryId = libraries[w % 3],
                    Kind = (int)CatalogNodeKind.Folder,
                    DisplayName = name,
                    RelativePath = "f" + w,
                    PathKey = "f" + w,
                    SortKey = SortKey.ForNode(CatalogNodeKind.Folder, name),
                    CreatedAt = now,
                });
            }
            db.CatalogNodes.AddRange(folderNodes);
            await db.SaveChangesAsync();
            var archives = new List<CatalogNodeEntity>(Works * 3);
            for (var w = 0; w < Works; w++)
            {
                for (var a = 1; a <= 3; a++)
                {
                    var name = $"Bench Series {w:D4} v0{a}";
                    archives.Add(new CatalogNodeEntity
                    {
                        PublicId = $"rba{w}x{a}",
                        LibraryId = folderNodes[w].LibraryId,
                        ParentId = folderNodes[w].Id,
                        Kind = (int)CatalogNodeKind.Archive,
                        DisplayName = name,
                        RelativePath = $"f{w}/a{a}",
                        PathKey = $"f{w}/a{a}",
                        SortKey = SortKey.ForNode(CatalogNodeKind.Archive, name),
                        CreatedAt = now,
                    });
                }
            }
            db.CatalogNodes.AddRange(archives);
            await db.SaveChangesAsync();

            // Records (600 linked series) and their relations.
            var records = new List<MetadataRecordEntity>();
            for (var r = 0; r < 600; r++)
            {
                records.Add(new MetadataRecordEntity
                {
                    PublicId = "rbr" + r,
                    Provider = "mangaupdates",
                    ExternalId = (700000 + r).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Title = "Bench Record " + r,
                    FetchedAt = now,
                });
            }
            for (var r = 0; r < 600; r++)
            {
                var a = (700000 + rng.Next(600)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var b = (700000 + rng.Next(600)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                records[r].RelationsJson = $$"""[{"externalId":"{{a}}","relation":"sequel"},{"externalId":"{{b}}","relation":"side story"}]""";
            }
            db.MetadataRecords.AddRange(records);
            await db.SaveChangesAsync();

            var works = new List<FacetWorkEntity>(Works);
            for (var w = 0; w < Works; w++)
            {
                var record = w % 5 == 0 && w / 5 < 600 ? records[w / 5].Id : (long?)null;
                works.Add(new FacetWorkEntity
                {
                    WorkKey = record is { } rid ? "rec:" + rid : "node:" + folderNodes[w].Id,
                    Kind = record is null ? (int)FacetWorkKind.Node : (int)FacetWorkKind.Record,
                    RecordId = record,
                    Title = folderNodes[w].DisplayName,
                    SortKey = folderNodes[w].DisplayName.ToLowerInvariant(),
                    UpdatedAt = now,
                });
            }
            db.FacetWorks.AddRange(works);
            await db.SaveChangesAsync();

            // Values per bucket, drawn with a popularity skew (the square of a uniform draw favours the low ids).
            var pools = new (FacetBucket Bucket, int Count, int PerWorkMin, int PerWorkMax)[]
            {
                (FacetBucket.Creators, 1800, 1, 2), (FacetBucket.Genres, 85, 2, 5), (FacetBucket.Tags, 2900, 4, 10),
                (FacetBucket.Magazine, 300, 0, 1), (FacetBucket.Type, 6, 1, 1), (FacetBucket.Publisher, 200, 1, 2), (FacetBucket.Year, 40, 1, 1),
            };
            var values = new Dictionary<FacetBucket, List<FacetValueEntity>>();
            foreach (var pool in pools)
            {
                var list = Enumerable.Range(0, pool.Count).Select(i => new FacetValueEntity
                {
                    PublicId = FacetPublicIds.NewValueId(),
                    Bucket = (int)pool.Bucket,
                    ValueKey = $"{pool.Bucket}:{i}",
                    Display = $"{pool.Bucket} value {i}",
                }).ToList();
                db.FacetValues.AddRange(list);
                values[pool.Bucket] = list;
            }
            await db.SaveChangesAsync();

            var entries = new List<FacetEntryEntity>();
            for (var w = 0; w < Works; w++)
            {
                foreach (var pool in pools)
                {
                    var n = rng.Next(pool.PerWorkMin, pool.PerWorkMax + 1);
                    var picked = new HashSet<int>();
                    while (picked.Count < n)
                    {
                        var u = rng.NextDouble();
                        picked.Add((int)(u * u * pool.Count));
                    }
                    foreach (var v in picked)
                    {
                        entries.Add(new FacetEntryEntity
                        {
                            WorkId = works[w].Id,
                            LocationNodeId = folderNodes[w].Id,
                            LibraryId = folderNodes[w].LibraryId,
                            ValueId = values[pool.Bucket][v].Id,
                            Sources = (int)FacetSources.Web,
                        });
                    }
                }
            }
            db.FacetEntries.AddRange(entries);
            await db.SaveChangesAsync();

            // The measured user's signals.
            var order = Enumerable.Range(0, Works).OrderBy(_ => rng.Next()).ToList();
            foreach (var w in order.Take(300))
                db.UserRatings.Add(new UserRatingEntity { UserId = userId, CatalogNodeId = folderNodes[w].Id, Score = rng.Next(1, 11), RatedAt = now });
            foreach (var w in order.Skip(300).Take(100))
                db.Favorites.Add(new FavoriteEntity { UserId = userId, CatalogNodeId = folderNodes[w].Id, CreatedAt = now });
            foreach (var w in order.Skip(400).Take(100))
            {
                for (var a = 0; a < 3; a++)
                    db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = archives[w * 3 + a].Id, MarkedAt = now, Source = "manual" });
            }
            foreach (var w in order.Skip(500).Take(1500))
                db.ReadMarks.Add(new ReadMarkEntity { UserId = userId, ItemId = archives[w * 3].Id, MarkedAt = now, Source = "manual" });
            foreach (var w in order.Skip(500).Take(2000))
            {
                db.ReadingProgress.Add(new ReadingProgressEntity
                {
                    UserId = userId,
                    ItemId = archives[w * 3 + 1].Id,
                    ContentVersion = 1,
                    EntryKey = "p1",
                    State = (int)ReadingState.InProgress,
                    LastMutationId = "m" + w,
                    UpdatedAt = now,
                });
            }
            await db.SaveChangesAsync();
            return folderNodes.Select(f => f.PublicId).ToList();
        });
    }
}
