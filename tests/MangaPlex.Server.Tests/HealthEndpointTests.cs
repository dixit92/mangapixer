namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using Xunit;

/// <summary>
/// P00 smoke test: health endpoints respond. Now uses MangaPlexWebApplicationFactory
/// for consistent configuration with other HTTP tests.
/// </summary>
[Collection("HttpSerial")]
public sealed class HealthEndpointTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;

    public HealthEndpointTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
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
