namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Flags;
using com.lifepixer.mangapixer.Server.Scanning;
using Xunit;

/// <summary>
/// Unit tests (no database, no network) of the stage-2 plumbing's pure parts: the
/// tree snapshot's shapes, work selection (highest candidate wins, links cover their
/// subtree, archive groups), the carry-over target mapping, reason codes, threshold
/// resolution and flag-note cleaning. Synthetic names only.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AutoMatchUnitTests
{
    private static LibraryTreeSnapshot.Node F(long id, long? parent, string name) => new(id, parent, true, name, "0" + name);
    private static LibraryTreeSnapshot.Node A(long id, long parent, string name) => new(id, parent, false, name, "1" + name);

    /// <summary>
    /// 1 Manga/ (category container)
    ///   2 Alpha Saga/ (series: 10, 11)
    ///   3 Beta Tale/ (series with units)
    ///     4 Volumes/ (12, 13)
    ///   5 Collection Shorts/ (14, 15, 16)
    /// 6 Gamma One/ (one-shot: 17)
    /// </summary>
    private static LibraryTreeSnapshot Tree() => LibraryTreeSnapshot.FromNodes(1,
    [
        F(1, null, "Manga"),
        F(2, 1, "Alpha Saga"), A(10, 2, "Alpha Saga v01"), A(11, 2, "Alpha Saga v02"),
        F(3, 1, "Beta Tale"), F(4, 3, "Volumes"), A(12, 4, "Beta Tale v01"), A(13, 4, "Beta Tale v02"),
        F(5, 1, "Collection Shorts"), A(14, 5, "Short One"), A(15, 5, "Short Two"), A(16, 5, "Short Three"),
        F(6, null, "Gamma One"), A(17, 6, "Gamma One"),
    ]);

    [Fact]
    public void Snapshot_ShapeOf_UsesNamesCountsDepthAndCategoryHint()
    {
        var tree = Tree();
        var shape = tree.ShapeOf(3);
        Assert.Equal("Beta Tale", shape.DisplayName);
        Assert.Equal(2, shape.Depth);
        Assert.Empty(shape.ArchiveNames);
        var volumes = Assert.Single(shape.Subfolders);
        Assert.Equal(("Volumes", 2), (volumes.DisplayName, volumes.DescendantArchiveCount));
        Assert.Equal(["Beta Tale v01", "Beta Tale v02"], volumes.ArchiveNames!); // a unit subfolder hands its names to the count rule (1.29.0)
        Assert.All(tree.ShapeOf(1).Subfolders, s => Assert.Null(s.ArchiveNames)); // other subfolders do not
        Assert.Equal("Manga", shape.ParentDisplayName);
        Assert.Equal("manga", shape.CategoryHint);

        Assert.Equal(1, tree.Depth(1));
        Assert.Null(tree.CategoryHint(1)); // The category folder itself has no category ancestor.
        Assert.Equal(7, tree.DescendantArchiveCount(1));
        Assert.Equal(["Short One", "Short Three", "Short Two"], tree.ShapeOf(5).ArchiveNames); // catalog (sort key) order
    }

    [Fact]
    public void Snapshot_ShapeOf_PassesTheProviderAuthors_ExceptToAFolderThatCarriesALinkItself()
    {
        var authors = new LibraryTreeSnapshot.ProviderAuthorSet(["Given Family"], new HashSet<long> { 2 }, new LibraryTreeSnapshot.LinkStamp(1, null, null));
        var tree = LibraryTreeSnapshot.FromNodes(1, Tree().Roots.SelectMany(Flatten), authors);

        Assert.Equal(["Given Family"], tree.ShapeOf(5).KnownAuthorNames);
        Assert.Null(tree.ShapeOf(2).KnownAuthorNames); // linked itself
        Assert.Null(Tree().ShapeOf(5).KnownAuthorNames); // no authors loaded
        Assert.Same(authors, tree.WithAuthors(authors).Authors);
        Assert.Equal(tree.Count, tree.WithAuthors(LibraryTreeSnapshot.ProviderAuthorSet.None).Count);

        IEnumerable<LibraryTreeSnapshot.Node> Flatten(LibraryTreeSnapshot.Node n)
        {
            var source = Tree();
            var stack = new Stack<LibraryTreeSnapshot.Node>([n]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                yield return current;
                foreach (var child in source.ChildrenOf(current.Id))
                    stack.Push(child);
            }
        }
    }

    [Fact]
    public void Snapshot_CategoryHint_IsTheNearestExactCategoryName_NeverAShelfWord()
    {
        var tree = LibraryTreeSnapshot.FromNodes(1,
        [
            F(1, null, "Manhwa"), F(2, 1, "Ongoing"), F(3, 2, "Alpha Saga"), A(10, 3, "Alpha Saga 001"),
            F(4, null, "Manga Collection"), F(5, 4, "Beta Tale"), A(11, 5, "Beta Tale v01"),
        ]);

        Assert.Equal("manhwa", tree.CategoryHint(3)); // "Ongoing" (a shelf word) is skipped
        Assert.Null(tree.CategoryHint(5)); // whole name only
    }

    [Fact]
    public void Select_QueuesSeriesLikeFoldersAndArchiveWorks_NotContainersOrUnits()
    {
        var works = AutoMatchWorkSelector.Select(Tree(), new FakeWorkDetector(), new Dictionary<long, SeriesLinkState>());
        var byAnchor = works.ToDictionary(w => w.AnchorNodeId);

        Assert.Equal(MatchLevel.Folder, byAnchor[2].Level);
        Assert.Equal(MatchLevel.Folder, byAnchor[3].Level);        // series with units: at the series folder
        Assert.False(byAnchor.ContainsKey(4));                      // the unit subfolder inherits
        Assert.False(byAnchor.ContainsKey(1));                      // the category container is never a work
        Assert.Equal(WorkClass.OneShot, byAnchor[6].Class);
        // Collection leaf: one work per archive (sorted: Short One, Short Three, Short Two).
        Assert.Equal(MatchLevel.Archive, byAnchor[14].Level);
        Assert.Equal(5, byAnchor[14].FolderId);
        Assert.Contains(16, byAnchor.Keys);
        Assert.Contains(15, byAnchor.Keys);
        Assert.Equal(6, works.Count);
    }

    [Fact]
    public void Select_ALinkOrDontMatchCoversItsSubtree_AndLinkedArchivesLeaveTheirGroup()
    {
        var links = new Dictionary<long, SeriesLinkState>
        {
            [1] = SeriesLinkState.DontMatch,     // everything below the container is excluded
            [17] = SeriesLinkState.Confirmed,    // the one-shot's archive is linked on its own
        };
        var works = AutoMatchWorkSelector.Select(Tree(), new FakeWorkDetector(), links);
        // Only the one-shot folder remains; the Don't match container covers 2..5.
        Assert.Equal([6L], works.Select(w => w.AnchorNodeId).ToArray());
    }

    [Fact]
    public void Select_WithScope_OnlyQueuesScopedFolders_ButStillRespectsHighestWins()
    {
        // New "Volumes" subfolder in scope: it is below the series with units, so nothing new is queued for it.
        var none = AutoMatchWorkSelector.Select(Tree(), new FakeWorkDetector(), new Dictionary<long, SeriesLinkState>(), new HashSet<long> { 4 });
        Assert.Empty(none);

        var one = AutoMatchWorkSelector.Select(Tree(), new FakeWorkDetector(), new Dictionary<long, SeriesLinkState>(), new HashSet<long> { 2, 1 });
        Assert.Equal([2L], one.Select(w => w.AnchorNodeId).ToArray());
    }

    [Fact]
    public void ArchiveWorks_GroupsNumberedArchives_AnchorIsFirstUnlinkedArchive()
    {
        var tree = LibraryTreeSnapshot.FromNodes(1,
        [
            F(1, null, "Pairs Folder"), A(10, 1, "Delta Part 1"), A(11, 1, "Delta Part 2"), A(12, 1, "Echo Part 1"), A(13, 1, "Echo Part 2"),
        ]);
        var classification = new FakeWorkDetector().Classify(tree.ShapeOf(1));
        var links = new Dictionary<long, SeriesLinkState> { [10] = SeriesLinkState.Confirmed };
        var works = AutoMatchWorkSelector.ArchiveWorks(tree, 1, classification, links).ToList();

        Assert.Equal(2, works.Count);
        Assert.Equal(11, works[0].AnchorNodeId);   // 10 is linked on its own: out of the group
        Assert.Empty(works[0].MemberNodeIds);
        Assert.Equal(12, works[1].AnchorNodeId);
        Assert.Equal([13L], works[1].MemberNodeIds);
    }

    // --- Carry-over mapping ---

    private static Dictionary<long, MetadataCarryOverService.TreeRow> Rows(params MetadataCarryOverService.TreeRow[] rows) =>
        rows.ToDictionary(r => r.Id);

    private static MetadataCarryOverService.TreeRow Folder(long id, long? parent, bool live = true) => new(id, parent, true, live);
    private static MetadataCarryOverService.TreeRow Archive(long id, long? parent, bool live = true) => new(id, parent, false, live);

    [Fact]
    public void MapTarget_RenamedFolder_MapsToTheNewFolder()
    {
        // T=2 (removed) held archives 10, 11; both now live in N=3.
        var nodes = Rows(Folder(1, null), Folder(2, 1, live: false), Folder(3, 1), Archive(10, 3), Archive(11, 3));
        var ledger = new[] { new ScanMove(10, 2), new ScanMove(11, 2) };
        Assert.Equal(3, MetadataCarryOverService.MapTarget(2, ledger, nodes));
    }

    [Fact]
    public void MapTarget_RenamedSubtree_MapsEachFolderAtTheSameRelativeDepth()
    {
        // T=2 (removed) / 4 Volumes (removed) held 10, 11 -> now N=3 / 5 Volumes.
        var nodes = Rows(Folder(1, null), Folder(2, 1, false), Folder(4, 2, false), Folder(3, 1), Folder(5, 3), Archive(10, 5), Archive(11, 5));
        var ledger = new[] { new ScanMove(10, 4), new ScanMove(11, 4) };
        Assert.Equal(3, MetadataCarryOverService.MapTarget(2, ledger, nodes)); // two levels up from the archives
        Assert.Equal(5, MetadataCarryOverService.MapTarget(4, ledger, nodes));
    }

    [Fact]
    public void MapTarget_SplitFolder_HasNoSingleTarget()
    {
        var nodes = Rows(Folder(1, null), Folder(2, 1, false), Folder(3, 1), Folder(6, 1), Archive(10, 3), Archive(11, 6));
        var ledger = new[] { new ScanMove(10, 2), new ScanMove(11, 2) };
        Assert.Null(MetadataCarryOverService.MapTarget(2, ledger, nodes));
    }

    [Fact]
    public void MapTarget_FewerThan80PercentMoved_StaysMissing()
    {
        // 1 of 3 archives moved; 2 are still tombstoned under T.
        var nodes = Rows(Folder(1, null), Folder(2, 1, false), Folder(3, 1), Archive(10, 3), Archive(11, 2, false), Archive(12, 2, false));
        var ledger = new[] { new ScanMove(10, 2) };
        Assert.Null(MetadataCarryOverService.MapTarget(2, ledger, nodes));

        // 4 of 5 moved = 80%: carried.
        var more = Rows(Folder(1, null), Folder(2, 1, false), Folder(3, 1),
            Archive(10, 3), Archive(11, 3), Archive(12, 3), Archive(13, 3), Archive(14, 2, false));
        var moves = new[] { new ScanMove(10, 2), new ScanMove(11, 2), new ScanMove(12, 2), new ScanMove(13, 2) };
        Assert.Equal(3, MetadataCarryOverService.MapTarget(2, moves, more));
    }

    [Fact]
    public void MapTarget_UnrelatedMoves_DoNotMapTheFolder()
    {
        var nodes = Rows(Folder(1, null), Folder(2, 1, false), Folder(3, 1), Folder(7, 1), Archive(10, 3));
        Assert.Null(MetadataCarryOverService.MapTarget(2, [new ScanMove(10, 7)], nodes));
    }

    // --- Small vocabularies ---

    [Fact]
    public void ReasonCodes_MapFlagsToChips()
    {
        Assert.Equal(["close_second", "author"], MatchReasonCodes.Of(MatchReason.CloseSecond | MatchReason.AuthorConflict));
        Assert.Empty(MatchReasonCodes.Of(MatchReason.None));
        Assert.Equal(["review_only"], MatchReasonCodes.Of((int)MatchReason.ReviewOnlyClass));
    }

    [Fact]
    public void Thresholds_NullColumnsAreDefaults_InvalidStoredSetFallsBack()
    {
        Assert.Equal(MatchThresholds.Default, MetadataThresholds.Resolve(null, null, null));
        Assert.Equal(new MatchThresholds(0.95, 0.10, 0.60), MetadataThresholds.Resolve(0.95, null, null));
        Assert.Equal(MatchThresholds.Default, MetadataThresholds.Resolve(0.50, 0.50, 0.95)); // out of bounds
        Assert.False(MetadataThresholds.FromDto(new() { AutoTitle = 0.86, Margin = 0.1, ReviewFloor = 0.88 }).IsValid); // floor above auto
    }

    [Fact]
    public void FlagNote_IsPlainTrimmedText()
    {
        Assert.Null(MetadataFlagService.CleanNote("   "));
        Assert.Equal("wrong\nseries", MetadataFlagService.CleanNote("  wrong\n\u0007series\u0000 "));
    }
}
