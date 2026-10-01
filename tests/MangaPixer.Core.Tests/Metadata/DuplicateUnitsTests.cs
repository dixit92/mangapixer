namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using Xunit;

/// <summary>
/// Unit tests for duplicate chapter / volume numbers (1.31.0): the pure finder, the per-folder rule, what is NOT a duplicate
/// (split chapters, ranges, restarted seasons), and the stack's distinct present / extra counts. Synthetic names only.
/// </summary>
public sealed class DuplicateUnitsTests
{
    private static int _seq;

    private static UnitNumbers U(string name) => AutoMatchText.UnitsOf(name);

    private static GroupingRow Row(string name, string? container = null) =>
        new("id" + ++_seq, GroupingRowKind.Archive, name, name.ToLowerInvariant(), container);

    private static IReadOnlyList<(MissingUnitKind, decimal, int)> Found(IEnumerable<DuplicateUnit> d) =>
        d.Select(x => (x.Kind, x.Number, x.Files)).ToList();

    [Fact]
    public void TheSameChapterInTwoFiles_IsADuplicate_WithItsFileCount()
    {
        var found = DuplicateUnits.Find(new[] { "Series c001", "Series c001 [2]", "Series c002", "Series c002 [2]", "Series c002 [3]", "Series c003" }.Select(U));

        Assert.Equal([(MissingUnitKind.Chapter, 1m, 2), (MissingUnitKind.Chapter, 2m, 3)], Found(found));
    }

    [Fact]
    public void NothingRepeated_IsNoDuplicate()
    {
        Assert.Empty(DuplicateUnits.Find(new[] { "Series c001", "Series c002", "Series c003" }.Select(U)));
        Assert.Empty(DuplicateUnits.Find([]));
    }

    [Fact]
    public void SplitChaptersRangesAndNamesWithoutANumber_AreNotDuplicates()
    {
        var names = new[] { "Series c002", "Series c002.1", "Series c002.2", "Series c005-c007", "Series c005-c007 [2]", "Series c006", "Cover", "Notes" };

        Assert.Empty(DuplicateUnits.Find(names.Select(U)));
    }

    [Fact]
    public void ADuplicatedExtraIsADuplicate_OfItsOwnNumber()
    {
        var found = DuplicateUnits.Find(new[] { "Series c010", "Series c010.5", "Series c010.5 [2]" }.Select(U));

        Assert.Equal([(MissingUnitKind.Chapter, 10.5m, 2)], Found(found));
    }

    [Fact]
    public void VolumesAndChaptersAreCountedApart_AndAFileNamingBothIsAChapter()
    {
        var found = DuplicateUnits.Find(new[] { "Series v03", "Series v03 [2]", "Series v03 c012", "Series v04 c012", "Series c001" }.Select(U));

        // Volume 3 twice; chapter 12 twice (v03 c012 and v04 c012 are chapter 12 of different volumes - the same chapter number).
        Assert.Equal([(MissingUnitKind.Volume, 3m, 2), (MissingUnitKind.Chapter, 12m, 2)], Found(found));
    }

    [Fact]
    public void FindIn_ComparesEachContainerOnItsOwn()
    {
        var rows = new List<GroupingRow>
        {
            Row("Series c001", "Season 1"), Row("Series c002", "Season 1"),
            Row("Series c001", "Season 2"), Row("Series c002", "Season 2"), // numbering restarts: not a duplicate
            Row("Series c003", "Season 2"), Row("Series c003 [2]", "Season 2"), // a real one
        };

        Assert.Equal([(MissingUnitKind.Chapter, 3m, 2)], Found(DuplicateUnits.FindIn(rows)));
    }

    [Fact]
    public void FindIn_ReadsABareNumberInAVolumesFolderAsAVolume_AndIgnoresFolders()
    {
        var rows = new List<GroupingRow>
        {
            Row("01", "Volumes"), Row("01 (2nd copy)", "Volumes"), Row("Series c001"),
            new("f1", GroupingRowKind.Folder, "Extras", "extras"), new("f2", GroupingRowKind.Folder, "Extras", "extras"),
        };

        var found = DuplicateUnits.FindIn(rows);

        Assert.Equal([(MissingUnitKind.Volume, 1m, 2)], Found(found));
        Assert.Equal(new DuplicateUnitDto { Kind = MissingUnitKind.Volume, Number = "1", Files = 2 }, DuplicateUnits.ToDto(found[0]));
    }

    // --- The stack ---

    private static VolumeMapInput Map(int volume, int from, int to) =>
        new([new VolumeMapVolume(volume, Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList())], null, null, false, VolumeListSource.MangaDex);

    [Fact]
    public void Stack_CountsEachChapterOnce_AndNamesTheDuplicates()
    {
        // The owner's shape: chapters 1 and 2 each uploaded twice (one chapter split into two files), 3 once; the list says 1-9.
        var rows = new List<GroupingRow> { Row("Series v01 c001"), Row("Series v01 c001 [part 2]"), Row("Series v01 c002"), Row("Series v01 c002 [part 2]"), Row("Series v01 c003") };

        var stack = Assert.Single(VolumeGrouping.Group(rows, Map(1, 1, 9)).Entries.Where(e => e.Kind == VolumeEntryKind.Stack)).Stack!;

        Assert.Equal(5, stack.Members.Count);
        Assert.Equal(3, stack.PresentCount); // distinct chapters: an "8/9" mark could not be reached by counting files
        Assert.Equal((9, 3), (stack.ChapterCount, stack.ChaptersPresent));
        Assert.Equal([(MissingUnitKind.Chapter, 1m, 2), (MissingUnitKind.Chapter, 2m, 2)], Found(stack.Duplicates));
        Assert.Equal(5, VolumeGrouping.Slots(stack).Count(s => s.Kind == VolumeSlotKind.Item)); // every file keeps its own card
    }

    [Fact]
    public void Stack_WithoutDuplicates_IsUnchanged()
    {
        var rows = new List<GroupingRow> { Row("Series v01 c001"), Row("Series v01 c002"), Row("Series v01 c002.1"), Row("Series v01 c002.2") };

        var stack = Assert.Single(VolumeGrouping.Group(rows, null).Entries.Where(e => e.Kind == VolumeEntryKind.Stack)).Stack!;

        Assert.Empty(stack.Duplicates);
        Assert.Equal(stack.Members.Count, stack.PresentCount);
    }

    [Fact]
    public void Stack_ADuplicatedExtra_IsOneExtra()
    {
        var rows = new List<GroupingRow> { Row("Series v01 c001"), Row("Series v01 c002"), Row("Series v01 c002.5"), Row("Series v01 c002.5 [2]") };

        var stack = Assert.Single(VolumeGrouping.Group(rows, Map(1, 1, 2)).Entries.Where(e => e.Kind == VolumeEntryKind.Stack)).Stack!;

        Assert.Equal((3, 1), (stack.PresentCount, stack.ExtraCount));
        Assert.Equal([(MissingUnitKind.Chapter, 2.5m, 2)], Found(stack.Duplicates));
    }

    [Fact]
    public void Stack_TwoEditionsOfAVolumeFile_AreNotChapterDuplicates()
    {
        var rows = new List<GroupingRow> { Row("Series v01"), Row("Series v01 [other scan]"), Row("Series v01 c001") };

        var stack = Assert.Single(VolumeGrouping.Group(rows, null).Entries.Where(e => e.Kind == VolumeEntryKind.Stack)).Stack!;

        Assert.Empty(stack.Duplicates);
        Assert.Equal(3, stack.PresentCount);
    }
}
