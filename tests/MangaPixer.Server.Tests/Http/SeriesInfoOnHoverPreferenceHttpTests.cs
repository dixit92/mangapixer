namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using com.lifepixer.mangapixer.Core.Api;
using Xunit;

/// <summary>
/// HTTP round-trip for the per-user "Series information on hover" option (1.27.0)
/// through the existing <c>reading/library-preferences</c> endpoint: ON by default,
/// OFF persists, and a PUT from a client that predates the field keeps it ON.
/// </summary>
[Collection("HttpSerial")]
public sealed class SeriesInfoOnHoverPreferenceHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string Url = "/api/v1/reading/library-preferences";
    private readonly MangaPixerWebApplicationFactory _factory;

    public SeriesInfoOnHoverPreferenceHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SeriesInfoOnHover_DefaultsOn_RoundTrips_AndAnOlderClientKeepsItOn()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        async Task<LibraryViewPreferencesDto> GetAsync() =>
            (await client.GetFromJsonAsync<LibraryViewPreferencesDto>(Url, TestJson.Web))!;

        var initial = await GetAsync();
        Assert.True(initial.SeriesInfoOnHover);

        (await client.PutAsJsonAsync(Url, initial with { SeriesInfoOnHover = false, ListColumns = 3 })).EnsureSuccessStatusCode();
        var off = await GetAsync();
        Assert.False(off.SeriesInfoOnHover);
        Assert.Equal(3, off.ListColumns);

        // The wire name is camelCase like the other preferences.
        var raw = JsonNode.Parse(await client.GetStringAsync(Url))!;
        Assert.False(raw["seriesInfoOnHover"]!.GetValue<bool>());

        (await client.PutAsJsonAsync(Url, off with { SeriesInfoOnHover = true })).EnsureSuccessStatusCode();
        Assert.True((await GetAsync()).SeriesInfoOnHover);

        // A client that does not know the field (no seriesInfoOnHover in the body) leaves it ON.
        (await client.PutAsJsonAsync(Url, off with { SeriesInfoOnHover = false })).EnsureSuccessStatusCode();
        var legacyBody = new { viewMode = "grid", density = "comfortable", sort = "name" };
        (await client.PutAsJsonAsync(Url, legacyBody)).EnsureSuccessStatusCode();
        Assert.True((await GetAsync()).SeriesInfoOnHover);
    }
}
