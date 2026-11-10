namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// 1.53.0 lane S, ratings follow-up 5c: the <c>rating:</c> term in quick results (<c>GET search/suggest</c>) and in the tag view (a search
/// with <c>f=</c>), as the plain search reads it since 1.52.0 - the VIEWER's own rating, never another reader's and never the server
/// average (the shared-ratings switch is on here on purpose). Before 1.53.0 quick results ignored it and the tag view read "rating" and
/// "4" as title words (no work matched).
///
/// Library "rq1": folders "Pluto Saga" (rqP), "Monster Saga" (rqM), "Twenty Saga" (rqT); works W-Pluto @rqP, W-Monster @rqM, W-Twenty @rqT,
/// all of genre Mystery. Reader A rates rqP 8 (4 stars), rqM 4 (2 stars). Reader B rates rqT 10 and shares; A shares too.
/// </summary>
public sealed class RatingTermEverywhereHttpTests
{
    private sealed record World(MangaPixerWebApplicationFactory Factory, AccessReader A, AccessReader B, string Mystery);

    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib = await s.LibraryAsync("rq1", "rq One");
        var pluto = await s.NodeAsync(lib, null, "rqP", "Pluto Saga");
        var monster = await s.NodeAsync(lib, null, "rqM", "Monster Saga");
        var twenty = await s.NodeAsync(lib, null, "rqT", "Twenty Saga");
        var mystery = await s.ValueAsync(FacetBucket.Genres, "mystery", "Mystery");
        foreach (var (key, title, node) in new[] { ("node:9101", "Pluto", pluto), ("node:9102", "Monster", monster), ("node:9103", "Twenty", twenty) })
            await s.EntryAsync(await s.WorkAsync(key, title), node, lib, mystery.Id);
        s.Bump();

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "rqreadera", [lib]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "rqreaderb", [lib]);
        foreach (var (client, node, score) in new[] { (a.Client, "rqP", 8), (a.Client, "rqM", 4), (b.Client, "rqT", 10) })
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/nodes/{node}/rating", new { score })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/admin/ratings/sharing", new { enabled = true })).StatusCode);
        return new World(factory, a, b, mystery.PublicId);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static async Task<List<string>> QuickFoldersAsync(HttpClient client, string text) =>
        (await GetAsync<SearchSuggestDto>(client, "/api/v1/search/suggest?q=" + Uri.EscapeDataString(text))).Folders.Select(f => f.Id).Order().ToList();

    private static async Task<List<string>> TagViewTitlesAsync(World w, HttpClient client, string text) =>
        (await GetAsync<SearchResultsDto>(client,
            "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + w.Mystery) + "&q=" + Uri.EscapeDataString(text)))
        .Works!.Select(x => x.Title).Order().ToList();

    [Fact]
    public async Task QuickResults_KeepOnlyTheViewersOwnRatedNodes()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.Equal(["rqM", "rqP", "rqT"], await QuickFoldersAsync(w.A.Client, "saga"));
        Assert.Equal(["rqP"], await QuickFoldersAsync(w.A.Client, "saga rating:3"));
        Assert.Equal(["rqM", "rqP"], await QuickFoldersAsync(w.A.Client, "rating:2 saga"));
        Assert.Equal(["rqT"], await QuickFoldersAsync(w.A.Client, "saga rating:none"));
        Assert.Equal(["rqP"], await QuickFoldersAsync(w.A.Incognito, "saga rating:4"));
        // B's own: only rqT - never A's ratings, never the server average (rqP's average is 8 for everyone).
        Assert.Equal(["rqT"], await QuickFoldersAsync(w.B.Client, "saga rating:3"));
        Assert.Equal(["rqM", "rqP"], await QuickFoldersAsync(w.B.Client, "saga rating:none"));
    }

    [Fact]
    public async Task TheTagView_ReadsRatingAsTheViewersOwnFilter_NotAsTitleText()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.Equal(["Monster", "Pluto", "Twenty"], await TagViewTitlesAsync(w, w.A.Client, ""));
        Assert.Equal(["Pluto"], await TagViewTitlesAsync(w, w.A.Client, "rating:3"));
        Assert.Equal(["Monster", "Pluto"], await TagViewTitlesAsync(w, w.A.Client, "rating:1"));
        Assert.Equal(["Twenty"], await TagViewTitlesAsync(w, w.A.Client, "rating:none"));
        Assert.Equal(["Pluto"], await TagViewTitlesAsync(w, w.A.Client, "plu rating:2"));
        Assert.Equal(["Pluto"], await TagViewTitlesAsync(w, w.A.Incognito, "rating:4"));
        Assert.Equal(["Twenty"], await TagViewTitlesAsync(w, w.B.Client, "rating:3"));
        Assert.Empty(await TagViewTitlesAsync(w, w.B.Client, "rating:3 pluto"));

        // A rating changed now is answered now (the kept works are part of the statement, so no cached count lies).
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.PutAsJsonAsync("/api/v1/nodes/rqM/rating", new { score = 9 })).StatusCode);
        Assert.Equal(["Monster", "Pluto"], await TagViewTitlesAsync(w, w.A.Client, "rating:3"));
        var page = await GetAsync<SearchResultsDto>(w.A.Client,
            "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + w.Mystery) + "&q=" + Uri.EscapeDataString("rating:3"));
        Assert.Equal(2, page.TotalCount);
    }
}
