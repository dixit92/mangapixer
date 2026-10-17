namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Diagnostics;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.44.0 lane C - DESIGN PROBE (phase 1, run by hand only): what it costs to answer the linked series of one browse level at the live
/// library's census shape (1008 series folders, 29 499 archives - about 29 per series) with synthetic rows. Prints timings to the file
/// named by <c>MANGAPIXER_LANE_C_PROBE_OUT</c>. Skipped in every normal run.
/// </summary>
public sealed class CompletionFilterScaleProbe : IDisposable
{
    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CompletionFilterScaleProbe()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-lanec-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "probe.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact(Skip = "lane C design probe - run by hand (remove Skip in a throwaway commit)")]
    public async Task Probe()
    {
        var output = new StringBuilder();
        void Line(string text)
        {
            output.AppendLine(text);
            Console.WriteLine(text);
        }

        long libId, userId;
        var sw = Stopwatch.StartNew();
        await using (var db = new MangaPixerDbContext(_options))
        {
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);
            var lib = await VolumeTestData.AddLibraryAsync(db);
            libId = lib.Id;
            userId = (await VolumeTestData.AddUserAsync(db)).Id;
            await SeedAsync(db, lib.Id, series: 1008, Line);
        }
        Line($"seed {sw.ElapsedMilliseconds} ms");

        await using (var db = new MangaPixerDbContext(_options))
        {
            var folders = await db.CatalogNodes.AsNoTracking().Where(n => n.ParentId == null && n.Kind == 0).OrderBy(n => n.SortKey)
                .Select(n => n.Id).ToListAsync();
            var links = await db.NodeSeriesLinks.AsNoTracking().ToDictionaryAsync(l => l.NodeId, l => l.RecordId!.Value);
            foreach (var n in new[] { 50, 200, folders.Count })
            {
                var targets = folders.Take(n).Select(f => new SeriesProgressTarget(f, links[f])).ToList();
                var times = new List<long>();
                Dictionary<SeriesAnswer, int> answers = [];
                for (var run = 0; run < 4; run++)
                {
                    await using var fresh = new MangaPixerDbContext(_options);
                    var t = Stopwatch.StartNew();
                    var progress = await new SeriesProgressLoader(fresh).LoadAsync(targets, default);
                    times.Add(t.ElapsedMilliseconds);
                    answers = progress.Values.GroupBy(p => p.Result.Answer).ToDictionary(g => g.Key, g => g.Count());
                }
                Line($"loader {n} series: {string.Join(" / ", times)} ms; answers {string.Join(", ", answers.OrderBy(a => a.Key).Select(a => $"{a.Key}={a.Value}"))}");
            }
        }

        foreach (var (label, completion) in new (string, SeriesAnswer?)[]
                 { ("browse no filter", null), ("browse HaveItAll", SeriesAnswer.HaveItAll), ("browse MissingSome", SeriesAnswer.MissingSome) })
        {
            var times = new List<long>();
            var total = 0;
            string? cursor = null;
            for (var run = 0; run < 4; run++)
            {
                await using var db = new MangaPixerDbContext(_options);
                var browse = new CatalogBrowseService(db, new LibraryAuthorizationService(db));
                var t = Stopwatch.StartNew();
                var page = await browse.BrowseAsync(userId, libId, null, run == 3 ? cursor : null, 50, completion: completion);
                times.Add(t.ElapsedMilliseconds);
                total = page.TotalCount;
                cursor ??= page.NextCursor;
            }
            Line($"{label}: {string.Join(" / ", times)} ms (last = page 2); total {total}");
        }

        var path = Environment.GetEnvironmentVariable("MANGAPIXER_LANE_C_PROBE_OUT");
        if (!string.IsNullOrEmpty(path))
            await File.WriteAllTextAsync(path, output.ToString());
    }

    /// <summary>
    /// <paramref name="series"/> linked series folders at the library root: ~29 archives each (volume files, chapter files, or a
    /// "Volumes" unit subfolder plus chapters), ComicInfo on every other archive, a MangaDex-style map, mixed statuses.
    /// </summary>
    private static async Task SeedAsync(MangaPixerDbContext db, long libraryId, int series, Action<string> line)
    {
        var random = new Random(144);
        var now = DateTimeOffset.UtcNow;
        CatalogNodeEntity Node(long? parent, CatalogNodeKind kind, string name)
        {
            var id = "p" + Guid.NewGuid().ToString("N")[..12];
            return new CatalogNodeEntity
            {
                PublicId = id,
                LibraryId = libraryId,
                ParentId = parent,
                Kind = (int)kind,
                DisplayName = name,
                RelativePath = id,
                PathKey = id,
                SortKey = SortKey.ForNode(kind, name),
                Availability = (int)CatalogNodeAvailability.Available,
                CreatedAt = now,
            };
        }

        var folders = Enumerable.Range(1, series).Select(i => Node(null, CatalogNodeKind.Folder, $"Synthetic Series {i:0000}")).ToList();
        db.CatalogNodes.AddRange(folders);
        await db.SaveChangesAsync();
        var units = new List<(CatalogNodeEntity Unit, int Series)>();
        for (var i = 0; i < series; i++)
        {
            if (i % 10 == 9)
                units.Add((Node(folders[i].Id, CatalogNodeKind.Folder, "Volumes"), i));
        }
        db.CatalogNodes.AddRange(units.Select(u => u.Unit));
        await db.SaveChangesAsync();
        var unitOf = units.ToDictionary(u => u.Series, u => u.Unit);

        var archives = new List<CatalogNodeEntity>();
        var volumesOf = new int[series];
        for (var i = 0; i < series; i++)
        {
            var count = random.Next(10, 49); // about 29 on average
            var name = $"Synthetic Series {i + 1:0000}";
            if (i % 10 == 9)
            {
                var vols = count / 2;
                volumesOf[i] = vols + (count - vols) / 8 + 1;
                for (var v = 1; v <= vols; v++)
                    archives.Add(Node(unitOf[i].Id, CatalogNodeKind.Archive, $"{name} v{v:00}.cbz"));
                for (var c = vols * 8 + 1; c <= vols * 8 + (count - vols); c++)
                    archives.Add(Node(folders[i].Id, CatalogNodeKind.Archive, $"{name} c{c:000}.cbz"));
            }
            else if (i % 3 == 1)
            {
                volumesOf[i] = count / 8 + 2;
                for (var c = 1; c <= count; c++)
                    archives.Add(Node(folders[i].Id, CatalogNodeKind.Archive, $"{name} c{c:000}.cbz"));
            }
            else
            {
                volumesOf[i] = count + (i % 4 == 0 ? 0 : 3);
                for (var v = 1; v <= count; v++)
                {
                    if (i % 7 == 3 && v == 5)
                        continue; // a hole
                    archives.Add(Node(folders[i].Id, CatalogNodeKind.Archive, $"{name} v{v:00}.cbz"));
                }
            }
        }
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();
        db.ArchiveItems.AddRange(archives.Select(a => new ArchiveItemEntity { NodeId = a.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 180 }));
        db.EmbeddedMetadata.AddRange(archives.Where((a, k) => k % 2 == 0)
            .Select(a => new EmbeddedMetadataEntity { NodeId = a.Id, Schema = 0, ContentVersion = 1, State = 1, Number = "1" }));
        await db.SaveChangesAsync();
        line($"seeded {series} series, {units.Count} unit folders, {archives.Count} archives");

        var records = Enumerable.Range(0, series).Select(i => new MetadataRecordEntity
        {
            PublicId = "r" + Guid.NewGuid().ToString("N")[..12],
            Provider = "mangaupdates",
            ExternalId = "x" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = $"Synthetic Record {i:0000}",
            OriginVolumes = volumesOf[i],
            OriginStatus = (int)(i % 3 == 0 ? MetadataOriginStatus.Complete : MetadataOriginStatus.Ongoing),
            PublishersJson = $"[{{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":{volumesOf[i] - (i % 5 == 0 ? 2 : 0)}}}]",
            FetchedAt = now,
        }).ToList();
        db.MetadataRecords.AddRange(records);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.AddRange(folders.Select((f, i) => new NodeSeriesLinkEntity
        {
            NodeId = f.Id,
            LibraryId = libraryId,
            State = (int)(i % 2 == 0 ? SeriesLinkState.Confirmed : SeriesLinkState.Auto),
            RecordId = records[i].Id,
            CreatedAt = now,
            UpdatedAt = now,
        }));
        db.SeriesVolumeMaps.AddRange(records.Select((r, i) => new SeriesVolumeMapEntity
        {
            RecordId = r.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[" + string.Join(",", Enumerable.Range(1, volumesOf[i]).Select(v =>
                $"{{\"v\":\"{v}\",\"c\":[{string.Join(",", Enumerable.Range((v - 1) * 8 + 1, 8).Select(c => $"\"{c}\""))}]}}")) + "]",
            ChaptersPerVolume = 8,
            KnownVolumeCount = volumesOf[i],
            ContentHash = "h" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Version = 1,
            FetchedAt = now,
        }));
        await db.SaveChangesAsync();
    }
}
