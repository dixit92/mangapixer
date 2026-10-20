namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Operations.Perf;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Xunit;

/// <summary>
/// Measurement-only request / SQL timing (1.46.0 lane M) through the public surface: off by default (no header, no
/// <c>perf</c> block); with <c>MangaPixer:Perf:RequestTiming=true</c> an API response carries the <c>Server-Timing</c>
/// fields and the admin diagnostics snapshot lists the route, the SQL statement shapes and the heap.
/// </summary>
public sealed class PerfTimingHttpTests
{
    private const string Diagnostics = "/api/v1/operations/diagnostics";

    [Fact]
    public async Task Default_AddsNoTimingHeader_AndNoPerfBlock()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync(Diagnostics);
        response.EnsureSuccessStatusCode();
        Assert.False(response.Headers.Contains("Server-Timing"));
        var diag = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(diag.TryGetProperty("perf", out _));
    }

    [Fact]
    public async Task Switched_On_TimesRequests_AndListsRoutesStatementsAndHeap()
    {
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
            new Dictionary<string, string?> { [PerfRecorder.ConfigKey] = "true" });
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();

        var first = await client.GetAsync(Diagnostics);
        first.EnsureSuccessStatusCode();
        var timing = string.Join(", ", first.Headers.GetValues("Server-Timing"));
        // Durations and counts only: every part is a known field with a number.
        Assert.All(timing.Split(", "), part => Assert.Matches(@"^(app|sql|sqln|lockw);dur=[0-9]+(\.[0-9])?$", part));
        Assert.Matches(@"(^|, )sqln;dur=[1-9][0-9]*", timing); // the cookie check and the snapshot's counts are SQL

        var diag = await client.GetFromJsonAsync<JsonElement>(Diagnostics);
        var perf = diag.GetProperty("perf");
        var routes = perf.GetProperty("routes").EnumerateArray().ToList();
        var route = Assert.Single(routes, r => r.GetProperty("key").GetString() == "GET api/v1/operations/diagnostics");
        Assert.True(route.GetProperty("count").GetInt64() >= 1);
        Assert.True(route.GetProperty("sqlCount").GetInt64() >= 1);
        var statements = perf.GetProperty("statements").EnumerateArray().ToList();
        Assert.Contains(statements, s => s.GetProperty("key").GetString() == "GET api/v1/operations/diagnostics"
            && s.GetProperty("shape").GetString()!.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        Assert.True(perf.GetProperty("heap").GetProperty("heapSizeBytes").GetInt64() > 0);
        Assert.Contains(perf.GetProperty("transactions").EnumerateArray(), t => t.GetProperty("key").GetString()!.EndsWith(":commit", StringComparison.Ordinal));
    }

    [Fact]
    public void StatementShape_KeepsNoValue()
    {
        var recorder = new PerfRecorder(enabled: false);
        var shape = recorder.ShapeOf("SELECT \"c\".\"Id1\", @__p_0 FROM t0\n  WHERE name = 'A Secret ''Title''' AND n = 42 AND x > 3.5");
        Assert.Equal("SELECT \"c\".\"Id1\", @__p_0 FROM t0 WHERE name = '?' AND n = # AND x > #", shape);
    }
}
