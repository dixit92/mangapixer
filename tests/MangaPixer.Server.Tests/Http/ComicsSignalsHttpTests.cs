namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Gcd;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of the 1.32.0 comics signals: an automatic matching pass started over HTTP reaches the
/// production <see cref="ComicsSignalReader"/> (declared type, ComicInfo publisher / web link, issue names, the folder's start
/// year), and the scorer receives the signs in <see cref="MatchContext.Comics"/> (observed through the existing scorer seam:
/// the production scorer wrapped by a recorder); a plain manga folder gets none. The series information panel shows a Grand
/// Comics Database / Metron link found in ComicInfo. Every outbound request ends in the scripted handler; names are synthetic.
/// </summary>
[Trait("Category", "Http")]
public sealed class ComicsSignalsHttpTests
{
    private const string LibPub = "cslib1";
    private const string ComicTitle = "Qzv Starfall Saga";
    private const string MangaTitle = "Qzv Lantern Tale";
    private const string GcdUrl = "https://www.comics.org/series/424242/";
    private const string MetronUrl = "https://metron.cloud/series/qzv-starfall-saga-2012/";

    /// <summary>The production scorer, recording the context of every query it scores (by the first query text).</summary>
    private sealed class RecordingScorer : IMatchScorer
    {
        private readonly MatchScorer _inner = new();

        public ConcurrentDictionary<string, ComicsSignal?> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public MatchOutcome Score(MatchQuery query, IReadOnlyList<MatchCandidate> candidates, MatchThresholds thresholds)
        {
            Seen[query.Variants.FirstOrDefault()?.Text ?? string.Empty] = query.Context.Comics;
            return _inner.Score(query, candidates, thresholds);
        }
    }

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

    /// <summary>csComic: <c>Title (2012)</c> with three <c>#N</c> issues tagged Image + a GCD / Metron link; csManga: plain volumes.</summary>
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Comics Lib", RootPath = "/synthetic/cs", CreatedAt = DateTimeOffset.UtcNow, MetadataEnabled = true };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var comic = Node("csComic", lib.Id, null, CatalogNodeKind.Folder, $"{ComicTitle} (2012)");
        var manga = Node("csManga", lib.Id, null, CatalogNodeKind.Folder, MangaTitle);
        db.CatalogNodes.AddRange(comic, manga);
        await db.SaveChangesAsync();
        var archives = new List<CatalogNodeEntity>();
        for (var i = 1; i <= 3; i++)
        {
            archives.Add(Node($"csC{i}", lib.Id, comic.Id, CatalogNodeKind.Archive, $"{ComicTitle} #{i:D3} (2012) (Digital)"));
            archives.Add(Node($"csM{i}", lib.Id, manga.Id, CatalogNodeKind.Archive, $"{MangaTitle} v{i:D2}"));
        }
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();
        foreach (var a in archives)
        {
            var isComic = a.PublicId.StartsWith("csC", StringComparison.Ordinal);
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = a.Id, ContentVersion = 1, PageCount = isComic ? 24 : 190 });
            if (isComic)
            {
                db.EmbeddedMetadata.Add(new EmbeddedMetadataEntity
                {
                    NodeId = a.Id,
                    ContentVersion = 1,
                    State = 1,
                    Series = ComicTitle,
                    Publisher = "Image",
                    WebUrlsJson = a.PublicId == "csC1" ? JsonSerializer.Serialize(new[] { GcdUrl, MetronUrl }) : null,
                    ReadAt = DateTimeOffset.UtcNow,
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    [Fact]
    public async Task AnAutomaticPass_CarriesTheComicsSigns_OfADeclaredComicFolder_AndNoneForManga()
    {
        var scorer = new RecordingScorer();
        using var factory = new MetadataNetworkWebApplicationFactory(configureServices: services =>
        {
            services.AddSingleton<IMatchScorer>(scorer);
            services.AddSingleton(new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero });
        });
        await SeedAsync(factory);
        // 1.32.0 lane B: the declared comic is routed to the Grand Comics Database first - its ComicInfo GCD id (tier 0) is not
        // found and the GCD name search is empty (recorded fixtures), so it falls back to MangaUpdates and is scored there.
        factory.Handler.Respond = request => GcdFixtures.Respond(request) ?? RespondMangaUpdates(request);
        static HttpResponseMessage RespondMangaUpdates(HttpRequestMessage request)
        {
            var comic = request.Method == HttpMethod.Post
                ? request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("Starfall", StringComparison.Ordinal)
                : request.RequestUri!.Segments[^1] == "9811";
            var (id, title) = comic ? (9811L, ComicTitle) : (9812L, MangaTitle);
            return request.Method == HttpMethod.Post
                ? ScriptedHandler.Json(MuJson.Search(new MuJson.Hit(id, title)))
                : ScriptedHandler.Json(MuJson.Get(id, title));
        }
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/folders/csComic/declared",
            new SetDeclaredFactsRequest { Type = DeclaredType.Comic }, TestJson.Web)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}/match", new MetadataMatchLibraryRequest())).EnsureSuccessStatusCode();

        var worker = factory.Services.GetServices<IHostedService>().OfType<MetadataAutoMatchHostedService>().Single();
        for (var pass = 0; pass < 5 && await worker.RunPassAsync(CancellationToken.None) > 0; pass++)
        {
        }

        Assert.True(scorer.Seen.TryGetValue(ComicTitle, out var comics), "the comic folder was never scored");
        Assert.NotNull(comics);
        Assert.Equal(
            ComicsSignalKind.DeclaredType | ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.WesternPublisher
                | ComicsSignalKind.IssueNumbering | ComicsSignalKind.StartYearAfterName,
            comics!.Kinds);
        Assert.True(comics.RoutesToComics);
        Assert.Equal(2012, comics.StartYear);
        Assert.Equal(ComicsPageShape.Issues, comics.PageShape);
        Assert.Contains(new ComicsId(ComicsIdSite.Gcd, ComicsIdKind.Series, "424242"), comics.Ids);

        Assert.True(scorer.Seen.TryGetValue(MangaTitle, out var manga), "the manga folder was never scored");
        Assert.Null(manga);
    }

    [Fact]
    public async Task TheSeriesInformationPanel_ShowsComicsDatabaseLinks_FromComicInfo()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var info = await OkAsync<SeriesInfoDto>(await admin.GetAsync("/api/v1/nodes/csComic/series-info"));

        Assert.Contains(GcdUrl, info.ComicInfo!.WebLinks!);
        Assert.Contains(MetronUrl, info.ComicInfo.WebLinks!);
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
