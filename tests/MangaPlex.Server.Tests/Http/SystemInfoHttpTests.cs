namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

/// <summary>
/// HTTP test for GET /api/v1/system/info.
/// Verifies the endpoint is accessible unauthenticated (the app footer needs it
/// before login) and returns a non-empty product version from the assembly
/// InformationalVersion attribute (sourced from Version.props at build time).
/// </summary>
[Collection("HttpSerial")]
public sealed class SystemInfoHttpTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;

    public SystemInfoHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetSystemInfo_ReturnsVersion_Unauthenticated()
    {
        // No login — the endpoint must be reachable before auth (footer use).
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/system/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.TryGetProperty("version", out var versionProp));
        var version = versionProp.GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        // InformationalVersion is sourced from Version.props at build time;
        // it contains at least a major.minor.patch (e.g. "1.3.0" or "1.3.0+sha.abc").
        Assert.Contains(".", version);
    }
}
