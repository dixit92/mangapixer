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
/// 1.51.0 lane F, through the real pipeline with two readers and an Incognito session: <c>GET favorites</c>'s new params - <c>before</c>
/// (backward paging, every window that does not start the list carries <c>prevCursor</c>), <c>sort=recentlyRead</c> and
/// <c>group=library</c> (with <c>favoriteGroups</c>) - and the new route <c>GET favorites/jump-index</c>. Every one of them lists nothing
/// of the Private library under Incognito (not its entries, not its group, not its id), and a second reader sees nothing of the first's
/// favorites under any of them, also when handed the first reader's cursors.
/// </summary>
public sealed class FavoritesRailGroupsHttpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record World(MangaPixerWebApplicationFactory Factory, AccessReader A, AccessReader B);

    /// <summary>
    /// Library "rg One" (rg1): folder "Maple" (rgM, starred) with rgM1 + rgM2 starred (a stack), rgM1 read at day 4; root archives "Alpha"
    /// (rgA, starred, in progress at day 6) and "Zulu" (rgZ, starred, never read). Library "rg Two" (rg2, Private for reader A): archive
    /// "Private One" (rgP, starred, read at day 9). Reader A's entries: rgM, the stack, rgA, rgZ, rgP. Reader B: nothing.
    /// </summary>
    private static async Task<World> SeedAsync()
    {
        var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);
        var lib1 = await s.LibraryAsync("rg1", "rg One");
        var lib2 = await s.LibraryAsync("rg2", "rg Two");

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

            var maple = await Node("rgM", lib1, null, CatalogNodeKind.Folder, "Maple");
            await Node("rgM1", lib1, maple, CatalogNodeKind.Archive, "Maple 01");
            await Node("rgM2", lib1, maple, CatalogNodeKind.Archive, "Maple 02");
            await Node("rgA", lib1, null, CatalogNodeKind.Archive, "Alpha");
            await Node("rgZ", lib1, null, CatalogNodeKind.Archive, "Zulu");
            await Node("rgP", lib2, null, CatalogNodeKind.Archive, "Private One");
            return map;
        });

        var a = await AccessTestUsers.ReaderAsync(factory, admin, "rgreadera", [lib1, lib2], privateLibraryIds: [lib2]);
        var b = await AccessTestUsers.ReaderAsync(factory, admin, "rgreaderb", [lib1, lib2]);

        await s.WithDbAsync(async db =>
        {
            var minute = 0;
            foreach (var id in new[] { "rgM", "rgM1", "rgM2", "rgA", "rgZ", "rgP" })
                db.Favorites.Add(new FavoriteEntity { UserId = a.UserId, CatalogNodeId = ids[id], CreatedAt = T0.AddMinutes(minute++) });
            db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = ids["rgM1"], MarkedAt = T0.AddDays(4), Source = "manual" });
            db.ReadMarks.Add(new ReadMarkEntity { UserId = a.UserId, ItemId = ids["rgP"], MarkedAt = T0.AddDays(9), Source = "manual" });
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = a.UserId,
                ItemId = ids["rgA"],
                State = (int)ReadingState.InProgress,
                Ordinal = 2,
                EntryKey = "e",
                LastMutationId = "rg-a",
                UpdatedAt = T0.AddDays(6),
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

    private static async Task<FavoritesJumpIndexDto> RailAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync("/api/v1/favorites/jump-index" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FavoritesJumpIndexDto>(TestJson.Web))!;
    }

    private static string Key(CatalogNodeDto n) => n.FavoriteStackCount is int ? $"stack:{n.Id}" : n.Id;

    private static string Esc(string value) => Uri.EscapeDataString(value);

    [Fact]
    public async Task Before_PagesUpFromAMidListWindow_EverySortAndGrouping()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        foreach (var q in new[] { "?sort=name", "?sort=name&direction=desc", "?sort=recentlyRead", "?sort=recentlyAdded", "?group=library",
                     "?sort=name&group=library", "?sort=recentlyRead&group=library" })
        {
            var whole = (await PageAsync(w.A.Client, q)).Items.Select(Key).ToList();
            Assert.Equal(5, whole.Count);
            var first = await PageAsync(w.A.Client, q + "&pageSize=3");
            Assert.False(first.HasPrevious);
            var landing = await PageAsync(w.A.Client, q + "&pageSize=2&cursor=" + Esc(first.NextCursor!));
            Assert.Equal(whole.Skip(3), landing.Items.Select(Key));
            Assert.True(landing.HasPrevious);
            var above = await PageAsync(w.A.Client, q + "&pageSize=2&before=" + Esc(landing.PrevCursor!));
            Assert.Equal(whole.Skip(1).Take(2), above.Items.Select(Key));
            Assert.True(above.HasPrevious);
            var top = await PageAsync(w.A.Client, q + "&pageSize=2&before=" + Esc(above.PrevCursor!));
            Assert.Equal(whole.Take(1), top.Items.Select(Key));
            Assert.False(top.HasPrevious);
            Assert.Null(top.PrevCursor);
        }
    }

    [Fact]
    public async Task RecentlyRead_OrdersByTheReadersOwnActivity_NeverReadLast()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        // Private One (day 9), Alpha (6), Maple and its stack (4, the read Maple 01 - the folder by its subtree, the stack by its member;
        // a tie by name: the folder before its stack), then never read: Zulu.
        Assert.Equal(new[] { "rgP", "rgA", "rgM", "stack:rgM", "rgZ" }, (await PageAsync(w.A.Client, "?sort=recentlyRead")).Items.Select(Key));
        Assert.Equal(new[] { "rgA", "rgM", "stack:rgM", "rgZ" }, (await PageAsync(w.A.Incognito, "?sort=recentlyRead")).Items.Select(Key));
    }

    [Fact]
    public async Task GroupByLibrary_NamesEachListedLibraryWithItsCount_AndIncognitoNamesNoPrivateLibrary()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var normal = await PageAsync(w.A.Client, "?sort=name&group=library");
        Assert.Equal(new[] { ("rg1", "rg One", 4), ("rg2", "rg Two", 1) }, normal.FavoriteGroups!.Select(g => (g.LibraryId, g.Name, g.Count)));
        Assert.Equal(new[] { "rgM", "stack:rgM", "rgA", "rgZ", "rgP" }, normal.Items.Select(Key));
        Assert.StartsWith("fl:", (await PageAsync(w.A.Client, "?sort=name&group=library&pageSize=1")).NextCursor, StringComparison.Ordinal);

        var raw = await w.A.Incognito.GetStringAsync("/api/v1/favorites?sort=name&group=library");
        Assert.DoesNotContain("rg2", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("rg Two", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("rgP", raw, StringComparison.Ordinal);
        var incognito = await PageAsync(w.A.Incognito, "?sort=name&group=library");
        Assert.Equal(new[] { ("rg1", 4) }, incognito.FavoriteGroups!.Select(g => (g.LibraryId, g.Count)));
        Assert.Empty((await PageAsync(w.A.Incognito, "?group=library&libraryId=rg2")).FavoriteGroups!);

        // Without group=library there are no groups (the home row's answer is unchanged).
        Assert.Null((await PageAsync(w.A.Client, string.Empty)).FavoriteGroups);
        Assert.Null((await PageAsync(w.A.Client, "?group=other")).FavoriteGroups);
    }

    [Fact]
    public async Task JumpIndex_BucketsLandOnTheirEntries_FollowTheFilters_HiddenWhileGrouped()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        var rail = await RailAsync(w.A.Client, string.Empty);
        // Two folder entries (Maple, its stack): the Folders chip; the archives A, P, Z.
        Assert.Equal(new[] { ("Folders", 2), ("A", 1), ("P", 1), ("Z", 1) }, rail.Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal("asc", rail.Direction);
        foreach (var bucket in rail.Buckets)
        {
            var page = await PageAsync(w.A.Client, "?sort=name&pageSize=1" + (bucket.FirstCursor is null ? "" : "&cursor=" + Esc(bucket.FirstCursor)));
            Assert.Equal(bucket.FirstNodeId, page.Items[0].Id);
        }
        var desc = await RailAsync(w.A.Client, "?direction=desc");
        Assert.Equal(new[] { "Z", "P", "A", "Folders" }, desc.Buckets.Select(b => b.Label));
        Assert.Equal("desc", desc.Direction);
        var aLanding = await PageAsync(w.A.Client, "?sort=name&direction=desc&pageSize=5&cursor=" + Esc(desc.Buckets[2].FirstCursor!));
        Assert.Equal(new[] { "rgA", "stack:rgM", "rgM" }, aLanding.Items.Select(Key));
        Assert.True(aLanding.HasPrevious);

        // Unread archives: Zulu only (the stack's folder is half read, Alpha in progress, Private One read).
        Assert.Equal(new[] { ("Z", 1) }, (await RailAsync(w.A.Client, "?kind=archives&readState=unread")).Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal(new[] { "Folders", "A", "Z" }, (await RailAsync(w.A.Client, "?libraryId=rg1")).Buckets.Select(b => b.Label));
        Assert.Empty((await RailAsync(w.A.Client, "?group=library")).Buckets);
    }

    [Fact]
    public async Task JumpIndex_UnderIncognito_NamesNothingOfThePrivateLibrary_EvenWhenAskedForIt()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;

        foreach (var q in new[] { string.Empty, "?direction=desc", "?libraryId=rg2", "?kind=archives" })
        {
            var raw = await w.A.Incognito.GetStringAsync("/api/v1/favorites/jump-index" + q);
            Assert.DoesNotContain("rgP", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("private one", raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Empty((await RailAsync(w.A.Incognito, "?libraryId=rg2")).Buckets);
        Assert.Equal(new[] { "Folders", "A", "Z" }, (await RailAsync(w.A.Incognito, string.Empty)).Buckets.Select(b => b.Label));
        Assert.Contains("P", (await RailAsync(w.A.Client, string.Empty)).Buckets.Select(b => b.Label));
    }

    [Fact]
    public async Task ASecondReader_SeesNothingOfTheFirstReadersFavorites_UnderTheNewParams_NorWithTheirCursors()
    {
        var w = await SeedAsync();
        await using var _ = w.Factory;
        var aName = await PageAsync(w.A.Client, "?sort=name&pageSize=2");
        var aGrouped = await PageAsync(w.A.Client, "?group=library&pageSize=2");
        var aRead = await PageAsync(w.A.Client, "?sort=recentlyRead&pageSize=2");

        foreach (var q in new[]
                 {
                     "?sort=recentlyRead", "?group=library", "?sort=name&group=library", "?sort=recentlyRead&group=library&libraryId=rg1",
                     "?sort=name&cursor=" + Esc(aName.NextCursor!), "?sort=name&before=" + Esc(aName.NextCursor!),
                     "?group=library&cursor=" + Esc(aGrouped.NextCursor!), "?group=library&before=" + Esc(aGrouped.NextCursor!),
                     "?sort=recentlyRead&cursor=" + Esc(aRead.NextCursor!),
                 })
        {
            var page = await PageAsync(w.B.Client, q);
            Assert.Equal(0, page.TotalCount);
            Assert.Empty(page.Items);
            Assert.False(page.HasPrevious);
            Assert.True(page.FavoriteGroups is null || page.FavoriteGroups.Count == 0);
        }
        foreach (var q in new[] { string.Empty, "?direction=desc", "?libraryId=rg1", "?libraryId=rg2" })
            Assert.Empty((await RailAsync(w.B.Client, q)).Buckets);
    }

    [Fact]
    public async Task TheNewRoute_NeedsASignedInUser()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync("/api/v1/favorites/jump-index");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
