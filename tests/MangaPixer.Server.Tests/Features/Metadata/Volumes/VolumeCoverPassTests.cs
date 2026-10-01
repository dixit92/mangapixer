namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>A cover renderer that never starts a worker: it writes a small file and answers a hash of the input bytes.</summary>
public sealed class FakeCoverRenderer : ICoverRenderer
{
    public List<CoverRenderRequest> Requests { get; } = [];
    public List<bool> InputExistedAfter { get; } = [];
    public string? FailWith { get; set; }

    public async Task<CoverRenderOutcome> RenderAsync(CoverRenderRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (FailWith is { } error)
            return new CoverRenderOutcome { Success = false, ErrorType = error };
        var input = await File.ReadAllBytesAsync(request.ImagePath!, ct);
        await File.WriteAllBytesAsync(request.OutputPath, Encoding.ASCII.GetBytes("RIFF0000WEBP"), ct);
        return new CoverRenderOutcome
        {
            Success = true,
            OutputPath = request.OutputPath,
            Width = 400,
            Height = 632,
            SourceWidth = 512,
            SourceHeight = 809,
            Hash = BitConverter.ToUInt64(SHA256.HashData(input), 0),
        };
    }
}

/// <summary>
/// The volume-cover pass over a <see cref="MetadataTestDb"/> (service-with-DB): the auto-match harness's gateway with the
/// REAL named clients, whose primary handler replays the recorded MangaDex answers (Fixtures/MangaDex) and synthetic
/// images; a <see cref="FakeCoverRenderer"/> instead of a worker. No automatic pacing (tests do not wait).
/// </summary>
public sealed class VolumePassHarness : IDisposable
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage>? _inner;

    public VolumePassHarness(MetadataTestDb db)
    {
        Db = db;
        // No automatic pacing and roomy MangaDex buckets: tests do not wait for tokens.
        var fast = new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = 1000,
            TokensPerPeriod = 1000,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(10),
            QueueLimit = 10,
            AutoReplenishment = true,
        };
        Auto = new AutoMatchHarness(db, new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero, MangaDexApi = fast, MangaDexImages = fast });
        Root = Path.Combine(Path.GetTempPath(), "mangapixer-vc-" + Guid.NewGuid().ToString("N")[..8]);
        Store = new VolumeCoverStore(Path.Combine(Root, VolumeCoverStore.FolderName));
        Scratch = new ScratchWorkspaceManager(Path.Combine(Root, "scratch"));
        Thumbnails = new ThumbnailStore(Path.Combine(Root, "thumbnails"));
        _inner = Auto.Handler.Respond;
        Auto.Handler.Respond = Route;
    }

    public MetadataTestDb Db { get; }
    public AutoMatchHarness Auto { get; }
    public string Root { get; }
    public VolumeCoverStore Store { get; }
    public ScratchWorkspaceManager Scratch { get; }
    public ThumbnailStore Thumbnails { get; }
    public FakeCoverRenderer Renderer { get; } = new();

    /// <summary>The real worker renderer for process tests (else <see cref="Renderer"/>).</summary>
    public ICoverRenderer? RendererOverride { get; set; }

    /// <summary>The image bytes MangaDex's image host answers (default: PNG magic bytes + the path).</summary>
    public byte[]? ImageBytes { get; set; }
    public VolumeCoverPassState PassState { get; } = new();
    public ScriptedHandler Handler => Auto.Handler;
    public ManualTime Time => Auto.Time;
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    /// <summary>AniList answers by id (GraphQL body contains the id).</summary>
    public Dictionary<int, string> AniListById { get; } = [];

    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        if (Override?.Invoke(request) is { } overridden)
            return overridden;
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.MangaDexImageHost)
            return ScriptedHandler.Bytes(ImageBytes ?? [.. MuFixtures.Png, .. Encoding.ASCII.GetBytes(uri.AbsolutePath)], "image/jpeg");
        if (uri.Host == MetadataHttp.MangaDexApiHost)
            return MdFixtures.Route(request)!;
        if (uri.Host == MetadataHttp.AniListHost)
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            foreach (var (id, json) in AniListById)
            {
                if (body.Contains("\"id\":" + id, StringComparison.Ordinal))
                    return ScriptedHandler.Json(json);
            }
            return ScriptedHandler.Json("{\"data\":{\"Page\":{\"media\":[]}}}");
        }
        return _inner!(request);
    }

    private ILogger<T> Log<T>() => Auto.Net.LoggerFactory.CreateLogger<T>();

    public CompanionLinkService Companions() =>
        new(Db.Db, Auto.Net.Gateway(), new MangaDexProvider(Auto.Net.HttpFactory), Store, Time, Log<CompanionLinkService>());

    public VolumeMapService Maps() =>
        new(Db.Db, Auto.Net.Gateway(), new MangaDexProvider(Auto.Net.HttpFactory), Auto.Net.Conversion(), Companions(), Time, Log<VolumeMapService>());

    public VolumeCoverFetcher Fetcher() => new(Db.Db, Auto.Net.Gateway(), RendererOverride ?? Renderer, Scratch, Store, Time, Log<VolumeCoverFetcher>());

    public VolumeCoverPass Pass(IDictionary<string, string?>? config = null) => new(
        Db.Db, Auto.Service(), config is null ? Auto.Net.Settings() : Settings(config), Companions(), Maps(), Fetcher(),
        new NullHasher(), Thumbnails, new CoverHashCache(), PassState, Time, Log<VolumeCoverPass>(), Decisions);

    /// <summary>The cover layer's queue: the pass asks it for a sweep after storing covers.</summary>
    public com.lifepixer.mangapixer.Server.Features.Covers.CoverDecisionQueue Decisions { get; } = new();

    private MetadataSettingsService Settings(IDictionary<string, string?> config) => new(
        Db.Db, new AuditService(Db.Db), new ConfigurationBuilder().AddInMemoryCollection(config).Build(), Time,
        Log<MetadataSettingsService>());

    /// <summary>Forgets the requests seen so far, keeping the routing.</summary>
    public void ResetRequests(bool failOnAnyRequest = false)
    {
        var respond = Handler.Respond;
        Handler.Reset();
        Handler.Respond = respond;
        Handler.FailOnAnyRequest = failOnAnyRequest;
    }

    public VolumeCoverAdminService Admin() => new(Db.Db, Auto.Net.Resolver(), Companions(), Pass(), Auto.Net.Settings(), Store, Auto.State,
        new AuditService(Db.Db), Log<VolumeCoverAdminService>());

    /// <summary>Runs one tick with a clean change tracker (as the hosted service does, one scope per tick).</summary>
    public async Task<VolumeCoverPassResult> TickAsync(IDictionary<string, string?>? config = null)
    {
        Db.Db.ChangeTracker.Clear();
        var result = await Pass(config).RunTickAsync();
        Db.Db.ChangeTracker.Clear();
        return result;
    }

    public IReadOnlyList<Uri> MangaDexRequests() =>
        Handler.Seen.Where(s => s.Uri.Host is MetadataHttp.MangaDexApiHost or MetadataHttp.MangaDexImageHost).Select(s => s.Uri).ToList();

    private sealed class NullHasher : ICoverHasher
    {
        public Task<ulong?> HashFileAsync(string path, CancellationToken ct) => Task.FromResult<ulong?>(null);
    }

    public void Dispose()
    {
        Auto.Dispose();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Service-with-DB tests (1.29.0) of the background volume-cover / volume-list pass: what it sends (only the linked
/// MangaUpdates title, MangaDex ids, the fixed filters), the cross-link on recorded answers, the stored lists and the
/// foreign-numbering guard, the AniList fallback by the MangaDex AniList link, cover downloads (volume 1 first, the
/// original language when the preferred one is missing, the worker seam, paths), the refusals that pause it without a
/// request (switches, allowlist, backoff), "Don't match", the refresh cadence and the time slice. Zero real network.
/// </summary>
public sealed class VolumeCoverPassTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private async Task<(CatalogNodeEntity Folder, MetadataRecordEntity Record)> SeriesAsync(
        string muId, string title, int? volumes, params string[] archives)
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf " + muId);
        foreach (var name in archives)
            await _t.AddArchiveAsync(folder, name);
        var record = await _t.AddRecordAsync(muId, title);
        record.OriginVolumes = volumes;
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Auto);
        return (folder, record);
    }

    [Fact]
    public async Task Pass_FindsTheCompanion_StoresBothLists_AndDownloadsVolume1ThenHeldVolumes()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (_, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz", "Synthetic Shelf v02.cbz");

        var result = await _h.TickAsync();

        Assert.Null(result.WaitingCode);
        var sent = _h.MangaDexRequests();
        // search + one cover page (99 covers) + aggregate + aggregate in the preferred language + two images (volumes 1 and 2).
        Assert.Equal(6, sent.Count);
        Assert.Equal(6, result.Requests);
        Assert.Equal("/manga", sent[0].AbsolutePath);
        Assert.StartsWith("?title=Berserk&", Uri.UnescapeDataString(sent[0].Query), StringComparison.Ordinal);
        Assert.DoesNotContain(sent, u => Uri.UnescapeDataString(u.ToString()).Contains("Synthetic", StringComparison.Ordinal)); // no folder or file name
        Assert.Contains(sent, u => u.AbsolutePath == $"/manga/{MdFixtures.BerserkId}/aggregate" && u.Query == "?includeUnavailable=1");
        Assert.Contains(sent, u => u.AbsolutePath == $"/manga/{MdFixtures.BerserkId}/aggregate"
            && Uri.UnescapeDataString(u.Query) == "?includeUnavailable=1&translatedLanguage[]=en");
        var covers = sent.Single(u => u.AbsolutePath == "/cover");
        Assert.Contains("locales[]=en&locales[]=ja", Uri.UnescapeDataString(covers.Query), StringComparison.Ordinal);
        Assert.All(sent.Where(u => u.Host == MetadataHttp.MangaDexImageHost), u =>
        {
            Assert.StartsWith($"/covers/{MdFixtures.BerserkId}/", u.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(".512.jpg", u.AbsolutePath, StringComparison.Ordinal);
        });
        Assert.All(_h.Handler.Seen, s => Assert.False(s.Headers.ContainsKey("Via")));
        Assert.All(_h.Handler.Seen, s => Assert.Equal(MetadataHttp.UserAgent, s.Headers["User-Agent"]));

        var companion = await _t.Db.MetadataCompanions.SingleAsync(c => c.RecordId == record.Id);
        Assert.Equal((int)CompanionState.Auto, companion.State);
        var md = await _t.Db.MetadataRecords.SingleAsync(r => r.Id == companion.CompanionRecordId);
        Assert.Equal(("mangadex", MdFixtures.BerserkId), (md.Provider, md.ExternalId));
        Assert.Contains("\"anilist\":\"30002\"", md.CrossIdsJson, StringComparison.Ordinal);
        Assert.False(await _t.Db.NodeSeriesLinks.AnyAsync(l => l.RecordId == md.Id)); // never linked to a node

        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.RecordId == record.Id);
        Assert.Equal(((int)VolumeMapSource.MangaDexAggregate, (int)VolumeMapState.Ok, 1), (map.Source, map.State, map.Version));
        Assert.Equal(43, map.KnownVolumeCount); // the highest /cover volume
        Assert.True(VolumeMapJson.Read(map.VolumesJson).Count >= 40);
        // Released in the preferred language (English): canonical chapter numbers, ascending.
        Assert.Equal("en", map.ReleasedLanguage);
        var released = VolumeMapJson.ReadChapters(map.ReleasedChaptersJson).Select(c => VolumeMapJson.Parse(c)!.Value).ToList();
        Assert.True(released.Count > 300);
        Assert.Equal(released.Order(), released);
        Assert.Contains(1m, released);

        // Preferred English is missing for Berserk: the Japanese covers of volumes 1 and 2 are stored.
        var stored = await _t.Db.VolumeCovers.Where(c => c.State == (int)VolumeCoverState.Stored).OrderBy(c => c.Volume).ToListAsync();
        Assert.Equal([1, 2], stored.Select(c => c.Volume!.Value));
        Assert.All(stored, c => Assert.Equal(("ja", 0, 1), (c.Locale, c.Variant, c.StoredVersion)));
        Assert.All(stored, c => Assert.NotNull(c.Hash));
        Assert.All(stored, c => Assert.True(File.Exists(_h.Store.PathFor(c.PublicId, c.StoredVersion))));
        Assert.True(await _t.Db.VolumeCovers.AnyAsync(c => c.Kind == (int)VolumeCoverKind.Main && c.RemoteId.StartsWith("main:")));
        // A series with volume 1 covers never downloads its main cover.
        Assert.False(await _t.Db.VolumeCovers.AnyAsync(c => c.Kind == (int)VolumeCoverKind.Main && c.State == (int)VolumeCoverState.Stored));

        // The worker got a SCRATCH file (deleted afterwards) and wrote into the data root's cover store.
        Assert.Equal(2, _h.Renderer.Requests.Count);
        Assert.All(_h.Renderer.Requests, r =>
        {
            Assert.Equal(CoverRenderSources.Image, r.Source);
            Assert.StartsWith(Path.Combine(_h.Root, "scratch"), r.ImagePath, StringComparison.Ordinal);
            Assert.False(File.Exists(r.ImagePath));
            Assert.StartsWith(_h.Store.Root, r.OutputPath, StringComparison.Ordinal);
            Assert.Null(r.ArchivePath);
        });

        // Logs carry no title, search text or address.
        Assert.DoesNotContain(_h.Auto.Net.Logs.Lines, l => l.Contains("Berserk", StringComparison.Ordinal) || l.Contains("uploads.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVolumeHeldAsAllOfItsChapters_GetsItsCover_AVolumeHeldInPartDoesNot()
    {
        // 1.30.0 (owner): MangaDex's exact list gives volume 41 chapters 358-364 and volume 40 chapters 351-357 (+ extras 356.1 /
        // 356.2). All of 41 is here as chapter files; 40 only in part (351-353); 42 by one chapter that names its volume.
        await _h.Auto.EnableAutomaticAsync();
        var chapters = Enumerable.Range(358, 7).Concat([351, 352, 353])
            .Select(c => $"Synthetic Shelf c{c:D3}.cbz").Append("Synthetic Shelf v42 c365.cbz").ToArray();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, chapters);

        var result = await _h.TickAsync();

        Assert.Null(result.WaitingCode);
        var images = _h.MangaDexRequests().Where(u => u.Host == MetadataHttp.MangaDexImageHost).ToList();
        // Volume 1 (always) and volume 41 - never 40 or 42.
        Assert.Equal(2, images.Count);
        var stored = await _t.Db.VolumeCovers.Where(c => c.State == (int)VolumeCoverState.Stored).Select(c => c.Volume).OrderBy(v => v).ToListAsync();
        Assert.Equal([1, 41], stored);
        // The same request kind as for a volume file: a cover image by the id MangaDex returned, nothing from the library.
        Assert.All(images, u => Assert.StartsWith($"/covers/{MdFixtures.BerserkId}/", u.AbsolutePath, StringComparison.Ordinal));
        Assert.DoesNotContain(_h.MangaDexRequests(), u => Uri.UnescapeDataString(u.ToString()).Contains("Synthetic", StringComparison.Ordinal));
    }

    [Fact]
    public void VolumesHeldAsChapters_TheExactListOnly_EveryListedChapter_SplitPartsCount()
    {
        static List<GroupingRow> Rows(params string[] names) => names.Select((n, i) => new GroupingRow(
            i.ToString(System.Globalization.CultureInfo.InvariantCulture), GroupingRowKind.Archive, n, n)).ToList();
        var exact = new List<VolumeMapVolume>
        {
            new(1, [1m, 2m, 3m]),
            new(2, [4m, 5m, 5.5m]), // 5.5 next to its listed whole chapter is an extra: not required
            new(4, [9m, 10m]),      // volume 3 is not listed: only bounded (chapters 6-8) - never downloaded as chapters
        };

        Assert.Equal([2], VolumeCoverPass.VolumesHeldAsChapters(Rows("S c004", "S c005"), exact).Order());
        // A split chapter on disk (4.1 + 4.2) counts as chapter 4.
        Assert.Equal([2], VolumeCoverPass.VolumesHeldAsChapters(Rows("S c004.1", "S c004.2", "S c005"), exact).Order());
        Assert.Empty(VolumeCoverPass.VolumesHeldAsChapters(Rows("S c004"), exact));
        Assert.Empty(VolumeCoverPass.VolumesHeldAsChapters(Rows("S c006", "S c007", "S c008"), exact));
        // A volume FILE is not "held as chapters" (its cover is fetched as a held volume file).
        Assert.Empty(VolumeCoverPass.VolumesHeldAsChapters(Rows("S v02", "S c004", "S c005"), exact));
        Assert.Equal([1, 2, 4], VolumeCoverPass.VolumesHeldAsChapters(
            Rows("S c001", "S c002", "S c003", "S c004", "S c005", "S c009", "S c010"), exact).Order());
        // No exact list: nothing.
        Assert.Empty(VolumeCoverPass.VolumesHeldAsChapters(Rows("S c004", "S c005"), []));
    }

    [Fact]
    public void EstimatedStackVolumes_AreTheViewsTildeStacks_NeverAVolumeFileOrAnExactStack()
    {
        static List<GroupingRow> Rows(params string[] names) => names.Select((n, i) => new GroupingRow(
            i.ToString(System.Globalization.CultureInfo.InvariantCulture), GroupingRowKind.Archive, n, n)).ToList();
        var chapters = Enumerable.Range(1, 25).Select(c => $"S c{c:000}").ToArray();
        // No exact list, an average of 10 chapters per volume over 10 volumes: "~ Volume 1" .. "~ Volume 3".
        var ratio = new VolumeMapInput([], 10, 10, true, VolumeListSource.AniList);

        Assert.Equal([1, 2, 3], VolumeCoverPass.EstimatedStackVolumes(Rows(chapters), ratio).Order());
        // A volume file of volume 2 is a real volume, not an estimated stack.
        Assert.Equal([1, 3], VolumeCoverPass.EstimatedStackVolumes(Rows([.. chapters, "S v02"]), ratio).Order());
        // The exact list places every chapter: exact stacks only (the held-as-chapters rule covers them).
        var exact = new VolumeMapInput([new(1, [.. Enumerable.Range(1, 12).Select(c => (decimal)c)]), new(2, [.. Enumerable.Range(13, 13).Select(c => (decimal)c)])],
            12.5, 2, false, VolumeListSource.MangaDex);
        Assert.Empty(VolumeCoverPass.EstimatedStackVolumes(Rows(chapters), exact));
        // Nothing to place a chapter with: no stack at all.
        Assert.Empty(VolumeCoverPass.EstimatedStackVolumes(Rows(chapters), VolumeMapInput.Empty));
    }

    [Fact]
    public async Task EstimatedStacks_ArePlanned_LikeTheVolumesTheFolderHolds()
    {
        // 1.31.0 (owner 2026-10-01): an estimated "~ Volume 2" stack shows its volume's web cover; the pass plans it (stored rows only).
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf Estimated");
        for (var c = 1; c <= 25; c++)
            await _t.AddArchiveAsync(folder, $"Synthetic Shelf Estimated c{c:000}.cbz");
        var record = await _t.AddRecordAsync("est1", "Synthetic Estimated");
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Auto);
        var md = new MetadataRecordEntity { PublicId = "mdest1", Provider = "mangadex", ExternalId = "00000000-0000-0000-0000-0000000e5701", Title = "Synthetic Estimated", FetchedAt = DateTimeOffset.UtcNow };
        _t.Db.MetadataRecords.Add(md);
        await _t.Db.SaveChangesAsync();
        _t.Db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = record.Id, Provider = "mangadex", CompanionRecordId = md.Id, State = (int)CompanionState.Auto, CheckedAt = DateTimeOffset.UtcNow });
        // MangaDex gives no volume list (foreign numbering); AniList's totals give the average: 10 chapters per volume.
        _t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity { RecordId = record.Id, Source = (int)VolumeMapSource.MangaDexAggregate, State = (int)VolumeMapState.Ok, ContentHash = "e", Version = 1, FetchedAt = DateTimeOffset.UtcNow });
        _t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity { RecordId = record.Id, Source = (int)VolumeMapSource.AniListRatio, State = (int)VolumeMapState.Ok, ChaptersPerVolume = 10, KnownVolumeCount = 10, ContentHash = "a", Version = 1, FetchedAt = DateTimeOffset.UtcNow });
        await _t.Db.SaveChangesAsync();

        Assert.Equal([2, 3], await _h.Pass().PlannedHeldVolumesAsync(record.Id, [folder.Id]));
    }

    [Fact]
    public async Task PendingCovers_CountsWantedCoversNotStoredYet_AndNoneWhileThePassWaits()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (_, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz", "Synthetic Shelf v02.cbz");
        await _h.TickAsync(); // stores volumes 1 and 2

        // Volumes 1 and 2 are stored; volumes 3 and 4 are listed but not downloaded.
        Assert.Equal(0, await _h.Pass().PendingCoversAsync(record.Id, [1, 2]));
        Assert.Equal(1, await _h.Pass().PendingCoversAsync(record.Id, [2, 3]));
        Assert.Equal(2, await _h.Pass().PendingCoversAsync(record.Id, [3, 4, 900])); // 900: no cover listed - never "on its way"

        // Switched off: the pass waits, so nothing is on its way.
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataVolumeCoversEnabled = false;
        await _t.Db.SaveChangesAsync();
        Assert.Equal(0, await _h.Pass().PendingCoversAsync(record.Id, [3]));
    }

    [Fact]
    public async Task PlannedHeldVolumes_AreWhatATickDownloads_SoTheOnTheirWayCountCanClear()
    {
        // 1.30.0 (lane S finding): the view counted covers the pass never fetches as "on their way" forever. The count now reads the
        // pass's own plan - the held volumes step 3 fetches (the short-circuit's skips excluded by the same code).
        await _h.Auto.EnableAutomaticAsync();
        var (folder, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz", "Synthetic Shelf v02.cbz", "Synthetic Shelf v03.cbz");
        await _h.TickAsync(); // lists + downloads volume 1, then the planned held volumes

        var planned = await _h.Pass().PlannedHeldVolumesAsync(record.Id, [folder.Id]);
        Assert.Equal([2, 3], planned);
        var stored = await _t.Db.VolumeCovers.Where(c => c.State == (int)VolumeCoverState.Stored).Select(c => c.Volume).OrderBy(v => v).ToListAsync();
        Assert.Equal([1, 2, 3], stored);
        Assert.Equal(0, await _h.Pass().PendingCoversAsync(record.Id, planned)); // everything planned is stored: nothing on its way
    }

    [Fact]
    public async Task SecondTick_SendsNothing_UntilTheRefreshCadence()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        await _h.TickAsync();
        _h.ResetRequests(failOnAnyRequest: true);

        _h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, (await _h.TickAsync()).Requests);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task AfterTheCadence_TheCompanionIsReadById_AndTheListsAgain_NothingDownloadedTwice()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        await _h.TickAsync();
        var firstVersion = (await _t.Db.SeriesVolumeMaps.SingleAsync()).Version;
        _h.ResetRequests();

        _h.Time.Advance(TimeSpan.FromDays(31)); // ongoing record: 30 days
        await _h.TickAsync();

        var sent = _h.MangaDexRequests();
        Assert.Equal(["/manga/" + MdFixtures.BerserkId, "/cover", $"/manga/{MdFixtures.BerserkId}/aggregate", $"/manga/{MdFixtures.BerserkId}/aggregate"],
            sent.Select(u => u.AbsolutePath));
        Assert.Equal(firstVersion, (await _t.Db.SeriesVolumeMaps.SingleAsync()).Version); // unchanged answer: no new version
    }

    [Fact]
    public async Task ForeignVolumeNumbering_StoresAnEmptyMap()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (_, record) = await SeriesAsync(MdFixtures.MuJojoPart3, "JoJo no Kimyou na Bouken Part 3: Stardust Crusaders", 16, "Synthetic Shelf c001.cbz");

        await _h.TickAsync();

        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.RecordId == record.Id && m.Source == (int)VolumeMapSource.MangaDexAggregate);
        Assert.Equal((int)VolumeMapState.Empty, map.State);
        Assert.Null(map.VolumesJson);
        Assert.Equal(16, map.KnownVolumeCount);
        Assert.Equal(MdFixtures.JojoPart3Id, (await _t.Db.MetadataRecords.SingleAsync(r => r.Provider == "mangadex")).ExternalId);
    }

    [Fact]
    public async Task NoVolumeList_AsksAniListByTheIdMangaDexLinks_AndStoresARatio()
    {
        await _h.Auto.EnableAutomaticAsync();
        _h.AniListById[85143] = """{"data":{"Media":{"id":85143,"title":{"romaji":"Kami no Tou","english":"Tower of God"},"synonyms":[],"format":"MANHWA","status":"FINISHED","volumes":20,"chapters":400,"startDate":{"year":2010},"siteUrl":"https://anilist.co/manga/85143"}}}""";
        var (_, record) = await SeriesAsync(MdFixtures.MuTowerOfGod, "Tower of God", null, "Synthetic Shelf c001.cbz");

        await _h.TickAsync();

        var aniList = _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.AniListHost).ToList();
        var body = Assert.Single(aniList).Body!;
        Assert.Contains("\"id\":85143", body, StringComparison.Ordinal);
        Assert.DoesNotContain("search", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic", body, StringComparison.Ordinal);

        var companion = await _t.Db.MetadataCompanions.SingleAsync(c => c.RecordId == record.Id && c.Provider == "anilist");
        Assert.Equal(((int)CompanionState.Auto, (int)CompanionMethod.FromCompanion), (companion.State, companion.Method));
        var ratio = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.RecordId == record.Id && m.Source == (int)VolumeMapSource.AniListRatio);
        Assert.Equal(((int)VolumeMapState.Ok, 20.0, 20), (ratio.State, ratio.ChaptersPerVolume!.Value, ratio.KnownVolumeCount!.Value));
        // The Missing report reads the same stored AniList row.
        Assert.True(await _t.Db.MetadataRecords.AnyAsync(r => r.Provider == "anilist" && r.ExternalId == "85143"));
    }

    [Fact]
    public async Task NoVolume1Cover_DownloadsTheMainCover_Once()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuTowerOfGod, "Tower of God", null, "Synthetic Shelf c001.cbz");

        await _h.TickAsync();

        var images = _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexImageHost).ToList();
        var image = Assert.Single(images);
        Assert.StartsWith($"/covers/{MdFixtures.TowerOfGodId}/", image.Uri.AbsolutePath, StringComparison.Ordinal);
        var main = await _t.Db.VolumeCovers.SingleAsync(c => c.Kind == (int)VolumeCoverKind.Main);
        Assert.Equal((int)VolumeCoverState.Stored, main.State);
        Assert.True(File.Exists(_h.Store.PathFor(main.PublicId, main.StoredVersion)));
        Assert.Equal(CoverRenderSources.Image, Assert.Single(_h.Renderer.Requests).Source);

        _h.ResetRequests(failOnAnyRequest: true);
        _h.Time.Advance(TimeSpan.FromDays(1));
        await _h.TickAsync();
        Assert.Equal(0, _h.Handler.CallCount); // stored: not fetched again
    }

    [Fact]
    public async Task StoredCovers_AskTheCoverLayerForASweep_NoCoversNoSweep()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        await _h.TickAsync();
        Assert.True(_h.Decisions.TakeSweepRequest());

        _h.Time.Advance(TimeSpan.FromDays(1));
        await _h.TickAsync(); // nothing new stored
        Assert.False(_h.Decisions.TakeSweepRequest());
    }

    [Fact]
    public async Task ANewPreferredLanguage_ReadsTheReleasedChaptersForIt_AndBumpsTheVersion()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (_, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        await _h.TickAsync();
        var before = await _t.Db.SeriesVolumeMaps.AsNoTracking().SingleAsync(m => m.RecordId == record.Id);
        Assert.Equal("en", before.ReleasedLanguage);

        // As the settings hook does: a new preferred language makes every MangaDex list due.
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataCoverLanguage = "fr";
        await _t.Db.SaveChangesAsync();
        await _t.Db.SeriesVolumeMaps.ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, (DateTimeOffset?)null));
        _h.ResetRequests();

        await _h.TickAsync();

        Assert.Contains(_h.MangaDexRequests(), u => Uri.UnescapeDataString(u.Query) == "?includeUnavailable=1&translatedLanguage[]=fr");
        var after = await _t.Db.SeriesVolumeMaps.AsNoTracking().SingleAsync(m => m.RecordId == record.Id);
        Assert.Equal(("fr", "[]"), (after.ReleasedLanguage, after.ReleasedChaptersJson)); // nothing recorded as released in French
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.VolumesJson, after.VolumesJson);
    }

    [Fact]
    public async Task AniListRemovedFromTheAllowlist_SkipsTheFallback_MangaDexWorkGoesOn()
    {
        await _h.Auto.EnableAutomaticAsync();
        Assert.Null(await _h.Auto.Net.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = ["anilist"] }, "admin"));
        await SeriesAsync(MdFixtures.MuTowerOfGod, "Tower of God", null, "Synthetic Shelf c001.cbz");

        var result = await _h.TickAsync();

        Assert.Null(result.WaitingCode);
        Assert.DoesNotContain(_h.Handler.Seen, s => s.Uri.Host == MetadataHttp.AniListHost);
        Assert.NotEmpty(_h.MangaDexRequests());
        Assert.False(await _t.Db.MetadataCompanions.AnyAsync(c => c.Provider == "anilist"));
    }

    [Fact]
    public async Task TheCrossLinkRule_RejectsANamesake_TriesOneAssociatedTitle_ThenWaitsForTheCadence()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (_, record) = await SeriesAsync(MdFixtures.MuJigokuraku2005, "Jigokuraku", 1, "Synthetic Shelf.cbz");
        record.AltTitlesJson = "[\"地獄楽\",\"Hell Paradise Oneshot\"]";
        await _t.Db.SaveChangesAsync();

        await _h.TickAsync();

        var searches = _h.MangaDexRequests().Where(u => u.AbsolutePath == "/manga").Select(u => Uri.UnescapeDataString(u.Query)).ToList();
        Assert.Equal(2, searches.Count);
        Assert.StartsWith("?title=Jigokuraku&", searches[0], StringComparison.Ordinal);
        Assert.StartsWith("?title=Hell Paradise Oneshot&", searches[1], StringComparison.Ordinal);
        var companion = await _t.Db.MetadataCompanions.SingleAsync();
        Assert.Equal((int)CompanionState.NotFound, companion.State);
        Assert.Null(companion.CompanionRecordId);
        Assert.False(await _t.Db.MetadataRecords.AnyAsync(r => r.Provider == "mangadex"));

        _h.ResetRequests(failOnAnyRequest: true);
        _h.Time.Advance(TimeSpan.FromDays(2));
        await _h.TickAsync();
        Assert.Equal(0, _h.Handler.CallCount);
    }

    public static TheoryData<string> Refusals => new() { "automatic_off", "provider_not_allowed", "volume_covers_off", "volume_covers_disabled", "metadata_disabled" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachSwitch_PausesThePass_WithoutARequest(string code)
    {
        await _h.Auto.EnableAutomaticAsync(automatic: code != "automatic_off");
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        var row = await _t.Db.AppSettings.FirstAsync();
        if (code == "provider_not_allowed")
            row.MetadataProvidersJson = MetadataProviderAllowlist.Write(["mangadex"]);
        if (code == "volume_covers_off")
            row.MetadataVolumeCoversEnabled = false;
        if (code == "metadata_disabled")
            row.MetadataConsentVersion = MetadataConsent.CurrentVersion - 1;
        await _t.Db.SaveChangesAsync();
        _h.Handler.FailOnAnyRequest = true;

        var result = await _h.TickAsync(code == "volume_covers_disabled"
            ? new Dictionary<string, string?> { ["Metadata:AutoMatch:VolumeCovers"] = "false" }
            : null);

        Assert.Equal(code, result.WaitingCode);
        Assert.Equal(code, _h.PassState.WaitingCode);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.False(await _t.Db.MetadataCompanions.AnyAsync()); // nothing counted as an attempt
    }

    [Fact]
    public async Task NothingUnderDontMatch_IsLookedUp()
    {
        await _h.Auto.EnableAutomaticAsync();
        var shelf = await _t.AddFolderAsync(null, "Synthetic Do Not Touch");
        await _t.AddLinkAsync(shelf, null, SeriesLinkState.DontMatch);
        var folder = await _t.AddFolderAsync(shelf, "Synthetic Series");
        await _t.AddArchiveAsync(folder, "Synthetic Series v01.cbz");
        await _t.AddLinkAsync(folder, await _t.AddRecordAsync(MdFixtures.MuBerserk, "Berserk"), SeriesLinkState.Confirmed);
        _h.Handler.FailOnAnyRequest = true;

        var result = await _h.TickAsync();

        Assert.Null(result.WaitingCode);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.Empty(await _h.Pass().EligibleSeriesAsync());
    }

    [Fact]
    public async Task AMangaDex429_PausesMangaDexOnly()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        _h.Override = request =>
        {
            if (request.RequestUri!.Host != MetadataHttp.MangaDexApiHost)
                return null;
            var r = ScriptedHandler.Json("{\"result\":\"error\"}", HttpStatusCode.TooManyRequests);
            r.Headers.Add(MetadataHttp.RateLimitRetryAfterHeader, _h.Time.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return r;
        };

        var result = await _h.TickAsync();

        Assert.Equal("provider_backoff", result.WaitingCode);
        Assert.Equal(1, _h.Handler.CallCount);
        var state = await _t.Db.MetadataProviderStates.SingleAsync(s => s.Provider == "mangadex");
        Assert.NotNull(state.BackoffUntil);
        Assert.Equal("rate_limited", state.LastErrorCode);
        Assert.Null((await _t.Db.AppSettings.FirstAsync()).MetadataBackoffUntil); // MangaUpdates is not paused
        Assert.Null(await _h.Auto.Service().CheckGlobalGateAsync()); // automatic matching still runs

        _h.ResetRequests(failOnAnyRequest: true);
        Assert.Equal("provider_backoff", (await _h.TickAsync()).WaitingCode);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task AMangaDex403_IsASlowDownToo()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        _h.Override = request => request.RequestUri!.Host == MetadataHttp.MangaDexApiHost
            ? ScriptedHandler.Json("{}", HttpStatusCode.Forbidden)
            : null;

        Assert.Equal("provider_backoff", (await _h.TickAsync()).WaitingCode);
        Assert.Equal("http_403", (await _t.Db.MetadataProviderStates.SingleAsync()).LastErrorCode);
    }

    [Fact]
    public async Task ATick_SendsAtMostTheSlice_AndTheNextTickGoesOn()
    {
        await _h.Auto.EnableAutomaticAsync();
        // AniList off the allowlist: one MangaDex search per series, nothing else.
        Assert.Null(await _h.Auto.Net.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = ["anilist"] }, "admin"));
        for (var i = 0; i < VolumeCoverPass.SliceRequests + 10; i++)
            await SeriesAsync((70_000_000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "Synthetic Unlisted Title " + i, 3);

        var first = await _h.TickAsync();
        Assert.Equal(VolumeCoverPass.SliceRequests, first.Requests);
        Assert.Equal(VolumeCoverPass.SliceRequests, await _t.Db.MetadataCompanions.CountAsync());

        var second = await _h.TickAsync();
        Assert.Equal(10, second.Requests);
        Assert.All(await _t.Db.MetadataCompanions.ToListAsync(), c => Assert.Equal((int)CompanionState.NotFound, c.State));
    }

    [Fact]
    public async Task ABusyWorker_LeavesTheCoverListed_AnUndecodableImageFails()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        _h.Renderer.FailWith = "busy";
        await _h.TickAsync();
        var one = await _t.Db.VolumeCovers.SingleAsync(c => c.Volume == 1 && c.Locale == "ja" && c.Variant == 0);
        Assert.Equal((int)VolumeCoverState.Listed, one.State);

        _h.Renderer.FailWith = CoverRenderErrors.DecodeFailed;
        await _h.TickAsync();
        one = await _t.Db.VolumeCovers.SingleAsync(c => c.Id == one.Id);
        Assert.Equal((int)VolumeCoverState.Failed, one.State);
        Assert.Empty(Directory.Exists(_h.Store.Root) ? Directory.GetFiles(_h.Store.Root, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task AnAdminsReference_IsConfirmed_NoneStopsLookups_AndDeleteRemovesStoredCovers()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (folder, record) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz");
        await _h.TickAsync();
        Assert.Equal(1, await _t.Db.VolumeCovers.CountAsync(c => c.State == (int)VolumeCoverState.Stored));

        // "Change MangaDex match...": one GET by id, Confirmed.
        _h.ResetRequests();
        var (error, list) = await _h.Admin().SetReferenceAsync(folder.PublicId, "https://mangadex.org/title/" + MdFixtures.ChainsawManId + "/x", "admin");
        Assert.Null(error);
        Assert.Equal(["/manga/" + MdFixtures.ChainsawManId], _h.MangaDexRequests().Select(u => u.AbsolutePath));
        var mangaDex = Assert.Single(list!, c => c.Provider == "mangadex");
        Assert.Equal((CompanionState.Confirmed, "https://mangadex.org/title/" + MdFixtures.ChainsawManId), (mangaDex.State, mangaDex.SiteUrl));
        // The previous record's covers and files are gone with it.
        Assert.False(await _t.Db.MetadataRecords.AnyAsync(r => r.ExternalId == MdFixtures.BerserkId));
        Assert.Empty(Directory.GetFiles(_h.Store.Root, "*.webp", SearchOption.AllDirectories));

        Assert.Equal("invalid_reference", (await _h.Admin().SetReferenceAsync(folder.PublicId, "not a reference", "admin")).Error);

        // "Not on MangaDex": never looked up there again, even after the cadence (AniList may still give totals).
        await _h.Admin().SetNoneAsync(folder.PublicId, "admin");
        _h.ResetRequests();
        _h.Time.Advance(TimeSpan.FromDays(120));
        await _h.TickAsync();
        Assert.Empty(_h.MangaDexRequests());
        Assert.Equal((int)CompanionState.None, (await _t.Db.MetadataCompanions.SingleAsync(c => c.RecordId == record.Id && c.Provider == "mangadex")).State);
        Assert.True(await _t.Db.AuditEvents.AnyAsync(a => a.Action == "metadata.companion.change"));
    }

    [Fact]
    public async Task DeleteStoredVolumeCovers_RemovesFilesRowsAndWebDecisions_LocalOnly()
    {
        await _h.Auto.EnableAutomaticAsync();
        var (folder, _) = await SeriesAsync(MdFixtures.MuBerserk, "Berserk", 43, "Synthetic Shelf v01.cbz", "Synthetic Shelf v02.cbz");
        await _h.TickAsync();
        var cover = await _t.Db.VolumeCovers.FirstAsync(c => c.State == (int)VolumeCoverState.Stored);
        _t.Db.NodeAutoCovers.Add(new NodeAutoCoverEntity
        {
            NodeId = folder.Id,
            Source = (int)AutoCoverSource.WebVolume,
            VolumeCoverId = cover.Id,
            InputsKey = "k",
            DecidedAt = _h.Time.GetUtcNow(),
        });
        await _t.Db.SaveChangesAsync();
        _h.ResetRequests(failOnAnyRequest: true);

        Assert.True(await _h.Admin().DeleteStoredAsync("admin") > 0);

        Assert.Equal(0, _h.Handler.CallCount);
        Assert.False(await _t.Db.VolumeCovers.AnyAsync());
        Assert.False(await _t.Db.NodeAutoCovers.AnyAsync());
        Assert.Empty(Directory.GetFiles(_h.Store.Root, "*", SearchOption.AllDirectories));
        Assert.True(await _t.Db.SeriesVolumeMaps.AnyAsync()); // the volume list stays
    }
}
