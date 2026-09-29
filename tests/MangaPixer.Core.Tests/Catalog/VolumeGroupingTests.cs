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
        Assert.Equal(["Vol. 1", "Vol. 2"], stacks.Select(s => s.Label));
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
        Assert.Equal("Vol. 1", r.Entries[0].Stack!.Label);
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
    public void LastVolume_OfAnOngoingSeries_DoesNotMarkTrailingChapters()
    {
        var rows = Chapters(1, 3).Concat(Chapters(4, 5)).ToList(); // volume 2 lists 4..8, only 4-5 on disk
        var ongoing = VolumeGrouping.Group(rows, Map(true, null, null, (1, 1, 3), (2, 4, 8)));
        var complete = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 3), (2, 4, 8)));

        Assert.Empty(Stacks(ongoing)[1].Stack!.MissingChapters);
        Assert.Equal([6, 7, 8], Stacks(complete)[1].Stack!.MissingChapters);
        // A volume that is not the last one always marks trailing chapters.
        var first = VolumeGrouping.Group(Chapters(1, 2).Concat(Chapters(4, 8)).ToList(), Map(true, null, null, (1, 1, 3), (2, 4, 8)));
        Assert.Equal([3], Stacks(first)[0].Stack!.MissingChapters);
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
        Assert.Equal("Vol. 50", bounded.Label);
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
        Assert.Equal(["~ Vol. 2", "~ Vol. 3", "~ Vol. 4"], stacks.Select(s => s.Label));
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
    public void AChapterBetweenTwoAdjacentVolumes_StaysLoose()
    {
        // Volume 1 ends at 10, volume 2 starts at 12: chapter 11 could be in either.
        var rows = new List<GroupingRow> { Archive("Synthetic - Chapter 010"), Archive("Synthetic - Chapter 011"), Archive("Synthetic - Chapter 012") };
        var r = VolumeGrouping.Group(rows, Map(false, null, null, (1, 1, 10), (2, 12, 20)));

        Assert.Equal(2, Stacks(r).Count);
        var loose = Assert.Single(r.Entries, e => e.Rank == 2);
        Assert.Equal("id:Synthetic - Chapter 011", loose.Row!.Id);
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
        Assert.Equal("~ Vol. 12", VolumeGrouping.LabelOf(12m, VolumeStackConfidence.Estimated));
    }

    [Fact]
    public void StackKeys_ListTheStacksInPositionOrder()
    {
        var rows = new List<GroupingRow> { Archive("Series v03 c010"), Archive("Series v01 c001"), Archive("Series v02"), Archive("Series v02 c005") };
        var r = VolumeGrouping.Group(rows, null);

        Assert.Equal(["1", "2", "3"], VolumeGrouping.StackKeys(r.Entries));
    }
}
