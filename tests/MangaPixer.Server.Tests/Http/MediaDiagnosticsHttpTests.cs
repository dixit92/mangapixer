namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// Media metrics through the public surface (1.42.0 profiling): a page response carries a <c>Server-Timing</c> header,
/// and the admin-only diagnostics endpoint returns the pool, page-cache and timing snapshot the page request fed.
/// </summary>
public sealed class MediaDiagnosticsHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly string _libRoot;

    public MediaDiagnosticsHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-mediadiag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    [Fact]
    public async Task CachedPage_CarriesServerTiming_AndFeedsTheDiagnosticsSnapshot()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId);
        await SeedCacheAsync(itemId, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        response.EnsureSuccessStatusCode();
        var timing = string.Join(", ", response.Headers.GetValues("Server-Timing"));
        Assert.Contains("cache;desc=hit", timing);
        Assert.Matches(@"(^|, )db;dur=[0-9.]+", timing);
        Assert.Matches(@"(^|, )total;dur=[0-9.]+", timing);
        // Timings only: nothing that names the item, the entry or a path.
        Assert.DoesNotContain(itemId, timing);
        Assert.DoesNotContain(entryKey, timing);
        Assert.DoesNotContain(_libRoot, timing);

        var diag = await client.GetFromJsonAsync<JsonElement>("/api/v1/operations/diagnostics");
        var media = diag.GetProperty("media");
        Assert.True(media.GetProperty("pool").GetProperty("maxConcurrentJobs").GetInt32() >= 1);
        Assert.True(media.GetProperty("cache").GetProperty("activeEntries").GetInt32() >= 1);
        Assert.True(CountOf(media, "cache_lookup", "hit") >= 1);
        Assert.True(CountOf(media, "page_status", "200") >= 1);
        var total = media.GetProperty("timings").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "page_total" && t.GetProperty("kind").GetString() == "page:hit");
        Assert.Equal(1, total.GetProperty("count").GetInt64());
        Assert.Equal(media.GetProperty("timingBucketBoundsMs").GetArrayLength() + 1, total.GetProperty("bucketCounts").GetArrayLength());
        Assert.DoesNotContain(_libRoot, diag.GetRawText());
    }

    [Fact]
    public async Task PrefetchHeader_MarksThePageAsAPrefetch_WithoutChangingTheResponse()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId);
        await SeedCacheAsync(itemId, variant: "webp");

        var plain = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/items/{itemId}/pages/{entryKey}");
        request.Headers.Add("X-MangaPixer-Prefetch", "1");
        var prefetch = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, prefetch.StatusCode);
        Assert.Equal(await plain.Content.ReadAsByteArrayAsync(), await prefetch.Content.ReadAsByteArrayAsync());
        Assert.Equal(plain.Headers.ETag, prefetch.Headers.ETag);
        Assert.False(prefetch.Headers.Contains("Vary") && prefetch.Headers.GetValues("Vary").Any(v => v.Contains("Prefetch", StringComparison.OrdinalIgnoreCase)));

        var media = (await client.GetFromJsonAsync<JsonElement>("/api/v1/operations/diagnostics")).GetProperty("media");
        var kinds = media.GetProperty("timings").EnumerateArray()
            .Where(t => t.GetProperty("name").GetString() == "page_total")
            .Select(t => t.GetProperty("kind").GetString())
            .ToList();
        Assert.Contains("page:hit", kinds);
        Assert.Contains("page_prefetch:hit", kinds);
    }

    [Fact]
    public async Task MissingPage_IsCountedByStatus_AndStillTimed()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId);

        var missing = new PageEntryKey(7).ToOpaque();
        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{missing}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.Contains("Server-Timing"));

        var media = (await client.GetFromJsonAsync<JsonElement>("/api/v1/operations/diagnostics")).GetProperty("media");
        Assert.True(CountOf(media, "page_status", "404") >= 1);
    }

    [Fact]
    public async Task Diagnostics_StaysAdminOnly()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/operations/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Metrics_AreOneSingleton_SharedByThePoolAndTheCache_WithTheSummaryRegistered()
    {
        var metrics = _factory.Services.GetRequiredService<MediaMetrics>();
        Assert.Same(metrics, _factory.Services.GetRequiredService<MediaWorkerPool>().Metrics);
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is MediaMetricsSummaryService);
    }

    private static long CountOf(JsonElement media, string name, string kind) =>
        media.GetProperty("counts").EnumerateArray()
            .Where(c => c.GetProperty("name").GetString() == name && c.GetProperty("kind").GetString() == kind)
            .Select(c => c.GetProperty("value").GetInt64())
            .DefaultIfEmpty(0)
            .Sum();

    private async Task<(HttpClient Client, string ItemId)> SetupLibraryAndScanAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        ZipFixtureGenerator.CreateZip(_libRoot, "test.cbz", "page001.png", "page002.png", "page003.png");

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Media Diagnostics Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var library = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();
        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

        // Wait for the scan to finish (not only for the node to appear) so the seeding below never races it.
        string? itemId = null;
        for (var i = 0; i < 200; i++)
        {
            var libraries = await client.GetFromJsonAsync<JsonElement>("/api/v1/libraries");
            var scanning = libraries.EnumerateArray().Any(l => l.GetProperty("isScanning").GetBoolean());
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            itemId = await db.CatalogNodes.Where(n => n.Kind == 1 && n.PublicId != null).Select(n => n.PublicId).FirstOrDefaultAsync();
            if (itemId is not null && !scanning)
                break;
            await Task.Delay(100);
        }
        Assert.NotNull(itemId);
        return (client, itemId!);
    }

    private async Task<string> PersistAnalysisResultAsync(string itemPublicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemPublicId);
        var archiveItem = await db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id);
        if (archiveItem is null)
        {
            archiveItem = new ArchiveItemEntity
            {
                NodeId = node.Id,
                ContentVersion = 1,
                ArchiveFormat = 0,
                ByteLength = 1024,
                ModificationTicks = DateTimeOffset.UtcNow.Ticks,
            };
            db.ArchiveItems.Add(archiveItem);
        }
        archiveItem.AnalysisState = 0;
        archiveItem.PageCount = 1;
        archiveItem.AnalysisError = null;
        archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        db.PageEntries.RemoveRange(await db.PageEntries.Where(p => p.ItemId == node.Id).ToListAsync());
        await db.SaveChangesAsync();
        db.PageEntries.Add(new PageEntryEntity
        {
            ItemId = node.Id,
            ContentVersion = archiveItem.ContentVersion,
            Ordinal = 0,
            EntryKey = new PageEntryKey(0).ToOpaque(),
            SourceEntryLocator = "page001.png",
            MediaType = "image/png",
            Width = 1,
            Height = 1,
            AnimationState = 0,
            PageState = 0,
            ByteSize = 100,
        });
        await db.SaveChangesAsync();
        return new PageEntryKey(0).ToOpaque();
    }

    private async Task SeedCacheAsync(string itemPublicId, string variant)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<CacheService>();
        cache.Initialize();
        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemPublicId);
        var archiveItem = await db.ArchiveItems.FirstAsync(a => a.NodeId == node.Id);
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, new PageEntryKey(0).ToOpaque(), variant);
        var tmp = Path.Combine(_libRoot, "seed-" + Guid.NewGuid().ToString("N")[..8] + ".webp");
        await File.WriteAllBytesAsync(tmp, SyntheticImages.MinimalPng);
        await cache.PublishAsync(cacheKey, tmp, "image/webp");
        try { File.Delete(tmp); } catch { /* best effort */ }
    }
}
