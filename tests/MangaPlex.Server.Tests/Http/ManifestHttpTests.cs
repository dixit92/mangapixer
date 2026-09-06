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
/// HTTP integration tests for manifest, readiness, and prepare endpoints.
/// Uses synthetic CBZ archives and direct DB setup to test the API surface
/// without spawning real worker processes.
/// </summary>
public sealed class ManifestHttpTests : IDisposable
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private readonly string _libRoot;

    public ManifestHttpTests()
    {
        _factory = new MangaPlexWebApplicationFactory();
        _libRoot = Path.Combine(Path.GetTempPath(), "mangaplex-manifest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_libRoot);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_libRoot, true); } catch { }
    }

    [Fact]
    public async Task GetManifest_NotAnalyzed_Returns404()
    {
        // Setup: register library, create archive, scan, but don't analyze
        var (client, itemId) = await SetupLibraryAndScanAsync();

        var response = await client.GetAsync($"/api/v1/items/{itemId}/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("not_analyzed", error?.Error);
    }

    [Fact]
    public async Task GetManifest_Analyzed_ReturnsPages()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        // Directly persist analysis results (simulating worker completion)
        await PersistAnalysisResultAsync(itemId, pageCount: 3);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/manifest");
        response.EnsureSuccessStatusCode();

        var manifest = await response.Content.ReadFromJsonAsync<ManifestResponse>();
        Assert.NotNull(manifest);
        Assert.Equal(itemId, manifest!.ItemId);
        Assert.Equal(3, manifest.PageCount);
        Assert.Equal(3, manifest.Pages.Count);
        Assert.All(manifest.Pages, p => Assert.False(string.IsNullOrEmpty(p.EntryKey)));
        Assert.All(manifest.Pages, p => Assert.False(string.IsNullOrEmpty(p.MediaType)));
    }

    [Fact]
    public async Task GetReadiness_NotAnalyzed_ReturnsPending()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        var response = await client.GetAsync($"/api/v1/items/{itemId}/readiness");
        response.EnsureSuccessStatusCode();

        var readiness = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        Assert.NotNull(readiness);
        Assert.Equal(1, readiness!.State); // Pending
    }

    [Fact]
    public async Task GetReadiness_Analyzed_ReturnsReady()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        var response = await client.GetAsync($"/api/v1/items/{itemId}/readiness");
        response.EnsureSuccessStatusCode();

        var readiness = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        Assert.NotNull(readiness);
        Assert.Equal(0, readiness!.State); // Ready
    }

    [Fact]
    public async Task GetReadiness_FailedAnalysis_ReturnsFailed()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        // Directly mark as failed
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var node = await db.CatalogNodes.FirstAsync(n => n.PublicId == itemId);
            var archiveItem = await db.ArchiveItems.FirstOrDefaultAsync(a => a.NodeId == node.Id);
            if (archiveItem is null)
            {
                archiveItem = new ArchiveItemEntity
                {
                    NodeId = node.Id,
                    ContentVersion = 0,
                    ByteLength = 100,
                    ModificationTicks = 0,
                };
                db.ArchiveItems.Add(archiveItem);
            }
            archiveItem.AnalysisState = 2; // failed
            archiveItem.AnalysisError = "enumeration_error";
            archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/v1/items/{itemId}/readiness");
        response.EnsureSuccessStatusCode();

        var readiness = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        Assert.NotNull(readiness);
        Assert.Equal(2, readiness!.State); // Failed
        Assert.Equal("enumeration_error", readiness.Error);
    }

    [Fact]
    public async Task Prepare_NotYetAnalyzed_Returns202()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();

        var response = await client.PostAsync($"/api/v1/items/{itemId}/prepare", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Prepare_AlreadyReady_Returns200()
    {
        var (client, itemId) = await SetupLibraryAndScanAsync();
        await PersistAnalysisResultAsync(itemId, pageCount: 2);

        var response = await client.PostAsync($"/api/v1/items/{itemId}/prepare", null);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task GetManifest_InvalidId_Returns404()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync("/api/v1/items/invalidid/manifest");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetManifest_FolderItem_Returns400()
    {
        // Setup library with a folder, scan, then try to get manifest for the folder
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create library with a subfolder
        Directory.CreateDirectory(Path.Combine(_libRoot, "Series A"));
        ZipFixtureGenerator.CreateZip(Path.Combine(_libRoot, "Series A"), "v01.cbz", "page001.png", "page002.png");

        // Register and scan
        var regResponse = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
        {
            DisplayName = "Manifest Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var library = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        scanResponse.EnsureSuccessStatusCode();

        // Wait for scan to complete
        await Task.Delay(2000);

        // Find the folder node
        string folderId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var folder = await db.CatalogNodes.FirstAsync(n => n.Kind == 0 && n.PublicId != null);
            folderId = folder.PublicId!;
        }

        var response = await client.GetAsync($"/api/v1/items/{folderId}/manifest");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
            DisplayName = "Manifest Test",
            RootPath = _libRoot,
        });
        regResponse.EnsureSuccessStatusCode();
        var library = await regResponse.Content.ReadFromJsonAsync<LibraryDto>();

        // Trigger scan
        var scanResponse = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
        Assert.Equal(HttpStatusCode.Accepted, scanResponse.StatusCode);

        // Wait for scan to complete (background task)
        await Task.Delay(5000);

        // Get the archive item ID
        string itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var scanRuns = await db.ScanRuns.ToListAsync();
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

        // Create or update archive item
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

        archiveItem.AnalysisState = 0; // ready
        archiveItem.PageCount = pageCount;
        archiveItem.AnalysisError = null;
        archiveItem.LastAnalyzedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Remove old page entries if any
        var oldPages = await db.PageEntries.Where(p => p.ItemId == node.Id).ToListAsync();
        if (oldPages.Count > 0)
        {
            db.PageEntries.RemoveRange(oldPages);
            await db.SaveChangesAsync();
        }

        // Add page entries
        for (int i = 0; i < pageCount; i++)
        {
            db.PageEntries.Add(new PageEntryEntity
            {
                ItemId = node.Id,
                ContentVersion = archiveItem.ContentVersion,
                Ordinal = i,
                EntryKey = com.lifepixer.mangaplex.Core.Catalog.OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
                SourceEntryLocator = $"page{i + 1:D3}.png",
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

    // --- Local DTOs (decoupled from internal types) ---

    private sealed class ManifestResponse
    {
        public string ItemId { get; set; } = string.Empty;
        public long ContentVersion { get; set; }
        public int ManifestVersion { get; set; }
        public int ArchiveFormat { get; set; }
        public int PageCount { get; set; }
        public List<ManifestPageResponse> Pages { get; set; } = [];
        public bool IsSolid { get; set; }
        public bool HasAnimatedPages { get; set; }
    }

    private sealed class ManifestPageResponse
    {
        public string EntryKey { get; set; } = string.Empty;
        public int PageIndex { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int AnimationState { get; set; }
        public long ByteSize { get; set; }
    }

    private sealed class ReadinessResponse
    {
        public string ItemId { get; set; } = string.Empty;
        public int State { get; set; }
        public long ContentVersion { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public bool IsAnalyzing { get; set; }
    }
}
