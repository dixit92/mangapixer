namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.TestSupport.Fixtures;
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
    private readonly MangaPlexWebApplicationFactory _factory;
    private readonly string _libRoot;

    public PageHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangaplex-page-" + Guid.NewGuid().ToString("N")[..8]);
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

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/0");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPage_Analyzed_ReturnsImage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 3);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/0");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task GetPage_InvalidIndex_Returns404()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/99");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCover_Analyzed_ReturnsFirstPage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 3);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/cover");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public async Task GetPageThumbnail_ReturnsImage()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/pages/0/thumbnail");
        response.EnsureSuccessStatusCode();

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task GetPage_InvalidItemId_Returns404()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/items/invalidid/pages/0");
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
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var nodes = await db.CatalogNodes.Where(n => n.Kind == 1 && n.PublicId != null).ToListAsync();
            Assert.NotEmpty(nodes);
            itemId = nodes[0].PublicId!;
        }

        return (client, itemId);
    }

    private async Task PersistAnalysisResultAsync(string itemPublicId, int pageCount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();

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

        // Add page entries with real entry keys matching the archive
        var entryNames = new[] { "page001.png", "page002.png", "page003.png" };
        for (int i = 0; i < pageCount; i++)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = archiveItem.ContentVersion,
                Ordinal = i,
                EntryKey = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
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
}
