namespace com.lifepixer.mangaplex.Tests.Server;

using System.Net;
using Xunit;
using ServerProgram = com.lifepixer.mangaplex.Server.Program;

/// <summary>
/// P00 smoke test: health endpoints respond. Full API tests are added in later packages.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ServerProgram>>
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ServerProgram> _factory;

    public HealthEndpointTests(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<ServerProgram> factory)
    {
        _factory = factory;
    }

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
