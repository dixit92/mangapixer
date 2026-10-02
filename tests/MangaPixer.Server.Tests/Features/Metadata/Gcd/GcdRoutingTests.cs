namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Gcd;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Comics routing of automatic matching (1.32.0, owner 2026-10-02), through the queue with the production matcher core and a
/// migrated database: a comics-signalled work is searched on the Grand Comics Database first (with the start year of its name),
/// MangaUpdates only when GCD has nothing close; manga never reach GCD; a removed GCD sends comics to MangaUpdates; a GCD bucket
/// with no token to spare, or a GCD backoff, DEFERS the comics work while manga keep being matched; a GCD id in ComicInfo is a
/// tier-0 GET; a GCD-linked record keeps the file's cover and its details on refresh. (A roomy GCD bucket: the production bucket
/// of 3 with 2 kept for admins is covered by <see cref="GcdProviderTests"/> and the deferral test here.)
/// </summary>
public sealed class GcdRoutingTests : IAsyncLifetime
{
    private MetadataTestDb _db = null!;
    private GcdHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new GcdHarness(_db, GcdHarness.RoomyGcdRates());
        _h.MuSearch["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.MuRecords[101] = MuJson.Get(101, "Alpha Saga", status: "2 Volumes (Ongoing)");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> ComicsFolderAsync(string name, params string[] archives)
    {
        var comics = await _db.Db.CatalogNodes.FirstOrDefaultAsync(n => n.DisplayName == "Comics")
            ?? await _db.AddFolderAsync(null, "Comics");
        var folder = await _db.AddFolderAsync(comics, name);
        foreach (var a in archives)
            await _db.AddArchiveAsync(folder, a);
        return folder;
    }

    private async Task<CatalogNodeEntity> MangaFolderAsync()
    {
        var manga = await _db.AddFolderAsync(null, "Manga");
        var folder = await _db.AddFolderAsync(manga, "Alpha Saga");
        await _db.AddArchiveAsync(folder, "Alpha Saga v01.cbz");
        await _db.AddArchiveAsync(folder, "Alpha Saga v02.cbz");
        return folder;
    }

    private async Task<(NodeSeriesLinkEntity? Link, MetadataRecordEntity? Record)> LinkOfAsync(CatalogNodeEntity node)
    {
        var link = await _db.Db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id);
        var record = link?.RecordId is { } id ? await _db.Db.MetadataRecords.AsNoTracking().FirstAsync(r => r.Id == id) : null;
        return (link, record);
    }

    [Fact]
    public async Task ComicsFolder_SearchesGcdFirst_WithTheNamesStartYear_AutoLinks_NoMangaUpdatesRequest()
    {
        await _h.EnableAsync();
        var bone = await ComicsFolderAsync("Bone (1991)", "Bone 001 (1991).cbz", "Bone 002 (1991).cbz");

        await _h.MatchLibraryAsync();

        var search = Assert.Single(_h.GcdRequests);
        Assert.Equal("/api/series/name/Bone/year/1991/", search.Uri.AbsolutePath);
        Assert.Empty(_h.MuRequests);
        var (link, record) = await LinkOfAsync(bone);
        Assert.Equal((int)SeriesLinkState.Auto, link!.State);
        Assert.Equal("gcd", record!.Provider);
        Assert.Equal("4347", record.ExternalId);
        // Comics keep the file's own cover: no image is stored or even fetched for a GCD record.
        Assert.Null(record.ImageRemoteUrl);
        Assert.NotEqual(1, record.ImageState);
        Assert.DoesNotContain(_h.Handler.Seen, r => r.Uri.Host == "files1.comics.org");
        var queue = await _db.Db.MetadataMatchQueue.AsNoTracking().FirstAsync(q => q.NodeId == bone.Id);
        Assert.Equal((int)MatchBand.Auto, queue.Outcome);
        Assert.Equal(MatchReason.ComicsStartYear, (MatchReason)queue.OutcomeReasons & MatchReason.ComicsStartYear);
    }

    [Fact]
    public async Task MangaFolder_MangaUpdatesOnly_GcdNeverContacted()
    {
        await _h.EnableAsync();
        var alpha = await MangaFolderAsync();

        await _h.MatchLibraryAsync();

        Assert.Empty(_h.GcdRequests);
        Assert.NotEmpty(_h.MuRequests);
        Assert.Equal("mangaupdates", (await LinkOfAsync(alpha)).Record?.Provider);
    }

    [Fact]
    public async Task ComicsFolder_GcdFindsNothingClose_ThenMangaUpdates()
    {
        await _h.EnableAsync();
        var folder = await ComicsFolderAsync("Alpha Saga (2001)", "Alpha Saga 001 (2001).cbz", "Alpha Saga 002 (2001).cbz");

        await _h.MatchLibraryAsync();

        Assert.Equal(
            ["/api/series/name/Alpha%20Saga/year/2001/", "/api/series/name/Alpha%20Saga/"],
            _h.GcdRequests.Select(r => r.Uri.AbsolutePath));
        Assert.NotEmpty(_h.MuRequests);
        Assert.Equal("mangaupdates", (await LinkOfAsync(folder)).Record?.Provider);
    }

    [Fact]
    public async Task GcdRemovedFromTheAllowlist_ComicsAreSearchedOnMangaUpdatesOnly()
    {
        await _h.EnableAsync(removedProvidersJson: MetadataProviderAllowlist.Write(["gcd"]));
        _h.MuSearch["Bone"] = [];
        await ComicsFolderAsync("Bone (1991)", "Bone 001 (1991).cbz", "Bone 002 (1991).cbz");

        await _h.MatchLibraryAsync();

        Assert.Empty(_h.GcdRequests);
        Assert.Contains(_h.MuRequests, r => r.Body!.Contains("\"Bone\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GcdHasNoTokenToSpare_ComicsWorkIsDeferred_MangaKeepBeingMatched()
    {
        _h.Dispose();
        _h = new GcdHarness(_db, GcdHarness.GcdReserveOnlyRates());
        _h.MuSearch["Alpha Saga"] = [new MuJson.Hit(101, "Alpha Saga")];
        _h.MuRecords[101] = MuJson.Get(101, "Alpha Saga", status: "2 Volumes (Ongoing)");
        await _h.EnableAsync();
        var bone = await ComicsFolderAsync("Bone (1991)", "Bone 001 (1991).cbz", "Bone 002 (1991).cbz");
        var alpha = await MangaFolderAsync();

        var processed = await _h.MatchLibraryAsync();

        Assert.Equal(2, processed);
        Assert.Empty(_h.GcdRequests); // Never waited for, never sent.
        var row = await _db.Db.MetadataMatchQueue.AsNoTracking().FirstAsync(q => q.NodeId == bone.Id);
        Assert.Equal(QueueState.Pending, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.True(row.NotBefore > _h.Time.GetUtcNow());
        Assert.Null((await LinkOfAsync(bone)).Link);
        Assert.Equal("mangaupdates", (await LinkOfAsync(alpha)).Record?.Provider);

        // When the work is due again it is not leased before its time, then it is.
        Assert.Null(await _h.AutoMatch().LeaseNextAsync("test"));
        _h.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(bone.Id, (await _h.AutoMatch().LeaseNextAsync("test"))?.NodeId);
    }

    [Fact]
    public async Task GcdBackoff_DefersComicsUntilItEnds_NothingSent()
    {
        await _h.EnableAsync();
        var until = await _h.Backoff().RecordRateLimitedAsync("gcd", TimeSpan.FromMinutes(30), null, "rate_limited");
        var bone = await ComicsFolderAsync("Bone (1991)", "Bone 001 (1991).cbz", "Bone 002 (1991).cbz");

        await _h.MatchLibraryAsync();

        Assert.Empty(_h.GcdRequests);
        var row = await _db.Db.MetadataMatchQueue.AsNoTracking().FirstAsync(q => q.NodeId == bone.Id);
        Assert.Equal(QueueState.Pending, row.State);
        Assert.Equal(until, row.NotBefore);
    }

    [Fact]
    public async Task GcdIdInComicInfo_IsATierZeroGetById()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Bone");
        var a1 = await _db.AddArchiveAsync(folder, "Bone 01.cbz");
        await _db.AddArchiveAsync(folder, "Bone 02.cbz");
        await _db.AddComicInfoAsync(a1, "Bone", web: ["https://www.comics.org/series/4347/"]);

        await _h.MatchLibraryAsync();

        Assert.Equal("/api/series/4347/", _h.GcdRequests.First().Uri.AbsolutePath);
        Assert.DoesNotContain(_h.GcdRequests, r => r.Uri.AbsolutePath.StartsWith("/api/series/name/", StringComparison.Ordinal));
        Assert.Equal("4347", (await LinkOfAsync(folder)).Record?.ExternalId);
    }

    [Fact]
    public async Task ManySameNamedEditions_GoToReview_WithGcdCandidates_NeverAWrongAutoLink()
    {
        await _h.EnableAsync();
        var folder = await ComicsFolderAsync("Blacksad", "Blacksad 01.cbz", "Blacksad 02.cbz");

        await _h.MatchLibraryAsync();

        var (link, _) = await LinkOfAsync(folder);
        Assert.Equal((int)SeriesLinkState.NeedsReview, link!.State);
        var candidates = await _db.Db.MetadataMatchCandidates.AsNoTracking().Where(c => c.NodeId == folder.Id).ToListAsync();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal("gcd", c.Provider));
        Assert.All(candidates, c => Assert.Null(c.ImageRemoteUrl));
    }

    [Fact]
    public async Task IdentifyPreviewAndRefresh_GcdRecordKeepsPublisherAndCredits_NeverStoresACover()
    {
        await _h.EnableAsync(automatic: false);
        var folder = await ComicsFolderAsync("Bone (1991)", "Bone 001 (1991).cbz");
        var identify = _h.Identify();

        var preview = (await identify.LookupAsync(folder.PublicId, new IdentifyLookupRequest { Reference = "https://www.comics.org/series/4347/" }))!;
        Assert.Equal("gcd", preview.Provider);
        Assert.Equal("Grand Comics Database", preview.ProviderName);
        Assert.Equal(["Cartoon Books"], preview.Publishers);
        Assert.Equal("Data: Grand Comics Database, CC BY-SA 4.0", preview.Credit);
        Assert.Equal("en", preview.Language);

        var (code, _) = await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = "gcd", ExternalId = "4347" }, "tester");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        _db.Db.ChangeTracker.Clear();
        var refreshed = await _h.Identify().RefreshAsync(folder.PublicId, "tester");
        Assert.Equal("Ok", refreshed!.State);
        Assert.False(refreshed.ImageUpdated);

        var record = await _db.Db.MetadataRecords.AsNoTracking().FirstAsync(r => r.Provider == "gcd");
        Assert.Contains("Cartoon Books", record.PublishersJson, StringComparison.Ordinal);
        Assert.Contains("Jeff Smith", record.CreatorsJson, StringComparison.Ordinal);
        Assert.Null(record.ImageRemoteUrl);
        Assert.NotEqual(1, record.ImageState);
        var node = await _db.Db.CatalogNodes.AsNoTracking().FirstAsync(n => n.Id == folder.Id);
        var info = await _h.Resolver().ResolveAsync(node);
        Assert.Equal("Data: Grand Comics Database, CC BY-SA 4.0", info.Web!.Credit);
        Assert.False(info.Web.HasImage);
    }
}
