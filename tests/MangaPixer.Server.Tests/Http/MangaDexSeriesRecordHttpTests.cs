namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.42.0 (lane D, owner-approved): MangaDex as the series record through the running app (WebApplicationFactory, the production DI -
/// <c>MangaDexSeriesProvider</c> is registered - every outbound request ends in the scripted handler). Identify offers MangaDex as its
/// third site; a search sends only the confirmed text and the fixed parameters; a pasted title address sends only its id; Link stores the
/// record, its poster and the "self" companion; series information names MangaDex as the source (no "Also on" line for itself); Refresh
/// reads the record by id and its lists - never a title search; the export carries <c>provider = mangadex</c> and the record as its own
/// companion; "Switch to MangaUpdates..." moves the link and keeps the confirmation; a Collection about label refuses a MangaDex record.
/// </summary>
public sealed class MangaDexSeriesRecordHttpTests
{
    private const string LibPub = "mdxlib1";
    private const string LanternId = "5e1a7c00-0000-4000-8000-00000000b001";
    private const long LanternMu = 61508275290;

    private static readonly MdJson.Manga Lantern = new(LanternId, "Silver Lantern Tales", Alt: ["Gin no Rantan"], Status: "completed",
        LastVolume: "4", LastChapter: "38", Tags: ["Action", "Long Strip"], Authors: ["Synthetic Mangaka"], Al: "12345",
        Description: "A synthetic **description**.");

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "MangaDex Lib", RootPath = "/synthetic/mdx", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var series = Node("mdxSeries", lib.Id, null, 0, "Silver Lantern Tales");
        var other = Node("mdxOther", lib.Id, null, 0, "Collection of Lanterns");
        db.CatalogNodes.AddRange(series, other);
        await db.SaveChangesAsync();
        var arc = Node("mdxArc", lib.Id, series.Id, 1, "Silver Lantern Tales v01");
        db.CatalogNodes.Add(arc);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static HttpResponseMessage Route(HttpRequestMessage request, MdJson.Manga manga)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "uploads.mangadex.org" || uri.Host == "cdn.mangaupdates.com")
            return ScriptedHandler.Bytes(MuFixtures.Png);
        if (uri.Host == "api.mangadex.org")
        {
            if (uri.AbsolutePath == "/manga")
                return ScriptedHandler.Json(MdJson.Search(manga));
            if (uri.AbsolutePath == "/manga/" + manga.Id)
                return ScriptedHandler.Json(MdJson.Entity(manga));
            if (uri.AbsolutePath.EndsWith("/aggregate", StringComparison.Ordinal))
            {
                // All languages: volumes 1-2; English: chapters 1-12.5; English and readable (1.43.0: without includeUnavailable): 1, 7 and 9 (9 is not
                // released in English - never counted).
                var english = uri.Query.Contains("translatedLanguage", StringComparison.Ordinal);
                if (english && !uri.Query.Contains("includeUnavailable", StringComparison.Ordinal))
                    return ScriptedHandler.Json("{\"result\":\"ok\",\"volumes\":{\"1\":{\"volume\":\"1\",\"count\":3,\"chapters\":{\"1\":{\"chapter\":\"1\",\"count\":1},\"7\":{\"chapter\":\"7\",\"count\":1},\"9\":{\"chapter\":\"9\",\"count\":1}}}}}");
                return ScriptedHandler.Json(english
                    ? "{\"result\":\"ok\",\"volumes\":{\"1\":{\"volume\":\"1\",\"count\":3,\"chapters\":{\"1\":{\"chapter\":\"1\",\"count\":1},\"12.5\":{\"chapter\":\"12.5\",\"count\":1},\"7\":{\"chapter\":\"7\",\"count\":1}}}}}"
                    : "{\"result\":\"ok\",\"volumes\":{\"1\":{\"volume\":\"1\",\"count\":2,\"chapters\":{\"1\":{\"chapter\":\"1\",\"count\":1},\"2\":{\"chapter\":\"2\",\"count\":1}}},\"2\":{\"volume\":\"2\",\"count\":2,\"chapters\":{\"3\":{\"chapter\":\"3\",\"count\":1},\"4\":{\"chapter\":\"4\",\"count\":1}}}}}");
            }
            if (uri.AbsolutePath == "/cover")
                return ScriptedHandler.Json("{\"result\":\"ok\",\"data\":[],\"limit\":100,\"offset\":0,\"total\":0}");
        }
        if (uri.Host == "api.mangaupdates.com" && uri.AbsolutePath.EndsWith("/" + LanternMu, StringComparison.Ordinal))
            return ScriptedHandler.Json(MuJson.Get(LanternMu, "Gin no Rantan Monogatari"));
        if (uri.Host == "api.mangaupdates.com")
            return ScriptedHandler.Json("{\"total_hits\":0,\"page\":1,\"per_page\":10,\"results\":[]}");
        return ScriptedHandler.Json("{}", HttpStatusCode.NotFound);
    }

    /// <summary>Forgets the requests seen so far, keeping this test's routing (<see cref="ScriptedHandler.Reset"/> drops it).</summary>
    private static void Forget(MetadataNetworkWebApplicationFactory factory, MdJson.Manga manga)
    {
        factory.Handler.Reset();
        factory.Handler.Respond = r => Route(r, manga);
    }

    private static async Task<HttpClient> StartAsync(MetadataNetworkWebApplicationFactory factory, MdJson.Manga manga)
    {
        await SeedAsync(factory);
        factory.Handler.Respond = r => Route(r, manga);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = true }))
            .EnsureSuccessStatusCode();
        return admin;
    }

    [Fact]
    public async Task IdentifyOffersMangaDex_SearchLinkSeriesInfoRefreshExport_SendOnlyWhatIsApproved()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await StartAsync(factory, Lantern);

        // The third site; MangaUpdates stays the default.
        var context = (await admin.GetFromJsonAsync<IdentifyContextDto>("/api/v1/admin/metadata/nodes/mdxSeries/identify", TestJson.Web))!;
        Assert.Equal("mangaupdates", context.Provider);
        var site = Assert.Single(context.Sites!, s => s.Id == "mangadex");
        Assert.True(site.Available);
        Assert.Equal("MangaDex", site.Name);
        Assert.Empty(factory.Handler.Seen);

        // A search sends the confirmed text and the fixed parameters only.
        var search = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdxSeries/search",
            new IdentifySearchRequest { Query = "Silver Lantern Tales", Provider = "mangadex", HideDoujinshiAndNovels = true });
        search.EnsureSuccessStatusCode();
        var found = (await search.Content.ReadFromJsonAsync<IdentifySearchResultDto>(TestJson.Web))!;
        Assert.Equal("mangadex", found.Provider);
        var hit = Assert.Single(found.Candidates);
        Assert.Equal(LanternId, hit.ExternalId);
        Assert.Null(hit.UnitKind); // no comics edition fields on a MangaDex hit
        Assert.Equal(MetadataOrigin.Japan, hit.Origin);
        var sent = Assert.Single(factory.Handler.Seen);
        Assert.Equal("https://api.mangadex.org/manga?title=Silver%20Lantern%20Tales&limit=10&offset=0&order%5Brelevance%5D=desc"
            + "&contentRating%5B%5D=safe&contentRating%5B%5D=suggestive&contentRating%5B%5D=erotica&contentRating%5B%5D=pornographic"
            + "&includes%5B%5D=cover_art&includes%5B%5D=author&includes%5B%5D=artist", sent.Uri.AbsoluteUri);

        // A pasted title address: only the id is sent (the search's answer is reused - no second request).
        Forget(factory, Lantern);
        var lookup = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdxSeries/lookup",
            new IdentifyLookupRequest { Reference = $"https://mangadex.org/title/{LanternId}/silver-lantern-tales" });
        lookup.EnsureSuccessStatusCode();
        var preview = (await lookup.Content.ReadFromJsonAsync<IdentifyPreviewDto>(TestJson.Web))!;
        Assert.Equal("mangadex", preview.Provider);
        Assert.Equal("MangaDex", preview.ProviderName);
        Assert.Equal(MetadataOriginStatus.Complete, preview.OriginStatus);
        Assert.Equal(4, preview.OriginVolumes);
        Assert.True(preview.Webtoon);
        Assert.Contains(preview.Creators, c => c.Name == "Synthetic Mangaka");
        Assert.Null(preview.MangaUpdatesId);
        Assert.Null(preview.Credit);
        Assert.All(factory.Handler.Seen, s => Assert.DoesNotContain("title=", s.Uri.Query, StringComparison.Ordinal));

        // Link: the record, its poster (the main cover, 512 px) and the self companion.
        Forget(factory, Lantern);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdxSeries/link", new LinkSeriesRequest { Provider = "mangadex", ExternalId = LanternId }))
            .EnsureSuccessStatusCode();
        Assert.Contains(factory.Handler.Seen, s => s.Uri.Host == "uploads.mangadex.org" && s.Uri.AbsolutePath.EndsWith(".512.jpg", StringComparison.Ordinal));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var record = await db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangadex" && r.ExternalId == LanternId);
            Assert.Equal(1, record.ImageState);
            Assert.Equal((int)MetadataFormat.Comic, record.Format);
            var self = await db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.RecordId == record.Id);
            Assert.Equal(record.Id, self.CompanionRecordId);
            Assert.Equal((int)CompanionState.Confirmed, self.State);
            Assert.Equal((int)CompanionMethod.Self, self.Method);
        }

        // Series information: the source is MangaDex; no "Also on: MangaDex" for itself; no Switch offer (no MangaUpdates link).
        var info = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/mdxSeries/series-info", TestJson.Web))!;
        Assert.Equal("Silver Lantern Tales", info.Title);
        Assert.Equal("MangaDex", info.Web!.ProviderName);
        Assert.Null(info.Web.MangaUpdatesId);
        Assert.Empty(info.Companions);

        // Refresh: the record by id, then its lists by id - never a title search.
        Forget(factory, Lantern);
        (await admin.PostAsync("/api/v1/admin/metadata/nodes/mdxSeries/refresh", null)).EnsureSuccessStatusCode();
        Assert.Contains(factory.Handler.Seen, s => s.Uri.AbsolutePath == "/manga/" + LanternId);
        Assert.Contains(factory.Handler.Seen, s => s.Uri.AbsolutePath == $"/manga/{LanternId}/aggregate");
        // 1.43.0: the readable list - the same list in the preferred language without includeUnavailable; only the id and the language.
        Assert.Equal(
            ["?includeUnavailable=1", "?includeUnavailable=1&translatedLanguage[]=en", "?translatedLanguage[]=en"],
            factory.Handler.Seen.Where(s => s.Uri.AbsolutePath == $"/manga/{LanternId}/aggregate").Select(s => Uri.UnescapeDataString(s.Uri.Query)));
        Assert.DoesNotContain(factory.Handler.Seen, s => s.Uri.Query.Contains("title=", StringComparison.Ordinal));
        Assert.All(factory.Handler.Seen, s => Assert.Contains(s.Uri.Host, new[] { "api.mangadex.org", "uploads.mangadex.org" }));

        // The export: provider mangadex, the record its own companion, the latest chapter in the preferred language, the final total.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var libraryId = await db.Libraries.Where(l => l.PublicId == LibPub).Select(l => l.Id).SingleAsync();
            var items = await scope.ServiceProvider.GetRequiredService<ExportItemBuilder>().BuildAsync(libraryId, DateTimeOffset.UtcNow);
            var item = Assert.Single(items, i => i.Item.NodeId == "mdxSeries").Item;
            Assert.Equal("mangadex", item.Record!.Provider);
            Assert.Equal(LanternId, item.Record.ExternalId);
            Assert.Equal("12.5", item.Record.LatestChapter);
            Assert.Equal(38, item.Record.TotalChapters);
            Assert.Equal(LanternId, item.Companions!.Mangadex);
            Assert.Equal("12.5", item.Companions.MangadexReleased!.LatestChapter);
            // 1.43.0: what can be read there - 1 and 7; 12.5 was taken down.
            Assert.Equal(("7", 2), (item.Companions.MangadexReleased.Readable!.LatestWholeChapter, item.Companions.MangadexReleased.Readable.Chapters));
        }
    }

    [Fact]
    public async Task SwitchToMangaUpdates_MovesTheLink_KeepsTheConfirmation_AndKeepsMangaDexAsTheCompanion()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var linked = Lantern with { Mu = MangaUpdatesReference.ToBase36(LanternMu) };
        var admin = await StartAsync(factory, linked);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdxSeries/link", new LinkSeriesRequest { Provider = "mangadex", ExternalId = LanternId }))
            .EnsureSuccessStatusCode();

        // MangaDex links a MangaUpdates series: series information offers the switch; the preview says so.
        var info = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/mdxSeries/series-info", TestJson.Web))!;
        Assert.Equal(LanternMu.ToString(System.Globalization.CultureInfo.InvariantCulture), info.Web!.MangaUpdatesId);

        Forget(factory, linked);
        var switched = await admin.PostAsync("/api/v1/admin/metadata/nodes/mdxSeries/switch-to-mangaupdates", null);
        switched.EnsureSuccessStatusCode();
        var change = (await switched.Content.ReadFromJsonAsync<NodeSeriesLinkChangeDto>(TestJson.Web))!;
        Assert.Equal("mangaupdates", change.Link!.Provider);
        Assert.Equal(SeriesLinkState.Confirmed, change.Link.State);
        Assert.Equal("mangadex", change.Previous!.Provider);
        // Only the MangaUpdates record by its id (and its poster) - nothing from the library.
        Assert.All(factory.Handler.Seen, s => Assert.Contains(s.Uri.Host, new[] { "api.mangaupdates.com", "cdn.mangaupdates.com" }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var mu = await db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangaupdates");
        var link = await db.NodeSeriesLinks.AsNoTracking().SingleAsync(l => l.RecordId == mu.Id);
        Assert.Equal((int)SeriesLinkState.Confirmed, link.State);
        var mangaDex = await db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangadex");
        var companion = await db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.Provider == "mangadex");
        Assert.Equal(mu.Id, companion.RecordId);
        Assert.Equal(mangaDex.Id, companion.CompanionRecordId);
        Assert.Equal((int)CompanionState.Auto, companion.State);

        // A second switch has nothing to do.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/metadata/nodes/mdxSeries/switch-to-mangaupdates", null)).StatusCode);
    }

    [Fact]
    public async Task ACollectionAboutLabel_RefusesAMangaDexRecord_AndUnlinkingKeepsAMangaDexRecordAnotherSeriesUsesAsItsCompanion()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await StartAsync(factory, Lantern);
        var collection = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdxOther/collection",
            new SetCollectionAboutRequest { Provider = "mangadex", ExternalId = LanternId });
        Assert.Equal(HttpStatusCode.BadRequest, collection.StatusCode);

        // The MangaDex record is linked to a folder AND is a MangaUpdates series' companion: unlinking the folder keeps it.
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdxSeries/link", new LinkSeriesRequest { Provider = "mangadex", ExternalId = LanternId }))
            .EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var md = await db.MetadataRecords.SingleAsync(r => r.Provider == "mangadex");
            var mu = new MetadataRecordEntity { PublicId = "mrmuhost", Provider = "mangaupdates", ExternalId = "9100", Title = "Host", FetchedAt = DateTimeOffset.UtcNow };
            db.MetadataRecords.Add(mu);
            await db.SaveChangesAsync();
            db.MetadataCompanions.Add(new MetadataCompanionEntity
            {
                RecordId = mu.Id,
                Provider = "mangadex",
                CompanionRecordId = md.Id,
                State = (int)CompanionState.Auto,
                Method = (int)CompanionMethod.CrossLink,
            });
            await db.SaveChangesAsync();
        }
        (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/mdxSeries/link")).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var md = await db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "mangadex");
            var companion = await db.MetadataCompanions.AsNoTracking().SingleAsync(c => c.CompanionRecordId == md.Id);
            Assert.NotEqual(md.Id, companion.RecordId); // only the other series' row; the self row went with the last link
        }
    }
}
