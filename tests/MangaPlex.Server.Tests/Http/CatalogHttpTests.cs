namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
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
    /// <summary>
    /// 1.6.0 folder read rollup through the public surface: the browse response
    /// carries a derived per-folder readRollup (string enum on the wire) that
    /// tracks the read-mark endpoints - null for an empty folder, Unread before
    /// any read-mark, Reading after one archive is marked, Read after the bulk
    /// folder mark, and back to Unread after the bulk clear.
    /// </summary>
    [Fact]
    public async Task Browse_FolderReadRollup_TracksReadMarksThroughApi()
    {
        string libPublicId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var library = new LibraryEntity
            {
                PublicId = "rolluplib",
                DisplayName = "Rollup Test Library",
                RootPath = "/tmp/rolluptest",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            libPublicId = library.PublicId;

            var series = new CatalogNodeEntity
            {
                PublicId = "rollupSeries",
                LibraryId = library.Id,
                Kind = 0,
                DisplayName = "Series",
                RelativePath = "Series",
                PathKey = "Series",
                SortKey = "0Series",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            var empty = new CatalogNodeEntity
            {
                PublicId = "rollupEmpty",
                LibraryId = library.Id,
                Kind = 0,
                DisplayName = "Empty",
                RelativePath = "Empty",
                PathKey = "Empty",
                SortKey = "0Empty",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.AddRange(series, empty);
            await db.SaveChangesAsync();

            var vol = new CatalogNodeEntity
            {
                PublicId = "rollupVol",
                LibraryId = library.Id,
                ParentId = series.Id,
                Kind = 0,
                DisplayName = "Vol 1",
                RelativePath = "Series/Vol 1",
                PathKey = "Series/Vol 1",
                SortKey = "0Vol 1",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(vol);
            await db.SaveChangesAsync();

            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "rollupCh1",
                LibraryId = library.Id,
                ParentId = vol.Id,
                Kind = 1,
                DisplayName = "Ch1.cbz",
                RelativePath = "Series/Vol 1/Ch1.cbz",
                PathKey = "Series/Vol 1/Ch1.cbz",
                SortKey = "1Ch1",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "rollupCh2",
                LibraryId = library.Id,
                ParentId = series.Id,
                Kind = 1,
                DisplayName = "Ch2.cbz",
                RelativePath = "Series/Ch2.cbz",
                PathKey = "Series/Ch2.cbz",
                SortKey = "1Ch2",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = await GetAuthenticatedClientAsync();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        async Task<Dictionary<string, FolderReadRollup?>> BrowseRootRollupsAsync()
        {
            var response = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name");
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
            Assert.NotNull(page);
            return page!.Items.ToDictionary(n => n.Id, n => n.ReadRollup);
        }

        // Nothing read yet: Series is Unread; Empty has nothing to roll up (null).
        var initial = await BrowseRootRollupsAsync();
        Assert.Equal(FolderReadRollup.Unread, initial["rollupSeries"]);
        Assert.Null(initial["rollupEmpty"]);

        // Wire check: the enum travels as its string name, like every other enum.
        var rawResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name");
        var raw = await rawResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"readRollup\":\"Unread\"", raw);
        Assert.Contains("\"readRollup\":null", raw);

        // Mark one nested archive read -> partial.
        var markOne = await client.PutAsJsonAsync("/api/v1/reading/rollupCh1/read", new { });
        markOne.EnsureSuccessStatusCode();
        Assert.Equal(FolderReadRollup.Reading, (await BrowseRootRollupsAsync())["rollupSeries"]);

        // Bulk-mark the folder read -> all read.
        var markAll = await client.PutAsJsonAsync("/api/v1/reading/folders/rollupSeries/read", new { });
        markAll.EnsureSuccessStatusCode();
        Assert.Equal(FolderReadRollup.Read, (await BrowseRootRollupsAsync())["rollupSeries"]);

        // Bulk-clear -> back to unread.
        var clearAll = await client.DeleteAsync("/api/v1/reading/folders/rollupSeries/read");
        clearAll.EnsureSuccessStatusCode();
        Assert.Equal(FolderReadRollup.Unread, (await BrowseRootRollupsAsync())["rollupSeries"]);
    }

    /// <summary>
    /// 1.7.0 next-unread "Continue" row through the public surface: the browse
    /// response carries an additive <c>nextUnread</c> field pointing at the folder's
    /// next-to-read descendant archive. It is null when every descendant is read and
    /// resumes an in-progress archive over the first-by-sort unread one.
    /// </summary>
    [Fact]
    public async Task Browse_NextUnread_TracksReadMarksThroughApi()
    {
        string libPublicId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var library = new LibraryEntity
            {
                PublicId = "nextunreadlib",
                DisplayName = "Next Unread Library",
                RootPath = "/tmp/nextunread",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            libPublicId = library.PublicId;

            var series = new CatalogNodeEntity
            {
                PublicId = "nuSeries",
                LibraryId = library.Id,
                Kind = 0,
                DisplayName = "Series",
                RelativePath = "Series",
                PathKey = "Series",
                SortKey = "0Series",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(series);
            await db.SaveChangesAsync();

            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "nuCh1",
                LibraryId = library.Id,
                ParentId = series.Id,
                Kind = 1,
                DisplayName = "Ch1.cbz",
                RelativePath = "Series/Ch1.cbz",
                PathKey = "Series/Ch1.cbz",
                SortKey = "1Ch1",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "nuCh2",
                LibraryId = library.Id,
                ParentId = series.Id,
                Kind = 1,
                DisplayName = "Ch2.cbz",
                RelativePath = "Series/Ch2.cbz",
                PathKey = "Series/Ch2.cbz",
                SortKey = "1Ch2",
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var client = await GetAuthenticatedClientAsync();
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        async Task<PageResponse<CatalogNodeDto>?> BrowseSeriesAsync()
        {
            var response = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?parentId=nuSeries&sort=name");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        }

        // Nothing read -> Continue points at the first unread by sort (Ch1).
        var initial = await BrowseSeriesAsync();
        Assert.NotNull(initial);
        Assert.NotNull(initial!.NextUnread);
        Assert.Equal("nuCh1", initial.NextUnread!.Id);
        Assert.Equal("Ch1.cbz", initial.NextUnread.DisplayName);
        Assert.Equal(CatalogNodeKind.Archive, initial.NextUnread.Kind);

        // Wire check: the additive field travels as `nextUnread` (camelCase).
        var rawResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?parentId=nuSeries&sort=name");
        var raw = await rawResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"nextUnread\":", raw);
        Assert.Contains("\"nuCh1\"", raw);

        // Mark Ch1 read -> Continue advances to Ch2.
        var markOne = await client.PutAsJsonAsync("/api/v1/reading/nuCh1/read", new { });
        markOne.EnsureSuccessStatusCode();
        var afterOne = await BrowseSeriesAsync();
        Assert.Equal("nuCh2", afterOne!.NextUnread!.Id);

        // Mark Ch2 read -> no unread descendant -> nextUnread is null.
        var markTwo = await client.PutAsJsonAsync("/api/v1/reading/nuCh2/read", new { });
        markTwo.EnsureSuccessStatusCode();
        var afterAll = await BrowseSeriesAsync();
        Assert.Null(afterAll!.NextUnread);

        // Library-root browse also surfaces the next-unread over the whole library.
        var rootResponse = await client.GetAsync($"/api/v1/libraries/{libPublicId}/browse?sort=name");
        rootResponse.EnsureSuccessStatusCode();
        var rootPage = await rootResponse.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(jsonOptions);
        // Both chapters read -> null at the root too.
        Assert.Null(rootPage!.NextUnread);
    }
}
