namespace com.lifepixer.mangaplex.Tests.Server.Contracts;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using com.lifepixer.mangaplex.Tests.Server.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// OpenAPI snapshot test (audit defect D35).
/// Fetches /openapi/v1.json through WebApplicationFactory and compares
/// against contracts/openapi.json. When MANGAPLEX_UPDATE_SNAPSHOT=1,
/// writes the live document to the snapshot instead of comparing.
/// </summary>
[Trait("Category", "Http")]
public sealed class OpenApiSnapshotTests
{
    private static readonly string SnapshotPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "openapi.json");

    [Fact]
    public async Task OpenApi_Endpoint_ReturnsDocument()
    {
        var factory = new MangaPlexWebApplicationFactory();
        using var client = factory.CreateClient();

        // The OpenAPI endpoint is unauthenticated (no private data)
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(json);

        // Parse and verify basic structure
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("openapi", out _));
        Assert.True(doc.RootElement.TryGetProperty("paths", out var paths));
        Assert.True(paths.EnumerateObject().Any());
    }

    [Fact]
    public async Task OpenApi_Snapshot_MatchesLiveDocument()
    {
        var factory = new MangaPlexWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var liveJson = await response.Content.ReadAsStringAsync();

        var updateSnapshot = Environment.GetEnvironmentVariable("MANGAPLEX_UPDATE_SNAPSHOT") == "1";

        if (updateSnapshot)
        {
            var formatted = FormatJson(liveJson);
            await File.WriteAllTextAsync(SnapshotPath, formatted);
            Assert.Fail($"Snapshot updated at {SnapshotPath}. Re-run without MANGAPLEX_UPDATE_SNAPSHOT to verify.");
        }

        // Compare against the committed snapshot
        Assert.True(File.Exists(SnapshotPath), $"Snapshot not found at {SnapshotPath}");
        var snapshotJson = await File.ReadAllTextAsync(SnapshotPath);

        var liveFormatted = FormatJson(liveJson);
        var snapshotFormatted = FormatJson(snapshotJson);

        Assert.Equal(snapshotFormatted, liveFormatted);
    }

    private static string FormatJson(string json)
    {
        var node = JsonNode.Parse(json);
        var options = new JsonSerializerOptions { WriteIndented = true };
        return node!.ToJsonString(options);
    }
}
