namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Gcd;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of Identify's site switch (1.32.0, lane B): the context lists MangaUpdates and the Grand
/// Comics Database and defaults to GCD for a folder below a Comics category folder; a GCD search carries the start year only when
/// it is the (YYYY) of the folder's own name; a pasted comics.org series address previews by id; an issue address is refused;
/// a GCD removed from the allowlist is refused with zero requests; a GCD-linked series shows its CC BY-SA credit. Every GCD answer
/// is a recorded fixture (Fixtures/Gcd); nothing reaches the network.
/// </summary>
public sealed class GcdIdentifyHttpTests
{
    private const string LibPub = "gcdlib1";

    // gcdBone: "Comics/Bone (1991)" with one archive; gcdPlain: a plain folder.
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        factory.Handler.Respond = request => GcdFixtures.Respond(request)
            ?? ScriptedHandler.Json("{\"total_hits\":0,\"results\":[]}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Comics Lib", RootPath = "/synthetic/gcd", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var comics = Node("gcdComics", lib.Id, null, 0, "Comics");
        var plain = Node("gcdPlain", lib.Id, null, 0, "Plain Folder");
        db.CatalogNodes.AddRange(comics, plain);
        await db.SaveChangesAsync();
        var bone = Node("gcdBone", lib.Id, comics.Id, 0, "Bone (1991)");
        db.CatalogNodes.Add(bone);
        await db.SaveChangesAsync();
        var arc = Node("gcdBoneArc", lib.Id, bone.Id, 1, "Bone 001 (1991)");
        db.CatalogNodes.Add(arc);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 28 });
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

    private static async Task<HttpClient> EnabledAdminAsync(MetadataNetworkWebApplicationFactory factory)
    {
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = true }))
            .EnsureSuccessStatusCode();
        return admin;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static IEnumerable<SeenRequest> Gcd(MetadataNetworkWebApplicationFactory f) =>
        f.Handler.Seen.Where(r => r.Uri.Host is "www.comics.org" or "files1.comics.org");

    [Fact]
    public async Task Context_ListsBothSites_DefaultsToGcdForAComicsFolder_MangaUpdatesOtherwise_NoRequest()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);

        var comics = await ReadAsync<IdentifyContextDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/gcdBone/identify"));
        Assert.Equal("gcd", comics.Provider);
        Assert.Equal("Grand Comics Database", comics.ProviderName);
        Assert.True(comics.ComicsSignalled);
        Assert.True(comics.FetchAvailable);
        Assert.Equal(["mangaupdates", "gcd"], comics.Sites.Select(s => s.Id));
        Assert.All(comics.Sites, s => Assert.True(s.Available));
        Assert.Contains("25 requests an hour", comics.Sites[1].Note, StringComparison.Ordinal);
        Assert.Equal(1991, comics.Local.YearHint);

        var plain = await ReadAsync<IdentifyContextDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/gcdPlain/identify"));
        Assert.Equal("mangaupdates", plain.Provider);
        Assert.False(plain.ComicsSignalled);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task Search_OnGcd_CarriesTheNamesStartYearOnly_RanksWholeSeries()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);

        var result = await ReadAsync<IdentifySearchResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/search",
            new IdentifySearchRequest { Query = "Bone", Provider = "gcd", StartYear = 1991 }));
        Assert.Equal("gcd", result.Provider);
        Assert.Equal("/api/series/name/Bone/year/1991/", Assert.Single(Gcd(factory)).Uri.AbsolutePath);
        var top = result.Candidates[0];
        Assert.Equal("4347", top.ExternalId);
        Assert.Equal(MetadataOrigin.EnglishOriginal, top.Origin);
        Assert.Equal("en", top.Language);
        Assert.Equal(20, top.UnitCount);
        Assert.Equal("issues", top.UnitKind);
        Assert.Null(top.ImageToken);

        // A year that is not the folder name's own is never sent.
        factory.Handler.Reset();
        factory.Handler.Respond = request => GcdFixtures.Respond(request) ?? ScriptedHandler.Json("{}");
        await ReadAsync<IdentifySearchResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/search",
            new IdentifySearchRequest { Query = "Blacksad", Provider = "gcd", StartYear = 2000 }));
        Assert.Equal("/api/series/name/Blacksad/", Assert.Single(Gcd(factory)).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Lookup_PastedComicsOrgSeries_PreviewsById_WithCredit_ThenLinks()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);

        var preview = await ReadAsync<IdentifyPreviewDto>(await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/lookup",
            new IdentifyLookupRequest { Reference = "https://www.comics.org/series/4347/" }));
        Assert.Equal("gcd", preview.Provider);
        Assert.Equal("Bone", preview.Title);
        Assert.Equal("Data: Grand Comics Database, CC BY-SA 4.0", preview.Credit);
        Assert.Equal(["Cartoon Books"], preview.Publishers);
        Assert.Equal("https://www.comics.org/series/4347/", preview.SiteUrl);
        // Only ids are sent: the series, its publisher and its first issue.
        Assert.Equal(["/api/series/4347/", "/api/publisher/672/", "/api/issue/49773/"], Gcd(factory).Select(r => r.Uri.AbsolutePath));

        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/link", new LinkSeriesRequest { Provider = "gcd", ExternalId = "4347" }))
            .EnsureSuccessStatusCode();
        var info = await ReadAsync<SeriesInfoDto>(await admin.GetAsync("/api/v1/nodes/gcdBone/series-info"));
        Assert.Equal("Grand Comics Database", info.Web!.ProviderName);
        Assert.Equal("Data: Grand Comics Database, CC BY-SA 4.0", info.Web.Credit);
        Assert.False(info.Web.HasImage); // The folder keeps its own cover.
        Assert.Equal(3, Gcd(factory).Count()); // Linking a stored record sends nothing more.
    }

    [Fact]
    public async Task Lookup_IssueAddress_IsRefusedWithAHint_NothingSent()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);

        var response = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/lookup",
            new IdentifyLookupRequest { Reference = "https://www.comics.org/issue/49773/" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("gcd_issue_url", (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task GcdRemovedFromTheAllowlist_SearchRefused_ZeroCalls_ComicsFolderFallsBackToMangaUpdates()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.AppSettings.FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);
            row.MetadataProvidersJson = com.lifepixer.mangapixer.Server.Features.Metadata.MetadataProviderAllowlist.Write(["gcd"]);
            await db.SaveChangesAsync();
        }

        var response = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/search",
            new IdentifySearchRequest { Query = "Bone", Provider = "gcd" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("provider_not_allowed", (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(0, factory.Handler.CallCount);

        var context = await ReadAsync<IdentifyContextDto>(await admin.GetAsync("/api/v1/admin/metadata/nodes/gcdBone/identify"));
        Assert.Equal("mangaupdates", context.Provider);
        Assert.False(context.Sites.Single(s => s.Id == "gcd").Available);
        Assert.Equal("provider_not_allowed", context.Sites.Single(s => s.Id == "gcd").UnavailableCode);
    }

    [Fact]
    public async Task UnknownSite_IsABadRequest()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        var admin = await EnabledAdminAsync(factory);
        var response = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/gcdBone/search",
            new IdentifySearchRequest { Query = "Bone", Provider = "comicvine" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
