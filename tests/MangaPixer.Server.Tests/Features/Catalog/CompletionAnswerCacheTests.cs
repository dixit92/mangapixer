namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach.LegacyReference;
using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Trackers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests for 1.46.0 lane C: the Completion filter keeps a level's answers while everything they were computed from is
/// unchanged (<c>CompletionInputs</c>), and the progress loader reads the volume contents answers of all series together and each map's
/// JSON once. Equivalence: the new path gives the same entries, answers and counts as the loader before 1.46.0
/// (<see cref="LegacySeriesProgressLoader"/>, a verbatim copy) on the same seeded rows - also after a link change, a declared-fact edit and
/// a new volume map. Invalidation: for each input, a first page right after a change the way production writes it serves the answer the
/// old loader computes from the changed rows (red on a constant stamp). Synthetic names.
/// </summary>
public sealed class CompletionAnswerCacheTests : IDisposable
{
    private static readonly JsonSerializerOptions WithFields = new() { IncludeFields = true };

    private static readonly SeriesAnswer[] AllAnswers =
        [SeriesAnswer.HaveItAll, SeriesAnswer.FinishedMissing, SeriesAnswer.UpToDate, SeriesAnswer.MissingSome, SeriesAnswer.CantTell];

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public CompletionAnswerCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-completion-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "completion-cache.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // --- Seed ---------------------------------------------------------------------------------------------------------------------

    private sealed class Seeded
    {
        public required MangaPixerDbContext Db { get; init; }
        public required LibraryEntity Library { get; init; }
        public required UserEntity User { get; init; }
        public Dictionary<string, CatalogNodeEntity> Series { get; } = [];
        public Dictionary<string, MetadataRecordEntity> Records { get; } = [];
        public Dictionary<string, CatalogNodeEntity> Nodes { get; } = [];
    }

    /// <summary>
    /// One root level of linked series, each with the input a test changes: volumes held whole or missing (catalog), a unit subfolder,
    /// files numbered only by ComicInfo, a MangaDex list with chapter files, a volume file whose chapters a scan can prove, a Wikipedia-filled
    /// list (the credit), an unlinked folder and a loose file.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var s = new Seeded { Db = db, Library = await VolumeTestData.AddLibraryAsync(db), User = await VolumeTestData.AddUserAsync(db) };
        var lib = s.Library.Id;

        async Task<CatalogNodeEntity> SeriesAsync(string key, string name, MetadataOriginStatus status, int? volumes, int? english,
            params string[] files)
        {
            var folder = await VolumeTestData.AddFolderAsync(db, lib, null, name);
            foreach (var f in files)
                s.Nodes[f] = await VolumeTestData.AddArchiveAsync(db, lib, folder.Id, f);
            var record = await VolumeTestData.AddRecordAsync(db, name + " Record", volumes, status, english);
            await VolumeTestData.LinkAsync(db, folder, record.Id);
            s.Series[key] = folder;
            s.Records[key] = record;
            return folder;
        }

        await SeriesAsync("whole", "Synthetic Alpha", MetadataOriginStatus.Complete, 3, 3, "Synthetic Alpha v01", "Synthetic Alpha v02", "Synthetic Alpha v03");
        await SeriesAsync("running", "Synthetic Beta", MetadataOriginStatus.Ongoing, 8, 2, "Synthetic Beta v01", "Synthetic Beta v02");
        await SeriesAsync("finished", "Synthetic Gamma", MetadataOriginStatus.Complete, 2, 2, "Synthetic Gamma v01");
        s.Records["finished-other"] = await VolumeTestData.AddRecordAsync(db, "Synthetic Gamma Other Record", 1, MetadataOriginStatus.Complete, 1);

        var units = await SeriesAsync("units", "Synthetic Delta", MetadataOriginStatus.Complete, 3, 3);
        var unitFolder = await VolumeTestData.AddFolderAsync(db, lib, units.Id, "Volumes");
        s.Nodes["Volumes"] = unitFolder;
        foreach (var v in new[] { 1, 2, 3 })
            s.Nodes[$"Synthetic Delta v0{v}"] = await VolumeTestData.AddArchiveAsync(db, lib, unitFolder.Id, $"Synthetic Delta v0{v}");

        await SeriesAsync("comicinfo", "Synthetic Epsilon", MetadataOriginStatus.Complete, 3, 3, "Synthetic Epsilon Alpha", "Synthetic Epsilon Beta",
            "Synthetic Epsilon Gamma");
        await VolumeTestData.AddComicInfoAsync(db, s.Nodes["Synthetic Epsilon Alpha"].Id, 1, null);
        await VolumeTestData.AddComicInfoAsync(db, s.Nodes["Synthetic Epsilon Beta"].Id, 2, null);

        await SeriesAsync("declared", "Synthetic Zeta", MetadataOriginStatus.Complete, 2, 2, "Synthetic Zeta v01", "Synthetic Zeta v02");

        var chapters = await SeriesAsync("chapters", "Synthetic Eta", MetadataOriginStatus.Ongoing, null, 3);
        for (var c = 1; c <= 12; c++)
            s.Nodes[$"Synthetic Eta c{c:000}"] = await VolumeTestData.AddArchiveAsync(db, lib, chapters.Id, $"Synthetic Eta c{c:000}");

        // Chapters 1-14 are out in English; volume 3 (not on the list) is estimated as 11-15 until a scan proves it holds 11-13.
        var scanned = await SeriesAsync("scanned", "Synthetic Theta", MetadataOriginStatus.Ongoing, 4, null,
            "Synthetic Theta v01", "Synthetic Theta v02", "Synthetic Theta v03", "Synthetic Theta c011", "Synthetic Theta c012",
            "Synthetic Theta c013");
        var scannedMap = await VolumeTestData.AddMapAsync(db, s.Records["scanned"].Id, knownVolumes: 4, chaptersPerVolume: 5,
            volumes: [(1, 1, 5), (2, 6, 10)]);
        await VolumeTestData.SetReleasedAsync(db, scannedMap, "en", 14);

        var wiki = await SeriesAsync("wikipedia", "Synthetic Iota", MetadataOriginStatus.Complete, 3, 3, "Synthetic Iota v01", "Synthetic Iota v02");
        await VolumeTestData.AddMapAsync(db, s.Records["wikipedia"].Id, chaptersPerVolume: 5, volumes: [(1, 1, 5), (2, 6, 10)]);
        await VolumeTestData.AddMapAsync(db, s.Records["wikipedia"].Id, source: VolumeMapSource.WikipediaList, volumes: [(1, 1, 5), (2, 6, 10), (3, 11, 15)]);
        db.WikipediaLists.Add(new WikipediaListEntity
        {
            RecordId = s.Records["wikipedia"].Id,
            PagesJson = """[{"t":"List of Synthetic Iota chapters","r":1}]""",
            CheckedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _ = wiki;
        _ = scanned;

        var plain = await VolumeTestData.AddFolderAsync(db, lib, null, "Synthetic Plain Folder");
        await VolumeTestData.AddArchiveAsync(db, lib, plain.Id, "Synthetic Plain v01");
        await VolumeTestData.AddArchiveAsync(db, lib, null, "Synthetic Loose File");
        return s;
    }

    // --- Old path / new path ----------------------------------------------------------------------------------------------------------

    /// <summary>The level's linked series as the filter reads them (child folders with their own Confirmed / Auto link to a record).</summary>
    private static async Task<List<SeriesProgressTarget>> LinkedAsync(Seeded s)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        return await (
            from n in s.Db.CatalogNodes.AsNoTracking()
            where n.LibraryId == s.Library.Id && n.ParentId == null && n.Kind == (int)CatalogNodeKind.Folder
                && n.Availability != (int)CatalogNodeAvailability.Tombstoned
            join l in s.Db.NodeSeriesLinks.AsNoTracking() on n.Id equals l.NodeId
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
            orderby n.Id
            select new SeriesProgressTarget(n.Id, l.RecordId!.Value, null)).ToListAsync();
    }

    /// <summary>The answers the filter served before 1.46.0: the old loader over the level, tracking off = no answer.</summary>
    private static async Task<Dictionary<long, SeriesAnswer?>> OldAnswersAsync(Seeded s)
    {
        var targets = await LinkedAsync(s);
        var progress = await new LegacySeriesProgressLoader(s.Db).LoadAsync(targets, default);
        return targets.ToDictionary(t => t.NodeId, t => progress.TryGetValue(t.NodeId, out var e) && !e.Result.Facts.TrackingOff
            ? e.Result.Answer
            : (SeriesAnswer?)null);
    }

    private sealed record Served(Dictionary<long, SeriesAnswer?> Answers, BrowseCompletionCountsSnapshot Counts);

    private sealed record BrowseCompletionCountsSnapshot(int HaveItAll, int FinishedMissing, int UpToDate, int MissingSome, int CantTell);

    /// <summary>What the browse serves now, first pages only: every linked series' answer (the list it appears in) and the counts.</summary>
    private static async Task<Served> ServedAsync(Seeded s, CatalogBrowseService browse)
    {
        var byPublicId = await s.Db.CatalogNodes.AsNoTracking().Where(n => n.LibraryId == s.Library.Id && n.ParentId == null)
            .ToDictionaryAsync(n => n.PublicId, n => n.Id);
        var linked = (await LinkedAsync(s)).Select(t => t.NodeId).ToList();
        var answers = linked.ToDictionary(id => id, _ => (SeriesAnswer?)null);
        BrowseCompletionCountsSnapshot? counts = null;
        foreach (var wanted in AllAnswers)
        {
            var page = await browse.BrowseAsync(s.User.Id, s.Library.Id, null, null, pageSize: 100, completion: wanted);
            Assert.False(page.HasMore);
            foreach (var item in page.Items)
            {
                var id = byPublicId[item.Id];
                Assert.Null(answers[id]);
                answers[id] = wanted;
            }
            var c = page.CompletionCounts!;
            var snapshot = new BrowseCompletionCountsSnapshot(c.HaveItAll, c.FinishedMissing, c.UpToDate, c.MissingSome, c.CantTell);
            Assert.True(counts is null || counts == snapshot);
            counts = snapshot;
        }
        return new Served(answers, counts!);
    }

    private static BrowseCompletionCountsSnapshot CountsOf(Dictionary<long, SeriesAnswer?> answers)
    {
        int Of(SeriesAnswer a) => answers.Values.Count(v => v == a);
        return new BrowseCompletionCountsSnapshot(Of(SeriesAnswer.HaveItAll), Of(SeriesAnswer.FinishedMissing), Of(SeriesAnswer.UpToDate),
            Of(SeriesAnswer.MissingSome), Of(SeriesAnswer.CantTell));
    }

    /// <summary>The browse serves exactly the old loader's answers and counts on the current rows.</summary>
    private static async Task AssertServesOldAnswersAsync(Seeded s, CatalogBrowseService browse)
    {
        var expected = await OldAnswersAsync(s);
        var served = await ServedAsync(s, browse);
        Assert.Equal(expected.OrderBy(e => e.Key), served.Answers.OrderBy(e => e.Key));
        Assert.Equal(CountsOf(expected), served.Counts);
    }

    private sealed class Probe : ILogger<CatalogBrowseService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        public int Reused => Lines.Count(l => l.Contains("reused True", StringComparison.Ordinal));

        public int Computed => Lines.Count(l => l.Contains("reused False", StringComparison.Ordinal) && l.Contains("memo False", StringComparison.Ordinal));
    }

    private sealed record Kit(Seeded S, CatalogBrowseService Browse, Probe Log, ShiftableTimeProvider Clock, MemoryCache Cache) : IDisposable
    {
        public void Dispose()
        {
            Cache.Dispose();
            S.Db.Dispose();
        }
    }

    private async Task<Kit> KitAsync()
    {
        var s = await SeedAsync();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var log = new Probe();
        var clock = new ShiftableTimeProvider();
        return new Kit(s, new CatalogBrowseService(s.Db, new LibraryAuthorizationService(s.Db), logger: log, cache: cache, time: clock), log, clock, cache);
    }

    /// <summary>Warms the cache and proves a first page with unchanged inputs reuses the answers (else the tests below prove nothing).</summary>
    private static async Task<Dictionary<long, SeriesAnswer?>> WarmAsync(Kit k)
    {
        await AssertServesOldAnswersAsync(k.S, k.Browse);
        Assert.Equal(1, k.Log.Computed);
        var before = await OldAnswersAsync(k.S);
        var reusedBefore = k.Log.Reused;
        await AssertServesOldAnswersAsync(k.S, k.Browse);
        Assert.Equal(1, k.Log.Computed);
        Assert.True(k.Log.Reused > reusedBefore);
        return before;
    }

    /// <summary>After a change: the old loader's answer for the series changed, and the browse serves the new answers at once.</summary>
    private static async Task AssertChangeServedAsync(Kit k, Dictionary<long, SeriesAnswer?> before, string key)
    {
        var after = await OldAnswersAsync(k.S);
        var id = k.S.Series[key].Id;
        string Dump(Dictionary<long, SeriesAnswer?> a) =>
            string.Join(", ", k.S.Series.Select(e => $"{e.Key}={(a.TryGetValue(e.Value.Id, out var v) ? v?.ToString() ?? "none" : "unlinked")}"));
        Assert.True(before.GetValueOrDefault(id) != after.GetValueOrDefault(id) || before.ContainsKey(id) != after.ContainsKey(id),
            $"the change must move the {key} answer; before: {Dump(before)}; after: {Dump(after)}");
        await AssertServesOldAnswersAsync(k.S, k.Browse);
    }

    private static async Task BumpRevisionAsync(Seeded s) =>
        await s.Db.Libraries.Where(l => l.Id == s.Library.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.CatalogRevision, l => l.CatalogRevision + 1));

    // --- Equivalence ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLoader_GivesTheOldEntries_RowsCountsAndCredit()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        await AddScanAsync(s);
        var targets = await LinkedAsync(s);

        var old = await new LegacySeriesProgressLoader(s.Db).LoadAsync(targets, default);
        var now = await new SeriesProgressLoader(s.Db).LoadAsync(targets, default);

        Assert.Equal(old.Keys.Order(), now.Keys.Order());
        foreach (var (id, entry) in old)
        {
            var other = now[id];
            Assert.Equal(JsonSerializer.Serialize(entry.Dto), JsonSerializer.Serialize(other.Dto));
            Assert.Equal(entry.Rows, other.Rows);
            Assert.Equal((entry.VolumeArchives, entry.ChapterArchives), (other.VolumeArchives, other.ChapterArchives));
            Assert.Equal(JsonSerializer.Serialize(entry.ListCredit), JsonSerializer.Serialize(other.ListCredit));
        }
        // The seed reaches the paths it is meant to: a Wikipedia credit and a scanned volume.
        Assert.NotNull(now[s.Series["wikipedia"].Id].ListCredit);
        Assert.Contains(now[s.Series["scanned"].Id].Rows, r => r.InVolumeFile is not null);
    }

    [Fact]
    public async Task TheFilter_ServesTheOldAnswersAndCounts_Computed_Reused_AndAfterALinkChangeADeclaredFactAndANewVolumeMap()
    {
        using var k = await KitAsync();
        var s = k.S;
        await WarmAsync(k);

        // A link change (the series re-pointed to another record), a declared-fact edit (tracking off) and a new volume map.
        var link = await s.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == s.Series["finished"].Id);
        link.RecordId = s.Records["finished-other"].Id;
        link.UpdatedAt = DateTimeOffset.UtcNow;
        await s.Db.SaveChangesAsync();
        await AssertServesOldAnswersAsync(s, k.Browse);
        await AddDeclaredAsync(s, "declared", DeclaredFactKeys.Tracking, DeclaredFactKeys.TrackingOff);
        await AssertServesOldAnswersAsync(s, k.Browse);
        await VolumeTestData.AddMapAsync(s.Db, s.Records["chapters"].Id, chaptersPerVolume: 5, volumes: [(1, 1, 5), (2, 6, 10)]);
        await AssertServesOldAnswersAsync(s, k.Browse);

        // ... and with nothing changed since, the next first page reuses them again.
        var reused = k.Log.Reused;
        await AssertServesOldAnswersAsync(s, k.Browse);
        Assert.True(k.Log.Reused > reused);
    }

    [Fact]
    public async Task TheVolumeContentsReader_ReadsManyScopesTogether_WithEachScopesOldAnswerAndOrder()
    {
        var s = await SeedAsync();
        using var _ = s.Db;
        var lib = s.Library.Id;
        var folder = await VolumeTestData.AddFolderAsync(s.Db, lib, null, "Synthetic Kappa");
        var low = await VolumeTestData.AddArchiveAsync(s.Db, lib, folder.Id, "Synthetic Kappa v01");
        var high = await VolumeTestData.AddArchiveAsync(s.Db, lib, folder.Id, "Synthetic Kappa v02");
        var shared = await VolumeTestData.AddArchiveAsync(s.Db, lib, folder.Id, "Synthetic Kappa c005");
        var other = await VolumeTestData.AddArchiveAsync(s.Db, lib, folder.Id, "Synthetic Kappa c006");
        var changed = await VolumeTestData.AddArchiveAsync(s.Db, lib, folder.Id, "Synthetic Kappa c007");
        // Both volume files claim chapter 5 (the first answer read wins it in VolumeContents.Apply, so the order matters); the second also
        // claims a chapter of another scope and a chapter whose file changed since the scan.
        AddContentRow(s, low, folder, 1, VolumeContentRowState.Known, (shared, 5));
        AddContentRow(s, high, folder, 2, VolumeContentRowState.Known, (shared, 5), (other, 6), (changed, 7));
        await s.Db.SaveChangesAsync();
        await s.Db.ArchiveItems.Where(a => a.NodeId == changed.Id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ContentVersion, 2));

        // Scope A lists the higher volume first and the lower one past the first chunk of ids (padding ids that hold nothing).
        var scopeA = new Dictionary<long, string> { [high.Id] = "h", [shared.Id] = "s" };
        for (var i = 0; i < VolumeContentReader.Chunk + 20; i++)
            scopeA[1_000_000 + i] = "pad" + i;
        scopeA[low.Id] = "l";
        scopeA[changed.Id] = "c";
        var scopeB = new Dictionary<long, string> { [low.Id] = "l2", [high.Id] = "h2", [shared.Id] = "s2", [other.Id] = "o2" };
        var scopeC = new Dictionary<long, string> { [other.Id] = "o3" };
        var scopeEmpty = new Dictionary<long, string>();
        IReadOnlyList<IReadOnlyDictionary<long, string>> scopes = [scopeA, scopeB, scopeC, scopeEmpty];

        var many = await VolumeContentReader.LoadManyAsync(s.Db, scopes, default);
        Assert.Equal(scopes.Count, many.Count);
        for (var i = 0; i < scopes.Count; i++)
        {
            var old = await LegacyVolumeContentReader.LoadAsync(s.Db, scopes[i], default);
            Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(many[i]));
            Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(await VolumeContentReader.LoadAsync(s.Db, scopes[i], default)));
        }
        // The cases are really there: scope A reads the higher volume first (chunk order), scope B by id.
        Assert.Equal(["h", "l"], many[0].Select(v => v.VolumeRowId));
        Assert.Equal(["l2", "h2"], many[1].Select(v => v.VolumeRowId));
        Assert.Empty(many[2]);
    }

    [Fact]
    public void StoredMaps_ReadsTheSameListsAsTheOldHelpers()
    {
        static SeriesVolumeMapEntity Map(VolumeMapSource source, VolumeMapState state, string? volumes, string? unassigned = null) =>
            new() { Source = (int)source, State = (int)state, VolumesJson = volumes, UnassignedJson = unassigned, ContentHash = "x" };
        var nearEmpty = Map(VolumeMapSource.MangaDexAggregate, VolumeMapState.Ok, """[{"v":"1","c":["1"]}]""", """["2","3","4","5","6"]""");
        var mangadex = Map(VolumeMapSource.MangaDexAggregate, VolumeMapState.Ok, """[{"v":"1","c":["1","2"]},{"v":"2","c":["3","4"]}]""", """["9"]""");
        var failed = Map(VolumeMapSource.MangaDexAggregate, VolumeMapState.Failed, """[{"v":"1","c":["1","2"]}]""");
        var wikipedia = Map(VolumeMapSource.WikipediaList, VolumeMapState.Ok, """[{"v":"1","c":["1","2"]},{"v":"3","c":["5","6"]}]""");
        var aniList = new SeriesVolumeMapEntity
        {
            Source = (int)VolumeMapSource.AniListRatio,
            State = (int)VolumeMapState.Ok,
            ChaptersPerVolume = 4,
            KnownVolumeCount = 7,
            ContentHash = "x",
        };
        var record = new SeriesProgressLoader.RecordRow(1, (int)MetadataOrigin.Japan, (int)MetadataOriginStatus.Ongoing, 7, "20 Chapters (Ongoing)", 12,
            null, null, null, null);
        var legacyRecord = new LegacySeriesProgressLoader.RecordRow(1, (int)MetadataOrigin.Japan, (int)MetadataOriginStatus.Ongoing, 7,
            "20 Chapters (Ongoing)", 12, null, null, null, null);
        List<SeriesVolumeMapEntity>[] sets =
        [
            [], [mangadex], [nearEmpty], [failed], [wikipedia], [aniList], [mangadex, wikipedia], [nearEmpty, wikipedia, aniList],
            [failed, mangadex, aniList], [nearEmpty, mangadex],
        ];
        foreach (var set in sets)
        {
            Assert.Equal(JsonSerializer.Serialize(LegacySeriesProgressLoader.ExactList(set)), JsonSerializer.Serialize(SeriesProgressLoader.ExactList(set)));
            Assert.Equal(JsonSerializer.Serialize(LegacySeriesProgressLoader.MapAndFacts(set, legacyRecord, "en"), WithFields),
                JsonSerializer.Serialize(SeriesProgressLoader.MapAndFacts(set, record, "en"), WithFields));
            Assert.Equal(JsonSerializer.Serialize(LegacySeriesProgressLoader.ListCreditOf(set, """[{"t":"A Page","r":3}]""")),
                JsonSerializer.Serialize(SeriesProgressLoader.ListCreditOf(set, """[{"t":"A Page","r":3}]""")));
        }
    }

    // --- Invalidation: one test per input ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Input_CatalogRevision_AScanThatAddsTheMissingVolume_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await VolumeTestData.AddArchiveAsync(k.S.Db, k.S.Library.Id, k.S.Series["finished"].Id, "Synthetic Gamma v02");
        await BumpRevisionAsync(k.S);
        await AssertChangeServedAsync(k, before, "finished");
    }

    [Fact]
    public async Task Input_TheLevelsLinks_AReLinkedSeries_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        var link = await k.S.Db.NodeSeriesLinks.SingleAsync(l => l.NodeId == k.S.Series["finished"].Id);
        link.RecordId = k.S.Records["finished-other"].Id;
        link.UpdatedAt = DateTimeOffset.UtcNow;
        await k.S.Db.SaveChangesAsync();
        await AssertChangeServedAsync(k, before, "finished");
    }

    [Fact]
    public async Task Input_LinkRowsBelowTheLevel_AUnitSubfolderGivenItsOwnLink_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await VolumeTestData.LinkAsync(k.S.Db, k.S.Nodes["Volumes"], null, SeriesLinkState.DontMatch);
        await AssertChangeServedAsync(k, before, "units");
    }

    [Fact]
    public async Task Input_DeclaredFacts_TrackingTurnedOff_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await AddDeclaredAsync(k.S, "declared", DeclaredFactKeys.Tracking, DeclaredFactKeys.TrackingOff);
        await AssertChangeServedAsync(k, before, "declared");
    }

    [Fact]
    public async Task Input_DeclaredFacts_AnEditionsVolumeTotal_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await AddDeclaredAsync(k.S, "declared", DeclaredFactKeys.Volumes, "3");
        await AssertChangeServedAsync(k, before, "declared");
    }

    [Fact]
    public async Task Input_PreferredLanguage_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await VolumeTestData.SetPreferredLanguageAsync(k.S.Db, "fr");
        await AssertChangeServedAsync(k, before, "running");
    }

    [Fact]
    public async Task Input_TheRecord_ASeriesThatFinished_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        var record = await k.S.Db.MetadataRecords.SingleAsync(r => r.Id == k.S.Records["running"].Id);
        record.OriginStatus = (int)MetadataOriginStatus.Complete;
        record.OriginVolumes = 2;
        await k.S.Db.SaveChangesAsync();
        await AssertChangeServedAsync(k, before, "running");
    }

    [Fact]
    public async Task Input_VolumeMaps_ANewMap_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await VolumeTestData.AddMapAsync(k.S.Db, k.S.Records["chapters"].Id, chaptersPerVolume: 5, volumes: [(1, 1, 5), (2, 6, 10), (3, 11, 15)]);
        await AssertChangeServedAsync(k, before, "chapters");
    }

    [Fact]
    public async Task Input_VolumeMaps_AChangedList_IsServedAtOnce()
    {
        using var k = await KitAsync();
        await VolumeTestData.AddMapAsync(k.S.Db, k.S.Records["chapters"].Id, chaptersPerVolume: 4, volumes: [(1, 1, 4), (2, 5, 8), (3, 9, 12)]);
        var before = await WarmAsync(k);
        // As VolumeMapService.StoreAsync writes a changed answer: the content, its hash and the version.
        var map = await k.S.Db.SeriesVolumeMaps.SingleAsync(m => m.RecordId == k.S.Records["chapters"].Id);
        map.VolumesJson = """[{"v":"1","c":["1","2","3","4","5"]},{"v":"2","c":["6","7","8","9","10"]},{"v":"3","c":["11","12","13","14","15"]}]""";
        map.ChaptersPerVolume = 5;
        map.ContentHash = "changed";
        map.Version++;
        await k.S.Db.SaveChangesAsync();
        await AssertChangeServedAsync(k, before, "chapters");
    }

    [Fact]
    public async Task Input_ComicInfo_AVolumeReadFromAFile_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await VolumeTestData.AddComicInfoAsync(k.S.Db, k.S.Nodes["Synthetic Epsilon Gamma"].Id, 3, null);
        await AssertChangeServedAsync(k, before, "comicinfo");
    }

    [Fact]
    public async Task Input_VolumeContents_AStoredScanAnswer_IsServedAtOnce()
    {
        using var k = await KitAsync();
        var before = await WarmAsync(k);
        await AddScanAsync(k.S);
        VolumeContentReader.Bump(); // as VolumeContentScanner does after storing an answer
        await AssertChangeServedAsync(k, before, "scanned");
    }

    [Fact]
    public async Task UnchangedInputs_AreReusedOnlyUpToTheMaximumAge()
    {
        using var k = await KitAsync();
        await WarmAsync(k);
        k.Clock.Advance(CatalogBrowseService.CompletionAnswersMaxAge + TimeSpan.FromSeconds(1));
        await AssertServesOldAnswersAsync(k.S, k.Browse);
        Assert.Equal(2, k.Log.Computed);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------------------------

    private static async Task AddDeclaredAsync(Seeded s, string series, string key, string value)
    {
        s.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = s.Library.Id,
            NodeId = s.Series[series].Id,
            Key = key,
            Value = value,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await s.Db.SaveChangesAsync();
    }

    /// <summary>The volume contents scan proved chapters 11-13 inside the third volume file of the scanned series.</summary>
    private static async Task AddScanAsync(Seeded s)
    {
        var volume = s.Nodes["Synthetic Theta v03"];
        AddContentRow(s, volume, s.Series["scanned"], 3, VolumeContentRowState.Known,
            (s.Nodes["Synthetic Theta c011"], 11), (s.Nodes["Synthetic Theta c012"], 12), (s.Nodes["Synthetic Theta c013"], 13));
        await s.Db.SaveChangesAsync();
    }

    private static void AddContentRow(Seeded s, CatalogNodeEntity volume, CatalogNodeEntity folder, int number, VolumeContentRowState state,
        params (CatalogNodeEntity Node, int Number)[] chapters)
    {
        long Version(CatalogNodeEntity n) => s.Db.ArchiveItems.AsNoTracking().Single(a => a.NodeId == n.Id).ContentVersion;
        s.Db.VolumeContents.Add(new VolumeContentEntity
        {
            VolumeNodeId = volume.Id,
            VolumeContentVersion = Version(volume),
            Volume = number,
            FolderId = folder.Id,
            State = (int)state,
            ChaptersJson = JsonSerializer.Serialize(chapters.Select((c, i) =>
                new StoredFoundChapter(c.Node.Id, Version(c.Node), c.Number, 2 + i * 10, 11 + i * 10, 10, 10)), VolumeContentReader.Json),
            CandidatesKey = "k",
            ScannedAt = DateTimeOffset.UtcNow,
        });
    }
}
