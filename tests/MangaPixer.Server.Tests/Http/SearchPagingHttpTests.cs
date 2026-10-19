namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.45.0 phase K4: search paging over HTTP (the <c>cursor</c> / <c>pageSize</c> query parameters reach the keyset), the per-user read state
/// on the result cards (archives: read mark / in-progress progress; folders: the browse read rollup) and the <c>readState</c> filter, applied
/// BEFORE paging so every page and the count agree. Synthetic names only.
///
/// Seed (every node matches "Pager" through its name or its path):
///   Pager Saga One/   Pager 01, 02, 03 - all read                 -> folder rolls up Read
///   Pager Saga Two/   Pager 01 in progress, 02, 03 untouched     -> folder rolls up Reading
///   Pager Saga Three/ Other 01 untouched                          -> folder rolls up Unread
/// The two folders' "Pager 0N.cbz" share a SortKey: a SortKey-only cursor would skip the second of a tie at a page boundary.
/// </summary>
public sealed class SearchPagingHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string Query = "Pager";
    private readonly MangaPixerWebApplicationFactory _factory;

    public SearchPagingHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record Seeded(HashSet<string> All, HashSet<string> Read, HashSet<string> Reading, HashSet<string> Unread, string InProgressId);

    private async Task<(HttpClient Client, Seeded Seed)> SetupAsync()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var adminId = (await db.Users.FirstAsync(u => u.NormalizedUserName == "ADMIN")).Id;

        if (!await db.Libraries.AnyAsync(l => l.PublicId == "pagerlib"))
        {
            var now = DateTimeOffset.UtcNow;
            var library = new LibraryEntity { PublicId = "pagerlib", DisplayName = "Pager Library", RootPath = "/tmp/pager", CreatedAt = now };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            CatalogNodeEntity Folder(string pid, string name) => new()
            {
                PublicId = pid, LibraryId = library.Id, Kind = (int)CatalogNodeKind.Folder, DisplayName = name,
                RelativePath = name, PathKey = name, SortKey = "0" + name, Availability = 0, CreatedAt = now,
            };
            var one = Folder("pgOne", "Pager Saga One");
            var two = Folder("pgTwo", "Pager Saga Two");
            var three = Folder("pgThree", "Pager Saga Three");
            db.CatalogNodes.AddRange(one, two, three);
            await db.SaveChangesAsync();

            CatalogNodeEntity Archive(string pid, CatalogNodeEntity parent, string name) => new()
            {
                PublicId = pid, LibraryId = library.Id, ParentId = parent.Id, Kind = (int)CatalogNodeKind.Archive, DisplayName = name,
                RelativePath = $"{parent.DisplayName}/{name}", PathKey = $"{parent.DisplayName}/{name}", SortKey = "1" + name,
                Availability = 0, CreatedAt = now,
            };
            var archives = new[]
            {
                Archive("pg1a", one, "Pager 01.cbz"), Archive("pg1b", one, "Pager 02.cbz"), Archive("pg1c", one, "Pager 03.cbz"),
                Archive("pg2a", two, "Pager 01.cbz"), Archive("pg2b", two, "Pager 02.cbz"), Archive("pg2c", two, "Pager 03.cbz"),
                Archive("pg3a", three, "Other 01.cbz"),
            };
            db.CatalogNodes.AddRange(archives);
            await db.SaveChangesAsync();

            foreach (var read in archives.Take(3))
                db.ReadMarks.Add(new ReadMarkEntity { UserId = adminId, ItemId = read.Id, MarkedAt = now, Source = "manual" });
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = adminId, ItemId = archives[3].Id, ContentVersion = 1, EntryKey = "p4", Ordinal = 4,
                State = (int)ReadingState.InProgress, Revision = 1, LastMutationId = Guid.NewGuid().ToString("N"), UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var seed = new Seeded(
            All: ["pgOne", "pgTwo", "pgThree", "pg1a", "pg1b", "pg1c", "pg2a", "pg2b", "pg2c", "pg3a"],
            Read: ["pgOne", "pg1a", "pg1b", "pg1c"],
            Reading: ["pgTwo", "pg2a"],
            Unread: ["pgThree", "pg3a", "pg2b", "pg2c"],
            InProgressId: "pg2a");
        return (client, seed);
    }

    /// <summary>Follows <c>nextCursor</c> from the first page to the last; returns every page.</summary>
    private static async Task<List<SearchResultsDto>> WalkAsync(HttpClient client, string extra, int pageSize)
    {
        var pages = new List<SearchResultsDto>();
        string? cursor = null;
        do
        {
            var url = $"/api/v1/search?q={Query}&pageSize={pageSize}{extra}"
                + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);
            Assert.NotNull(page);
            pages.Add(page!);
            cursor = page!.HasMore ? page.NextCursor : null;
            Assert.True(pages.Count <= 20, "the cursor must move forward");
        }
        while (cursor is not null);
        return pages;
    }

    [Fact]
    public async Task Cursor_ReachesPageTwo_AndEveryResultOnceAcrossTies()
    {
        var (client, seed) = await SetupAsync();

        var pages = await WalkAsync(client, "", pageSize: 3);

        Assert.Equal(3, pages[0].Items.Count);
        Assert.True(pages[0].HasMore);
        Assert.NotNull(pages[0].NextCursor);
        Assert.True(pages.Count >= 2, "page 2 must be reachable over HTTP");
        var ids = pages.SelectMany(p => p.Items).Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(seed.All.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.All(pages, p => Assert.Equal(seed.All.Count, p.TotalCount));
        Assert.False(pages[^1].HasMore);
        Assert.Null(pages[^1].NextCursor);
    }

    [Fact]
    public async Task Results_CarryTheViewersReadState_AsBrowseCardsDo()
    {
        var (client, _) = await SetupAsync();

        var items = (await WalkAsync(client, "", pageSize: 50)).SelectMany(p => p.Items).ToDictionary(n => n.Id);

        Assert.True(items["pg1a"].IsRead);
        Assert.False(items["pg2a"].IsRead);
        Assert.Equal(ReadingState.InProgress, items["pg2a"].ReadingState);
        Assert.Equal(4, items["pg2a"].LastReadPage);
        Assert.False(items["pg2b"].IsRead);
        Assert.Null(items["pg2b"].ReadingState);
        Assert.Equal(FolderReadRollup.Read, items["pgOne"].ReadRollup);
        Assert.Equal(FolderReadRollup.Reading, items["pgTwo"].ReadRollup);
        Assert.Equal(FolderReadRollup.Unread, items["pgThree"].ReadRollup);
    }

    [Theory]
    [InlineData("unread")]
    [InlineData("reading")]
    [InlineData("read")]
    public async Task ReadStateFilter_AppliesBeforePaging_EveryPageAndTheCountAgree(string readState)
    {
        var (client, seed) = await SetupAsync();
        var expected = readState switch { "unread" => seed.Unread, "reading" => seed.Reading, _ => seed.Read };

        var pages = await WalkAsync(client, "&readState=" + readState, pageSize: 1);

        var ids = pages.SelectMany(p => p.Items).Select(n => n.Id).ToList();
        Assert.Equal(expected.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.Equal(expected.Count, pages.Count);
        Assert.All(pages, p => Assert.Equal(expected.Count, p.TotalCount));
        foreach (var node in pages.SelectMany(p => p.Items))
        {
            var state = node.Kind == CatalogNodeKind.Folder
                ? node.ReadRollup switch { FolderReadRollup.Read => "read", FolderReadRollup.Reading => "reading", _ => "unread" }
                : node.IsRead ? "read" : node.ReadingState == ReadingState.InProgress ? "reading" : "unread";
            Assert.Equal(readState, state);
        }
    }

    [Fact]
    public async Task UnknownReadState_IsNoFilter_AndPageSizeIsBounded()
    {
        var (client, seed) = await SetupAsync();

        var response = await client.GetAsync($"/api/v1/search?q={Query}&readState=whatever&pageSize=0");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<SearchResultsDto>(TestJson.Web);

        Assert.Equal(seed.All.Count, page!.TotalCount);
        Assert.Single(page.Items); // pageSize below 1 is raised to 1
        Assert.True(page.HasMore);
    }
}
