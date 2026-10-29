namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;

/// <summary>
/// 1.50.0 lane F (the Favorites view, owner decisions 6-9): <c>GET favorites</c> with <c>sort</c> / <c>direction</c> / <c>readState</c> /
/// <c>kind</c> / <c>libraryId</c>, through the real pipeline with two readers. Every sort pages every entry exactly once (a starred folder
/// and the stack of its own starred children are two entries with one node id); each filter applies before the page is cut (TotalCount,
/// HasMore and the cursors agree); the library filter is an id route that lists nothing outside what the viewer may see (Private under
/// Incognito included); a second reader sees none of the first's favorites under any parameter; and no parameter = the listing the home
/// Favorites row reads, unchanged (recently favorited).
/// </summary>
public sealed class FavoritesViewHttpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record World(MangaPixerWebApplicationFactory Factory, AccessReader A, AccessReader B);

    /// <summary>
    /// Library fv1: folder "Maple" (fvM, starred) with three archives - fvM1 and fvM2 starred (a stack), fvM1 read; an empty folder
    /// "Birch" (fvB, starred); root archives "Alpha" (fvA, starred, read) and "Zulu" (fvZ, starred, in progress). Library fv2 (Private for
    /// reader A): archive "Private One" (fvP, starred). Reader A's entries: fvM, stack fvM, fvB, fvA, fvZ, fvP. Reader B: nothing.
    /// </summary>
    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib1 = await s.LibraryAsync("fv1", "Favorites View One");
        var lib2 = await s.LibraryAsync("fv2", "Favorites View Two");

        var ids = await s.WithDbAsync(async db =>
        {
            var map = new Dictionary<string, long>();
            async Task<long> Node(string id, long lib, long? parent, CatalogNodeKind kind, string name, int addedDay, int? latestDay = null)
            {
                var node = new CatalogNodeEntity
                {
                    PublicId = id,
                    LibraryId = lib,
                    ParentId = parent,
                    Kind = (int)kind,
                    DisplayName = name,
                    RelativePath = id,
                    PathKey = id,
                    SortKey = SortKey.ForNode(kind, name),
                    CreatedAt = T0.AddDays(addedDay),
                    LatestDescendantAddedAt = latestDay is int d ? T0.AddDays(d) : null,
                };
                db.CatalogNodes.Add(node);
                await db.SaveChangesAsync();
                return map[id] = node.Id;
            }

            var maple = await Node("fvM", lib1, null, CatalogNodeKind.Folder, "Maple", addedDay: 1, latestDay: 9);
            await Node("fvM1", lib1, maple, CatalogNodeKind.Archive, "Maple 01", addedDay: 5);
            await Node("fvM2", lib1, maple, CatalogNodeKind.Archive, "Maple 02", addedDay: 8);
            await Node("fvM3", lib1, maple, CatalogNodeKind.Archive, "Maple 03", addedDay: 9);
            await Node("fvB", lib1, null, CatalogNodeKind.Folder, "Birch", addedDay: 2);
            await Node("fvA", lib1, null, CatalogNodeKind.Archive, "Alpha", addedDay: 7);
            await Node("fvZ", lib1, null, CatalogNodeKind.Archive, "Zulu", addedDay: 6);
            await Node("fvP", lib2, null, CatalogNodeKind.Archive, "Private One", addedDay: 3);
            return map;
        });

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "fvreadera", [lib1, lib2], privateLibraryIds: [lib2]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "fvreaderb", [lib1, lib2]);

        await s.WithDbAsync(async db =>
        {
            var minute = 0;
            foreach (var id in new[] { "fvM", "fvM1", "fvM2", "fvB", "fvA", "fvZ", "fvP" })
                db.Favorites.Add(new FavoriteEntity { UserId = a.UserId, CatalogNodeId = ids[id], CreatedAt = T0.AddMinutes(minute++) });
            db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = ids["fvA"], MarkedAt = T0 });
            db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = ids["fvM1"], MarkedAt = T0 });
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = a.UserId,
                ItemId = ids["fvZ"],
                State = (int)ReadingState.InProgress,
                Ordinal = 3,
                EntryKey = "e",
                LastMutationId = "fv-z",
                UpdatedAt = T0,
            });
            await db.SaveChangesAsync();
        });
        return new World(factory, a, b);
    }

    private static async Task<PageResponse<CatalogNodeDto>> PageAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/v1/favorites" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(TestJson.Web))!;
    }

    /// <summary>An entry's identity: a stack shares its folder's node id, so it is told apart by its count.</summary>
    private static string Key(CatalogNodeDto n) => n.FavoriteStackCount is int c ? $"stack:{n.Id}:{c}" : n.Id;

    /// <summary>Walks every page of <paramref name="query"/> with <paramref name="pageSize"/>, following nextCursor.</summary>
    private static async Task<(List<string> Keys, int TotalCount)> WalkAsync(HttpClient client, string query, int pageSize)
    {
        var keys = new List<string>();
        string? cursor = null;
        int total = -1;
        for (var guard = 0; guard < 50; guard++)
        {
            var sep = query.Length == 0 ? "?" : "&";
            var page = await PageAsync(client, query + sep + "pageSize=" + pageSize
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            total = page.TotalCount;
            keys.AddRange(page.Items.Select(Key));
            Assert.Equal(page.HasMore, page.NextCursor is not null);
            if (!page.HasMore)
                return (keys, total);
            cursor = page.NextCursor;
        }
        throw new InvalidOperationException("paging did not end");
    }

    [Theory]
    [InlineData("")]
    [InlineData("?sort=favorited")]
    [InlineData("?sort=name")]
    [InlineData("?sort=name&direction=desc")]
    [InlineData("?sort=recentlyAdded")]
    [InlineData("?sort=recentlyUpdated")]
    [InlineData("?sort=name&readState=reading")]
    [InlineData("?sort=recentlyAdded&kind=archives")]
    public async Task EverySort_PagesEachEntryExactlyOnce_InTheOrderOfOnePage(string query)
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var whole = await PageAsync(w.A.Client, query);
        foreach (var size in new[] { 1, 2 })
        {
            var (keys, total) = await WalkAsync(w.A.Client, query, size);
            Assert.Equal(whole.Items.Select(Key), keys);
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.Equal(whole.TotalCount, total);
            Assert.Equal(keys.Count, total);
        }
    }

    [Fact]
    public async Task TheSorts_OrderAsDecided()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        async Task<string[]> Order(string q) => [.. (await PageAsync(w.A.Client, q)).Items.Select(Key)];

        // No parameter = recently favorited, newest first (the home row): fvP, fvZ, fvA, fvB, the stack (its newest star, fvM2), fvM.
        Assert.Equal(["fvP", "fvZ", "fvA", "fvB", "stack:fvM:2", "fvM"], await Order(string.Empty));
        Assert.Equal(await Order(string.Empty), await Order("?sort=favorited"));
        // Name A-Z: folders first (the catalog key), a stack right after its own starred folder; then the archives.
        Assert.Equal(["fvB", "fvM", "stack:fvM:2", "fvA", "fvP", "fvZ"], await Order("?sort=name"));
        Assert.Equal(["fvZ", "fvP", "fvA", "stack:fvM:2", "fvM", "fvB"], await Order("?sort=name&direction=desc"));
        // Recently added: the stack at its newest member (day 8), then Alpha (7), Zulu (6), Private (3), Birch (2), Maple itself (1).
        Assert.Equal(["stack:fvM:2", "fvA", "fvZ", "fvP", "fvB", "fvM"], await Order("?sort=recentlyAdded"));
        // Recently updated: Maple by its newest archive (day 9), the stack (8), Alpha, Zulu, Private; Birch has no archive - last.
        Assert.Equal(["fvM", "stack:fvM:2", "fvA", "fvZ", "fvP", "fvB"], await Order("?sort=recentlyUpdated"));
        // An unknown sort is recently favorited; a cursor of another sort reads as the first page.
        Assert.Equal(await Order(string.Empty), await Order("?sort=bogus"));
        var first = await PageAsync(w.A.Client, "?sort=name&pageSize=2");
        var foreign = await PageAsync(w.A.Client, "?sort=recentlyAdded&pageSize=2&cursor=" + Uri.EscapeDataString(first.NextCursor!));
        Assert.Equal(["stack:fvM:2", "fvA"], foreign.Items.Select(Key));
    }

    [Fact]
    public async Task TheFilters_ApplyBeforeThePage_AStackCountsAsArchives_AndIsJudgedByItsWholeFolder()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        // "keys in order | TotalCount"
        async Task<string> Of(string q)
        {
            var page = await PageAsync(w.A.Client, "?sort=name" + q);
            return string.Join(",", page.Items.Select(Key)) + "|" + page.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal("fvB,fvM|2", await Of("&kind=folders"));
        Assert.Equal("stack:fvM:2,fvA,fvP,fvZ|4", await Of("&kind=archives"));
        // Read: Alpha. Reading: Maple (one of three read - the stack too, by the whole folder), Zulu (in progress). Unread: the empty
        // Birch (as browse), Private One.
        Assert.Equal("fvA|1", await Of("&readState=read"));
        Assert.Equal("fvM,stack:fvM:2,fvZ|3", await Of("&readState=reading"));
        Assert.Equal("fvB,fvP|2", await Of("&readState=unread"));
        Assert.Equal("stack:fvM:2,fvZ|2", await Of("&readState=reading&kind=archives"));
        // The library filter, and a filter value nobody sent: no filter.
        Assert.Equal("fvP|1", await Of("&libraryId=fv2"));
        Assert.EndsWith("|5", await Of("&libraryId=fv1"), StringComparison.Ordinal);
        Assert.EndsWith("|6", await Of("&readState=all&kind=everything"), StringComparison.Ordinal);
        // A filtered page cut mid-list: HasMore and TotalCount follow the filter.
        var cut = await PageAsync(w.A.Client, "?sort=name&readState=reading&pageSize=2");
        Assert.True(cut.HasMore);
        Assert.Equal(3, cut.TotalCount);
    }

    [Fact]
    public async Task TheCards_CarryTheirCovers_AndReadState()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var page = await PageAsync(w.A.Client, "?sort=name");
        var alpha = page.Items.Single(n => n.Id == "fvA");
        Assert.Equal("/api/v1/items/fvA/cover", alpha.CoverUrl?.Split('?')[0]);
        Assert.True(alpha.IsRead);
        Assert.Equal(ReadingState.InProgress, page.Items.Single(n => n.Id == "fvZ").ReadingState);
        var stack = page.Items.Single(n => n.FavoriteStackCount == 2);
        Assert.NotNull(stack.CoverUrl);
        Assert.Equal(FolderReadRollup.Reading, stack.ReadRollup);
    }

    [Fact]
    public async Task Incognito_HidesThePrivateLibrary_EvenWhenAskedForByName()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        foreach (var q in new[] { string.Empty, "?sort=name", "?sort=recentlyAdded&kind=archives", "?readState=unread", "?libraryId=fv2" })
        {
            var raw = await w.A.Incognito.GetStringAsync("/api/v1/favorites" + q);
            Assert.DoesNotContain("fvP", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("Private One", raw, StringComparison.Ordinal);
        }
        Assert.Equal(0, (await PageAsync(w.A.Incognito, "?libraryId=fv2")).TotalCount);
        Assert.Equal(5, (await PageAsync(w.A.Incognito, "?libraryId=fv1")).TotalCount);
        Assert.Equal(1, (await PageAsync(w.A.Client, "?libraryId=fv2")).TotalCount);
    }

    [Fact]
    public async Task ASecondReader_SeesNoneOfTheFirstReadersFavorites_UnderAnyParameter()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        foreach (var q in new[]
                 {
                     string.Empty, "?sort=name", "?sort=name&direction=desc", "?sort=recentlyAdded", "?sort=recentlyUpdated",
                     "?readState=read", "?readState=unread", "?kind=folders", "?kind=archives", "?libraryId=fv1", "?libraryId=fv2",
                 })
        {
            var page = await PageAsync(w.B.Client, q);
            Assert.Equal(0, page.TotalCount);
            Assert.Empty(page.Items);
        }
    }

    [Fact]
    public async Task AnUnknownOrUngrantedLibrary_ListsNothing()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        var s = new FacetQueryTestSupport(w.Factory);
        var other = await s.LibraryAsync("fv3", "Favorites View Three");
        await s.WithDbAsync(async db =>
        {
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = "fvU",
                LibraryId = other,
                Kind = (int)CatalogNodeKind.Archive,
                DisplayName = "Ungranted",
                RelativePath = "fvU",
                PathKey = "fvU",
                SortKey = SortKey.ForNode(CatalogNodeKind.Archive, "Ungranted"),
                CreatedAt = T0,
            });
            await db.SaveChangesAsync();
        });

        Assert.Equal(0, (await PageAsync(w.A.Client, "?libraryId=fv3")).TotalCount);
        Assert.Equal(0, (await PageAsync(w.A.Client, "?libraryId=nosuchlibrary")).TotalCount);
    }
}
