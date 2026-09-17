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

    /// <summary>
    /// "/health/ready" runs a distinct database-backed readiness check (see
    /// com.lifepixer.mangapixer.Server.DatabaseReadinessHealthCheck), while
    /// "/health" is the cheap in-process liveness check only. Corrupting the
    /// live SQLite file must fail readiness while liveness keeps reporting
    /// the process itself is still running.
    /// </summary>
    [Fact]
    public async Task Ready_FailsWhenDatabaseUnreachable_WhileLivenessStaysUp()
    {
        // Bootstraps the DB (setup creates the schema); the health checks
        // themselves are exercised via a separate, unauthenticated client
        // below so cookie authentication's own session lookup (which also
        // hits the DB) doesn't confound the readiness assertion.
        await _factory.LoginAsAdminWithChangedPasswordAsync();
        var anonymous = _factory.CreateClient();

        var readyBefore = await anonymous.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readyBefore.StatusCode);

        // Corrupt the live DB file past the header so a real query fails.
        var dbPath = Path.Combine(_factory.DataRoot, "mangapixer.db");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(dbPath);
        for (var i = 100; i < Math.Min(bytes.Length, 4096); i++)
            bytes[i] = 0xFF;
        await File.WriteAllBytesAsync(dbPath, bytes);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var readyAfter = await anonymous.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyAfter.StatusCode);

        var liveAfter = await anonymous.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, liveAfter.StatusCode);
    }
}
