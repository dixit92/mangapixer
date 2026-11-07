namespace com.lifepixer.mangapixer.Tests.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using Xunit;
using SortKey = com.lifepixer.mangapixer.Core.Ordering.SortKey;

/// <summary>
/// 1.51.0 lane F (unit): the Favorites jump rail's chips (<see cref="FavoriteJumpIndex"/>) and the new orders of
/// <see cref="FavoriteStacks"/> (Recently read; grouped by library).
/// </summary>
public sealed class FavoriteJumpIndexTests
{
    private static FavoriteJumpIndex.Row Folder(string name) => new(SortKey.ForNode(CatalogNodeKind.Folder, name), true);

    private static FavoriteJumpIndex.Row Archive(string name) => new(SortKey.ForNode(CatalogNodeKind.Archive, name), false);

    private static List<(int Start, int Count, string Label, string? Group)> Spans(params FavoriteJumpIndex.Row[] rows) =>
        FavoriteJumpIndex.Build(rows).Select(s => (s.Start, s.Count, s.Label, s.Group)).ToList();

    [Fact]
    public void OneKind_LettersByTheNamesFirstCharacterAsItSorts_DigitsAndPunctuationHash_CaseFolded()
    {
        var spans = Spans(Archive("07 Ghost"), Archive("[Oneshot] x"), Archive("apple"), Archive("Avocado"), Archive("berserk"), Archive("Zulu"));

        Assert.Equal(new[] { (0, 2, "#", (string?)null), (2, 2, "A", null), (4, 1, "B", null), (5, 1, "Z", null) }, spans);
    }

    [Fact]
    public void TwoKinds_TheSmallerKindUnderTwentyRows_IsOneChip_AtItsPlace_BothDirections()
    {
        var asc = Spans(Folder("Kiwi"), Folder("Maple"), Archive("apple"), Archive("Banana"), Archive("Zulu"));
        var desc = Spans(Archive("Zulu"), Archive("Banana"), Archive("apple"), Folder("Maple"), Folder("Kiwi"));

        Assert.Equal(new[] { (0, 2, "Folders", (string?)null), (2, 1, "A", null), (3, 1, "B", null), (4, 1, "Z", null) }, asc);
        Assert.Equal(new[] { (0, 1, "Z", (string?)null), (1, 1, "B", null), (2, 1, "A", null), (3, 2, "Folders", null) }, desc);
    }

    [Fact]
    public void TwoKinds_MoreFoldersThanArchives_TheArchivesAreTheFilesChip()
    {
        var spans = Spans(Folder("Alpha"), Folder("Beta"), Folder("Gamma"), Archive("Delta"));

        Assert.Equal(new[] { (0, 1, "A", (string?)null), (1, 1, "B", null), (2, 1, "G", null), (3, 1, "Files", null) }, spans);
    }

    [Fact]
    public void TwoKinds_BothWithTwentyRows_EachKindHasItsOwnLetters_NamingItsGroup()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Folder((i < 10 ? "A" : "B") + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(Enumerable.Range(0, 20).Select(i => Archive("C" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();

        var spans = Spans(rows);

        Assert.Equal(new[] { (0, 10, "A", (string?)"folders"), (10, 10, "B", "folders"), (20, 20, "C", "files") }, spans);
    }

    [Fact]
    public void ALabelInTwoRuns_KeepsItsBiggerRun_TheOtherJoinsTheChipBefore_SoEveryChipIsContiguous()
    {
        // In key order hiragana (U+3040..) sort before the CJK ideographs and half-width katakana (U+FF65..) after them: "Kana" is two runs.
        // The first (a tie keeps the first) is the chip; the second joins the CJK chip before it.
        var spans = Spans(Archive("\u3042"), Archive("\u5B57"), Archive("\u6F22"), Archive("\uFF71"));

        Assert.Equal(new[] { (0, 1, "Kana", (string?)null), (1, 3, "CJK", null) }, spans);
    }

    [Fact]
    public void ADoujinList_LabelsByTheFirstLetterInsideTheTag()
    {
        var spans = Spans(Archive("[Alpha] One"), Archive("[Alpha] Two"), Archive("[Circle] Three"), Archive("Plain"));

        Assert.Equal(new[] { (0, 2, "[A", (string?)null), (2, 1, "[C", null), (3, 1, "P", null) }, spans);
    }

    [Fact]
    public void NoRows_NoChips()
    {
        Assert.Empty(FavoriteJumpIndex.Build([]));
    }

    [Fact]
    public void RecentlyRead_NewestFirst_NeverReadLastByName_AStackAtItsMostRecentlyReadMember()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FavoriteStacks.Key K(long id, long? parent, CatalogNodeKind kind, string name, long? stamp) =>
            new(id, t.AddMinutes(id), id, parent, (int)kind, SortKey.ForNode(kind, name), parent is null ? "" : SortKey.ForNode(CatalogNodeKind.Folder, "Shelf"),
                ReadStamp: stamp);
        var keys = new[]
        {
            K(1, null, CatalogNodeKind.Archive, "Zeta", null),
            K(2, null, CatalogNodeKind.Archive, "Alpha", null),
            K(3, null, CatalogNodeKind.Archive, "Mid", 50),
            K(4, 9, CatalogNodeKind.Archive, "Shelf 1", 10),
            K(5, 9, CatalogNodeKind.Archive, "Shelf 2", 70),
            K(6, null, CatalogNodeKind.Folder, "Folder", 60),
        };

        var entries = FavoriteStacks.Group(keys, FavoriteStacks.Order.RecentlyRead);

        Assert.Equal(new[] { (9L, true), (6L, false), (3L, false), (2L, false), (1L, false) }, entries.Select(e => (e.NodeId, e.IsStack)));
        Assert.Equal(70, entries[0].ReadStamp);
    }

    [Fact]
    public void Grouped_TheLibraryRankFirst_ThenTheSort()
    {
        var entries = new List<FavoriteStacks.Entry>
        {
            new(1, null, 0, 0, "1a", LibraryId: 20),
            new(2, null, 0, 0, "1b", LibraryId: 10),
            new(3, null, 0, 0, "1c", LibraryId: 20),
            new(4, null, 0, 0, "1d", LibraryId: 10),
        };
        var rank = new Dictionary<long, int> { [20] = 0, [10] = 1 };

        FavoriteStacks.Sort(entries, FavoriteStacks.Order.NameAscending, rank);

        Assert.Equal(new long[] { 1, 3, 2, 4 }, entries.Select(e => e.NodeId));
        Assert.Equal(new long[] { 2, 4 }, FavoriteStacks.After(entries, FavoriteStacks.Order.NameAscending, entries[1], rank).Select(e => e.NodeId));
        FavoriteStacks.Sort(entries, FavoriteStacks.Order.NameAscending);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, entries.Select(e => e.NodeId));
    }
}
