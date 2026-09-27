namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using Xunit;

/// <summary>
/// HTTP tests for the search-engine opt-out (1.27.0): by default every response carries
/// <c>X-Robots-Tag: noindex, nofollow</c> and <c>/robots.txt</c> disallows crawling (it used to
/// fall through to the SPA fallback); <c>MangaPixer:Network:AllowSearchIndexing=true</c> turns both off.
/// </summary>
[Collection("HttpSerial")]
public sealed class SearchIndexingHttpTests
{
    [Fact]
    public async Task Default_RobotsTxt_DisallowsEverything_Unauthenticated()
    {
        await using var factory = new MangaPixerWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/robots.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("User-agent: *\nDisallow: /\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("noindex, nofollow", RobotsHeader(response));
    }

    [Fact]
    public async Task Default_RobotsTxt_AnswersHead()
    {
        await using var factory = new MangaPixerWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/robots.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("noindex, nofollow", RobotsHeader(response));
    }

    [Theory]
    [InlineData("/api/v1/system/info")]   // anonymous API
    [InlineData("/api/v1/auth/me")]        // 401 without a session
    [InlineData("/health")]
    public async Task Default_EveryResponse_CarriesNoIndexHeader(string path)
    {
        await using var factory = new MangaPixerWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal("noindex, nofollow", RobotsHeader(response));
    }

    [Fact]
    public async Task AllowSearchIndexing_RobotsTxtAllows_AndNoHeader()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { ["MangaPixer:Network:AllowSearchIndexing"] = "true" });
        var client = factory.CreateClient();

        var robots = await client.GetAsync("/robots.txt");
        var api = await client.GetAsync("/api/v1/system/info");

        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Equal("User-agent: *\nDisallow:\n", await robots.Content.ReadAsStringAsync());
        Assert.Null(RobotsHeader(robots));
        Assert.Null(RobotsHeader(api));
    }

    private static string? RobotsHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Robots-Tag", out var values) ? string.Join(",", values) : null;
}
