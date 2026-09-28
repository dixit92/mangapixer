namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the cover comparison's request discipline (1.28.0) through the PRODUCTION lookup
/// (<see cref="AutoMatchLookup.LookupAsync"/>, real planner and scorer, real gateway over scripted answers): two tied
/// candidates, at most two image GETs, each counted in the daily budget; nothing downloaded without a stored local
/// thumbnail, with the setting off, or after the first image failed; a budget refusal propagates like every
/// automatic call. The hash is faked from the image bytes' last byte; synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class CoverComparisonTests : IAsyncLifetime
{
    private const string Title = "Qzv Tidal Saga";
    private static readonly byte[] s_png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mp-cover-" + Guid.NewGuid().ToString("N")[..8]);
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;
    private ThumbnailStore _thumbnails = null!;
    private ScratchWorkspaceManager _scratch = null!;

    /// <summary>Image answers by id (the URL's file name); a missing id is a 404.</summary>
    private readonly Dictionary<string, char> _images = new(StringComparer.Ordinal) { ["901"] = 'B', ["902"] = 'A' };

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        _h.Net.Handler.Respond = Route;
        _thumbnails = new ThumbnailStore(Path.Combine(_root, "thumbnails"));
        _thumbnails.Initialize();
        _scratch = new ScratchWorkspaceManager(Path.Combine(_root, "scratch"));
        _scratch.Initialize();
        await _h.EnableAutomaticAsync();
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string ImageUrl(long id) => $"https://{MetadataHttp.MangaUpdatesImageHost}/image/thumb/{id}.png";

    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.MangaUpdatesImageHost)
        {
            var id = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            return _images.TryGetValue(id, out var marker)
                ? ScriptedHandler.Bytes([.. s_png, (byte)marker])
                : ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
        }
        if (request.Method == HttpMethod.Post)
            return ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(901, Title, Image: ImageUrl(901)), new MuJson.Hit(902, Title, Image: ImageUrl(902))));
        var get = long.Parse(uri.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
        return ScriptedHandler.Json(MuJson.Get(get, Title, image: ImageUrl(get)));
    }

    /// <summary>"A" images hash to 0, anything else to all ones (64 bits apart).</summary>
    private sealed class MarkerHasher : ICoverHasher
    {
        public int Calls { get; private set; }

        public async Task<ulong?> HashFileAsync(string path, CancellationToken ct)
        {
            Calls++;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return bytes[^1] == (byte)'A' ? 0UL : ulong.MaxValue;
        }
    }

    private sealed class Setting(bool on) : ICoverCompareSetting
    {
        public Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(on);
    }

    /// <summary>A volume folder whose cover archive (v01) has a stored thumbnail ending in <paramref name="localMarker"/>.</summary>
    private async Task<(LibraryTreeSnapshot Tree, DetectedWork Work, WorkClassification Classification)> WorkAsync(char? localMarker = 'A')
    {
        var folder = await _db.AddFolderAsync(null, Title);
        var first = await _db.AddArchiveAsync(folder, Title + " v01.cbz");
        await _db.AddArchiveAsync(folder, Title + " v02.cbz");
        await _db.AddArchiveAsync(folder, Title + " v03.cbz");
        if (localMarker is { } marker)
        {
            var path = _thumbnails.GetThumbnailPath(first.Id, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [.. s_png, (byte)marker]);
        }
        var tree = await LibraryTreeSnapshot.LoadAsync(_db.Db, _db.LibraryId, CancellationToken.None);
        var classification = new WorkDetector().Classify(tree.ShapeOf(folder.Id));
        return (tree, new DetectedWork(folder.Id, folder.Id, classification.Level, classification.Class, []), classification);
    }

    private async Task<(WorkLookupResult Result, MetadataCallContext Call, MarkerHasher Hasher)> LookupAsync(char? localMarker = 'A', bool setting = true)
    {
        var (tree, work, classification) = await WorkAsync(localMarker);
        var hasher = new MarkerHasher();
        var gateway = _h.Net.Gateway();
        var comparer = new AutoMatchCoverComparer(_db.Db, gateway, hasher, new Setting(setting), _thumbnails, _scratch, new CoverHashCache());
        var lookup = new AutoMatchLookup(_db.Db, gateway, new MatchQueryPlanner(), new MatchScorer(), comparer);
        var call = MetadataCallContext.Automatic();
        var result = await lookup.LookupAsync(tree, work, classification, MatchThresholds.Default, false, call, CancellationToken.None);
        return (result, call, hasher);
    }

    private int ImageRequests => _h.Handler.Seen.Count(r => r.Uri.Host == MetadataHttp.MangaUpdatesImageHost);

    [Fact]
    public async Task ATie_ComparesBothCovers_TheMatchingOneRanksFirst_AndTheImagesCountInTheBudget()
    {
        var (result, call, hasher) = await LookupAsync();

        Assert.Equal(2, ImageRequests);
        Assert.Equal(2, result.CoversCompared);
        Assert.Equal(CoverCheck.Matched, result.CoverCheck);
        Assert.Equal(3, hasher.Calls); // the local thumbnail + two candidates
        var top = result.Outcome.Ranked[0];
        Assert.Equal("902", top.Candidate.ExternalId); // without the cover, 901 ranks first (same title, id order)
        Assert.True((top.Reasons & MatchReason.CoverMatch) != 0);
        Assert.Equal(MatchBand.NeedsReview, result.Outcome.Band); // a tie stays in review at the default lead
        Assert.Equal(_h.Handler.CallCount, call.RequestsSent);   // search + 2 GETs + 2 images, all counted
        Assert.Equal(call.RequestsSent, (await _h.Net.Budget().GetAsync()).Used);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "scratch"), "*.img", SearchOption.AllDirectories)); // discarded
    }

    [Fact]
    public async Task NoStoredThumbnail_DownloadsNothing()
    {
        var (result, _, hasher) = await LookupAsync(localMarker: null);

        Assert.Equal(0, ImageRequests);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(CoverCheck.NoLocalCover, result.CoverCheck);
        Assert.Equal("901", result.Outcome.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public async Task TheSettingOff_DownloadsNothing()
    {
        var (result, _, hasher) = await LookupAsync(setting: false);

        Assert.Equal(CoverCheck.Off, result.CoverCheck);
        Assert.Equal(0, ImageRequests);
        Assert.Equal(0, hasher.Calls);
    }

    [Fact]
    public async Task AFailedFirstImage_StopsTheComparison_OneCoverCannotBreakATie()
    {
        _images.Remove("901");

        var (result, _, _) = await LookupAsync();

        Assert.Equal(1, ImageRequests);
        Assert.Equal(CoverCheck.ImageFailed, result.CoverCheck);
        Assert.Equal(0, (int)(result.Outcome.Ranked[0].Reasons & MatchReason.CoverMatch));
    }

    [Fact]
    public async Task BothCoversTheSame_IsNoSignal()
    {
        _images["901"] = 'A';

        var (result, _, _) = await LookupAsync();

        Assert.Equal(2, ImageRequests);
        Assert.Equal(CoverCheck.Compared, result.CoverCheck);
        Assert.Equal("901", result.Outcome.Ranked[0].Candidate.ExternalId);
        Assert.All(result.Outcome.Ranked, r => Assert.Equal(0, (int)(r.Reasons & MatchReason.CoverMatch)));
    }

    [Fact]
    public async Task ABudgetSpentBeforeTheImages_IsARefusal_LikeEveryAutomaticCall()
    {
        var row = await _db.Db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
        row.MetadataDailyBudget = 3; // the search and both GETs fit, the first image does not
        await _db.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => LookupAsync());

        Assert.True(MetadataAutoMatchService.IsRefusal(ex), ex.Code);
        Assert.Equal(0, ImageRequests);
    }

    [Fact]
    public void CoverArchive_IsTheFirstArchiveBySortKey_OrTheAnchor()
    {
        var tree = LibraryTreeSnapshot.FromNodes(1,
        [
            new(1, null, true, "Saga", "0saga"),
            new(2, 1, true, "Volumes", "0volumes"),
            new(10, 2, false, "Saga v02", "1saga v02"),
            new(11, 2, false, "Saga v01", "1saga v01"),
            new(12, 1, false, "Saga Extra", "1saga extra"),
        ]);

        Assert.Equal(12, AutoMatchCoverComparer.CoverArchiveOf(tree, new DetectedWork(1, 1, MatchLevel.Folder, WorkClass.SeriesWithUnits, [])));
        Assert.Equal(11, AutoMatchCoverComparer.CoverArchiveOf(tree, new DetectedWork(2, 2, MatchLevel.Folder, WorkClass.Series, [])));
        Assert.Equal(10, AutoMatchCoverComparer.CoverArchiveOf(tree, new DetectedWork(10, 2, MatchLevel.Archive, WorkClass.CollectionLeaf, [11])));
    }
}
