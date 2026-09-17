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
/// End-to-end HTTP cover for natural name sort (1.15.0), over the surfaces the persisted
/// <c>SortKey</c> drives: browse listings, folder covers, next/prev navigation, the
/// pinned Continue row (first unread) and the jump rail.
///
/// The fixture is deliberately built with NO "Chapter 1": the chapters start at 2, so the
/// raw-key ordering the server used before this change ("Chapter 10", "Chapter 100",
/// "Chapter 2", "Chapter 20", "Chapter 3") differs from natural order at the FIRST
/// element. Every assertion below therefore fails on the old behaviour rather than
/// passing by coincidence - including the ones that only look at a single item, like the
/// folder cover and the Continue row.
///
/// Sort keys are produced with <see cref="SortKey.ForNode"/>, the same call the scanner
/// makes, so the fixture cannot drift from what production persists.
/// </summary>
[Collection("HttpSerial")]
public sealed class NaturalSortHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
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

    /// <summary>Chapter names in the order natural sort must produce.</summary>
    private static readonly string[] ChaptersInNaturalOrder =
        ["Chapter 2.cbz", "Chapter 3.cbz", "Chapter 10.cbz", "Chapter 20.cbz", "Chapter 100.cbz"];

    public NaturalSortHttpTests(MangaPixerWebApplicationFactory factory)
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

    private async Task<(string LibraryId, string SeriesId, Dictionary<string, string> ChapterIds)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "natsortlib");
        if (library is null)
        {
            library = new LibraryEntity
            {
                PublicId = "natsortlib",
                DisplayName = "Natural Sort Library",
                RootPath = "/private/natsort",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // Top level: a punctuation-leading folder, a digit-leading folder, a
            // letter-leading folder, then archives. Exercises the digit-vs-letter
            // ordering the encoder's run marker controls.
            foreach (var name in new[] { "-Bonus", "10-Anthology", "Series" })
            {
                db.CatalogNodes.Add(NewNode($"natsort-{name}", library.Id, null, CatalogNodeKind.Folder, name));
            }
            db.CatalogNodes.Add(NewNode("natsort-loose", library.Id, null, CatalogNodeKind.Archive, "1 Loose.cbz"));
            db.CatalogNodes.Add(NewNode("natsort-beta", library.Id, null, CatalogNodeKind.Archive, "Beta.cbz"));
            await db.SaveChangesAsync();

            var series = await db.CatalogNodes.FirstAsync(n => n.PublicId == "natsort-Series");

            // Insertion order is deliberately NOT sorted order, so nothing can pass by
            // falling back on insertion or ID order.
            foreach (var name in new[] { "Chapter 10.cbz", "Chapter 100.cbz", "Chapter 2.cbz", "Chapter 20.cbz", "Chapter 3.cbz" })
            {
                var node = NewNode($"natsort-{name}", library.Id, series.Id, CatalogNodeKind.Archive, name);
                node.ArchiveItem = new ArchiveItemEntity
                {
                    ArchiveFormat = 1,
                    ByteLength = 1024,
                    ModificationTicks = DateTimeOffset.UtcNow.UtcTicks,
                    ContentVersion = 1,
                    AnalysisState = 3, // analyzed
                    PageCount = 1,
                };
                db.CatalogNodes.Add(node);
            }
            await db.SaveChangesAsync();
        }

        var seriesId = (await db.CatalogNodes.FirstAsync(n => n.PublicId == "natsort-Series")).PublicId;
        var chapterIds = ChaptersInNaturalOrder.ToDictionary(n => n, n => $"natsort-{n}");
        return (library.PublicId, seriesId, chapterIds);
    }

    private static CatalogNodeEntity NewNode(
        string publicId, long libraryId, long? parentId, CatalogNodeKind kind, string displayName) =>
        new()
        {
            PublicId = publicId,
            LibraryId = libraryId,
            ParentId = parentId,
            Kind = (int)kind,
            DisplayName = displayName,
            RelativePath = displayName,
            PathKey = parentId is null ? displayName : $"Series/{displayName}",
            // The production encoder - not a hand-crafted key.
            SortKey = SortKey.ForNode(kind, displayName),
            Availability = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private async Task<PageResponse<CatalogNodeDto>> BrowseAsync(
        HttpClient client, string libraryId, string? parentId = null, string? cursor = null)
    {
        var url = $"/api/v1/libraries/{libraryId}/browse?sort=name&pageSize=50";
        if (parentId is not null) url += $"&parentId={parentId}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";

        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(JsonOptions);
        Assert.NotNull(page);
        return page!;
    }

    /// <summary>
    /// The headline fix, through the public browse endpoint: chapters come back in
    /// numeric order. Under the old raw key this listing started at "Chapter 10".
    /// </summary>
    [Fact]
    public async Task Browse_ChapterListing_IsInNaturalOrder()
    {
        var (libraryId, seriesId, _) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var page = await BrowseAsync(client, libraryId, seriesId);

        Assert.Equal(ChaptersInNaturalOrder, page.Items.Select(i => i.DisplayName).ToArray());
    }

    /// <summary>
    /// At the library root: folders before archives, and within each kind the encoded key
    /// puts punctuation before digits before letters - matching plain ordinal comparison
    /// of the names, which is what the comparer does for a digit against a letter.
    /// </summary>
    [Fact]
    public async Task Browse_LibraryRoot_OrdersFoldersThenArchives_DigitsBeforeLetters()
    {
        var (libraryId, _, _) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var page = await BrowseAsync(client, libraryId);

        Assert.Equal(
            ["-Bonus", "10-Anthology", "Series", "1 Loose.cbz", "Beta.cbz"],
            page.Items.Select(i => i.DisplayName).ToArray());
    }

    /// <summary>
    /// Keyset pagination walks the same order: paging through in small pages must yield
    /// the natural sequence with no gaps or repeats, because the cursor IS the sort key.
    /// </summary>
    [Fact]
    public async Task Browse_KeysetPagination_WalksNaturalOrderWithoutGaps()
    {
        var (libraryId, seriesId, _) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var seen = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/v1/libraries/{libraryId}/browse?sort=name&pageSize=2&parentId={seriesId}";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(JsonOptions);
            Assert.NotNull(page);
            seen.AddRange(page!.Items.Select(i => i.DisplayName));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(ChaptersInNaturalOrder, seen.ToArray());
    }

    /// <summary>
    /// Folder covers resolve to the first descendant archive in sort-key order. With the
    /// chapters starting at 2, the raw key would have picked "Chapter 10".
    /// </summary>
    [Fact]
    public async Task Browse_FolderCover_ComesFromFirstChapterInNaturalOrder()
    {
        var (libraryId, _, chapterIds) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var page = await BrowseAsync(client, libraryId);
        var series = page.Items.Single(i => i.DisplayName == "Series");

        Assert.Equal($"/api/v1/items/{chapterIds["Chapter 2.cbz"]}/cover", series.CoverUrl);
    }

    /// <summary>
    /// Next/prev navigation steps through siblings in sort-key order: from "Chapter 10"
    /// the reader goes back to "Chapter 3" and forward to "Chapter 20". Under the raw key
    /// "Chapter 10" was the FIRST sibling (no previous at all) and its next was
    /// "Chapter 100".
    /// </summary>
    [Fact]
    public async Task Neighbors_StepThroughChaptersInNaturalOrder()
    {
        var (_, _, chapterIds) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var response = await client.GetAsync($"/api/v1/nodes/{chapterIds["Chapter 10.cbz"]}/neighbors");
        response.EnsureSuccessStatusCode();
        var neighbors = await response.Content.ReadFromJsonAsync<NeighborsPayload>(JsonOptions);

        Assert.NotNull(neighbors);
        Assert.Equal("Chapter 3.cbz", neighbors!.Previous?.DisplayName);
        Assert.Equal("Chapter 20.cbz", neighbors.Next?.DisplayName);
    }

    /// <summary>
    /// Both ends of the listing: the natural-order first chapter has no previous and the
    /// natural-order last has no next. This is the assertion the raw key inverted -
    /// "Chapter 2" had a previous ("Chapter 100") and "Chapter 100" was second.
    /// </summary>
    [Fact]
    public async Task Neighbors_FirstAndLastChapters_HaveNoOuterNeighbor()
    {
        var (_, _, chapterIds) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var first = await client.GetFromJsonAsync<NeighborsPayload>(
            $"/api/v1/nodes/{chapterIds["Chapter 2.cbz"]}/neighbors", JsonOptions);
        Assert.NotNull(first);
        Assert.Null(first!.Previous);
        Assert.Equal("Chapter 3.cbz", first.Next?.DisplayName);

        var last = await client.GetFromJsonAsync<NeighborsPayload>(
            $"/api/v1/nodes/{chapterIds["Chapter 100.cbz"]}/neighbors", JsonOptions);
        Assert.NotNull(last);
        Assert.Equal("Chapter 20.cbz", last!.Previous?.DisplayName);
        Assert.Null(last.Next);
    }

    /// <summary>
    /// The pinned Continue row resolves the first UNREAD descendant in sort-key order,
    /// so it must offer "Chapter 2" first and advance to "Chapter 3" once that is read -
    /// not jump to "Chapter 10" as the raw key did.
    /// </summary>
    [Fact]
    public async Task Browse_ContinueRow_FollowsNaturalOrderAndAdvancesOnRead()
    {
        var (libraryId, seriesId, chapterIds) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var before = await BrowseAsync(client, libraryId, seriesId);
        Assert.NotNull(before.NextUnread);
        Assert.Equal("Chapter 2.cbz", before.NextUnread!.DisplayName);

        var mark = await client.PutAsJsonAsync($"/api/v1/reading/{chapterIds["Chapter 2.cbz"]}/read", new { });
        mark.EnsureSuccessStatusCode();

        try
        {
            var after = await BrowseAsync(client, libraryId, seriesId);
            Assert.NotNull(after.NextUnread);
            Assert.Equal("Chapter 3.cbz", after.NextUnread!.DisplayName);
        }
        finally
        {
            // Leave the fixture as found - the factory is shared across this class.
            var clear = await client.DeleteAsync($"/api/v1/reading/{chapterIds["Chapter 2.cbz"]}/read");
            clear.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// The jump rail's cursors are raw sort keys, so they only land correctly if the rail
    /// and the listing agree on order. The "#" bucket's cursor must put browse on
    /// "10-Anthology", the first digit-leading node - which now sits between "-Bonus" and
    /// the letter-leading folders.
    /// </summary>
    [Fact]
    public async Task JumpIndex_HashBucketCursor_LandsOnFirstDigitLeadingNode()
    {
        var (libraryId, _, _) = await SeedAsync();
        var client = await GetAuthenticatedClientAsync();

        var index = await client.GetFromJsonAsync<JumpIndexDto>(
            $"/api/v1/libraries/{libraryId}/jump-index", JsonOptions);

        Assert.NotNull(index);
        var hash = index!.Buckets.Single(b => b.Label == "#");
        Assert.NotNull(hash.FirstCursor);

        var landed = await BrowseAsync(client, libraryId, parentId: null, cursor: hash.FirstCursor);
        Assert.NotEmpty(landed.Items);
        Assert.Equal("10-Anthology", landed.Items[0].DisplayName);
    }

    /// <summary>
    /// The full wiring, with nothing hand-seeded: a real directory of numbered chapters is
    /// registered as a library, scanned through the admin endpoint, and read back through
    /// browse. This is the only test in which the sort keys are produced by the SCANNER
    /// rather than by the fixture, so it is what actually proves the encoder is wired into
    /// the persist path - the defect this feature fixes was precisely an encoder that was
    /// correct but never called.
    /// </summary>
    [Fact]
    public async Task ScanThenBrowse_OverHttp_ListsChaptersInNaturalOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "mangapixer-natsort-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Scanned Series"));
        foreach (var name in ChaptersInNaturalOrder)
            await File.WriteAllTextAsync(Path.Combine(root, "Scanned Series", name), "x");

        var client = await GetAuthenticatedClientAsync();
        try
        {
            var register = await client.PostAsJsonAsync("/api/v1/admin/libraries", new RegisterLibraryRequest
            {
                DisplayName = "Scanned Natural Sort",
                RootPath = root,
            });
            register.EnsureSuccessStatusCode();
            var library = await register.Content.ReadFromJsonAsync<LibraryDto>(JsonOptions);
            Assert.NotNull(library);

            var scan = await client.PostAsync($"/api/v1/admin/libraries/{library!.Id}/scan", null);
            Assert.True(
                scan.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.Conflict,
                $"Unexpected scan trigger status {scan.StatusCode}");

            // The scan runs as a background job, so poll for the chapters rather than
            // assuming a fixed delay. Bounded, and it asserts on the final observation so a
            // timeout fails with the actual listing rather than a bare timeout message.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string[] chapters = [];
            while (DateTime.UtcNow < deadline)
            {
                var rootPage = await BrowseAsync(client, library.Id);
                var series = rootPage.Items.FirstOrDefault(i => i.DisplayName == "Scanned Series");
                if (series is not null)
                {
                    var page = await BrowseAsync(client, library.Id, series.Id);
                    chapters = page.Items.Select(i => i.DisplayName).ToArray();
                    if (chapters.Length == ChaptersInNaturalOrder.Length)
                        break;
                }
                await Task.Delay(250);
            }

            Assert.Equal(ChaptersInNaturalOrder, chapters);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// Local mirror of the neighbors response shape (the server-side record lives in the
    /// server assembly's browse service, not in the shared DTO assembly).
    /// </summary>
    private sealed record NeighborsPayload
    {
        public NeighborPayload? Previous { get; init; }
        public NeighborPayload? Next { get; init; }
    }

    private sealed record NeighborPayload
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
    }
}
