namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// C01 HTTP tests: browse with public IDs, breadcrumbs, neighbors,
/// library list with real counts (D5/D29/D30).
/// </summary>
[Collection("HttpSerial")]
public sealed class CatalogHttpTests : IClassFixture<MangaPlexWebApplicationFactory>
{
    private readonly MangaPlexWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    public CatalogHttpTests(MangaPlexWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpClient> GetAuthenticatedClientAsync()
    {
        if (_authenticatedClient is not null)
            return _authenticatedClient;
        _authenticatedClient = await _factory.LoginAsAdminWithChangedPasswordAsync();
        return _authenticatedClient;
    }

    [Fact]
    public async Task GetLibraries_ReturnsRealItemCountAndScanningState()
    {
        // Seed a library with catalog nodes directly in the DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var library = new LibraryEntity
            {
                PublicId = "testlib1",
                DisplayName = "Test Library",
                RootPath = "/tmp/test",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // Add 3 archive nodes and 1 folder
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "node1",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "Archive1.cbz",
                RelativePath = "Archive1.cbz",
                PathKey = "Archive1.cbz",
                SortKey = "1Archive1.cbz",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "node2",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "Archive2.cbz",
                RelativePath = "Archive2.cbz",
                PathKey = "Archive2.cbz",
                SortKey = "1Archive2.cbz",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "node3",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "Archive3.cbz",
                RelativePath = "Archive3.cbz",
                PathKey = "Archive3.cbz",
                SortKey = "1Archive3.cbz",
                Availability = 5, // tombstoned — should NOT count
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "folder1",
                LibraryId = library.Id,
                Kind = 0,
                DisplayName = "Folder",
                RelativePath = "Folder",
                PathKey = "Folder",
                SortKey = "0Folder",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/libraries");
        response.EnsureSuccessStatusCode();

        var libraries = await response.Content.ReadFromJsonAsync<List<LibraryDto>>();
        Assert.NotNull(libraries);
        var lib = libraries!.First(l => l.Name == "Test Library");
        Assert.Equal(2, lib.ItemCount); // 3 archives minus 1 tombstoned
        Assert.False(lib.IsScanning);
    }

    /// <summary>
    /// 1.5.0: an explicit `direction` query param overrides the user's stored
    /// library-preferences direction, and an omitted one falls back to it.
    /// </summary>
    [Fact]
    public async Task Browse_DirectionQueryParam_OverridesStoredPreference()
    {
        string libPublicId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var library = new LibraryEntity
            {
                PublicId = "directiontestlib",
                DisplayName = "Direction Test Library",
                RootPath = "/tmp/directiontest",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            libPublicId = library.PublicId;

            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "dirnodeA",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "A.cbz",
                RelativePath = "A.cbz",
                PathKey = "A.cbz",
                SortKey = "1A",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "dirnodeB",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = "B.cbz",
                RelativePath = "B.cbz",
                PathKey = "B.cbz",
                SortKey = "1B",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = await GetAuthenticatedClientAsync();

        // The server serializes enums (e.g. CatalogNodeDto.Kind) as strings — match
        // that here, same as JumpIndexHttpTests, since ReadFromJsonAsync's defaults
        // expect numeric enums otherwise.
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        // No stored preference, no query param → Name defaults ascending.
        var defaultResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name");
        defaultResponse.EnsureSuccessStatusCode();
        var defaultPage = await defaultResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        Assert.Equal("A.cbz", defaultPage!.Items[0].DisplayName);
        Assert.Equal("B.cbz", defaultPage.Items[1].DisplayName);

        // Explicit direction=desc query param → reversed, regardless of stored prefs.
        var descResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name&direction=desc");
        descResponse.EnsureSuccessStatusCode();
        var descPage = await descResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        Assert.Equal("B.cbz", descPage!.Items[0].DisplayName);
        Assert.Equal("A.cbz", descPage.Items[1].DisplayName);

        // Persist direction=desc as the stored preference.
        var putResponse = await client.PutAsJsonAsync("/api/v1/reading/library-preferences",
            new { viewMode = "grid", density = "comfortable", sort = "name", direction = "desc" });
        putResponse.EnsureSuccessStatusCode();

        // Omitted direction query param → falls back to the stored "desc" preference.
        var storedResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name");
        storedResponse.EnsureSuccessStatusCode();
        var storedPage = await storedResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        Assert.Equal("B.cbz", storedPage!.Items[0].DisplayName);
        Assert.Equal("A.cbz", storedPage.Items[1].DisplayName);

        // Explicit direction=asc query param still overrides the stored "desc".
        var overrideResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name&direction=asc");
        overrideResponse.EnsureSuccessStatusCode();
        var overridePage = await overrideResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        Assert.Equal("A.cbz", overridePage!.Items[0].DisplayName);
        Assert.Equal("B.cbz", overridePage.Items[1].DisplayName);
    }

    [Fact]
    public async Task Browse_WithUnknownPublicLibraryId_Returns404()
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/libraries/nonexistentpub/browse");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Breadcrumbs_WithUnknownPublicNodeId_Returns404()
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/nodes/nonexistentpub/breadcrumbs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Neighbors_WithUnknownPublicNodeId_Returns404()
    {
        var client = await GetAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/nodes/nonexistentpub/neighbors");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
