namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.TestSupport.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for durable thumbnail serving and the admin
/// regenerate endpoint (1.2.0). Covers:
/// - Cover endpoint serves from the durable ThumbnailStore
/// - Cover endpoint returns typed pending (202) when no thumbnail exists
/// - Cover endpoint returns pending when item is not yet analyzed
/// - Admin regenerate endpoint enqueues backfill and reports a count
/// - Thumbnails persist across a simulated restart (cache clear)
/// </summary>
[Collection("HttpSerial")]
public sealed class ThumbnailHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly string _libRoot;

    public ThumbnailHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangapixer-thumb-http-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    [Fact]
    public async Task GetCover_NotAnalyzed_ReturnsPending()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        // Item exists but has not been analyzed (no worker in the HTTP factory)
        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");

        // Pending — not a 404 broken image
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("pending", error!.Error);
    }

    [Fact]
    public async Task GetCover_AnalyzedWithThumbnail_ReturnsImage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 3);
        await SeedDurableThumbnailAsync(itemId);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);

        // Long-lived immutable cache headers keyed by content version
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task GetCover_AnalyzedWithoutThumbnail_ReturnsPending()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        // Analyzed but no thumbnail in the durable store yet
        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("pending", error!.Error);
    }

    [Fact]
    public async Task GetCover_ThumbnailPersistsAcrossCacheClear()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);
        await SeedDurableThumbnailAsync(itemId);

        // Clear the page cache — thumbnails live in the durable store, not the cache
        using (var scope = _factory.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<CacheService>();
            // Evict everything from the cache
            cache.HandleDiskFull();
        }

        // The cover should still be served from the durable store
        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_RegenerateThumbnails_ReturnsQueuedCount()
    {
        var (client, itemId, libraryId) = await SetupLibraryAndScanWithLibraryIdAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        // No thumbnails exist yet — regenerate should queue at least 1
        var response = await client.PostAsync($"/api/v1/admin/libraries/{libraryId}/thumbnails/regenerate", null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ThumbnailRegenerateResponse>();
        Assert.NotNull(result);
        Assert.True(result!.QueuedCount >= 1);
    }

    [Fact]
    public async Task Admin_RegenerateThumbnails_AllCurrent_ReturnsZero()
    {
        var (client, itemId, libraryId) = await SetupLibraryAndScanWithLibraryIdAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);
        await SeedDurableThumbnailAsync(itemId);

        // Mark the thumbnail as current in the DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemId);
            var item = await db.ArchiveItems.FirstAsync(a => a.NodeId == node.Id);
            item.ThumbnailState = 1;
            item.ThumbnailContentVersion = item.ContentVersion;
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsync($"/api/v1/admin/libraries/{libraryId}/thumbnails/regenerate", null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ThumbnailRegenerateResponse>();
        Assert.Equal(0, result!.QueuedCount);
    }

    [Fact]
    public async Task Admin_RegenerateThumbnails_UnknownLibrary_Returns404()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.PostAsync("/api/v1/admin/libraries/nonexistent/thumbnails/regenerate", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Helpers ---

    private async Task<(HttpClient Client, string ItemId)> SetupLibraryAndScanAsync()
    {
        var (client, itemId, _) = await SetupLibraryAndScanWithLibraryIdAsync();
        return (client, itemId);
    }

    private async Task<(HttpClient Client, string ItemId, string LibraryId)> SetupLibraryAndScanWithLibraryIdAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        ZipFixtureGenerator.CreateZip(_libRoot, "test.cbz", "page001.png", "page002.png", "page003.png");

        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Thumb Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var library = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

        await Task.Delay(5000);

        string itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var nodes = await db.CatalogNodes.Where(n => n.Kind == 1 && n.PublicId != null).ToListAsync();
            Assert.NotEmpty(nodes);
            itemId = nodes[0].PublicId!;
        }

        return (client, itemId, library.Id);
    }

    private async Task PersistAnalysisResultAsync(string itemPublicId, int pageCount)
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
        archiveItem.PageCount = pageCount;
        archiveItem.AnalysisError = null;
        archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var oldPages = await db.PageEntries.Where(p => p.ItemId == node.Id).ToListAsync();
        if (oldPages.Count > 0)
        {
            db.PageEntries.RemoveRange(oldPages);
            await db.SaveChangesAsync();
        }

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
                Width = 1,
                Height = 1,
                AnimationState = 0,
                PageState = 0,
                ByteSize = 100,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Publishes a durable thumbnail into the ThumbnailStore so the cover
    /// endpoint can serve it without a running worker.
    /// </summary>
    private async Task SeedDurableThumbnailAsync(string itemPublicId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<ThumbnailStore>();
        store.Initialize();

        var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemPublicId);
        var archiveItem = await db.ArchiveItems.FirstAsync(a => a.NodeId == node.Id);

        var tmpFile = Path.Combine(_libRoot, "thumb-" + Guid.NewGuid().ToString("N")[..8] + ".webp");
        await File.WriteAllBytesAsync(tmpFile, SyntheticImages.MinimalPng);
        await store.PublishAsync(node.Id, archiveItem.ContentVersion, tmpFile);

        archiveItem.ThumbnailState = 1;
        archiveItem.ThumbnailContentVersion = archiveItem.ContentVersion;
        await db.SaveChangesAsync();

        try { File.Delete(tmpFile); } catch { /* best effort */ }
    }
}
