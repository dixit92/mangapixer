namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Ratings;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.53.0 lane S - shared ratings through the real pipeline with three readers, an admin and Incognito sessions: the admin switch
/// (<c>GET</c> / <c>PUT admin/ratings/sharing</c>, off by default, audited), each user's choice (<c>GET</c> / <c>PUT ratings/sharing</c>,
/// on by default, never in the <c>library-preferences</c> blob), <c>CatalogNodeDto.ServerRating</c> on the single node only, the browse
/// sort <c>serverRating</c> (only while the switch is on), and the rules: a user who stops sharing, a library its RATER marks Private and
/// the switch going off each withdraw at once (the cached average included); nothing is deleted.
/// </summary>
public sealed class SharedRatingsHttpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record World(MangaPixerWebApplicationFactory Factory, HttpClient Admin, AccessReader A, AccessReader B, AccessReader C);

    /// <summary>
    /// Library "sr1": folder "Berserk" (srB) with "Berserk 01" (srB1); root archives "Akira" (srA), "Monster" (srM), "Pluto" (srPl).
    /// Library "sr2": "Private Tale" (srP). Library "sr3" granted to nobody: "Hidden Tale" (srU). Readers A, B, C open sr1 and sr2.
    /// Ratings - A: srA 8, srB 6, srP 10, srU 9 (written directly: A cannot open sr3). B: srA 4, srM 10, srP 2. C: srA 6, srM 6.
    /// So srA = 6.0 of 3, srM = 8.0 of 2, srB = 6.0 of 1, srP = 6.0 of 2, srPl none.
    /// </summary>
    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib1 = await s.LibraryAsync("sr1", "sr One");
        var lib2 = await s.LibraryAsync("sr2", "sr Two");
        var lib3 = await s.LibraryAsync("sr3", "sr Three");
        await s.WithDbAsync(async db =>
        {
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
                return node.Id;
            }

            var berserk = await Node("srB", lib1, null, CatalogNodeKind.Folder, "Berserk");
            await Node("srB1", lib1, berserk, CatalogNodeKind.Archive, "Berserk 01");
            await Node("srA", lib1, null, CatalogNodeKind.Archive, "Akira");
            await Node("srM", lib1, null, CatalogNodeKind.Archive, "Monster");
            await Node("srPl", lib1, null, CatalogNodeKind.Archive, "Pluto");
            await Node("srP", lib2, null, CatalogNodeKind.Archive, "Private Tale");
            await Node("srU", lib3, null, CatalogNodeKind.Archive, "Hidden Tale");
        });

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "srreadera", [lib1, lib2]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "srreaderb", [lib1, lib2]);
        var c = await AccessTestUsers.ReaderAsync(factory, admin, "srreaderc", [lib1, lib2]);
        foreach (var (client, node, score) in new[]
        {
            (a.Client, "srA", 8), (a.Client, "srB", 6), (a.Client, "srP", 10),
            (b.Client, "srA", 4), (b.Client, "srM", 10), (b.Client, "srP", 2),
            (c.Client, "srA", 6), (c.Client, "srM", 6),
        })
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/v1/nodes/{node}/rating", new { score })).StatusCode);
        await s.WithDbAsync(async db =>
        {
            var hidden = await db.CatalogNodes.Where(n => n.PublicId == "srU").Select(n => n.Id).SingleAsync();
            db.UserRatings.Add(new UserRatingEntity { UserId = a.UserId, CatalogNodeId = hidden, Score = 9, RatedAt = T0 });
            await db.SaveChangesAsync();
        });
        return new World(factory, admin, a, b, c);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static Task<CatalogNodeDto> NodeAsync(HttpClient client, string id) => GetAsync<CatalogNodeDto>(client, "/api/v1/nodes/" + id);

    private static async Task SwitchAsync(World w, bool enabled)
    {
        using var response = await w.Admin.PutAsJsonAsync("/api/v1/admin/ratings/sharing", new { enabled });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(enabled, (await response.Content.ReadFromJsonAsync<AdminRatingSharingDto>(TestJson.Web))!.Enabled);
    }

    private static async Task ShareAsync(AccessReader reader, bool share) =>
        Assert.Equal(HttpStatusCode.NoContent, (await reader.Client.PutAsJsonAsync("/api/v1/ratings/sharing", new { share })).StatusCode);

    private static void AssertAverage(CatalogNodeDto node, double average, int count)
    {
        Assert.NotNull(node.ServerRating);
        Assert.Equal(average, node.ServerRating!.Average, 2);
        Assert.Equal(count, node.ServerRating.Count);
    }

    private static async Task<List<string>> ServerSortAsync(HttpClient client, int pageSize)
    {
        var ids = new List<string>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var page = await GetAsync<PageResponse<CatalogNodeDto>>(client,
                $"/api/v1/libraries/sr1/browse?sort=serverRating&pageSize={pageSize}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            ids.AddRange(page.Items.Select(n => n.Id));
            Assert.All(page.Items, n => Assert.Null(n.ServerRating)); // lists never carry the average
            if (!page.HasMore)
                return ids;
            cursor = page.NextCursor;
        }
        throw new InvalidOperationException("paging did not end");
    }

    [Fact]
    public async Task ByDefault_TheSwitchIsOff_NoAverageAnywhere_TheUserChoiceIsOn_AndTheSortFallsBackToName()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        Assert.False((await GetAsync<AdminRatingSharingDto>(w.Admin, "/api/v1/admin/ratings/sharing")).Enabled);
        var mine = await GetAsync<RatingSharingDto>(w.A.Client, "/api/v1/ratings/sharing");
        Assert.True(mine.Share);
        Assert.False(mine.ServerEnabled);

        foreach (var reader in new[] { w.A, w.B })
        {
            Assert.Null((await NodeAsync(reader.Client, "srA")).ServerRating);
            Assert.Null((await NodeAsync(reader.Incognito, "srA")).ServerRating);
            Assert.DoesNotContain("serverRating", await reader.Client.GetStringAsync("/api/v1/nodes/srM"), StringComparison.OrdinalIgnoreCase);
        }
        // The sort is not offered while the switch is off: browse answers the name order (folders first).
        Assert.Equal(["srB", "srA", "srM", "srPl"], await ServerSortAsync(w.B.Client, 2));
    }

    [Fact]
    public async Task SwitchOn_EveryReaderSeesTheAverageAndCount_IncognitoToo_OnTheSingleNodeOnly_AndTheChangeIsAudited()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        await SwitchAsync(w, true);
        await SwitchAsync(w, true); // the same state again: no second audit row

        Assert.True((await GetAsync<AdminRatingSharingDto>(w.Admin, "/api/v1/admin/ratings/sharing")).Enabled);
        Assert.True((await GetAsync<RatingSharingDto>(w.B.Client, "/api/v1/ratings/sharing")).ServerEnabled);
        foreach (var client in new[] { w.A.Client, w.B.Client, w.B.Incognito, w.C.Client, w.Admin })
        {
            AssertAverage(await NodeAsync(client, "srA"), 6.0, 3);
            AssertAverage(await NodeAsync(client, "srM"), 8.0, 2);
            AssertAverage(await NodeAsync(client, "srB"), 6.0, 1); // no rollup: the folder's own rating, not its archive's
            Assert.Null((await NodeAsync(client, "srPl")).ServerRating);
            Assert.Null((await NodeAsync(client, "srB1")).ServerRating);
        }
        // A rating never opens a node: the hidden library still answers 404, average or not.
        Assert.Equal(HttpStatusCode.NotFound, (await w.B.Client.GetAsync("/api/v1/nodes/srU")).StatusCode);

        // Lists never carry it: browse, search.
        Assert.All((await GetAsync<PageResponse<CatalogNodeDto>>(w.B.Client, "/api/v1/libraries/sr1/browse")).Items, n => Assert.Null(n.ServerRating));
        Assert.All((await GetAsync<SearchResultsDto>(w.B.Client, "/api/v1/search?q=akira")).Items, n => Assert.Null(n.ServerRating));

        var audit = await new FacetQueryTestSupport(w.Factory).WithDbAsync(db =>
            db.AuditEvents.Where(e => e.Action.StartsWith("ratings.sharing")).Select(e => e.Action).ToListAsync());
        Assert.Equal([RatingAuditActions.SharingEnable], audit);
    }

    [Fact]
    public async Task AUserWhoStopsSharing_IsWithdrawnAtOnce_EvenFromACachedAverage_AndCanShareAgain()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        await SwitchAsync(w, true);
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 6.0, 3); // computed and cached

        await ShareAsync(w.A, false);
        Assert.False((await GetAsync<RatingSharingDto>(w.A.Client, "/api/v1/ratings/sharing")).Share);
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 5.0, 2); // (4 + 6) / 2 - A's 8 is out on the very next request
        AssertAverage(await NodeAsync(w.A.Client, "srA"), 5.0, 2); // A's own rating does not count for A either
        Assert.Null((await NodeAsync(w.B.Client, "srB")).ServerRating); // only A rated it
        Assert.Equal(8, (await NodeAsync(w.A.Client, "srA")).MyRating); // the rating itself is kept

        await ShareAsync(w.A, true);
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 6.0, 3);

        // A changed rating moves the average at once too.
        Assert.Equal(HttpStatusCode.NoContent, (await w.C.Client.PutAsJsonAsync("/api/v1/nodes/srA/rating", new { score = 9 })).StatusCode);
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 7.0, 3);
        Assert.Equal(HttpStatusCode.NoContent, (await w.C.Client.DeleteAsync("/api/v1/nodes/srA/rating")).StatusCode);
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 6.0, 2);
    }

    [Fact]
    public async Task ARatingInTheRatersPrivateLibrary_NeverCounts_ForAnyViewer_AndMarkingItPrivateWithdrawsAtOnce()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        await SwitchAsync(w, true);
        AssertAverage(await NodeAsync(w.B.Client, "srP"), 6.0, 2); // A's 10 + B's 2, cached

        // A marks sr2 Private (Settings): A's rating there stops counting for everyone, on the next request.
        Assert.Equal(HttpStatusCode.NoContent,
            (await w.A.Client.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = ["sr2"] })).StatusCode);
        foreach (var client in new[] { w.B.Client, w.B.Incognito, w.C.Client, w.A.Client, w.A.Incognito })
            AssertAverage(await NodeAsync(client, "srP"), 2.0, 1);
        // A's ratings in the other library still count (Private is per library and per rater).
        AssertAverage(await NodeAsync(w.B.Client, "srA"), 6.0, 3);

        // Sharing on or off changes nothing about the Private rule.
        await ShareAsync(w.A, true);
        AssertAverage(await NodeAsync(w.C.Client, "srP"), 2.0, 1);

        Assert.Equal(HttpStatusCode.NoContent,
            (await w.A.Client.PutAsJsonAsync("/api/v1/reading/private-libraries", new SetPrivateLibrariesRequest { LibraryIds = [] })).StatusCode);
        AssertAverage(await NodeAsync(w.C.Client, "srP"), 6.0, 2);
    }

    [Fact]
    public async Task TheServerRatingSort_OrdersByAverageThenCount_PagesByOffset_AndStopsWithTheSwitch_WhichDeletesNothing()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        await SwitchAsync(w, true);

        // srM 8.0 (2), srA 6.0 (3), srB 6.0 (1), srPl none - the same for every viewer, Incognito included.
        foreach (var client in new[] { w.A.Client, w.B.Client, w.B.Incognito })
        {
            Assert.Equal(["srM", "srA", "srB", "srPl"], await ServerSortAsync(client, 2));
            Assert.Equal(["srM", "srA", "srB", "srPl"], await ServerSortAsync(client, 50));
        }

        await ShareAsync(w.B, false); // srM = 6.0 (1), srA = 7.0 (2), srB = 6.0 (1)
        Assert.Equal(["srA", "srB", "srM", "srPl"], await ServerSortAsync(w.C.Client, 3));

        var before = await new FacetQueryTestSupport(w.Factory).WithDbAsync(db => db.UserRatings.CountAsync());
        await SwitchAsync(w, false);
        Assert.Equal(["srB", "srA", "srM", "srPl"], await ServerSortAsync(w.C.Client, 2));
        Assert.Null((await NodeAsync(w.C.Client, "srA")).ServerRating);
        Assert.Equal(before, await new FacetQueryTestSupport(w.Factory).WithDbAsync(db => db.UserRatings.CountAsync()));
        var audit = await new FacetQueryTestSupport(w.Factory).WithDbAsync(db =>
            db.AuditEvents.Where(e => e.Action.StartsWith("ratings.sharing")).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal([RatingAuditActions.SharingEnable, RatingAuditActions.SharingDisable], audit);
    }

    [Fact]
    public async Task TheUserChoice_IsNotPartOfTheLibraryPreferencesBlob_WhichCarriesTheRecommendationsRow()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var prefs = await GetAsync<LibraryViewPreferencesDto>(w.A.Client, "/api/v1/reading/library-preferences");
        Assert.True(prefs.ShowRecommendationsHomeRow); // on by default
        var raw = await w.A.Client.GetStringAsync("/api/v1/reading/library-preferences");
        Assert.DoesNotContain("shareRatings", raw, StringComparison.OrdinalIgnoreCase);

        // A stale or hostile blob that names the privacy choice changes nothing about it.
        using (var put = await w.A.Client.PutAsJsonAsync("/api/v1/reading/library-preferences",
            new { viewMode = "grid", density = "comfortable", sort = "name", shareRatings = false, showRecommendationsHomeRow = false }))
            Assert.True(put.IsSuccessStatusCode, put.StatusCode.ToString());
        Assert.True((await GetAsync<RatingSharingDto>(w.A.Client, "/api/v1/ratings/sharing")).Share);
        Assert.False((await GetAsync<LibraryViewPreferencesDto>(w.A.Client, "/api/v1/reading/library-preferences")).ShowRecommendationsHomeRow);

        // A client that predates the field keeps the row on.
        using (var old = await w.B.Client.PutAsJsonAsync("/api/v1/reading/library-preferences", new { viewMode = "grid", density = "comfortable", sort = "name" }))
            Assert.True(old.IsSuccessStatusCode);
        Assert.True((await GetAsync<LibraryViewPreferencesDto>(w.B.Client, "/api/v1/reading/library-preferences")).ShowRecommendationsHomeRow);

        // And the dedicated route never touches the blob.
        await ShareAsync(w.A, false);
        Assert.False((await GetAsync<LibraryViewPreferencesDto>(w.A.Client, "/api/v1/reading/library-preferences")).ShowRecommendationsHomeRow);
        Assert.False((await GetAsync<RatingSharingDto>(w.A.Incognito, "/api/v1/ratings/sharing")).Share);
    }

    [Fact]
    public async Task TheRoutes_RefuseAnonymousCallers_TheAdminSwitchRefusesReaders_AndAnEmptyBodyIs400()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        using var anon = w.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/ratings/sharing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/v1/ratings/sharing", new { share = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/admin/ratings/sharing")).StatusCode);
        foreach (var client in new[] { w.A.Client, w.A.Incognito })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/ratings/sharing")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/admin/ratings/sharing", new { enabled = true })).StatusCode);
        }
        Assert.False((await GetAsync<AdminRatingSharingDto>(w.Admin, "/api/v1/admin/ratings/sharing")).Enabled);

        using var empty = new StringContent("null", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await w.A.Client.PutAsync("/api/v1/ratings/sharing", empty)).StatusCode);
        Assert.True((await GetAsync<RatingSharingDto>(w.A.Client, "/api/v1/ratings/sharing")).Share);
    }
}
