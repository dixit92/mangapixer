using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// End-to-end HTTP cover for case-insensitive name sort: browse listings, keyset paging
/// (the cursor is the raw sort key, which now carries a U+0001 separator through the query
/// string), the jump rail, and a real scan so the SCANNER's keys are exercised too.
///
/// Under the old case-sensitive key every capitalised name listed before every lower-case
/// one, so each assertion below fails on the old behaviour at its first element.
/// </summary>
[Collection("HttpSerial")]
public sealed class CaseInsensitiveSortHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private HttpClient? _authenticatedClient;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Root folders in the order case-insensitive sort must produce.</summary>
    private static readonly string[] FoldersInOrder = ["apple", "Apricot", "Berserk", "berserk", "cherry", "Zebra"];

    public CaseInsensitiveSortHttpTests(MangaPixerWebApplicationFactory factory)
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

    private async Task<string> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "casesortlib");
        if (library is null)
        {
            library = new LibraryEntity
            {
                PublicId = "casesortlib",
                DisplayName = "Case Sort Library",
                RootPath = "/private/casesort",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // Insertion order is deliberately not sorted order.
            foreach (var name in new[] { "Zebra", "berserk", "apple", "cherry", "Berserk", "Apricot" })
            {
                db.CatalogNodes.Add(new CatalogNodeEntity
                {
                    PublicId = $"casesort-{name}",
                    LibraryId = library.Id,
                    Kind = (int)CatalogNodeKind.Folder,
                    DisplayName = name,
                    RelativePath = name,
                    PathKey = name,
                    // The production encoder - not a hand-crafted key.
                    SortKey = SortKey.ForNode(CatalogNodeKind.Folder, name),
                    Availability = 0,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }

        return library.PublicId;
    }

    private async Task<PageResponse<CatalogNodeDto>> BrowseAsync(
        HttpClient client, string libraryId, string? parentId = null, string? cursor = null, int pageSize = 50)
    {
        var url = $"/api/v1/libraries/{libraryId}/browse?sort=name&pageSize={pageSize}";
        if (parentId is not null) url += $"&parentId={parentId}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(JsonOptions);
        Assert.NotNull(page);
        return page!;
    }

    [Fact]
    public async Task Browse_Listing_IsCaseInsensitive()
    {
        var libraryId = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var page = await BrowseAsync(client, libraryId);

        Assert.Equal(FoldersInOrder, page.Items.Select(i => i.DisplayName).ToArray());
    }

    /// <summary>
    /// One item per page: every cursor is a raw key containing the U+0001 separator, sent
    /// back through the query string. The walk must visit every folder once, including the
    /// two that differ only in case (equal keys would make the exclusive cursor skip one).
    /// </summary>
    [Fact]
    public async Task Browse_KeysetPagination_WalksEveryNodeOnce_IncludingCaseOnlyTwins()
    {
        var libraryId = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var seen = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var page = await BrowseAsync(client, libraryId, cursor: cursor, pageSize: 1);
            seen.AddRange(page.Items.Select(i => i.DisplayName));
            if (!page.HasMore) break;
            cursor = page.NextCursor;
        }

        Assert.Equal(FoldersInOrder, seen.ToArray());
    }

    /// <summary>
    /// The jump rail groups by first letter in key order; with case folded, each letter is one
    /// contiguous run, so the "B" cursor lands on the first B-name and the bucket counts both.
    /// </summary>
    [Fact]
    public async Task JumpIndex_LetterBucket_CoversBothCases_AndLandsOnItsFirstNode()
    {
        var libraryId = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var index = await client.GetFromJsonAsync<JumpIndexDto>(
            $"/api/v1/libraries/{libraryId}/jump-index", JsonOptions);

        Assert.NotNull(index);
        var a = index!.Buckets.Single(b => b.Label == "A");
        var bBucket = index.Buckets.Single(b => b.Label == "B");
        Assert.Equal(2, a.Count);
        Assert.Equal(2, bBucket.Count);

        var landed = await BrowseAsync(client, libraryId, cursor: bBucket.FirstCursor);
        Assert.Equal(["Berserk", "berserk", "cherry", "Zebra"], landed.Items.Select(i => i.DisplayName).ToArray());
    }

    /// <summary>
    /// The full wiring with nothing hand-seeded: real files, scanned through the admin
    /// endpoint, read back through browse - so the keys come from the scanner's persist path.
    /// </summary>
    [Fact]
    public async Task ScanThenBrowse_OverHttp_ListsMixedCaseArchivesCaseInsensitively()
    {
        string[] archivesInOrder = ["alpha.cbz", "Beta.cbz", "delta.cbz", "Gamma.cbz"];
        var root = Path.Combine(Path.GetTempPath(), "mangapixer-casesort-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Scanned Case"));
        foreach (var name in new[] { "Gamma.cbz", "alpha.cbz", "delta.cbz", "Beta.cbz" })
            await File.WriteAllTextAsync(Path.Combine(root, "Scanned Case", name), "x");

        var client = await GetAuthenticatedClientAsync();
        try
        {
            var register = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
            {
                DisplayName = "Scanned Case Sort",
                RootPath = root,
            });
            register.EnsureSuccessStatusCode();
            var library = await register.Content.ReadFromJsonAsync<LibraryDto>(JsonOptions);
            Assert.NotNull(library);

            var scan = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
            Assert.True(
                scan.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.Conflict,
                $"Unexpected scan trigger status {scan.StatusCode}");

            // The scan is a background job: poll (bounded) and assert on the final observation.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string[] archives = [];
            while (DateTime.UtcNow < deadline)
            {
                var rootPage = await BrowseAsync(client, library.Id);
                var folder = rootPage.Items.FirstOrDefault(i => i.DisplayName == "Scanned Case");
                if (folder is not null)
                {
                    var page = await BrowseAsync(client, library.Id, folder.Id);
                    archives = page.Items.Select(i => i.DisplayName).ToArray();
                    if (archives.Length == archivesInOrder.Length)
                        break;
                }
                await Task.Delay(250);
            }

            Assert.Equal(archivesInOrder, archives);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
