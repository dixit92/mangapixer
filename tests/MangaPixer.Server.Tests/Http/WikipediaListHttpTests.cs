namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes.Wikipedia;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the Wikipedia companion (1.32.0), through the REAL DI graph: its admin endpoints (admin-only, 404 /
/// 400, choose a page, "no list", check again, the refusals that make no request), the hosted service's volume-cover tick reaching
/// Wikidata / Wikipedia only through the scripted handler, and the credit the Volumes view and a stack carry once Wikipedia completed the
/// list. No folder name, title or address reaches a log line.
/// </summary>
[Trait("Category", "Http")]
public sealed class WikipediaListHttpTests
{
    private const string LibPub = "wplib1";
    private const string Sentinel = "Qzvwikisentinel";
    private const string MuId = MdFixtures.MuBerserk;
    private const string Article = "Synthetic Saga (manga)";
    private const string ListTitle = "List of Synthetic Saga chapters";

    private static void Services(IServiceCollection services)
    {
        var fast = new TokenBucketRateLimiterOptions { TokenLimit = 1000, TokensPerPeriod = 1000, ReplenishmentPeriod = TimeSpan.FromMilliseconds(10), QueueLimit = 10 };
        services.AddSingleton(new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero, MangaDexApi = fast, MangaDexImages = fast, Wikipedia = fast });
    }

    private static string Base36 => MangaUpdatesReference.ToBase36(long.Parse(MuId, System.Globalization.CultureInfo.InvariantCulture));

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, CatalogNodeKind kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == CatalogNodeKind.Folder ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A linked series (\"wpFolder\" with the chapter files \"wpA1\" ... \"wpA3\"), an unlinked folder ("wpLoose") and - when asked - a MangaDex list of four volumes.</summary>
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory, bool mangaDexList = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Wiki Lib", RootPath = "/synthetic/wp", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var folder = Node("wpFolder", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Saga");
        var loose = Node("wpLoose", lib.Id, null, CatalogNodeKind.Folder, $"{Sentinel} Loose");
        db.CatalogNodes.AddRange(folder, loose);
        await db.SaveChangesAsync();
        // Chapter files: chapters 1-3 of volume 1 form a chapter-only stack ("wpA1" is chapter 1).
        foreach (var n in new[] { 1, 2, 3 })
        {
            var archive = Node($"wpA{n}", lib.Id, folder.Id, CatalogNodeKind.Archive, $"{Sentinel} Saga c00{n}");
            db.CatalogNodes.Add(archive);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, PageCount = 2 });
        }
        var record = new MetadataRecordEntity
        {
            PublicId = "wprec",
            Provider = "mangaupdates",
            ExternalId = MuId,
            Title = "Synthetic Saga",
            OriginVolumes = 6,
            FetchedAt = DateTimeOffset.UtcNow,
        };
        db.MetadataRecords.Add(record);
        await db.SaveChangesAsync();
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = folder.Id,
            LibraryId = lib.Id,
            State = (int)SeriesLinkState.Auto,
            RecordId = record.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (mangaDexList)
        {
            var list = Enumerable.Range(1, 4)
                .Select(v => new VolumeMapEntry(v.ToString(), Enumerable.Range(((v - 1) * 4) + 1, 4).Select(c => c.ToString()).ToList())).ToList();
            db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = VolumeMapJson.Write(list),
                ContentHash = "seed",
                Version = 1,
                FetchedAt = DateTimeOffset.UtcNow,
                NextCheckAt = DateTimeOffset.UtcNow.AddDays(30),
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<HttpClient> StartAsync(
        MetadataNetworkWebApplicationFactory factory, FakeWikimedia wiki, bool automatic = false, bool mangaDexList = true)
    {
        await SeedAsync(factory, mangaDexList);
        wiki.Items[Base36] = ["Q4242"];
        wiki.Sitelinks["Q4242"] = Article;
        wiki.Pages[Article] = new FakeWikimedia.Page(1, "A short article.");
        wiki.Pages[ListTitle] = new FakeWikimedia.Page(100, FakeWikimedia.ListPage("Volumes", FakeWikimedia.Run(6)));
        factory.Handler.Respond = request => wiki.Respond(request) ?? MdFixtures.Route(request)!;
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = automatic,
            AcceptedAutoConsentVersion = automatic ? MetadataAutoConsent.CurrentVersion : null,
        })).EnsureSuccessStatusCode();
        return admin;
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static int WikimediaCalls(MetadataNetworkWebApplicationFactory factory) =>
        factory.Handler.Seen.Count(s => FakeWikimedia.IsWikimedia(s.Uri));

    [Fact]
    public async Task TheEndpoints_AreAdminOnly_And404ForAnUnlinkedNode()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Services);
        var admin = await StartAsync(factory, new FakeWikimedia());
        var reader = await factory.CreateReaderClientAsync("wpreader", LibPub);

        var forbidden = new[]
        {
            await reader.GetAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"),
            await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia", new WikipediaPageRequest { Page = "Anything" }),
            await reader.DeleteAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"),
            await reader.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null),
        };
        Assert.All(forbidden, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/nodes/wpLoose/wikipedia")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpLoose/wikipedia", new WikipediaPageRequest { Page = ListTitle })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/wpLoose/wikipedia")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/metadata/nodes/wpLoose/wikipedia/recheck", null)).StatusCode);
        Assert.Equal(0, WikimediaCalls(factory));
    }

    [Fact]
    public async Task APageIsChosenByAddress_ReadNow_AndCleared_ThenCheckedAgain()
    {
        var sink = new CollectingSink();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink, configureServices: Services);
        var admin = await StartAsync(factory, new FakeWikimedia());

        var before = await OkAsync<WikipediaListDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"));
        Assert.Equal((WikipediaListState.NotFound, 0), (before.State, before.Volumes));
        Assert.Equal(0, WikimediaCalls(factory)); // looking at it asks nobody

        // An address that is not en.wikipedia.org is refused locally.
        var bad = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia", new WikipediaPageRequest { Page = "https://example.invalid/wiki/Anything" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_page", (await bad.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(0, WikimediaCalls(factory));

        var set = await OkAsync<WikipediaListDto>(await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia",
            new WikipediaPageRequest { Page = "https://en.wikipedia.org/wiki/List_of_Synthetic_Saga_chapters" }));
        Assert.Equal((WikipediaListState.Found, WikipediaListMethod.Admin, 6), (set.State, set.Method, set.Volumes));
        Assert.Equal(ListTitle, set.AdminTitle);
        var page = Assert.Single(set.Pages);
        Assert.Equal((ListTitle, 100L, "https://en.wikipedia.org/wiki/List_of_Synthetic_Saga_chapters"), (page.Title, page.Revision, page.Url));
        Assert.Equal(6, set.Details.Count);
        Assert.Equal(("1", "2021-01-01", FakeWikimedia.IsbnOf(1)), (set.Details[0].Volume, set.Details[0].EnglishDate, set.Details[0].EnglishIsbn));

        // An archive inside the linked folder resolves to the same series.
        Assert.Equal(WikipediaListState.Found, (await OkAsync<WikipediaListDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/wpA1/wikipedia"))).State);

        var cleared = await OkAsync<WikipediaListDto>(await admin.DeleteAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"));
        Assert.Equal((WikipediaListState.None, 0), (cleared.State, cleared.Volumes));
        var callsSoFar = factory.Handler.CallCount;
        factory.Handler.FailOnAnyRequest = true;
        Assert.Equal(WikipediaListState.None, (await OkAsync<WikipediaListDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"))).State);
        Assert.Equal(callsSoFar, factory.Handler.CallCount); // "no list" is looked at locally

        factory.Handler.FailOnAnyRequest = false;
        var again = await OkAsync<WikipediaListDto>(await admin.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null));
        Assert.Equal((WikipediaListState.Found, 6), (again.State, again.Volumes));

        // Everything sent carries only titles and the fixed User-Agent; nothing of the library; nothing in the logs.
        foreach (var seen in factory.Handler.Seen.Where(s => FakeWikimedia.IsWikimedia(s.Uri)))
        {
            Assert.Equal(MetadataHttp.UserAgent, seen.Headers["User-Agent"]);
            Assert.DoesNotContain(Sentinel, Uri.UnescapeDataString(seen.Uri.ToString()), StringComparison.OrdinalIgnoreCase);
        }
        // The framework's own Trace-level argument logging is off in production (Microsoft.AspNetCore: Warning); our categories are checked.
        foreach (var e in sink.Events.Where(e => !(e.Properties.TryGetValue("SourceContext", out var source) && source.ToString().Contains("Microsoft.AspNetCore", StringComparison.Ordinal))))
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), "A name leaked into a log line: " + e.MessageTemplate.Text);
            Assert.False(rendered.Contains("Synthetic Saga", StringComparison.OrdinalIgnoreCase), "A title leaked into a log line: " + e.MessageTemplate.Text);
            Assert.False(rendered.Contains("en.wikipedia", StringComparison.OrdinalIgnoreCase), "An address leaked into a log line: " + e.MessageTemplate.Text);
        }
    }

    [Fact]
    public async Task ARemovedSite_RefusesTheActions_WithoutARequest_AndAFreshInstallMakesNoCall()
    {
        using var fresh = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true, configureServices: Services);
        await SeedAsync(fresh);
        var freshAdmin = await fresh.LoginAsAdminWithChangedPasswordAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await freshAdmin.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await freshAdmin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia", new WikipediaPageRequest { Page = ListTitle })).StatusCode);
        Assert.Equal(0, fresh.Handler.CallCount);

        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Services);
        var admin = await StartAsync(factory, new FakeWikimedia());
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { RemovedProviders = [MetadataProviderAllowlist.Wikipedia] })).EnsureSuccessStatusCode();

        var refused = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia", new WikipediaPageRequest { Page = ListTitle });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("provider_not_allowed", (await refused.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null)).StatusCode);
        Assert.Equal(0, WikimediaCalls(factory));
        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().WikipediaLists.AnyAsync()); // a refused action stores nothing
    }

    [Fact]
    public async Task AMaxlagAnswer_IsA503WithRetryAfter_ThenRefusedWithoutARequest()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Services);
        var wiki = new FakeWikimedia();
        var admin = await StartAsync(factory, wiki);
        wiki.MaxLagSeconds = 9;

        var first = await admin.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.True(first.Headers.RetryAfter is not null);
        Assert.Equal("provider_backoff", (await first.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(1, WikimediaCalls(factory));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia/recheck", null)).StatusCode);
        Assert.Equal(1, WikimediaCalls(factory)); // refused by the persisted backoff: no second request
    }

    [Fact]
    public async Task TheWorkerTick_ThroughDI_ReadsWikipedia_AndTheViewsCarryTheCredit()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: Services);
        var admin = await StartAsync(factory, new FakeWikimedia(), automatic: true);

        // Before: MangaDex's four volumes are the list; no credit.
        var plain = await OkAsync<VolumeViewDto>(await admin.GetAsync("/api/v1/nodes/wpFolder/volume-view"));
        Assert.Null(plain.Progress?.ListCredit);

        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();
        await worker.RunVolumeCoversAsync(CancellationToken.None);

        Assert.Equal(3, WikimediaCalls(factory)); // Wikidata item search + sitelink, the pages (MangaDex's list has a gap: 4 of 6 volumes)
        Assert.DoesNotContain(factory.Handler.Seen, s => Uri.UnescapeDataString(s.Uri.ToString()).Contains(Sentinel, StringComparison.OrdinalIgnoreCase));
        var stored = await OkAsync<WikipediaListDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/wpFolder/wikipedia"));
        Assert.Equal((WikipediaListState.Found, WikipediaListMethod.Wikidata, 6), (stored.State, stored.Method, stored.Volumes));

        var view = await OkAsync<VolumeViewDto>(await admin.GetAsync("/api/v1/nodes/wpFolder/volume-view"));
        var credit = view.Progress?.ListCredit;
        Assert.NotNull(credit);
        Assert.Equal(("Wikipedia", ListTitle, "https://en.wikipedia.org/wiki/List_of_Synthetic_Saga_chapters"), (credit.Name, credit.Title, credit.Url));
        var stack = await OkAsync<VolumeStackDto>(await admin.GetAsync("/api/v1/nodes/wpFolder/volumes/1"));
        Assert.Equal(credit, stack.ListCredit);

        // A second tick before the cadence sends nothing to Wikimedia.
        var calls = WikimediaCalls(factory);
        await worker.RunVolumeCoversAsync(CancellationToken.None);
        Assert.Equal(calls, WikimediaCalls(factory));
    }
}
