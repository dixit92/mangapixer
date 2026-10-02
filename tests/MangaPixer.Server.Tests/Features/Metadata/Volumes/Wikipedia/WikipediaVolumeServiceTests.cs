namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes.Wikipedia;

using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.32.0) of the Wikipedia companion over a <see cref="FakeWikimedia"/>: the real gateway, named clients (fixed
/// User-Agent, host allowlist handler) and limiter, a real SQLite database - discovery through Wikidata by the MangaUpdates id and by the
/// record's title, the revision-first check, the hub page, validation (a refused list never replaces a good one), the stored numbers /
/// dates / ISBNs, the refusals and the maxlag backoff. Synthetic pages and ids only; zero real network.
/// </summary>
public sealed class WikipediaVolumeServiceTests : IAsyncLifetime
{
    private const string MuId = MdFixtures.MuBerserk;
    private const string Article = "Synthetic Saga (manga)";
    private const string ListTitle = "List of Synthetic Saga chapters";

    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;
    private readonly FakeWikimedia _wiki = new();

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
        _h.Override = _wiki.Respond;
        await _h.Auto.EnableAutomaticAsync();
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private static string MuBase36 => MangaUpdatesReference.ToBase36(long.Parse(MuId, System.Globalization.CultureInfo.InvariantCulture));

    private WikipediaVolumeService Service() => new(_t.Db, _h.Auto.Net.Gateway(), new WikipediaApi(_h.Auto.Net.HttpFactory), _h.Maps(), _h.Time,
        _h.Auto.Net.LoggerFactory.CreateLogger<WikipediaVolumeService>());

    /// <summary>A series linked to a MangaUpdates record whose MangaDex map holds <paramref name="mangaDexVolumes"/> volumes of 4 chapters (+ unassigned chapters).</summary>
    private async Task<MetadataRecordEntity> SeriesAsync(int totalVolumes, int mangaDexVolumes, int unassignedFrom = 0, int unassignedTo = -1, string title = "Synthetic Saga")
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf");
        await _t.AddArchiveAsync(folder, "Synthetic Shelf v01.cbz");
        var record = await _t.AddRecordAsync(MuId, title);
        record.OriginVolumes = totalVolumes;
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Auto);
        if (mangaDexVolumes > 0)
        {
            var list = Enumerable.Range(1, mangaDexVolumes)
                .Select(v => new VolumeMapEntry(v.ToString(), Enumerable.Range(((v - 1) * 4) + 1, 4).Select(c => c.ToString()).ToList()))
                .ToList();
            var now = _h.Time.GetUtcNow();
            _t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = VolumeMapJson.Write(list),
                UnassignedJson = unassignedTo >= unassignedFrom ? VolumeMapJson.WriteChapters(Enumerable.Range(unassignedFrom, unassignedTo - unassignedFrom + 1).Select(c => c.ToString()).ToList()) : null,
                ContentHash = "seed",
                Version = 1,
                FetchedAt = now,
                NextCheckAt = now.AddDays(30),
            });
            await _t.Db.SaveChangesAsync();
        }
        return record;
    }

    private void KnowTheSeries(int revision = 100, params FakeWikimedia.Volume[] volumes)
    {
        _wiki.Items[MuBase36] = ["Q4242"];
        _wiki.Sitelinks["Q4242"] = Article;
        _wiki.Pages[Article] = new FakeWikimedia.Page(1, "Synthetic Saga is a manga series.\n== Plot ==\nText.");
        _wiki.Pages[ListTitle] = new FakeWikimedia.Page(revision, FakeWikimedia.ListPage("Volumes", volumes));
    }

    private async Task<WikipediaListEntity?> StepAsync(MetadataRecordEntity series, bool force = false, bool adminAsked = false)
    {
        _t.Db.ChangeTracker.Clear();
        var tracked = await _t.Db.MetadataRecords.FirstAsync(r => r.Id == series.Id);
        return await Service().StepAsync(tracked, _t.LibraryId, call: null, force, adminAsked);
    }

    private IReadOnlyList<Uri> WikimediaRequests() => _h.Handler.Seen.Where(s => FakeWikimedia.IsWikimedia(s.Uri)).Select(s => s.Uri).ToList();

    [Fact]
    public async Task FindsThePageByWikidata_FillsTheGap_AndStoresOnlyNumbersDatesAndIsbns()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));

        var row = await StepAsync(series);

        Assert.NotNull(row);
        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row.State);
        Assert.Equal(WikipediaListMethod.Wikidata, (WikipediaListMethod)row.Method);
        var sent = WikimediaRequests();
        // Wikidata search + sitelink, then the article and the usual list titles in ONE Wikipedia call.
        Assert.Equal(3, sent.Count);
        Assert.Equal(MetadataHttp.WikidataHost, sent[0].Host);
        Assert.Contains($"haswbstatement:P11149={MuBase36}", Uri.UnescapeDataString(sent[0].Query), StringComparison.Ordinal);
        Assert.Contains("ids=Q4242", sent[1].Query, StringComparison.Ordinal);
        Assert.Equal(MetadataHttp.WikipediaHost, sent[2].Host);
        var asked = Uri.UnescapeDataString(sent[2].Query);
        Assert.Contains($"titles={Article}|{ListTitle}|Lists of Synthetic Saga chapters", asked, StringComparison.Ordinal);
        Assert.Contains("redirects=1", asked, StringComparison.Ordinal);
        Assert.Contains("maxlag=5", asked, StringComparison.Ordinal);
        // Nothing of the library, only the fixed User-Agent, no Via.
        Assert.DoesNotContain(sent, u => Uri.UnescapeDataString(u.ToString()).Contains("Synthetic Shelf", StringComparison.Ordinal));
        Assert.All(_h.Handler.Seen.Where(s => FakeWikimedia.IsWikimedia(s.Uri)), s =>
        {
            Assert.Equal(MetadataHttp.UserAgent, s.Headers["User-Agent"]);
            Assert.False(s.Headers.ContainsKey("Via"));
        });

        // The stored list: numbers only.
        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.RecordId == series.Id && m.Source == (int)VolumeMapSource.WikipediaList);
        Assert.Equal((int)VolumeMapState.Ok, map.State);
        Assert.Equal(6, VolumeMapJson.Read(map.VolumesJson).Count);
        Assert.DoesNotContain("Synthetic chapter", map.VolumesJson, StringComparison.Ordinal);
        var stored = await _t.Db.WikipediaLists.AsNoTracking().SingleAsync(l => l.RecordId == series.Id);
        Assert.DoesNotContain("summary", stored.DetailsJson + stored.PagesJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([new WikipediaVolumeService.StoredPage(ListTitle, 100)], WikipediaVolumeService.ReadPages(stored.PagesJson));
        var details = WikipediaVolumeService.ReadDetails(stored.DetailsJson);
        Assert.Equal(6, details.Count);
        Assert.Equal(("1", "2021-01-01", FakeWikimedia.IsbnOf(1)), (details[0].Volume, details[0].Date, details[0].Isbn));

        // The MangaDex list is untouched; the merged list the views read has the two volumes and the 8 chapters MangaDex lacked.
        var maps = await _t.Db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == series.Id).ToListAsync();
        Assert.Equal(4, VolumeMapJson.Read(maps.Single(m => m.Source == (int)VolumeMapSource.MangaDexAggregate).VolumesJson).Count);
        var exact = SeriesProgressLoader.ExactList(maps);
        Assert.True(exact.WikipediaFilled);
        Assert.Equal(6, exact.Volumes.Count);
        Assert.Empty(exact.Unassigned);
        Assert.Equal(["21", "22", "23", "24"], exact.Volumes.Single(v => v.Volume == "6").Chapters);
        var credit = SeriesProgressLoader.ListCreditOf(maps, stored.PagesJson);
        Assert.Equal(("Wikipedia", ListTitle), (credit?.Name, credit?.Title));
        Assert.Equal("https://en.wikipedia.org/wiki/List_of_Synthetic_Saga_chapters", credit?.Url);

        // Logs carry ids, counts and codes only.
        Assert.DoesNotContain(_h.Auto.Net.Logs.Lines, l => l.Contains("Synthetic", StringComparison.Ordinal) || l.Contains("en.wikipedia", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MangaDexWins_WhereItPlacesAChapter_WikipediaFillsTheRest()
    {
        var series = await SeriesAsync(totalVolumes: 8, mangaDexVolumes: 6, unassignedFrom: 25, unassignedTo: 32);
        // Wikipedia puts chapter 24 (MangaDex: volume 6) in volume 7 - one disagreement in 24 shared chapters (95.8%): accepted.
        KnowTheSeries(100,
            [.. FakeWikimedia.Run(5), new FakeWikimedia.Volume(6, 21, 23), new FakeWikimedia.Volume(7, 24, 28), new FakeWikimedia.Volume(8, 29, 32)]);

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        var maps = await _t.Db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == series.Id).ToListAsync();
        var exact = SeriesProgressLoader.ExactList(maps);
        Assert.Equal(["21", "22", "23", "24"], exact.Volumes.Single(v => v.Volume == "6").Chapters); // MangaDex's volume 6 keeps chapter 24
        Assert.Equal(["25", "26", "27", "28"], exact.Volumes.Single(v => v.Volume == "7").Chapters);
        Assert.Equal(["29", "30", "31", "32"], exact.Volumes.Single(v => v.Volume == "8").Chapters);
        Assert.Empty(exact.Unassigned);
    }

    [Fact]
    public async Task OnlyAsksWhereItCanAdd_ACompleteMangaDexListMakesNoRequest()
    {
        var series = await SeriesAsync(totalVolumes: 4, mangaDexVolumes: 4); // every volume, nothing unassigned
        KnowTheSeries(100, FakeWikimedia.Run(4));
        _h.ResetRequests(failOnAnyRequest: true);

        var row = await StepAsync(series);

        Assert.Null(row);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.False(await _t.Db.WikipediaLists.AnyAsync());
    }

    [Fact]
    public async Task AnAdminRequest_AsksEvenWhereMangaDexHasNoGap()
    {
        var series = await SeriesAsync(totalVolumes: 4, mangaDexVolumes: 4);
        KnowTheSeries(100, FakeWikimedia.Run(4));

        var row = await StepAsync(series, force: true, adminAsked: true);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        Assert.NotEmpty(WikimediaRequests());
    }

    [Fact]
    public async Task ASeriesWithNoMangaDexList_UsesTheWikipediaListAlone()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 0);
        KnowTheSeries(100, FakeWikimedia.Run(6));

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        var maps = await _t.Db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == series.Id).ToListAsync();
        var (map, _) = SeriesProgressLoader.MapAndFacts(maps, null, "en");
        Assert.Equal(6, map.Volumes.Count);
        Assert.Equal(VolumeListSource.Wikipedia, map.Source);
    }

    [Fact]
    public async Task RevisionFirst_AnUnchangedPageCostsOneRequest_AChangedOneIsReadAgain()
    {
        var series = await SeriesAsync(totalVolumes: 7, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        await StepAsync(series);

        // Not due yet: nothing is asked.
        _h.ResetRequests(failOnAnyRequest: true);
        Assert.NotNull(await StepAsync(series));
        Assert.Equal(0, _h.Handler.CallCount);

        // Due, page unchanged: ONE prop=info request, no wikitext.
        _h.Time.Now = _h.Time.Now.AddDays(31);
        _h.ResetRequests();
        var unchanged = await StepAsync(series);
        var sent = WikimediaRequests();
        Assert.Single(sent);
        Assert.Contains("prop=info", sent[0].Query, StringComparison.Ordinal);
        Assert.DoesNotContain("revisions", sent[0].Query, StringComparison.Ordinal);
        Assert.Equal(_h.Time.Now.AddDays(30), unchanged!.NextCheckAt); // ongoing series: the 30-day cadence
        Assert.Equal(1, (await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList)).Version);

        // Due again, page changed (a seventh volume): the revision check, then the main page only (no Wikidata again).
        _h.Time.Now = _h.Time.Now.AddDays(31);
        _wiki.Pages[ListTitle] = new FakeWikimedia.Page(101, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(7)));
        _h.ResetRequests();
        await StepAsync(series);
        sent = WikimediaRequests();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, u => Assert.Equal(MetadataHttp.WikipediaHost, u.Host));
        Assert.Contains("prop=info", sent[0].Query, StringComparison.Ordinal);
        Assert.Contains("prop=revisions", sent[1].Query, StringComparison.Ordinal);
        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList);
        Assert.Equal(7, VolumeMapJson.Read(map.VolumesJson).Count);
        Assert.Equal(2, map.Version);
        Assert.Equal(101, WikipediaVolumeService.ReadPages((await _t.Db.WikipediaLists.AsNoTracking().SingleAsync()).PagesJson)[0].Revision);
    }

    [Fact]
    public async Task WithoutAWikidataItem_TheRecordsTitleIsTriedAsListTitlesOnly()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24, title: "Synthetic Saga (SOMEONE Author)");
        _wiki.Pages[ListTitle] = new FakeWikimedia.Page(100, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(6)));

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        Assert.Equal(WikipediaListMethod.Title, (WikipediaListMethod)row.Method);
        var sent = WikimediaRequests();
        Assert.Equal(2, sent.Count); // the item search (empty) + the pages
        var asked = Uri.UnescapeDataString(sent[1].Query);
        Assert.Contains($"titles={ListTitle}|Lists of Synthetic Saga chapters", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("titles=Synthetic Saga|", asked, StringComparison.Ordinal); // a guess is never read as an article
    }

    [Fact]
    public async Task NoPage_IsNotFound_AndAskedAgainOnTheCadence()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);

        var row = await StepAsync(series);

        Assert.Equal((WikipediaListState.NotFound, "no_page"), ((WikipediaListState)row!.State, row.RejectCode));
        Assert.Equal(_h.Time.Now.AddDays(30), row.NextCheckAt);
        Assert.False(await _t.Db.SeriesVolumeMaps.AnyAsync(m => m.Source == (int)VolumeMapSource.WikipediaList));
        _h.ResetRequests(failOnAnyRequest: true);
        Assert.NotNull(await StepAsync(series)); // not due: no request
    }

    [Fact]
    public async Task AnArticleWithoutAList_IsNoList()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        _wiki.Items[MuBase36] = ["Q4242"];
        _wiki.Sitelinks["Q4242"] = Article;
        _wiki.Pages[Article] = new FakeWikimedia.Page(1, "A short article about the series.\n== Plot ==\nText.");

        var row = await StepAsync(series);

        Assert.Equal((WikipediaListState.NotFound, "no_list"), ((WikipediaListState)row!.State, row.RejectCode));
    }

    [Fact]
    public async Task AHubPage_ReadsItsRangePages_AsOneList()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        _wiki.Items[MuBase36] = ["Q4242"];
        _wiki.Sitelinks["Q4242"] = Article;
        _wiki.Pages[Article] = new FakeWikimedia.Page(1, "An article.");
        _wiki.Pages["Lists of Synthetic Saga chapters"] = new FakeWikimedia.Page(5,
            "The list is split.\n* [[List of Synthetic Saga chapters (1–12)|Chapters 1–12]]\n* [[List of Synthetic Saga chapters (13–24)]]\n");
        _wiki.Pages["List of Synthetic Saga chapters (1–12)"] = new FakeWikimedia.Page(6, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(3)));
        _wiki.Pages["List of Synthetic Saga chapters (13–24)"] = new FakeWikimedia.Page(7,
            FakeWikimedia.ListPage("Volumes", new FakeWikimedia.Volume(4, 13, 16), new FakeWikimedia.Volume(5, 17, 20), new FakeWikimedia.Volume(6, 21, 24)));

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        Assert.Equal(4, WikimediaRequests().Count); // item search, sitelink, the three titles, the two range pages
        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList);
        Assert.Equal(6, VolumeMapJson.Read(map.VolumesJson).Count);
        var pages = WikipediaVolumeService.ReadPages(row.PagesJson);
        Assert.Equal(["Lists of Synthetic Saga chapters", "List of Synthetic Saga chapters (1–12)", "List of Synthetic Saga chapters (13–24)"], pages.Select(p => p.Title));
    }

    [Fact]
    public async Task ARefusedList_IsNeverUsed_AndTheLastGoodListStays()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        await StepAsync(series);
        var good = (await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList)).ContentHash;

        // Vandalised: thirty volumes for a series with six.
        _wiki.Pages[ListTitle] = new FakeWikimedia.Page(101, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(30)));
        _h.Time.Now = _h.Time.Now.AddDays(31);
        var row = await StepAsync(series);

        Assert.Equal((WikipediaListState.Rejected, WikipediaValidation.TooManyVolumes), ((WikipediaListState)row!.State, row.RejectCode));
        Assert.Equal(good, (await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList)).ContentHash);
        Assert.Equal(101, WikipediaVolumeService.ReadPages(row.PagesJson)[0].Revision); // remembered: not downloaded again until it changes
        _h.Time.Now = _h.Time.Now.AddDays(31);
        _h.ResetRequests();
        await StepAsync(series);
        Assert.Single(WikimediaRequests());
    }

    [Fact]
    public async Task AListThatDisagreesWithMangaDex_IsRefused()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        // Same chapters, two volumes later: not this series' list.
        KnowTheSeries(100, [.. Enumerable.Range(1, 6).Select(v => new FakeWikimedia.Volume(v + 1, ((v - 1) * 4) + 1, v * 4))]);

        var row = await StepAsync(series);

        Assert.Equal((WikipediaListState.Rejected, WikipediaValidation.DisagreesWithMangaDex), ((WikipediaListState)row!.State, row.RejectCode));
        Assert.False(await _t.Db.SeriesVolumeMaps.AnyAsync(m => m.Source == (int)VolumeMapSource.WikipediaList));
    }

    [Fact]
    public async Task ARowsOnlyArticle_KeepsDatesAndIsbns_ButNoList()
    {
        var series = await SeriesAsync(totalVolumes: 3, mangaDexVolumes: 0);
        _wiki.Items[MuBase36] = ["Q4242"];
        _wiki.Sitelinks["Q4242"] = Article;
        _wiki.Pages[Article] = new FakeWikimedia.Page(1, FakeWikimedia.ListPage("Volumes",
            new FakeWikimedia.Volume(1, 1, 0, "March 3, 2020", FakeWikimedia.IsbnOf(1)), new FakeWikimedia.Volume(2, 1, 0, "June 3, 2020", FakeWikimedia.IsbnOf(2))));

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Found, (WikipediaListState)row!.State);
        Assert.Equal(2, WikipediaVolumeService.ReadDetails(row.DetailsJson).Count);
        var map = await _t.Db.SeriesVolumeMaps.SingleAsync(m => m.Source == (int)VolumeMapSource.WikipediaList);
        Assert.Equal((int)VolumeMapState.Empty, map.State); // no chapters: the Volumes view never reads it
        Assert.False(SeriesProgressLoader.ExactList([map]).WikipediaFilled);
    }

    // --- Refusals and failures --------------------------------------------------------------------------------------------

    [Fact]
    public async Task WikipediaRemovedFromTheAllowlist_StopsEverything_WithoutARequest()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        Assert.Null(await _h.Auto.Net.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = [MetadataProviderAllowlist.Wikipedia] }, "admin"));
        _h.ResetRequests(failOnAnyRequest: true);

        var refusal = await Assert.ThrowsAsync<MetadataGatewayException>(() => StepAsync(series));

        Assert.Equal("provider_not_allowed", refusal.Code);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.False(await _t.Db.WikipediaLists.AnyAsync());
    }

    [Fact]
    public async Task AMaxlagAnswer_IsASlowDown_ThatPausesWikimediaWithoutStoringAFailure()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        _wiki.MaxLagSeconds = 7;

        var refusal = await Assert.ThrowsAsync<MetadataGatewayException>(() => StepAsync(series));

        Assert.Equal("provider_backoff", refusal.Code);
        Assert.NotNull(refusal.RetryAt);
        Assert.Single(WikimediaRequests()); // the first request only
        // The backoff holds: the next look is refused without any request.
        _h.ResetRequests(failOnAnyRequest: true);
        Assert.Equal("provider_backoff", (await Assert.ThrowsAsync<MetadataGatewayException>(() => StepAsync(series))).Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task AProviderError_IsRecordedAsFailed_AndRetriedAfterADay()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        _wiki.Status = HttpStatusCode.BadGateway;

        var row = await StepAsync(series);

        Assert.Equal(WikipediaListState.Failed, (WikipediaListState)row!.State);
        Assert.Equal("provider_error", row.RejectCode);
        Assert.Equal(_h.Time.Now.AddDays(1), row.NextCheckAt);
    }

    [Fact]
    public async Task APageTitleAnAdminChose_IsReadDirectly_AndSurvivesAReCheck()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        _wiki.Pages["Custom Page"] = new FakeWikimedia.Page(9, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(6)));

        _t.Db.ChangeTracker.Clear();
        var row = await Service().SetPageAsync(await _t.Db.MetadataRecords.FirstAsync(r => r.Id == series.Id), _t.LibraryId, "Custom Page", call: null);

        Assert.Equal((WikipediaListState.Found, WikipediaListMethod.Admin, "Custom Page"), ((WikipediaListState)row!.State, (WikipediaListMethod)row.Method, row.AdminTitle));
        var sent = WikimediaRequests();
        Assert.Single(sent); // no Wikidata: an admin's page is not looked up
        Assert.Contains("titles=Custom Page", Uri.UnescapeDataString(sent[0].Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearingTheList_RemovesIt_AndNeverAsksAgain_UntilRechecked()
    {
        var series = await SeriesAsync(totalVolumes: 6, mangaDexVolumes: 4, unassignedFrom: 17, unassignedTo: 24);
        KnowTheSeries(100, FakeWikimedia.Run(6));
        await StepAsync(series);

        _t.Db.ChangeTracker.Clear();
        await Service().ClearAsync(await _t.Db.MetadataRecords.FirstAsync(r => r.Id == series.Id));

        Assert.False(await _t.Db.SeriesVolumeMaps.AnyAsync(m => m.Source == (int)VolumeMapSource.WikipediaList));
        Assert.Equal((int)WikipediaListState.None, (await _t.Db.WikipediaLists.AsNoTracking().SingleAsync()).State);
        _h.Time.Now = _h.Time.Now.AddDays(400);
        _h.ResetRequests(failOnAnyRequest: true);
        Assert.Equal(WikipediaListState.None, (WikipediaListState)(await StepAsync(series, force: true))!.State);
        Assert.Equal(0, _h.Handler.CallCount);

        _h.ResetRequests();
        _t.Db.ChangeTracker.Clear();
        var again = await Service().RecheckAsync(await _t.Db.MetadataRecords.FirstAsync(r => r.Id == series.Id), _t.LibraryId, call: null);
        Assert.Equal(WikipediaListState.Found, (WikipediaListState)again!.State);
    }
}
