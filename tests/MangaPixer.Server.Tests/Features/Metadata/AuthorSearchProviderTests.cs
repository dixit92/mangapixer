namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using Xunit;

/// <summary>
/// Author search by name (1.45.0): the MangaUpdates provider method sends exactly the fixed body and reads the answer tolerantly (unit,
/// over the real named client with the scripted handler); the reference format of a picked author. Synthetic names.
/// </summary>
public sealed class AuthorSearchProviderTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;
    private GatewayHarness _h = null!;

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new GatewayHarness(_t);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    [Fact]
    public async Task SearchAuthors_PostsExactlyTheTextPageAndPageSize()
    {
        _h.Handler.Respond = _ => ScriptedHandler.Json("""{"total_hits":0,"results":[]}""");
        var page = await _h.Provider.SearchAuthorsAsync("Beta \"Painter\"", CancellationToken.None);

        Assert.Empty(page.Hits);
        Assert.Equal(0, page.Total);
        var sent = Assert.Single(_h.Handler.Seen);
        Assert.Equal((HttpMethod.Post, "https://api.mangaupdates.com/v1/authors/search"), (sent.Method, sent.Uri.AbsoluteUri));
        // The whole body, byte for byte: no added_by, letter, genre, orderby or pending - ever.
        Assert.Equal("""{"search":"Beta "Painter"","page":1,"perpage":10}""", sent.Body);
        using var doc = JsonDocument.Parse(sent.Body!);
        Assert.Equal(["search", "page", "perpage"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ToAuthorSearch_ReadsBothGenreShapes_SkipsUnusableHits_AndKeepsOnlyMangaUpdatesPages()
    {
        var body = JsonSerializer.Deserialize<MuAuthorSearchResponse>("""
            {"total_hits": 1, "results": [
              {"record": {"id": 7, "name": "Beta Painter", "url": "https://www.mangaupdates.com/author/x/beta",
                          "genres": [{"genre": "Drama"}, "Romance", 5, {"other": 1}, null], "stats": {"total_series": 4}}, "hit_name": "beta painter"},
              {"record": {"id": 7, "name": "Duplicate"}},
              {"record": {"id": 8, "name": "Gamma Inker", "url": "https://mangaupdates.com.evil.example/a", "genres": "Drama",
                          "stats": {"total_series": -1}}, "hit_name": "G. Inker"},
              {"record": {"id": -3, "name": "Negative"}},
              {"record": null}
            ]}
            """)!;
        var page = MangaUpdatesMapping.ToAuthorSearch(body);

        Assert.Equal(2, page.Total); // at least the hits
        Assert.Equal(["7", "8"], page.Hits.Select(h => h.ExternalId).ToArray());
        var beta = page.Hits[0];
        Assert.Equal(("Beta Painter", (string?)null, (int?)4, "https://www.mangaupdates.com/author/x/beta"), (beta.Name, beta.HitName, beta.TotalSeries, beta.SiteUrl));
        Assert.Equal(["Drama", "Romance"], beta.Genres);
        var gamma = page.Hits[1];
        Assert.Equal(("G. Inker", (int?)null, (string?)null), (gamma.HitName, gamma.TotalSeries, gamma.SiteUrl));
        Assert.Empty(gamma.Genres);
    }

    [Fact]
    public async Task SearchAuthors_AnErrorStatus_IsAnHttpFailure_ForTheGateway()
    {
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAnyAsync<Exception>(() => _h.Provider.SearchAuthorsAsync("Beta Painter", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("mangaupdates", "4815162342", "mangaupdates:4815162342", null)]
    [InlineData(" mangaupdates ", " 42 ", "mangaupdates:42", null)]
    [InlineData("mangaupdates", "0", null, "author_ref_invalid")]
    [InlineData("mangaupdates", "042", null, "author_ref_invalid")]
    [InlineData("mangaupdates", "-4", null, "author_ref_invalid")]
    [InlineData("mangaupdates", "12345678901234567890", null, "author_ref_invalid")]
    [InlineData("MangaUpdates", "42", null, "author_ref_invalid")]
    [InlineData("mangadex", "42", null, "author_ref_invalid")]
    [InlineData("mangaupdates", null, null, "author_ref_invalid")]
    [InlineData(null, "42", null, "author_ref_invalid")]
    public void Reference_FromRequest(string? provider, string? id, string? reference, string? error)
    {
        Assert.Equal((reference, error), AuthorReference.FromRequest(provider, id));
        if (reference is not null)
            Assert.Equal(id!.Trim(), AuthorReference.MangaUpdatesIdOf(reference));
    }

    [Theory]
    [InlineData("mangaupdates:42", "42")]
    [InlineData("mangaupdates:", null)]
    [InlineData("mangaupdates:4a", null)]
    [InlineData("gcd:42", null)]
    [InlineData(null, null)]
    public void Reference_MangaUpdatesIdOf(string? reference, string? id) => Assert.Equal(id, AuthorReference.MangaUpdatesIdOf(reference));
}
