namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// A volume list that names a chapter 0 (1.43.2, owner): MangaDex lists a prologue, pilot or announcement as chapter 0 of volume 1, and
/// many releases have none. Before, a folder holding chapters 1-6 showed volume 1 as "5 of 6" with a "Ch. 0 - missing" placeholder. A
/// listed 0 now counts only when a file numbered 0 is here; every other number keeps its rules. Made-up names in the owner's shape.
/// </summary>
public sealed class ChapterZeroTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static List<GroupingRow> Chapters(params int[] numbers) =>
        numbers.Select(n => Archive($"Ch. {n:0000}.00.cbz")).ToList();

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    // MangaDex's list: volume 1 = 0-6 (chapter 0 a prologue), volume 2 = 7-12.
    private static VolumeMapInput Map() => new(
        [new VolumeMapVolume(1, Range(0, 6)), new VolumeMapVolume(2, Range(7, 12))],
        6.5, 2, true, VolumeListSource.MangaDex);

    private static VolumeStack Volume1(IReadOnlyList<GroupingRow> rows, out VolumeGroupingResult result)
    {
        result = VolumeGrouping.Group(rows, Map());
        return result.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).Single(s => s.Key == "1");
    }

    [Fact]
    public void AListedChapterZero_WithNoFileZero_IsNeverMissing_NorCounted()
    {
        var stack = Volume1(Chapters(1, 2, 3, 4, 5, 6, 7), out var result);

        Assert.Empty(stack.MissingChapters);
        Assert.DoesNotContain(VolumeGrouping.Slots(stack), s => s.Kind == VolumeSlotKind.Missing);
        Assert.Equal((6, 6), (stack.ChapterCount!.Value, stack.ChaptersPresent!.Value)); // "6 of 6", not "5 of 6"
        Assert.DoesNotContain(0m, result.MissingChapterUnits);
    }

    [Fact]
    public void AFileNumberedZero_CountsTheListedZero()
    {
        var stack = Volume1(Chapters(0, 1, 2, 3, 4, 5, 6, 7), out _);

        Assert.Empty(stack.MissingChapters);
        Assert.Equal((7, 7), (stack.ChapterCount!.Value, stack.ChaptersPresent!.Value));
        Assert.Contains(stack.Members, m => m.Chapter == 0m && !m.IsExtra);
    }

    [Fact]
    public void EveryOtherNumber_KeepsItsRules()
    {
        // Chapter 3 is not here and 7 is: 3 is missing as before; the listed 0 still is not.
        var stack = Volume1(Chapters(1, 2, 4, 5, 6, 7), out _);

        Assert.Equal([3m], stack.MissingChapters);
        Assert.Equal((6, 5), (stack.ChapterCount!.Value, stack.ChaptersPresent!.Value));
    }
}
