namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Admin Performance settings through HTTP (1.42.0, wins 3 + 5 + 6): defaults with their sources, a change applies LIVE to
/// the worker pool and the page cache, reset returns to the default, bad values are refused, a value pinned by
/// configuration is shown as such and cannot be changed, and the stored settings apply at startup.
/// </summary>
public sealed class PerformanceSettingsHttpTests : IDisposable
{
    private const string Route = "/api/v1/admin/performance";
    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_ShowsTheDefaults_WithTheirSources()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var s = await client.GetFromJsonAsync<JsonElement>(Route);

        var processors = s.GetProperty("processorCount").GetInt32();
        Assert.True(processors >= 1);
        Assert.Equal(Math.Clamp(processors / 4, 2, 4), s.GetProperty("mediaWorkersDefault").GetInt32());
        Assert.Equal(s.GetProperty("mediaWorkersDefault").GetInt32(), s.GetProperty("mediaWorkers").GetInt32());
        Assert.Equal("default", s.GetProperty("mediaWorkersSource").GetString());
        Assert.Equal(1, s.GetProperty("mediaWorkersMin").GetInt32());
        Assert.Equal(8, s.GetProperty("mediaWorkersMax").GetInt32());
        Assert.Equal(1L << 30, s.GetProperty("pageCacheBytes").GetInt64());
        Assert.Equal("default", s.GetProperty("pageCacheSource").GetString());
        Assert.Equal("slow", s.GetProperty("backgroundWhileReading").GetString());
        Assert.Equal("default", s.GetProperty("backgroundWhileReadingSource").GetString());
        // ... and the running pool / cache carry exactly that.
        Assert.Equal(s.GetProperty("mediaWorkers").GetInt32(), _factory.Services.GetRequiredService<MediaWorkerPool>().MaxConcurrentJobs);
        Assert.Equal(BackgroundWhileReading.Slow, _factory.Services.GetRequiredService<MediaWorkerPool>().BackgroundWhileReading);
        Assert.Equal(1L << 30, _factory.Services.GetRequiredService<CacheService>().BudgetBytes);
    }

    [Fact]
    public async Task Put_AppliesLive_AndResetReturnsToTheDefault()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var pool = _factory.Services.GetRequiredService<MediaWorkerPool>();
        var cache = _factory.Services.GetRequiredService<CacheService>();

        var put = await client.PutAsJsonAsync(Route, new { mediaWorkers = 3, pageCacheBytes = 512L << 20, backgroundWhileReading = "pause" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var s = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((3, "settings"), (s.GetProperty("mediaWorkers").GetInt32(), s.GetProperty("mediaWorkersSource").GetString()));
        Assert.Equal(512L << 20, s.GetProperty("pageCacheBytes").GetInt64());
        Assert.Equal("pause", s.GetProperty("backgroundWhileReading").GetString());
        Assert.Equal(3, pool.MaxConcurrentJobs);
        Assert.Equal(BackgroundWhileReading.Pause, pool.BackgroundWhileReading);
        Assert.Equal(512L << 20, cache.BudgetBytes);

        // Only the named field changes; a partial update leaves the rest.
        var partial = await (await client.PutAsJsonAsync(Route, new { backgroundWhileReading = "normal" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, partial.GetProperty("mediaWorkers").GetInt32());
        Assert.Equal(BackgroundWhileReading.Normal, pool.BackgroundWhileReading);

        var reset = await client.PutAsJsonAsync(Route, new { reset = new[] { "mediaWorkers", "pageCacheBytes", "backgroundWhileReading" } });
        var r = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("default", r.GetProperty("mediaWorkersSource").GetString());
        Assert.Equal(r.GetProperty("mediaWorkersDefault").GetInt32(), pool.MaxConcurrentJobs);
        Assert.Equal(1L << 30, cache.BudgetBytes);
        Assert.Equal(BackgroundWhileReading.Slow, pool.BackgroundWhileReading);

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().AppSettings.AsNoTracking().SingleAsync();
        Assert.Null(row.PerformanceMediaWorkers);
        Assert.Null(row.PerformancePageCacheBytes);
        Assert.Null(row.PerformanceBackgroundWhileReading);
    }

    [Theory]
    [InlineData("{\"mediaWorkers\":0}")]
    [InlineData("{\"mediaWorkers\":9}")]
    [InlineData("{\"pageCacheBytes\":1048576}")]
    [InlineData("{\"pageCacheBytes\":137438953472}")]
    [InlineData("{\"backgroundWhileReading\":\"sometimes\"}")]
    [InlineData("{\"reset\":[\"everything\"]}")]
    public async Task Put_RefusesValuesOutOfRange(string body)
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var response = await client.PutAsync(Route, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConfiguredValue_IsShownAsPinned_AndCannotBeChanged_WhileTheOthersCan()
    {
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            [PerformanceConfiguration.WorkersKey] = "5",
        });
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = await client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal((5, "configuration"), (s.GetProperty("mediaWorkers").GetInt32(), s.GetProperty("mediaWorkersSource").GetString()));
        Assert.Equal(5, factory.Services.GetRequiredService<MediaWorkerPool>().MaxConcurrentJobs);

        var refused = await client.PutAsJsonAsync(Route, new { mediaWorkers = 2 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("managed_by_config", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(Route, new { reset = new[] { "mediaWorkers" } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Route, new { backgroundWhileReading = "pause" })).StatusCode);
    }

    [Fact]
    public async Task StoredSettings_ApplyWhenTheServerLoadsThem()
    {
        await _factory.LoginAsAdminWithChangedPasswordAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Id == AppSettingsEntity.SingletonId);
            if (row is null)
            {
                row = new AppSettingsEntity();
                db.AppSettings.Add(row);
            }
            row.PerformanceMediaWorkers = 7;
            row.PerformanceBackgroundWhileReading = (int)BackgroundWhileReading.Normal;
            await db.SaveChangesAsync();
        }

        // What the startup hosted service runs.
        await _factory.Services.GetRequiredService<PerformanceSettingsResolver>().ReloadAsync();

        Assert.Equal(7, _factory.Services.GetRequiredService<MediaWorkerPool>().MaxConcurrentJobs);
        Assert.Equal(BackgroundWhileReading.Normal, _factory.Services.GetRequiredService<MediaWorkerPool>().BackgroundWhileReading);
    }

    [Fact]
    public async Task Endpoint_IsAdminOnly()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
    }
}
