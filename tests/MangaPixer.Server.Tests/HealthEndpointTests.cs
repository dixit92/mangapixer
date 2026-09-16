namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using Xunit;

/// <summary>
/// Smoke test: health endpoints respond. Uses MangaPixerWebApplicationFactory
/// for consistent configuration with other HTTP tests.
/// </summary>
[Collection("HttpSerial")]
public sealed class HealthEndpointTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public HealthEndpointTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_ReturnHealthy(string path)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
