namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.52.0 lane R, through the real pipeline with two readers and an Incognito session: <c>PUT</c> / <c>DELETE nodes/{id}/rating</c>
/// (idempotent, 400 outside 1-10, 404 without access), <c>MyRating</c> on browse / search / the single node / the Favorites list, browse and
/// Favorites <c>sort=myRating</c> (paged), the Favorites <c>rating=rated|unrated</c> filter and the <c>rating:</c> search term. A rating is
/// the reader's own: the other reader never sees it, under any route or cursor; a rating in the reader's Private library can be set under
/// Incognito but is not listed there.
/// </summary>
public sealed class RatingsHttpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record World(MangaPixerWebApplicationFactory Factory, AccessReader A, AccessReader B, string Lib1, IReadOnlyDictionary<string, long> Ids);

    /// <summary>
    /// Library "rt One" (rt1): folder "Berserk" (rtB) with "Berserk 01" (rtB1) and "Berserk 02" (rtB2); root archives "Akira" (rtA), "Monster"
    /// (rtM), "Pluto" (rtPl). Library "rt Two" (rt2, Private for reader A): "Private Tale" (rtP). Library "rt Three" (rt3, granted to nobody):
    /// "Hidden Tale" (rtU). Reader A rates rtB 8, rtB1 10, rtA 7, rtP 9 and stars rtB1, rtB2 (a stack of rtB), rtA, rtM, rtP. Reader B rates
    /// rtM 2 and nothing else.
    /// </summary>
    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib1 = await s.LibraryAsync("rt1", "rt One");
        var lib2 = await s.LibraryAsync("rt2", "rt Two");
        var lib3 = await s.LibraryAsync("rt3", "rt Three");

        var ids = await s.WithDbAsync(async db =>
        {
            var map = new Dictionary<string, long>();
            async Task<long> Node(string id, long lib, long? parent, CatalogNodeKind kind, string name)
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
                    CreatedAt = T0,
                };
                db.CatalogNodes.Add(node);
                await db.SaveChangesAsync();
                return map[id] = node.Id;
            }

            var berserk = await Node("rtB", lib1, null, CatalogNodeKind.Folder, "Berserk");
            await Node("rtB1", lib1, berserk, CatalogNodeKind.Archive, "Berserk 01");
            await Node("rtB2", lib1, berserk, CatalogNodeKind.Archive, "Berserk 02");
            await Node("rtA", lib1, null, CatalogNodeKind.Archive, "Akira");
            await Node("rtM", lib1, null, CatalogNodeKind.Archive, "Monster");
            await Node("rtPl", lib1, null, CatalogNodeKind.Archive, "Pluto");
            await Node("rtP", lib2, null, CatalogNodeKind.Archive, "Private Tale");
            await Node("rtU", lib3, null, CatalogNodeKind.Archive, "Hidden Tale");
            return map;
        });

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "rtreadera", [lib1, lib2], privateLibraryIds: [lib2]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "rtreaderb", [lib1, lib2]);

        foreach (var (node, score) in new[] { ("rtB", 8), ("rtB1", 10), ("rtA", 7), ("rtP", 9) })
            Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(a.Client, node, score)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(b.Client, "rtM", 2)).StatusCode);
        await s.WithDbAsync(async db =>
        {
            var minute = 0;
            foreach (var id in new[] { "rtB1", "rtB2", "rtA", "rtM", "rtP" })
                db.Favorites.Add(new FavoriteEntity { UserId = a.UserId, CatalogNodeId = ids[id], CreatedAt = T0.AddMinutes(minute++) });
            await db.SaveChangesAsync();
        });
        return new World(factory, a, b, "rt1", ids);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string nodeId, int score) =>
        client.PutAsJsonAsync($"/api/v1/nodes/{nodeId}/rating", new { score });

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string query) =>
        GetAsync<PageResponse<CatalogNodeDto>>(client, "/api/v1/libraries/rt1/browse" + query);

    private static Task<PageResponse<CatalogNodeDto>> FavoritesAsync(HttpClient client, string query) =>
        GetAsync<PageResponse<CatalogNodeDto>>(client, "/api/v1/favorites" + query);

    private static Task<SearchResultsDto> SearchAsync(HttpClient client, string text) =>
        GetAsync<SearchResultsDto>(client, "/api/v1/search?q=" + Uri.EscapeDataString(text));

    private static string Key(CatalogNodeDto n) => n.FavoriteStackCount is int ? $"stack:{n.Id}" : n.Id;

    private static async Task<List<CatalogNodeDto>> AllPagesAsync(Func<string?, Task<PageResponse<CatalogNodeDto>>> page)
    {
        var all = new List<CatalogNodeDto>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var p = await page(cursor);
            all.AddRange(p.Items);
            if (!p.HasMore)
                return all;
            Assert.NotNull(p.NextCursor);
            cursor = p.NextCursor;
        }
        throw new InvalidOperationException("paging did not end");
    }

    [Fact]
    public async Task Put_SetsChangesAndIsIdempotent_Delete_Clears_BothAnswer204()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.Equal(8, (await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtB")).MyRating);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w.A.Client, "rtB", 8)).StatusCode); // the same score again
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w.A.Client, "rtB", 3)).StatusCode); // changed
        Assert.Equal(3, (await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtB")).MyRating);

        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.DeleteAsync("/api/v1/nodes/rtB/rating")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.DeleteAsync("/api/v1/nodes/rtB/rating")).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.DeleteAsync("/api/v1/nodes/nosuchnode/rating")).StatusCode);
        Assert.Null((await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtB")).MyRating);

        // One row per (user, node), whatever was sent.
        var rows = await new FacetQueryTestSupport(w.Factory).WithDbAsync(db =>
            db.UserRatings.Where(r => r.UserId == w.A.UserId).OrderBy(r => r.CatalogNodeId).Select(r => r.Score).ToListAsync());
        Assert.Equal([10, 7, 9], rows);
    }

    [Fact]
    public async Task Put_OutsideOneToTen_Is400_AndANodeTheReaderCannotOpen_Is404()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        foreach (var bad in new[] { 0, 11, -1, 100 })
            Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(w.A.Client, "rtM", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.Client.PutAsJsonAsync("/api/v1/nodes/rtM/rating", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.Client.PutAsJsonAsync("/api/v1/nodes/rtM/rating", new { score = 3.5 })).StatusCode);
        Assert.Null((await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtM")).MyRating);

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(w.A.Client, "nosuchnode", 5)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(w.A.Client, "rtU", 5)).StatusCode); // rt3 is not granted
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Client.DeleteAsync("/api/v1/nodes/rtU/rating")).StatusCode);
        Assert.False(await new FacetQueryTestSupport(w.Factory).WithDbAsync(db => db.UserRatings.AnyAsync(r => r.CatalogNodeId == w.Ids["rtU"])));

        using var anon = w.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAsync(anon, "rtM", 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync("/api/v1/nodes/rtM/rating")).StatusCode);
    }

    [Fact]
    public async Task MyRating_IsTheReadersOwn_OnBrowse_Search_TheNode_AndFavorites()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var aRoot = (await BrowseAsync(w.A.Client, "")).Items.ToDictionary(n => n.Id, n => n.MyRating);
        Assert.Equal(8, aRoot["rtB"]);
        Assert.Equal(7, aRoot["rtA"]);
        Assert.Null(aRoot["rtM"]);
        var bRoot = (await BrowseAsync(w.B.Client, "")).Items.ToDictionary(n => n.Id, n => n.MyRating);
        Assert.Equal(2, bRoot["rtM"]);
        Assert.Null(bRoot["rtB"]);
        Assert.Null(bRoot["rtA"]);

        Assert.Equal(10, (await BrowseAsync(w.A.Client, "?parentId=rtB")).Items.Single(n => n.Id == "rtB1").MyRating);
        Assert.Null((await GetAsync<CatalogNodeDto>(w.B.Client, "/api/v1/nodes/rtB1")).MyRating);

        var aSearch = (await SearchAsync(w.A.Client, "berserk")).Items.ToDictionary(n => n.Id, n => n.MyRating);
        Assert.Equal(8, aSearch["rtB"]);
        Assert.Equal(10, aSearch["rtB1"]);
        Assert.Null(aSearch["rtB2"]);
        Assert.All((await SearchAsync(w.B.Client, "berserk")).Items, n => Assert.Null(n.MyRating));

        // Favorites: a stack (rtB1 + rtB2 starred under rtB) carries its FOLDER's rating, not a member's.
        var favorites = (await FavoritesAsync(w.A.Client, "")).Items.ToDictionary(Key, n => n.MyRating);
        Assert.Equal(8, favorites["stack:rtB"]);
        Assert.Equal(7, favorites["rtA"]);
        Assert.Equal(9, favorites["rtP"]);
        Assert.Null(favorites["rtM"]);
    }

    [Fact]
    public async Task BrowseSortMyRating_HighestFirst_ThenUnratedByName_PagedByOffset_AndPerReader()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var a = await BrowseAsync(w.A.Client, "?sort=myRating");
        Assert.Equal(["rtB", "rtA", "rtM", "rtPl"], a.Items.Select(n => n.Id));
        Assert.Equal(4, a.TotalCount);
        var paged = await AllPagesAsync(c => BrowseAsync(w.A.Client, "?sort=myRating&pageSize=1" + (c is null ? "" : "&cursor=" + Uri.EscapeDataString(c))));
        Assert.Equal(["rtB", "rtA", "rtM", "rtPl"], paged.Select(n => n.Id));

        // Reader B: only B's own score counts - rtM (2) first, then the rest by name (the folder first, as in the name sort).
        Assert.Equal(["rtM", "rtB", "rtA", "rtPl"], (await BrowseAsync(w.B.Client, "?sort=myRating")).Items.Select(n => n.Id));
        // Inside the folder: rtB1 (10) before the unrated rtB2.
        Assert.Equal(["rtB1", "rtB2"], (await BrowseAsync(w.A.Client, "?parentId=rtB&sort=myRating")).Items.Select(n => n.Id));
        // A cursor of the rating sort handed to the name sort is ignored (the first page), as the other sorts' cursors are.
        Assert.Equal("rtB", (await BrowseAsync(w.A.Client, "?pageSize=1&cursor=m:2")).Items.Single().Id);
    }

    [Fact]
    public async Task FavoritesSortMyRating_AndTheRatedFilter_PerReader_AndUnderIncognito()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.Equal(["rtP", "stack:rtB", "rtA", "rtM"], (await FavoritesAsync(w.A.Client, "?sort=myRating")).Items.Select(Key));
        var paged = await AllPagesAsync(c => FavoritesAsync(w.A.Client, "?sort=myRating&pageSize=1" + (c is null ? "" : "&cursor=" + Uri.EscapeDataString(c))));
        Assert.Equal(["rtP", "stack:rtB", "rtA", "rtM"], paged.Select(Key));
        // The page just above a mid-list window (before = prevCursor) under this sort too.
        var landing = await FavoritesAsync(w.A.Client, "?sort=myRating&pageSize=2&cursor=" + Uri.EscapeDataString(
            (await FavoritesAsync(w.A.Client, "?sort=myRating&pageSize=2")).NextCursor!));
        Assert.Equal(["rtA", "rtM"], landing.Items.Select(Key));
        Assert.Equal(["rtP", "stack:rtB"], (await FavoritesAsync(w.A.Client, "?sort=myRating&pageSize=2&before=" + Uri.EscapeDataString(landing.PrevCursor!))).Items.Select(Key));

        Assert.Equal(["rtA", "rtP", "stack:rtB"], (await FavoritesAsync(w.A.Client, "?rating=rated&sort=name")).Items.Select(Key).Order(StringComparer.Ordinal));
        Assert.Equal(["rtM"], (await FavoritesAsync(w.A.Client, "?rating=unrated")).Items.Select(Key));
        Assert.Equal(3, (await FavoritesAsync(w.A.Client, "?rating=rated")).TotalCount);

        // Under Incognito the Private library's rated favorite is not listed - by any sort or filter.
        Assert.Equal(["stack:rtB", "rtA", "rtM"], (await FavoritesAsync(w.A.Incognito, "?sort=myRating")).Items.Select(Key));
        Assert.Equal(2, (await FavoritesAsync(w.A.Incognito, "?rating=rated")).TotalCount);

        // Reader B has no favorites: nothing of A's, even with A's cursor.
        var aCursor = (await FavoritesAsync(w.A.Client, "?sort=myRating&pageSize=1")).NextCursor!;
        Assert.Empty((await FavoritesAsync(w.B.Client, "?sort=myRating&cursor=" + Uri.EscapeDataString(aCursor))).Items);
        Assert.Empty((await FavoritesAsync(w.B.Client, "?rating=rated")).Items);
    }

    [Fact]
    public async Task SearchRatingTerm_IsAMinimum_CombinesWithWordsAndMinusTerms_PerReader_AndUnderIncognito()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        static async Task<string[]> Ids(Task<SearchResultsDto> search) => (await search).Items.Select(n => n.Id).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["rtB", "rtB1", "rtP"], await Ids(SearchAsync(w.A.Client, "rating:4")));
        Assert.Equal(["rtA", "rtB", "rtB1", "rtP"], await Ids(SearchAsync(w.A.Client, "rating:3.5")));
        Assert.Equal(["rtB1"], await Ids(SearchAsync(w.A.Client, "rating:5")));
        Assert.Equal(["rtB", "rtB1"], await Ids(SearchAsync(w.A.Client, "berserk rating:4")));
        Assert.Equal(["rtB2"], await Ids(SearchAsync(w.A.Client, "berserk rating:none")));
        Assert.Equal(["rtP"], await Ids(SearchAsync(w.A.Client, "rating:4 -berserk")));
        Assert.Empty(await Ids(SearchAsync(w.A.Client, "rating:none")));
        // A value the term does not take stays plain words (nothing is named "rating 9").
        Assert.Empty(await Ids(SearchAsync(w.A.Client, "rating:9")));

        // Under Incognito: the Private library's rated archive is not found.
        Assert.Equal(["rtB", "rtB1"], await Ids(SearchAsync(w.A.Incognito, "rating:4")));
        // With a scope word too (is: / library: - the plain search gets the rest).
        Assert.Equal(["rtB", "rtB1"], await Ids(SearchAsync(w.A.Client, "library:\"rt One\" rating:4")));

        // Reader B: only B's own rating.
        Assert.Empty(await Ids(SearchAsync(w.B.Client, "rating:4")));
        Assert.Equal(["rtM"], await Ids(SearchAsync(w.B.Client, "rating:1")));
        Assert.Equal(["rtB", "rtB1", "rtB2"], await Ids(SearchAsync(w.B.Client, "berserk rating:none")));
    }

    [Fact]
    public async Task PrivateLibrary_UnderIncognito_CanBeRated_ButIsNotListed()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w.A.Incognito, "rtP", 4)).StatusCode);
        Assert.Equal(4, (await GetAsync<CatalogNodeDto>(w.A.Incognito, "/api/v1/nodes/rtP")).MyRating); // a direct URL, by design
        Assert.Equal(4, (await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtP")).MyRating);
        Assert.Equal(HttpStatusCode.NoContent, (await w.A.Incognito.DeleteAsync("/api/v1/nodes/rtP/rating")).StatusCode);
        Assert.Null((await GetAsync<CatalogNodeDto>(w.A.Client, "/api/v1/nodes/rtP")).MyRating);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(w.A.Incognito, "rtP", 9)).StatusCode);

        // Not listed under Incognito: browse of the Private library, search, Favorites.
        Assert.Empty((await GetAsync<PageResponse<CatalogNodeDto>>(w.A.Incognito, "/api/v1/libraries/rt2/browse?sort=myRating")).Items);
        Assert.DoesNotContain("rtP", (await SearchAsync(w.A.Incognito, "rating:4")).Items.Select(n => n.Id));
        Assert.DoesNotContain("rtP", (await FavoritesAsync(w.A.Incognito, "?sort=myRating")).Items.Select(n => n.Id));
        // Listed without Incognito.
        Assert.Equal(9, (await GetAsync<PageResponse<CatalogNodeDto>>(w.A.Client, "/api/v1/libraries/rt2/browse?sort=myRating")).Items.Single().MyRating);

        // Reader B can open the library (granted) and sees no rating of A's on it.
        Assert.Null((await GetAsync<PageResponse<CatalogNodeDto>>(w.B.Client, "/api/v1/libraries/rt2/browse")).Items.Single().MyRating);
    }

    [Fact]
    public async Task UnratedNodes_LeaveMyRatingOutOfTheJson()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var json = await w.A.Client.GetStringAsync("/api/v1/nodes/rtM");
        Assert.DoesNotContain("myRating", json, StringComparison.Ordinal);
        Assert.Contains("\"myRating\":8", await w.A.Client.GetStringAsync("/api/v1/nodes/rtB"), StringComparison.Ordinal);
    }
}
