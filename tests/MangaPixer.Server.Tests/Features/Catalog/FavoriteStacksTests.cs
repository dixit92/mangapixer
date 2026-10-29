namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the pure favorites-stacking grouping (1.27.0): threshold, direct
/// parent, folders never stacking, newest-member placement, id tie-break, and the
/// cursor walk.
/// </summary>
public sealed class FavoriteStacksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int Folder = (int)CatalogNodeKind.Folder;
    private const int Archive = (int)CatalogNodeKind.Archive;

    private static FavoriteStacks.Key K(long favId, int minute, long nodeId, long? parentId, int kind = Archive)
        => new(favId, T0.AddMinutes(minute), nodeId, parentId, kind);

    [Fact]
    public void TwoArchivesInOneFolder_FormOneStack_AtTheNewestMember()
    {
        var entries = FavoriteStacks.Group([K(1, 0, 10, 100), K(2, 1, 20, null), K(3, 2, 11, 100)]);

        Assert.Equal(
            new[] { (100L, (int?)2, 3L), (20L, null, 2L) },
            entries.Select(e => (e.NodeId, e.StackCount, e.RepresentativeId)).ToArray());
    }

    [Fact]
    public void BelowThreshold_RootArchives_AndFolders_StaySingles()
    {
        var entries = FavoriteStacks.Group([
            K(1, 0, 10, 100),               // alone in folder 100
            K(2, 1, 20, null),              // library root archive
            K(3, 2, 30, null),              // another root archive: no parent, no stack
            K(4, 3, 40, 200, Folder),       // two starred folders under 200 ...
            K(5, 4, 41, 200, Folder),       // ... never stack
        ]);

        Assert.All(entries, e => Assert.Null(e.StackCount));
        Assert.Equal(new long[] { 41, 40, 30, 20, 10 }, entries.Select(e => e.NodeId).ToArray());
    }

    [Fact]
    public void SameTimestamp_OrdersByFavoriteIdDescending()
    {
        var entries = FavoriteStacks.Group([K(7, 0, 10, null), K(9, 0, 20, null), K(8, 0, 30, null)]);

        Assert.Equal(new long[] { 20, 30, 10 }, entries.Select(e => e.NodeId).ToArray());
    }

    [Fact]
    public void After_ResumesStrictlyPastTheCursorEntry()
    {
        var entries = FavoriteStacks.Group([K(1, 0, 10, 100), K(2, 1, 20, null), K(3, 2, 11, 100), K(4, 3, 30, null)]);
        var first = entries[0];

        var rest = FavoriteStacks.After(entries, first.RepresentativeTicks, first.RepresentativeId).ToList();

        Assert.Equal(new long[] { 100, 20 }, rest.Select(e => e.NodeId).ToArray());
    }

    // ---------------------------------------------------------------- 1.50.0 lane F: the Favorites view's sorts

    /// <summary>A key with what the sorts read: its sort key (folder "0" / archive "1" prefix, as the catalog's), its parent's, its dates.</summary>
    private static FavoriteStacks.Key S(long favId, int favMinute, long nodeId, long? parentId, int kind, string name,
        int addedDay, int? latestDay = null, string parentName = "")
        => new(favId, T0.AddMinutes(favMinute), nodeId, parentId, kind,
            SortKey: (kind == Folder ? "0" : "1") + name, ParentSortKey: "0" + parentName,
            AddedAt: T0.AddDays(addedDay), LatestDescendantAddedAt: latestDay is int d ? T0.AddDays(d) : null);

    /// <summary>
    /// A world with every tie the sorts must break: folder 100 ("m") is starred AND holds a stack of two starred archives (one node, one
    /// sort key, two entries); two starred folders share a sort key from two libraries; a folder with no archive yet (no
    /// LatestDescendantAddedAt); two archives added on the same day.
    /// </summary>
    private static List<FavoriteStacks.Key> World() =>
    [
        S(1, 0, 100, null, Folder, "m", addedDay: 1, latestDay: 9),         // starred folder "m" (newest archive day 9)
        S(2, 1, 10, 100, Archive, "m 1", addedDay: 5, parentName: "m"),      // stack of "m": days 5 and 8 -> sits at day 8
        S(3, 2, 11, 100, Archive, "m 2", addedDay: 8, parentName: "m"),
        S(4, 3, 200, null, Folder, "b", addedDay: 2, latestDay: null),       // folder with no archive yet: last under Recently updated
        S(5, 4, 300, null, Folder, "s", addedDay: 3, latestDay: 6),          // two folders named "s" (two libraries): same sort key
        S(6, 5, 301, null, Folder, "s", addedDay: 3, latestDay: 6),
        S(7, 6, 20, null, Archive, "a", addedDay: 7),                        // two root archives added the same day
        S(8, 7, 21, null, Archive, "z", addedDay: 7),
    ];

    private static (long, bool)[] Ids(IEnumerable<FavoriteStacks.Entry> entries) => entries.Select(e => (e.NodeId, e.IsStack)).ToArray();

    [Fact]
    public void Name_AtoZ_FoldersFirst_ThenStackAfterItsOwnFolder_ThenNodeId()
    {
        var entries = FavoriteStacks.Group(World(), FavoriteStacks.Order.NameAscending);

        Assert.Equal(new[] { (200L, false), (100L, false), (100L, true), (300L, false), (301L, false), (20L, false), (21L, false) },
            Ids(entries));
    }

    [Fact]
    public void Name_ZtoA_IsTheExactReverse()
    {
        var asc = FavoriteStacks.Group(World(), FavoriteStacks.Order.NameAscending);
        var desc = FavoriteStacks.Group(World(), FavoriteStacks.Order.NameDescending);

        Assert.Equal(Enumerable.Reverse(Ids(asc)), Ids(desc));
    }

    [Fact]
    public void RecentlyAdded_NewestFirst_AStackAtItsNewestMember_TiesByName()
    {
        var entries = FavoriteStacks.Group(World(), FavoriteStacks.Order.RecentlyAdded);

        // stack (day 8), archives a / z (day 7, by name), the "s" folders (day 3, by node id), b (2), the folder m itself (1).
        Assert.Equal(new[] { (100L, true), (20L, false), (21L, false), (300L, false), (301L, false), (200L, false), (100L, false) },
            Ids(entries));
    }

    [Fact]
    public void RecentlyUpdated_FolderByNewestArchive_NoArchiveLast_AStackAtItsNewestMember()
    {
        var entries = FavoriteStacks.Group(World(), FavoriteStacks.Order.RecentlyUpdated);

        // m (day 9), the stack (8), a / z (7), the "s" folders (6), then b (no archive yet).
        Assert.Equal(new[] { (100L, false), (100L, true), (20L, false), (21L, false), (300L, false), (301L, false), (200L, false) },
            Ids(entries));
    }

    [Theory]
    [InlineData((int)FavoriteStacks.Order.Favorited)]
    [InlineData((int)FavoriteStacks.Order.NameAscending)]
    [InlineData((int)FavoriteStacks.Order.NameDescending)]
    [InlineData((int)FavoriteStacks.Order.RecentlyAdded)]
    [InlineData((int)FavoriteStacks.Order.RecentlyUpdated)]
    public void EverySort_PagesEachEntryExactlyOnce_OneEntryPerPage(int sort)
    {
        // FavoriteStacks is internal: the theory takes the sort as its number.
        var order = (FavoriteStacks.Order)sort;
        var entries = FavoriteStacks.Group(World(), order);
        var walked = new List<FavoriteStacks.Entry>();
        IEnumerable<FavoriteStacks.Entry> remaining = entries;
        while (remaining.FirstOrDefault() is { NodeId: not 0 } next)
        {
            walked.Add(next);
            remaining = FavoriteStacks.After(entries, order, FavoriteStacks.PositionOf(next, order));
        }

        Assert.Equal(Ids(entries), Ids(walked));
        Assert.Equal(7, walked.Count);
    }

    [Fact]
    public void After_AVanishedPosition_ResumesAtTheNextEntry()
    {
        var entries = FavoriteStacks.Group(World(), FavoriteStacks.Order.NameAscending);
        // The cursor of a folder "n" that was unstarred meanwhile (not in the list): resumes at the first entry after "n".
        var gone = new FavoriteStacks.Entry(999, null, 0, 0, "0n");

        var rest = FavoriteStacks.After(entries, FavoriteStacks.Order.NameAscending, gone).ToList();

        Assert.Equal(new[] { (300L, false), (301L, false), (20L, false), (21L, false) }, Ids(rest));
    }

    [Fact]
    public void Favorited_DefaultGroup_IsUnchanged_ByTheNewFields()
    {
        var plain = FavoriteStacks.Group(World().Select(k => new FavoriteStacks.Key(k.FavoriteId, k.FavoritedAt, k.NodeId, k.ParentId, k.Kind)));
        var full = FavoriteStacks.Group(World());

        Assert.Equal(Ids(plain), Ids(full));
        Assert.Equal(new[] { (21L, false), (20L, false), (301L, false), (300L, false), (200L, false), (100L, true), (100L, false) }, Ids(full));
    }
}
