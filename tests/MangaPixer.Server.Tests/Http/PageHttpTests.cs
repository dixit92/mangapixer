namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for page and cover delivery endpoints.
/// Uses synthetic CBZ archives with real PNG pages.
/// </summary>
[Collection("HttpSerial")]
public sealed class PageHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly string _libRoot;

    public PageHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-page-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    [Fact]
    public async Task GetPage_NotAnalyzed_Returns404()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        // Use a deterministic entry key that won't exist for unanalyzed items
        var entryKey = new com.lifepixer.mangapixer.Core.Catalog.PageEntryKey(0).ToOpaque();
        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPage_Analyzed_ReturnsImage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 3);
        // The HTTP factory runs no worker, so seed the cache: this asserts the
        // controller's cache-serve + headers path. Real worker extraction/encoding
        // is covered by the Process-category WorkerProcessTests.
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task GetPage_NoMaxDim_ServesFullVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        response.EnsureSuccessStatusCode();

        Assert.Equal("webp", VariantHeader(response));
    }

    [Fact]
    public async Task GetPage_MaxDimZero_ServesFullVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=0");
        response.EnsureSuccessStatusCode();

        Assert.Equal("webp", VariantHeader(response));
    }

    [Fact]
    public async Task GetPage_MaxDim_SnapsUpToBucketAndServesSizedVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);
        // Only the bucket entry is seeded: if the controller picked any other
        // variant it would miss the cache and fail (no worker in this factory).
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp@1440");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=1300");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("webp@1440", VariantHeader(response));
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task GetPage_MaxDim_SizedVariantGetsItsOwnETag()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp@1440");

        var full = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}");
        var sized = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=1440");
        full.EnsureSuccessStatusCode();
        sized.EnsureSuccessStatusCode();

        Assert.NotNull(full.Headers.ETag);
        Assert.NotNull(sized.Headers.ETag);
        Assert.NotEqual(full.Headers.ETag!.Tag, sized.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task GetPage_MaxDimAboveLadder_ServesFullVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 9000, pageHeight: 9000);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=99999");
        response.EnsureSuccessStatusCode();

        Assert.Equal("webp", VariantHeader(response));
    }

    [Fact]
    public async Task GetPage_PageSmallerThanBucket_ServesFullVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        // 900x600 already fits inside the 1440 bucket: sizing it would be an
        // upscale and a pointless second cache entry.
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 900, pageHeight: 600);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "webp");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=1300");
        response.EnsureSuccessStatusCode();

        Assert.Equal("webp", VariantHeader(response));
    }

    [Fact]
    public async Task GetPage_NonIntegerMaxDim_Returns400()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}?maxDim=abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_request", error?.Error);
    }

    [Fact]
    public async Task GetPageThumbnail_ReportsThumbnailVariant()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 1, pageWidth: 3000, pageHeight: 2000);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "thumbnail");

        // maxDim is a page-endpoint concern; the thumbnail endpoint ignores it.
        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}/thumbnail");
        response.EnsureSuccessStatusCode();

        Assert.Equal("thumbnail", VariantHeader(response));
    }

    [Fact]
    public async Task GetPage_InvalidIndex_Returns404()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        // Use a non-existent entry key
        var fakeKey = new com.lifepixer.mangapixer.Core.Catalog.PageEntryKey(99).ToOpaque();
        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{fakeKey}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCover_Analyzed_ReturnsFirstPage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 3);

        // Cover is now served from the durable thumbnail store (1.2.0),
        // not the evictable page cache.
        await SeedDurableThumbnailAsync(itemId);
        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task GetPageThumbnail_ReturnsImage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        var entryKey = await PersistAnalysisResultAsync(itemId, pageCount: 2);
        await SeedCacheAsync(itemId, ordinal: 0, variant: "thumbnail");

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/{entryKey}/thumbnail");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetPage_InvalidItemId_Returns404()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var entryKey = new com.lifepixer.mangapixer.Core.Catalog.PageEntryKey(0).ToOpaque();
        var response = await client.GetAsync($"/api/v1/items/invalidid/pages/{entryKey}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPage_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/items/someid/pages/0");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ---

    /// <summary>
    /// Reads the X-MangaPixer-Variant header the page endpoints stamp with the
    /// variant actually served ("webp", "webp@1440", "thumbnail").
    /// </summary>
    private static string? VariantHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-MangaPixer-Variant", out var values)
            ? values.FirstOrDefault()
            : null;

    private async Task<(HttpClient Client, string ItemId)> SetupLibraryAndScanAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create a real CBZ archive with PNG pages
        ZipFixtureGenerator.CreateZip(_libRoot, "test.cbz", "page001.png", "page002.png", "page003.png");

        // Register library
        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Page Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var library = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        // Trigger scan
        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

        // Wait for scan to complete
        await Task.Delay(5000);

        // Get the archive item ID
        string itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var nodes = await db.CatalogNodes.Where(n => n.Kind == 1 && n.PublicId != null).ToListAsync();
            Assert.NotEmpty(nodes);
            itemId = nodes[0].PublicId!;
        }

        return (client, itemId);
    }

    /// <summary>
    /// Publishes a page image into the cache under the key the controller will look
    /// up, so cache-serve behaviour can be tested without a running worker (the HTTP
    /// factory removes the worker service). Bytes are placeholder; the test asserts
    /// the served media type + headers, not pixel content.
    /// </summary>
    private async Task SeedCacheAsync(string itemPublicId, int ordinal, string variant)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<CacheService>();
        cache.Initialize();

        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemPublicId);
        var archiveItem = await db.ArchiveItems.FirstAsync(a => a.NodeId == node.Id);
        var entryKey = new PageEntryKey(ordinal).ToOpaque();
        var cacheKey = CacheService.BuildCacheKey(node.Id, archiveItem.ContentVersion, entryKey, variant);

        var tmp = Path.Combine(_libRoot, "seed-" + Guid.NewGuid().ToString("N")[..8] + ".webp");
        await File.WriteAllBytesAsync(tmp, SyntheticImages.MinimalPng);
        await cache.PublishAsync(cacheKey, tmp, "image/webp");
        try { File.Delete(tmp); } catch { /* best effort */ }
    }

    /// <summary>
    /// Publishes a durable thumbnail into the ThumbnailStore so the cover
    /// endpoint can serve it without a running worker (1.2.0).
    /// </summary>
    private async Task SeedDurableThumbnailAsync(string itemPublicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        store.Initialize();

        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemPublicId);
        var archiveItem = await db.ArchiveItems.FirstAsync(a => a.NodeId == node.Id);

        var tmp = Path.Combine(_libRoot, "thumb-" + Guid.NewGuid().ToString("N")[..8] + ".webp");
        await File.WriteAllBytesAsync(tmp, SyntheticImages.MinimalPng);
        await store.PublishAsync(node.Id, archiveItem.ContentVersion, tmp);

        archiveItem.ThumbnailState = 1;
        archiveItem.ThumbnailContentVersion = archiveItem.ContentVersion;
        await db.SaveChangesAsync();

        try { File.Delete(tmp); } catch { /* best effort */ }
    }

    /// <summary>
    /// Seeds analysis state and page entries. <paramref name="pageWidth"/> /
    /// <paramref name="pageHeight"/> default to the original 1x1 placeholder so
    /// pre-existing tests are unaffected; the page-variant tests pass realistic
    /// intrinsic sizes because the controller consults them before sizing a page.
    /// </summary>
    private async Task<string> PersistAnalysisResultAsync(string itemPublicId, int pageCount, int pageWidth = 1, int pageHeight = 1)
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
                ArchiveFormat = 0, // ZIP
                ByteLength = 1024,
                ModificationTicks = DateTimeOffset.UtcNow.Ticks,
            };
            db.ArchiveItems.Add(archiveItem);
        }

        archiveItem.AnalysisState = 0;
        archiveItem.PageCount = pageCount;
        archiveItem.AnalysisError = null;
        archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Remove old page entries
        var oldPages = await db.PageEntries.Where(p => p.ItemId == node.Id).ToListAsync();
        if (oldPages.Count > 0)
        {
            db.PageEntries.RemoveRange(oldPages);
            await db.SaveChangesAsync();
        }

        // Add page entries with deterministic entry keys (audit defect D4)
        var entryNames = new[] { "page001.png", "page002.png", "page003.png" };
        for (int i = 0; i < pageCount; i++)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = archiveItem.ContentVersion,
                Ordinal = i,
                EntryKey = new com.lifepixer.mangapixer.Core.Catalog.PageEntryKey(i).ToOpaque(),
                SourceEntryLocator = entryNames[i],
                MediaType = "image/png",
                Width = pageWidth,
                Height = pageHeight,
                AnimationState = 0,
                PageState = 0,
                ByteSize = 100,
            });
        }
        await db.SaveChangesAsync();

        // Return the first page's entry key for use in URLs
        return new com.lifepixer.mangapixer.Core.Catalog.PageEntryKey(0).ToOpaque();
    }
}
