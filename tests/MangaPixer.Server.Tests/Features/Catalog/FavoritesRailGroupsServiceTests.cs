namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using SortKey = com.lifepixer.mangapixer.Core.Ordering.SortKey;

/// <summary>
/// 1.51.0 lane F (service-with-DB, real file-backed SQLite): the Favorites list's backward paging (every sort, grouped or not), the
/// Recently read sort, group by library and the Favorites jump index.
/// <para>
/// World - library "beta Lib" (created FIRST, so a lower id than "Alpha Lib": the picker's order is by name, not id): starred folder
/// "Maple" with archives "Maple 01" + "Maple 02" starred (a stack) and "Maple 03" not starred; root archives "Zulu" and "apple" starred.
/// "Alpha Lib": starred folder "Kiwi" with "Kiwi 01" not starred; root archives "Banana" and "cherry" starred. "Pvt Lib" (the viewer's
/// Private library): root archive "Pear" starred. Reading: Maple 03 (not starred) read at day 30 - the Maple FOLDER's recency (its whole
/// subtree) but not its stack's; Pear in progress at day 20; Zulu read at day 9; Kiwi 01 in progress at day 7 (the Kiwi folder's);
/// Maple 01 in progress at day 5 (the stack's: its most recently read starred member); Banana read at day 3; apple, cherry, Maple 02 never.
/// </para>
/// </summary>
public sealed class FavoritesRailGroupsServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public FavoritesRailGroupsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-favrail-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(DatabaseInitialization.BuildConnectionString(Path.Combine(_tempDir, "favrail.db")))
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private sealed record World(MangaPixerDbContext Db, CatalogBrowseService Service, long UserId, Dictionary<string, long> Ids,
        string AlphaId, string BetaId, string PrivateId);

    private async Task<World> SeedAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var user = new UserEntity
        {
            PublicId = "favrailuser",
            UserName = "favrail",
            NormalizedUserName = "FAVRAIL",
            IsActive = true,
            IsAdmin = true,
            PasswordHash = "hash",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = T0,
        };
        db.Users.Add(user);
        var beta = new LibraryEntity { PublicId = "frbeta", DisplayName = "beta Lib", RootPath = "/synthetic/fr-b", CreatedAt = T0 };
        var alpha = new LibraryEntity { PublicId = "fralpha", DisplayName = "Alpha Lib", RootPath = "/synthetic/fr-a", CreatedAt = T0 };
        var pvt = new LibraryEntity { PublicId = "frpvt", DisplayName = "Pvt Lib", RootPath = "/synthetic/fr-p", CreatedAt = T0 };
        db.Libraries.Add(beta);
        await db.SaveChangesAsync();
        db.Libraries.Add(alpha);
        await db.SaveChangesAsync();
        db.Libraries.Add(pvt);
        await db.SaveChangesAsync();
        db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = user.Id, LibraryId = pvt.Id, MarkedAt = T0 });

        var ids = new Dictionary<string, long>();
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
            return ids[id] = node.Id;
        }

        var maple = await Node("frM", beta.Id, null, CatalogNodeKind.Folder, "Maple", 1, latestDay: 12);
        await Node("frM1", beta.Id, maple, CatalogNodeKind.Archive, "Maple 01", 4);
        await Node("frM2", beta.Id, maple, CatalogNodeKind.Archive, "Maple 02", 6);
        await Node("frM3", beta.Id, maple, CatalogNodeKind.Archive, "Maple 03", 12);
        await Node("frZ", beta.Id, null, CatalogNodeKind.Archive, "Zulu", 2);
        await Node("fra", beta.Id, null, CatalogNodeKind.Archive, "apple", 8);
        var kiwi = await Node("frK", alpha.Id, null, CatalogNodeKind.Folder, "Kiwi", 3, latestDay: 10);
        await Node("frK1", alpha.Id, kiwi, CatalogNodeKind.Archive, "Kiwi 01", 10);
        await Node("frB", alpha.Id, null, CatalogNodeKind.Archive, "Banana", 5);
        await Node("frc", alpha.Id, null, CatalogNodeKind.Archive, "cherry", 7);
        await Node("frP", pvt.Id, null, CatalogNodeKind.Archive, "Pear", 9);

        var minute = 0;
        foreach (var id in new[] { "frM", "frM1", "frM2", "frZ", "fra", "frK", "frB", "frc", "frP" })
            db.Favorites.Add(new FavoriteEntity { UserId = user.Id, CatalogNodeId = ids[id], CreatedAt = T0.AddMinutes(minute++) });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = ids["frM3"], MarkedAt = T0.AddDays(30), Source = "manual" });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = ids["frZ"], MarkedAt = T0.AddDays(9), Source = "manual" });
        db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = ids["frB"], MarkedAt = T0.AddDays(3), Source = "manual" });
        foreach (var (item, day) in new[] { ("frP", 20), ("frK1", 7), ("frM1", 5) })
        {
            db.ReadingProgress.Add(new ReadingProgressEntity
            {
                UserId = user.Id,
                ItemId = ids[item],
                State = (int)ReadingState.InProgress,
                Ordinal = 2,
                EntryKey = "e",
                LastMutationId = "fr-" + item,
                UpdatedAt = T0.AddDays(day),
            });
        }
        await db.SaveChangesAsync();
        return new World(db, new CatalogBrowseService(db, new LibraryAuthorizationService(db)), user.Id, ids, alpha.PublicId, beta.PublicId, pvt.PublicId);
    }

    /// <summary>An entry's identity: a stack shares its folder's node id, so it is told apart by its count.</summary>
    private static string Key(CatalogNodeDto n) => n.FavoriteStackCount is int c ? $"stack:{n.Id}" : n.Id;

    private static FavoritesQuery Query(string sort, string direction = "asc", bool grouped = false) =>
        new(Sort: sort, Direction: direction == "desc" ? SortDirection.Descending : SortDirection.Ascending, GroupByLibrary: grouped);

    private static Task<PageResponse<CatalogNodeDto>> PageAsync(World w, FavoritesQuery q, string? cursor = null, string? before = null,
        int pageSize = 100, bool incognito = false) =>
        w.Service.ListFavoritesAsync(w.UserId, q, cursor, before, pageSize, incognito, CancellationToken.None);

    [Theory]
    [InlineData("favorited", "asc", false)]
    [InlineData("favorited", "asc", true)]
    [InlineData("name", "asc", false)]
    [InlineData("name", "asc", true)]
    [InlineData("name", "desc", false)]
    [InlineData("name", "desc", true)]
    [InlineData("recentlyAdded", "asc", false)]
    [InlineData("recentlyAdded", "asc", true)]
    [InlineData("recentlyUpdated", "asc", false)]
    [InlineData("recentlyUpdated", "asc", true)]
    [InlineData("recentlyRead", "asc", false)]
    [InlineData("recentlyRead", "asc", true)]
    public async Task EverySort_PagesForwardAndBackFromEveryPosition_EachEntryOnce_AcrossGroupBoundaries(string sort, string direction, bool grouped)
    {
        var w = await SeedAsync();
        await using var db = w.Db;
        var q = Query(sort, direction, grouped);
        var all = await PageAsync(w, q);
        var order = all.Items.Select(Key).ToList();
        Assert.Equal(8, order.Count); // 9 favorites, Maple 01 + 02 one stack; Pear's Private library included (no Incognito)
        Assert.False(all.HasPrevious);
        Assert.Null(all.PrevCursor);

        // The cursor after each entry, by walking one entry per page.
        var after = new List<string>();
        string? cursor = null;
        for (var i = 0; i < order.Count - 1; i++)
        {
            var one = await PageAsync(w, q, cursor, pageSize: 1);
            Assert.Equal(order[i], Key(Assert.Single(one.Items)));
            Assert.Equal(i > 0, one.HasPrevious);
            cursor = one.NextCursor!;
            after.Add(cursor);
        }

        // From a window that starts at every position k: forward in pages of 2 lists order[k..]; backward from it in pages of 2 lists
        // order[..k] - so a jump landing anywhere scrolls both ways, over the library boundaries too.
        for (var k = 1; k < order.Count; k++)
        {
            var landing = await PageAsync(w, q, after[k - 1], pageSize: 2);
            Assert.True(landing.HasPrevious);
            var forward = landing.Items.Select(Key).ToList();
            var next = landing.NextCursor;
            while (next is not null)
            {
                var page = await PageAsync(w, q, next, pageSize: 2);
                forward.AddRange(page.Items.Select(Key));
                next = page.NextCursor;
            }
            Assert.Equal(order.Skip(k), forward);

            var backward = new List<string>();
            var prev = landing.PrevCursor;
            for (var guard = 0; prev is not null && guard < 20; guard++)
            {
                var page = await PageAsync(w, q, before: prev, pageSize: 2);
                Assert.True(page.HasMore); // the client's window sits below a backward page
                backward.InsertRange(0, page.Items.Select(Key));
                Assert.Equal(page.HasPrevious, page.PrevCursor is not null);
                prev = page.PrevCursor;
            }
            Assert.Equal(order.Take(k), backward);
        }
    }

    [Fact]
    public async Task RecentlyRead_NewestActivityFirst_AFolderBySubtree_AStackByItsNewestStarredMember_NeverReadLastByName()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        var page = await PageAsync(w, Query("recentlyRead"));

        // Maple folder (day 30, its unstarred Maple 03), Pear (20), Zulu (9), Kiwi folder (7, its unstarred Kiwi 01), the Maple stack (5:
        // Maple 01 - NOT the folder's day 30), Banana (3), then never read by name: apple, cherry.
        Assert.Equal(new[] { "frM", "frP", "frZ", "frK", "stack:frM", "frB", "fra", "frc" }, page.Items.Select(Key));
        Assert.Equal(8, page.TotalCount);
    }

    [Fact]
    public async Task RecentlyRead_UnderIncognito_LeavesThePrivateLibraryOut_AsBrowse()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        var page = await PageAsync(w, Query("recentlyRead"), incognito: true);

        Assert.Equal(new[] { "frM", "frZ", "frK", "stack:frM", "frB", "fra", "frc" }, page.Items.Select(Key));
    }

    [Fact]
    public async Task GroupByLibrary_PickerOrder_ThenTheSort_WithEachLibrarysCount()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        var byName = await PageAsync(w, Query("name", grouped: true));
        var byRead = await PageAsync(w, Query("recentlyRead", grouped: true));

        // "Alpha Lib" < "beta Lib" < "Pvt Lib" case-insensitively ("beta Lib" has the lower id).
        Assert.Equal(new[] { "frK", "frB", "frc", "frM", "stack:frM", "fra", "frZ", "frP" }, byName.Items.Select(Key));
        Assert.Equal(new[] { "frK", "frB", "frc", "frM", "frZ", "stack:frM", "fra", "frP" }, byRead.Items.Select(Key));
        Assert.Equal(new[] { (w.AlphaId, "Alpha Lib", 3), (w.BetaId, "beta Lib", 4), (w.PrivateId, "Pvt Lib", 1) },
            byName.FavoriteGroups!.Select(g => (g.LibraryId, g.Name, g.Count)));
        Assert.Equal(byName.TotalCount, byName.FavoriteGroups!.Sum(g => g.Count));
        Assert.All(byName.Items, n => Assert.Contains(byName.FavoriteGroups!, g => g.LibraryId == n.LibraryId));
    }

    [Fact]
    public async Task GroupByLibrary_CountsFollowTheFilters_AndIncognitoNamesNoPrivateGroup()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        var archives = await PageAsync(w, Query("name", grouped: true) with { Kind = FavoriteKindFilter.Archives });
        var incognito = await PageAsync(w, Query("name", grouped: true), incognito: true);
        var oneLibrary = await PageAsync(w, Query("name", grouped: true) with { LibraryId = w.BetaId });

        Assert.Equal(new[] { (w.AlphaId, 2), (w.BetaId, 3), (w.PrivateId, 1) }, archives.FavoriteGroups!.Select(g => (g.LibraryId, g.Count)));
        Assert.Equal(new[] { w.AlphaId, w.BetaId }, incognito.FavoriteGroups!.Select(g => g.LibraryId));
        Assert.DoesNotContain(incognito.Items, n => n.LibraryId == w.PrivateId);
        Assert.Equal(new[] { (w.BetaId, 4) }, oneLibrary.FavoriteGroups!.Select(g => (g.LibraryId, g.Count)));
    }

    [Fact]
    public async Task TheDefaultQuery_IsUnchanged_NoGroups_NoPreviousPage()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        var page = await w.Service.ListFavoritesAsync(w.UserId, pageSize: 3);

        Assert.Null(page.FavoriteGroups);
        Assert.False(page.HasPrevious);
        Assert.Null(page.PrevCursor);
        // Recently favorited, newest first: Pear (the last starred), cherry, Banana.
        Assert.Equal(new[] { "frP", "frc", "frB" }, page.Items.Select(Key));
        Assert.StartsWith("f:", page.NextCursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForeignOrStaleCursor_IsTheFirstPage_OrResumesInOrder()
    {
        var w = await SeedAsync();
        await using var db = w.Db;
        var name = Query("name");
        var grouped = Query("name", grouped: true);
        var nameFirst = (await PageAsync(w, name)).Items.Select(Key).ToList();
        var groupedFirst = (await PageAsync(w, grouped)).Items.Select(Key).ToList();
        var nameCursor = (await PageAsync(w, name, pageSize: 2)).NextCursor!;
        var groupedCursor = (await PageAsync(w, grouped, pageSize: 2)).NextCursor!;
        var readCursor = (await PageAsync(w, Query("recentlyRead"), pageSize: 2)).NextCursor!;
        Assert.StartsWith("fn:", nameCursor, StringComparison.Ordinal);
        Assert.StartsWith("fl:", groupedCursor, StringComparison.Ordinal);
        Assert.StartsWith("fr:", readCursor, StringComparison.Ordinal);

        // A cursor of the other grouping, of another sort, malformed, or naming a library the viewer cannot see now: the first page.
        Assert.Equal(nameFirst, (await PageAsync(w, name, groupedCursor)).Items.Select(Key));
        Assert.Equal(groupedFirst, (await PageAsync(w, grouped, nameCursor)).Items.Select(Key));
        Assert.Equal(nameFirst, (await PageAsync(w, name, readCursor)).Items.Select(Key));
        Assert.Equal(groupedFirst, (await PageAsync(w, grouped, "fl:x:" + nameCursor)).Items.Select(Key));
        var privateLibraryId = await db.Libraries.Where(l => l.PublicId == w.PrivateId).Select(l => l.Id).SingleAsync();
        var incognitoFirst = (await PageAsync(w, grouped, incognito: true)).Items.Select(Key).ToList();
        Assert.Equal(incognitoFirst, (await PageAsync(w, grouped, $"fl:{privateLibraryId}:{nameCursor}", incognito: true)).Items.Select(Key));

        // A foreign `before` lists the first page with nothing above it.
        var foreignBefore = await PageAsync(w, grouped, before: nameCursor);
        Assert.Equal(groupedFirst, foreignBefore.Items.Select(Key));
        Assert.False(foreignBefore.HasPrevious);

        // A stale cursor (its entry unstarred meanwhile) resumes at the next entry in order, both ways.
        var third = await PageAsync(w, grouped, (await PageAsync(w, grouped, pageSize: 2)).NextCursor, pageSize: 1);
        Assert.Equal("frc", Key(Assert.Single(third.Items)));
        var cherryCursor = third.NextCursor!;
        var cherryPrev = third.PrevCursor!;
        db.Favorites.Remove(await db.Favorites.SingleAsync(f => f.CatalogNodeId == w.Ids["frc"]));
        await db.SaveChangesAsync();
        Assert.Equal("frM", Key((await PageAsync(w, grouped, cherryCursor, pageSize: 1)).Items[0]));
        Assert.Equal(new[] { "frK", "frB" }, (await PageAsync(w, grouped, before: cherryPrev, pageSize: 5)).Items.Select(Key));
    }

    [Fact]
    public async Task JumpIndex_LettersOfTheFavorites_EachBucketsCursorLandsOnItsFirstEntry_BothDirections()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        foreach (var direction in new[] { "asc", "desc" })
        {
            var q = Query("name", direction);
            var rail = await w.Service.GetFavoritesJumpIndexAsync(w.UserId, q, incognito: false, CancellationToken.None);
            var order = (await PageAsync(w, q)).Items.ToList();

            // Three folder entries (Kiwi, Maple, the Maple stack) under 20: one "Folders" chip; the archives by letter.
            var expected = direction == "asc"
                ? new[] { ("Folders", 3), ("A", 1), ("B", 1), ("C", 1), ("P", 1), ("Z", 1) }
                : new[] { ("Z", 1), ("P", 1), ("C", 1), ("B", 1), ("A", 1), ("Folders", 3) };
            Assert.Equal(expected, rail.Buckets.Select(b => (b.Label, b.Count)));
            Assert.Equal(direction, rail.Direction);
            Assert.Equal("letters", rail.Mode);
            Assert.Null(rail.Buckets[0].FirstCursor);
            var start = 0;
            foreach (var bucket in rail.Buckets)
            {
                var landed = await PageAsync(w, q, bucket.FirstCursor, pageSize: bucket.Count);
                Assert.Equal(order.Skip(start).Take(bucket.Count).Select(Key), landed.Items.Select(Key));
                Assert.Equal(bucket.FirstNodeId, landed.Items[0].Id);
                Assert.Equal(start > 0, landed.HasPrevious);
                start += bucket.Count;
            }
            Assert.Equal(order.Count, start);
        }
    }

    [Fact]
    public async Task JumpIndex_FollowsTheFilters_AndIncognito_AndIsEmptyWhileGrouped()
    {
        var w = await SeedAsync();
        await using var db = w.Db;

        async Task<List<(string, int)>> RailAsync(FavoritesQuery q, bool incognito = false) =>
            (await w.Service.GetFavoritesJumpIndexAsync(w.UserId, q, incognito, CancellationToken.None)).Buckets.Select(b => (b.Label, b.Count)).ToList();

        // Archives = starred archives and stacks: the Maple stack is its folder's card and sorts among the folders.
        Assert.Equal(new[] { ("Folders", 1), ("A", 1), ("B", 1), ("C", 1), ("P", 1), ("Z", 1) },
            await RailAsync(Query("name") with { Kind = FavoriteKindFilter.Archives }));
        Assert.Equal(new[] { ("Folders", 3), ("A", 1), ("B", 1), ("C", 1), ("Z", 1) }, await RailAsync(Query("name"), incognito: true));
        Assert.Equal(new[] { ("Folders", 1), ("B", 1), ("C", 1) }, await RailAsync(Query("name") with { LibraryId = w.AlphaId }));
        Assert.Equal(new[] { ("B", 1), ("Z", 1) },
            await RailAsync(Query("name") with { ReadState = BrowseReadStateFilter.Read, Kind = FavoriteKindFilter.Archives }));
        Assert.Empty(await RailAsync(Query("name", grouped: true)));
        Assert.Empty(await RailAsync(Query("name") with { LibraryId = "nosuchlibrary" }));
    }
}
