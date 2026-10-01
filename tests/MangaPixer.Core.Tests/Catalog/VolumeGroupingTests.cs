namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the pure Volumes-view grouping (1.29.0, P2.3 / P2.4 / 7.4 / 7.5): local names and ComicInfo, the
/// provider's exact list, bounded and estimated volumes (with the known-volume cap), extras, the entry order (by volume,
/// not by file name), coexistence of a real volume archive with chapters, missing chapters and the ongoing-series rule.
/// Synthetic names only.
/// </summary>
public sealed class VolumeGroupingTests
{
    private static GroupingRow Archive(string name, string? container = null, int? ciVolume = null, string? ciNumber = null) =>
        new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant(), container, ciVolume, ciNumber);

    private static GroupingRow Folder(string name) => new("id:" + name, GroupingRowKind.Folder, name, name.ToLowerInvariant());

    private static List<GroupingRow> Chapters(int from, int to, string prefix = "Synthetic - Chapter ") =>
        Enumerable.Range(from, to - from + 1).Select(n => Archive($"{prefix}{n:000}")).ToList();

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    private static IReadOnlySet<decimal> Units(int from, int to) => Range(from, to).ToHashSet();

    private static VolumeMapInput Map(bool ongoing = false, int? known = null, double? cpv = null, params (int Volume, int From, int To)[] volumes) =>
        new(volumes.Select(v => new VolumeMapVolume(v.Volume, Range(v.From, v.To))).ToList(), cpv, known, ongoing, VolumeListSource.MangaDex);

    private static IReadOnlyList<VolumeEntry> Stacks(VolumeGroupingResult r) => r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).ToList();

    [Fact]
    public void LocalNames_GroupChaptersByTheirStatedVolume_WithoutAnyMap()
    {
        var rows = new List<GroupingRow>
        {
            Archive("Series v01 c001"), Archive("Series v01 c002"), Archive("Series v01 c003"),
            Archive("Series v02 c004"), Archive("Series v02 c005"),
        };
        var r = VolumeGrouping.Group(rows, null);

        Assert.True(r.Grouped);
        Assert.Equal(2, r.StackCount);
        var stacks = Stacks(r).Select(e => e.Stack!).ToList();
        Assert.Equal(["1", "2"], stacks.Select(s => s.Key));
        Assert.Equal(["Volume 1", "Volume 2"], stacks.Select(s => s.Label));
        Assert.Equal([3, 2], stacks.Select(s => s.PresentCount));
        Assert.All(stacks, s => Assert.Equal(VolumeStackConfidence.Exact, s.Confidence));
        Assert.All(stacks, s => Assert.Equal(VolumeListSource.FileNames, s.Source));
        // No provider list: nothing is "missing".
        Assert.All(stacks, s => Assert.Empty(s.MissingChapters));
        Assert.All(stacks, s => Assert.Null(s.ChapterCount));
    }

    [Fact]
    public void LocalNames_NeedHalfTheChaptersToStateAVolume_WhenThereIsNoMap()
    {
        var rows = new List<GroupingRow> { Archive("Series v01 c001"), Archive("Series c002"), Archive("Series c003"), Archive("Series c004") };
        var r = VolumeGrouping.Group(rows, null);

        Assert.False(r.Grouped);
        Assert.All(r.Entries, e => Assert.Equal((VolumeEntryKind.Archive, 2), (e.Kind, e.Rank)));
    }

    [Fact]
    public void LocalNames_GroupBelowTheHalfShare_WhenAMapExists()
    {
        var rows = new List<GroupingRow> { Archive("Series v01 c001"), Archive("Series c002"), Archive("Series c003"), Archive("Series c004") };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 4)));

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal(4, stack.PresentCount);
        Assert.Equal(4, stack.ChapterCount);
    }

    [Fact]
    public void EntryOrder_IsByVolume_NotByFileName()
    {
        // "series c019" sorts before "series v01" by name; volume 2 must still come after volume 1.
        var rows = new List<GroupingRow> { Archive("Series c019"), Archive("Series c020"), Archive("Series v01 c001"), Archive("Series v01 c002") };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 2), (2, 19, 20)));

        Assert.Equal(["1", "2"], Stacks(r).Select(e => e.Stack!.Key));
        Assert.Equal([0, 0], r.Entries.Select(e => e.Rank));
        Assert.True(string.CompareOrdinal(r.Entries[0].VolumeKey, r.Entries[1].VolumeKey) < 0);
    }

    [Fact]
    public void EntryOrder_VolumesThenSubfoldersThenLooseChapters()
    {
        var rows = new List<GroupingRow>
        {
            Archive("Series - Chapter 099"), Folder("Season 2"), Archive("Series v02"), Archive("Series v01 c001"), Archive("Series v01 c002"),
        };
        var r = VolumeGrouping.Group(rows, null);

        Assert.Equal([0, 0, 1, 2], r.Entries.Select(e => e.Rank));
        Assert.Equal([VolumeEntryKind.Stack, VolumeEntryKind.Archive, VolumeEntryKind.Folder, VolumeEntryKind.Archive], r.Entries.Select(e => e.Kind));
        Assert.Equal("Volume 1", r.Entries[0].Stack!.Label);
        Assert.Equal("id:Series v02", r.Entries[1].Row!.Id);
    }

    [Fact]
    public void Extras_FollowTheirIntegerChapter_AndAreNeverMissing()
    {
        var rows = Chapters(41, 44).Concat(Chapters(46, 50)).Append(Archive("Synthetic - c045.5")).ToList();
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (5, 41, 50)));

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal(10, stack.ChapterCount);
        Assert.Equal(1, stack.ExtraCount);
        // 45 itself is missing; the extra 45.5 does not fill it, and is never missing itself.
        Assert.Equal([45], stack.MissingChapters);
        Assert.Equal(10, stack.PresentCount); // 9 whole chapters + the extra
        var slots = VolumeGrouping.Slots(stack);
        Assert.Equal(["44", "45", "45.5", "46"], slots.Select(s => s.Chapter).SkipWhile(c => c != "44").Take(4));
        Assert.Equal(VolumeSlotKind.Missing, slots.Single(s => s.Chapter == "45").Kind);
    }

    [Fact]
    public void Extras_AreLoose_WhenTheirIntegerChapterCannotBePlaced()
    {
        var rows = new List<GroupingRow> { Archive("Synthetic - c900.5") };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 10)));

        Assert.False(r.Grouped);
        Assert.Equal(2, r.Entries[0].Rank);
    }

    [Fact]
    public void ExactList_PlacesChaptersAndFlagsMissingOnes()
    {
        var rows = Chapters(1, 3).Concat(Chapters(4, 4)).Concat(Chapters(6, 6)).ToList();
        var r = VolumeGrouping.Group(rows, Map(true, null, null, (1, 1, 3), (2, 4, 6)));

        var second = Stacks(r)[1].Stack!;
        Assert.Equal([5], second.MissingChapters);
        Assert.Equal((3, 2, 1), (second.ChapterCount, second.PresentCount, second.MissingChapters.Count));
        Assert.Equal(VolumeListSource.MangaDex, second.Source);
        Assert.Equal("4", second.FirstChapter);
        Assert.Equal("6", second.LastChapter);
    }

    [Fact]
    public void TrailingChapters_AreMissingOnlyWhenReleasedInThePreferredLanguage()
    {
        var rows = Chapters(1, 3).Concat(Chapters(4, 5)).ToList(); // volume 2 lists 4..8, only 4-5 on disk
        var unknown = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 3), (2, 4, 8)));
        var released = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 3), (2, 4, 8)) with { ReleasedChapters = Units(1, 7) });

        // Nothing after chapter 5 is here, and nothing says 6-8 are released: not missing (also for a complete series).
        Assert.Empty(Stacks(unknown)[1].Stack!.MissingChapters);
        Assert.Equal([6m, 7m], Stacks(released)[1].Stack!.MissingChapters);
        Assert.Equal(2, released.MissingChapterCount);
    }

    [Fact]
    public void AGapBelowTheHighestChapterPresent_IsMissing_InAnyVolume()
    {
        // Chapter 3 is not here, but 4 is: 3 exists and is missing - also when the series is ongoing and the list says nothing else.
        var first = VolumeGrouping.Group(Chapters(1, 2).Concat(Chapters(4, 8)).ToList(), Map(true, null, null, (1, 1, 3), (2, 4, 8)));
        Assert.Equal([3m], Stacks(first)[0].Stack!.MissingChapters);
        Assert.Equal((3, 2), (Stacks(first)[0].Stack!.ChapterCount, Stacks(first)[0].Stack!.ChaptersPresent));
        Assert.Equal(1, first.MissingChapterCount);
    }

    [Fact]
    public void AVolumeFileHere_RaisesTheHighestChapter_ItsVolumeCovers()
    {
        // Volume 2 (chapters 6-10) is a file; chapters 1-2 of volume 1 are here: 3-5 exist and are missing.
        var rows = Chapters(1, 2).Append(Archive("Series v02")).ToList();
        var r = VolumeGrouping.Group(rows, Map(true, null, null, (1, 1, 5), (2, 6, 10)));

        Assert.Equal([3m, 4m, 5m], Stacks(r)[0].Stack!.MissingChapters);
    }

    [Fact]
    public void SplitChapters_ArePartsWhenTheListHasNoPlainNumber()
    {
        var map = new VolumeMapInput([new VolumeMapVolume(1, [3m, 4.1m, 4.2m, 5.1m, 5.2m, 5.3m, 6m])], null, null, true, VolumeListSource.MangaDex);
        var rows = new List<GroupingRow>
        {
            Archive("Series c003"), Archive("Series c004.1"), Archive("Series c004.2"), Archive("Series c005.1"), Archive("Series c005.3"), Archive("Series c006"),
        };
        var stack = Assert.Single(Stacks(VolumeGrouping.Group(rows, map))).Stack!;

        // 5.2 is missing (5.1 and 5.3 are here); chapter 4 is complete through its parts; no part is an extra.
        Assert.Equal([5.2m], stack.MissingChapters);
        Assert.Equal((4, 3, 0), (stack.ChapterCount, stack.ChaptersPresent, stack.ExtraCount));
        Assert.All(stack.Members, m => Assert.False(m.IsExtra));
        Assert.Equal(["3", "4.1", "4.2", "5.1", "5.2", "5.3", "6"], VolumeGrouping.Slots(stack).Select(s => s.Chapter));
        Assert.Equal(VolumeSlotKind.Missing, VolumeGrouping.Slots(stack).Single(s => s.Chapter == "5.2").Kind);
    }

    [Fact]
    public void AWholeChapterFile_CoversItsListedParts()
    {
        var map = new VolumeMapInput([new VolumeMapVolume(1, [3m, 4.1m, 4.2m, 5m])], null, null, true, VolumeListSource.MangaDex);
        var stack = Assert.Single(Stacks(VolumeGrouping.Group([Archive("Series c003"), Archive("Series c004"), Archive("Series c005")], map))).Stack!;

        Assert.Empty(stack.MissingChapters);
        Assert.Equal((3, 3), (stack.ChapterCount, stack.ChaptersPresent));
    }

    [Fact]
    public void AFractionNextToItsListedWhole_StaysAnExtra_AndIsNeverMissing()
    {
        var map = new VolumeMapInput([new VolumeMapVolume(1, [9m, 10m, 10.5m, 11m])], null, null, true, VolumeListSource.MangaDex);

        var without = Assert.Single(Stacks(VolumeGrouping.Group(Chapters(9, 11), map))).Stack!;
        Assert.Empty(without.MissingChapters); // 10.5 is not here and not missing
        Assert.Equal((3, 3), (without.ChapterCount, without.ChaptersPresent));

        var extraOnly = Assert.Single(Stacks(VolumeGrouping.Group([Archive("Series c009"), Archive("Series c010.5"), Archive("Series c011")], map))).Stack!;
        Assert.Equal([10m], extraOnly.MissingChapters); // the extra never fills chapter 10
        Assert.Equal(1, extraOnly.ExtraCount);
    }

    [Fact]
    public void PartsOnDisk_MakeUpAListedWholeChapter()
    {
        // The 1.29.0 soak-test shape: the list says 1-5, the files are split into parts ("3" is the first part of 3 + 3.2).
        var map = new VolumeMapInput([new VolumeMapVolume(1, [1m, 2m, 3m, 4m, 5m])], null, null, true, VolumeListSource.MangaDex);
        var rows = new List<GroupingRow>
        {
            Archive("0001 [Ch. 0001 - Synthetic]"), Archive("0002 [Ch. 0002.1 - Synthetic]"), Archive("0003 [Ch. 0002.2 - Synthetic]"),
            Archive("0004 [Ch. 0003 - Synthetic]"), Archive("0005 [Ch. 0003.2 - Synthetic]"), Archive("0006 [Ch. 0004.1 - Synthetic]"),
            Archive("0007 [Ch. 0004.2 - Synthetic]"), Archive("0008 [Ch. 0004.3 - Synthetic]"), Archive("0009 [Ch. 0005.1 - Synthetic]"),
            Archive("0010 [Ch. 0005.2 - Synthetic]"),
        };
        var r = VolumeGrouping.Group(rows, map);
        var stack = Assert.Single(Stacks(r)).Stack!;

        Assert.Empty(stack.MissingChapters);
        Assert.Equal(0, r.MissingChapterCount);
        Assert.Equal((5, 5, 0), (stack.ChapterCount, stack.ChaptersPresent, stack.ExtraCount));
        Assert.All(stack.Members, m => Assert.False(m.IsExtra));
        Assert.Equal(["1", "2.1", "2.2", "3", "3.2", "4.1", "4.2", "4.3", "5.1", "5.2"], VolumeGrouping.Slots(stack).Select(s => s.Chapter));
        Assert.All(VolumeGrouping.Slots(stack), s => Assert.Equal(VolumeSlotKind.Item, s.Kind));
    }

    [Fact]
    public void AGapBetweenPartsOnDisk_IsAMissingPart()
    {
        var map = new VolumeMapInput([new VolumeMapVolume(1, [3m, 4m, 5m])], null, null, true, VolumeListSource.MangaDex);
        var stack = Assert.Single(Stacks(VolumeGrouping.Group(
            [Archive("Series c003"), Archive("Series c004.1"), Archive("Series c004.3"), Archive("Series c005")], map))).Stack!;

        Assert.Equal([4.2m], stack.MissingChapters);
        Assert.Equal((3, 2, 0), (stack.ChapterCount, stack.ChaptersPresent, stack.ExtraCount)); // chapter 4 is incomplete
        Assert.Equal(["3", "4.1", "4.2", "4.3", "5"], VolumeGrouping.Slots(stack).Select(s => s.Chapter));
        Assert.Equal(VolumeSlotKind.Missing, VolumeGrouping.Slots(stack).Single(s => s.Chapter == "4.2").Kind);
    }

    [Fact]
    public void AChapterListedInTwoVolumes_IsShownOnce_AndMissingInNeither()
    {
        // The soak-test shape: the list splits chapter 15 across volumes 3 and 4; its parts sit in volume 3's stack.
        var map = new VolumeMapInput(
            [new VolumeMapVolume(3, [11m, 12m, 13m, 14m, 15m]), new VolumeMapVolume(4, [15m, 16m, 17m])], null, null, true, VolumeListSource.MangaDex);
        var rows = new List<GroupingRow>
        {
            Archive("Series c011"), Archive("Series c012"), Archive("Series c013"), Archive("Series c014"),
            Archive("Series c015.1"), Archive("Series c015.2"), Archive("Series c015.3"), Archive("Series c016"), Archive("Series c017"),
        };
        var r = VolumeGrouping.Group(rows, map);
        var stacks = Stacks(r).Select(e => e.Stack!).ToList();

        Assert.Equal(["3", "4"], stacks.Select(s => s.Key));
        Assert.Equal(["11", "12", "13", "14", "15.1", "15.2", "15.3"], VolumeGrouping.Slots(stacks[0]).Select(s => s.Chapter));
        Assert.Equal(["16", "17"], VolumeGrouping.Slots(stacks[1]).Select(s => s.Chapter));
        Assert.All(stacks, s => Assert.Empty(s.MissingChapters));
        Assert.Equal((5, 5), (stacks[0].ChapterCount, stacks[0].ChaptersPresent));
        Assert.Equal((3, 3), (stacks[1].ChapterCount, stacks[1].ChaptersPresent));
        Assert.Equal(0, r.MissingChapterCount);

        // Without the chapter anywhere, the second volume still says it is missing.
        var without = Stacks(VolumeGrouping.Group(rows.Where(x => !x.Name.Contains("c015")).ToList(), map)).Select(e => e.Stack!).ToList();
        Assert.Equal([15m], without[1].MissingChapters);
    }

    [Fact]
    public void AnExtrasSubfolder_StaysAFolderNextToTheVolumeStacks()
    {
        // Owner (1.30.0, folder-native): Series/c001..c020 + Series/Extras/special.cbz, 10 chapters a volume -> two stacks AND the
        // "Extras" folder card; nothing inside Extras is grouped or counted.
        var map = Map(false, null, null, (1, 1, 10), (2, 11, 20));
        var rows = Chapters(1, 20).Append(Folder("Extras")).ToList();
        var r = VolumeGrouping.Group(rows, map, markMissingVolumes: true);

        Assert.Equal(["1", "2"], Stacks(r).Select(e => e.Stack!.Key));
        var folder = Assert.Single(r.Entries, e => e.Kind == VolumeEntryKind.Folder);
        Assert.Equal("Extras", folder.Row!.Name);
        Assert.Equal(0, r.MissingVolumeCount);
    }

    [Fact]
    public void WithoutAList_FractionsStayExtras()
    {
        var stack = Assert.Single(Stacks(VolumeGrouping.Group([Archive("Series v01 c004.1"), Archive("Series v01 c004.2"), Archive("Series v01 c005")], null))).Stack!;

        Assert.Equal(2, stack.ExtraCount);
        Assert.Empty(stack.MissingChapters);
        Assert.Null(stack.ChapterCount);
    }

    [Fact]
    public void BoundedVolume_IsTheOnlyVolumeBetweenTwoKnownOnes()
    {
        var rows = Chapters(478, 490).ToList();
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (49, 470, 480), (51, 490, 500)));

        var stacks = Stacks(r).Select(e => e.Stack!).ToList();
        Assert.Equal(["49", "50", "51"], stacks.Select(s => s.Key));
        var bounded = stacks[1];
        Assert.Equal(VolumeStackConfidence.Exact, bounded.Confidence);
        Assert.Equal("Volume 50", bounded.Label);
        Assert.Equal(9, bounded.PresentCount); // 481..489
        Assert.Equal(9, bounded.ChapterCount);
        Assert.Empty(bounded.MissingChapters);
        Assert.All(bounded.Members, m => Assert.Equal(VolumePlacement.Bounded, m.Placement));
    }

    [Fact]
    public void EstimatedVolumes_SplitTheGapEvenly_AndSayTheyAreEstimated()
    {
        var rows = Chapters(11, 40).ToList();
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 10), (5, 41, 50)));

        var stacks = Stacks(r).Select(e => e.Stack!).ToList();
        Assert.Equal(["2", "3", "4"], stacks.Select(s => s.Key));
        Assert.All(stacks, s => Assert.Equal(VolumeStackConfidence.Estimated, s.Confidence));
        Assert.Equal(["~ Volume 2", "~ Volume 3", "~ Volume 4"], stacks.Select(s => s.Label));
        Assert.Equal([10, 10, 10], stacks.Select(s => s.PresentCount));
        Assert.Equal("11", stacks[0].FirstChapter);
        Assert.Equal("40", stacks[2].LastChapter);
    }

    [Fact]
    public void EstimatedVolumes_AfterTheLastKnownOne_StopAtTheKnownVolumeCount()
    {
        var rows = Chapters(1, 35).ToList();
        var r = VolumeGrouping.Group(rows, Map(false, 3, 10, (1, 1, 10)));

        Assert.Equal(["1", "2", "3"], Stacks(r).Select(e => e.Stack!.Key));
        // Chapters 31..35 would be volume 4: past the known volume count they stay loose.
        var loose = r.Entries.Where(e => e.Rank == 2).ToList();
        Assert.Equal(5, loose.Count);
        Assert.Equal(VolumeStackConfidence.Estimated, Stacks(r)[1].Stack!.Confidence);
    }

    [Fact]
    public void RatioOnlyMap_EstimatesFromChapterOne()
    {
        var rows = Chapters(1, 25).ToList();
        var r = VolumeGrouping.Group(rows, Map(true, 3, 10));

        Assert.Equal(["1", "2", "3"], Stacks(r).Select(e => e.Stack!.Key));
        Assert.Equal([10, 10, 5], Stacks(r).Select(e => e.Stack!.PresentCount));
        Assert.All(Stacks(r), e => Assert.Equal(VolumeStackConfidence.Estimated, e.Stack!.Confidence));
        // The last volume of an ongoing series: its 5 missing trailing chapters are not marked.
        Assert.Empty(Stacks(r)[2].Stack!.MissingChapters);
    }

    [Fact]
    public void AChapterBetweenTwoAdjacentVolumes_GoesAtTheEndOfThePreviousVolume()
    {
        // Volume 1 ends at 10, volume 2 starts at 12: chapter 11 (and 11.5) belong to neither list - the end of volume 1.
        var rows = new List<GroupingRow>
        {
            Archive("Synthetic - Chapter 010"), Archive("Synthetic - Chapter 011"), Archive("Synthetic - c011.5"), Archive("Synthetic - Chapter 012"),
        };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 10), (2, 12, 20)));

        Assert.DoesNotContain(r.Entries, e => e.Rank == 2);
        var first = Stacks(r)[0].Stack!;
        Assert.Equal(["id:Synthetic - Chapter 010", "id:Synthetic - Chapter 011", "id:Synthetic - c011.5"], first.Members.Select(m => m.Row.Id));
        Assert.Equal(VolumePlacement.Adjacent, first.Members[1].Placement);
        // Not listed, so never counted as missing, and not part of the volume's chapter count.
        Assert.Equal(10, first.ChapterCount);
    }

    [Fact]
    public void AFractionalVolumeFile_GoesAtTheEndOfThePreviousVolumesStack()
    {
        var r = VolumeGrouping.Group([Archive("Series v02"), Archive("Series v02.5"), Archive("Series v03")], null);

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal("2", stack.Key);
        Assert.Equal(["id:Series v02", "id:Series v02.5"], stack.Members.Select(m => m.Row.Id));
        Assert.True(stack.Members[1].IsBonusVolume);
        Assert.Equal(1, stack.ExtraCount);
        Assert.Equal("id:Series v02.5", VolumeGrouping.Slots(stack)[^1].Member!.Row.Id);
        // Alone (volume 2 has nothing here) it stays its own card in its place.
        var alone = VolumeGrouping.Group([Archive("Series v01"), Archive("Series v02.5"), Archive("Series v03")], null);
        Assert.Equal(["id:Series v01", "id:Series v02.5", "id:Series v03"], alone.Entries.Select(e => e.Id));
        Assert.False(alone.Grouped);
    }

    [Fact]
    public void AFractionalListVolume_JoinsThePreviousVolume()
    {
        var map = new VolumeMapInput(
            [new VolumeMapVolume(2, [5m, 6m]), new VolumeMapVolume(2.5m, [6.1m, 6.2m])], null, null, false, VolumeListSource.MangaDex);
        var r = VolumeGrouping.Group([Archive("Series c005"), Archive("Series c006"), Archive("Series c006.1")], map);

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal("2", stack.Key);
        Assert.Equal(3, stack.PresentCount);
    }

    [Fact]
    public void MissingVolumes_AreGapsBelowTheHighestVolume_AndReleasedVolumesAfterIt()
    {
        var rows = new List<GroupingRow> { Archive("Series v01"), Archive("Series v02"), Archive("Series v05") };
        var map = VolumeMapInput.Empty with { ReleasedVolumeCount = 7 };

        var r = VolumeGrouping.Group(rows, map, markMissingVolumes: true);

        Assert.Equal(["id:Series v01", "id:Series v02", "vm:3", "vm:4", "id:Series v05", "vm:6", "vm:7"], r.Entries.Select(e => e.Id));
        Assert.Equal(4, r.MissingVolumeCount);
        Assert.True(r.Grouped);
        Assert.All(r.Entries.Where(e => e.Kind == VolumeEntryKind.MissingVolume), e => Assert.Equal(0, e.Rank));
        Assert.Equal(3m, r.Entries.First(e => e.Kind == VolumeEntryKind.MissingVolume).Volume);
        // Unknown release total: only the gaps. Not asked for (an unlinked folder, a Season subfolder): none.
        Assert.Equal(2, VolumeGrouping.Group(rows, VolumeMapInput.Empty, markMissingVolumes: true).MissingVolumeCount);
        Assert.Equal(0, VolumeGrouping.Group(rows, map).MissingVolumeCount);
    }

    [Fact]
    public void MissingVolumes_NeedAVolumeHere_AndARangeFileCoversItsRange()
    {
        var chaptersOnly = VolumeGrouping.Group(Chapters(1, 3), VolumeMapInput.Empty with { ReleasedVolumeCount = 5 }, markMissingVolumes: true);
        Assert.Equal(0, chaptersOnly.MissingVolumeCount);

        var range = VolumeGrouping.Group([Archive("Series Vol. 01-03"), Archive("Series v05")], null, markMissingVolumes: true);
        Assert.Equal(["vm:4"], range.Entries.Where(e => e.Kind == VolumeEntryKind.MissingVolume).Select(e => e.Id));
    }

    [Fact]
    public void ChapterStacks_CountAsPresentVolumes()
    {
        // Volumes 3 and 4 are stacks of chapters; 1 and 2 have nothing here.
        var r = VolumeGrouping.Group(Chapters(21, 40), Map(true, null, null, (1, 1, 10), (2, 11, 20), (3, 21, 30), (4, 31, 40)), markMissingVolumes: true);

        Assert.Equal(["vm:1", "vm:2", "vs:3", "vs:4"], r.Entries.Select(e => e.Id));
        // Chapters 1-20 belong to the missing volumes: they are not counted again as missing chapters.
        Assert.Equal(0, r.MissingChapterCount);
    }

    [Fact]
    public void GapsBetweenLooseChapters_CountAsMissingChapters()
    {
        var rows = Chapters(1, 10).Append(Archive("Synthetic - Chapter 012")).Append(Archive("Synthetic - Chapter 015")).ToList();
        var r = VolumeGrouping.Group(rows, Map(true, null, null, (1, 1, 10)));

        Assert.Equal(3, r.MissingChapterCount); // 11, 13, 14
        Assert.Equal(2, r.Entries.Count(e => e.Rank == 2));
    }

    [Fact]
    public void AChapterInsideAKnownVolumesSpan_BelongsToIt()
    {
        var rows = new List<GroupingRow> { Archive("Synthetic - Chapter 001"), Archive("Synthetic - Chapter 002"), Archive("Synthetic - Chapter 003") };
        var map = new VolumeMapInput([new VolumeMapVolume(1, [1m, 3m])], null, null, false, VolumeListSource.MangaDex);
        var r = VolumeGrouping.Group(rows, map);

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal(3, stack.PresentCount);
        Assert.Equal(VolumePlacement.Bounded, stack.Members.Single(m => m.Chapter == 2m).Placement);
    }

    [Fact]
    public void ARealVolumeArchive_MergesWithTheChaptersOfItsVolume_IntoOneStack()
    {
        var rows = new List<GroupingRow> { Archive("Series v01"), Archive("Series v01 c001"), Archive("Series v01 c002") };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 5)));

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.True(stack.HasVolumeArchive);
        Assert.Equal(3, stack.PresentCount);
        Assert.Equal("id:Series v01", stack.Members[0].Row.Id); // the volume archive first
        // The volume covers all its chapters: no placeholders, no incomplete mark.
        Assert.Empty(stack.MissingChapters);
    }

    [Fact]
    public void ARealVolumeArchive_WithoutChapters_IsAPlainCard_AndTwoEditionsAreTwoCards()
    {
        var rows = new List<GroupingRow> { Archive("Series v01"), Archive("Series v01 (Digital)"), Archive("Series v02") };
        var r = VolumeGrouping.Group(rows, null);

        Assert.False(r.Grouped);
        Assert.Equal([VolumeEntryKind.Archive, VolumeEntryKind.Archive, VolumeEntryKind.Archive], r.Entries.Select(e => e.Kind));
        Assert.Equal([0, 0, 0], r.Entries.Select(e => e.Rank));
        Assert.Equal(["id:Series v01", "id:Series v01 (Digital)", "id:Series v02"], r.Entries.Select(e => e.Id));
    }

    [Fact]
    public void ARangeVolumeArchive_OwnsTheVolumesInItsRange()
    {
        var rows = new List<GroupingRow> { Archive("Series Vol. 01-02"), Archive("Series v02 c009") };
        var r = VolumeGrouping.Group(rows, null);

        var stack = Assert.Single(Stacks(r)).Stack!;
        Assert.Equal("1", stack.Key);
        Assert.Equal(2, stack.PresentCount);
    }

    [Fact]
    public void ComicInfoVolume_GroupsAChapterWhoseNameStatesNone()
    {
        var rows = new List<GroupingRow>
        {
            Archive("Series - Chapter 001", ciVolume: 1), Archive("Series - Chapter 002", ciVolume: 1), Archive("Series - Chapter 003", ciVolume: 2),
        };
        var r = VolumeGrouping.Group(rows, null);

        Assert.Equal(["1", "2"], Stacks(r).Select(e => e.Stack!.Key));
    }

    [Fact]
    public void ComicInfoVolume_IgnoresAYear_AndTheNameWins()
    {
        var year = VolumeGrouping.Group([Archive("Series - Chapter 001", ciVolume: 2019), Archive("Series - Chapter 002", ciVolume: 2019)], null);
        Assert.False(year.Grouped);

        var named = VolumeGrouping.Group([Archive("Series v03 c001", ciVolume: 1), Archive("Series v03 c002", ciVolume: 1)], null);
        Assert.Equal("3", Assert.Single(Stacks(named)).Stack!.Key);
    }

    [Fact]
    public void ComicInfo_NumberBecomesTheChapter_ButAVolumesOwnNumberDoesNot()
    {
        var chapters = VolumeGrouping.Group([Archive("Untitled A", ciVolume: 2, ciNumber: "5"), Archive("Untitled B", ciVolume: 2, ciNumber: "6")], null);
        var stack = Stacks(chapters)[0].Stack!;
        Assert.Equal("5", stack.FirstChapter);
        Assert.Equal("6", stack.LastChapter);

        // Volume 2, Number 2: the book's own issue number - a volume archive, a plain card.
        var volume = VolumeGrouping.Group([Archive("Untitled Book", ciVolume: 2, ciNumber: "2")], null);
        Assert.False(volume.Grouped);
        Assert.Equal((VolumeEntryKind.Archive, 0), (volume.Entries[0].Kind, volume.Entries[0].Rank));
    }

    [Fact]
    public void InAVolumesFolder_ABareNumberIsAVolume()
    {
        var rows = new List<GroupingRow> { Archive("01.cbz", "Volumes"), Archive("02.cbz", "Volumes"), Archive("Series - Chapter 020") };
        var r = VolumeGrouping.Group(rows, null);

        Assert.Equal([0, 0, 2], r.Entries.Select(e => e.Rank));
        Assert.Equal(["id:01.cbz", "id:02.cbz", "id:Series - Chapter 020"], r.Entries.Select(e => e.Id));
    }

    [Fact]
    public void NothingGroups_WhenNoNameOrMapStatesAVolume()
    {
        var r = VolumeGrouping.Group(Chapters(1, 12), null);

        Assert.False(r.Grouped);
        Assert.Equal(12, r.Entries.Count);
        Assert.All(r.Entries, e => Assert.Equal(2, e.Rank));
    }

    [Fact]
    public void Slots_PutTheVolumeArchiveFirst_AndAPlaceholderWhereAChapterIsMissing()
    {
        var rows = Chapters(36, 38).Concat(Chapters(40, 44)).ToList();
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (5, 36, 44)));

        var stack = Assert.Single(Stacks(r)).Stack!;
        var slots = VolumeGrouping.Slots(stack);
        Assert.Equal(["36", "37", "38", "39", "40", "41", "42", "43", "44"], slots.Select(s => s.Chapter));
        Assert.Equal([VolumeSlotKind.Missing], slots.Where(s => s.Kind == VolumeSlotKind.Missing).Select(s => s.Kind));
        Assert.Equal("39", slots.Single(s => s.Kind == VolumeSlotKind.Missing).Chapter);
        Assert.Null(slots.Single(s => s.Kind == VolumeSlotKind.Missing).Member);
    }

    [Fact]
    public void VolumeKeys_AreSortableAndCanonical()
    {
        Assert.Equal("00003.00", VolumeGrouping.SortableKey(3m));
        Assert.Equal("00002.50", VolumeGrouping.SortableKey(2.5m));
        Assert.True(string.CompareOrdinal(VolumeGrouping.SortableKey(9m), VolumeGrouping.SortableKey(10m)) < 0);
        Assert.Equal("3", VolumeGrouping.KeyOf(3.00m));
        Assert.Equal("2.5", VolumeGrouping.KeyOf(2.50m));
        Assert.Equal("~ Volume 12", VolumeGrouping.LabelOf(12m, VolumeStackConfidence.Estimated));
    }

    [Fact]
    public void StackKeys_ListTheStacksInPositionOrder()
    {
        var rows = new List<GroupingRow> { Archive("Series v03 c010"), Archive("Series v01 c001"), Archive("Series v02"), Archive("Series v02 c005") };
        var r = VolumeGrouping.Group(rows, null);

        Assert.Equal(["1", "2", "3"], VolumeGrouping.StackKeys(r.Entries));
    }
}
