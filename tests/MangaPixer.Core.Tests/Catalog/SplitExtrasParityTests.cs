namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Tests.Core.Metadata;
using Xunit;

/// <summary>
/// Every reader of <c>MissingUnits.SplitsOf</c> agrees on a folder whose split chapters sit next to their extras (owner, 2026-10-15:
/// MangaList parity). The folder holds 6.1 + 6.2 (chapter 6 in two parts), 6.5 "Extras" and 6.6 "Bonus" - and the same shape for 7, 8,
/// 9, 11, 12 and 13. Before, 6.6 continued the split (only .5 waited for .4), so 6.3, 6.4 and the held 6.5 were missing parts: the
/// Volumes view showed them as missing, chapter 6 never counted as read for the trackers. Made-up names
/// (<see cref="MissingUnitsTests.SplitFolderNames"/>).
/// </summary>
public sealed class SplitExtrasParityTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static List<GroupingRow> Rows() => MissingUnitsTests.SplitFolderNames().Select(Archive).ToList();

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    // MangaDex's exact list: volume 1 = 1-6, volume 2 = 7-12, volume 3 = 13-19.
    private static VolumeMapInput Map() => new(
        [new VolumeMapVolume(1, Range(1, 6)), new VolumeMapVolume(2, Range(7, 12)), new VolumeMapVolume(3, Range(13, 19))],
        6.3, 3, true, VolumeListSource.MangaDex);

    [Fact]
    public void VolumesView_ShowsNoMissingPart_AndEveryChapterPresent()
    {
        var r = VolumeGrouping.Group(Rows(), Map());
        var stacks = r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToList();

        Assert.Equal(["1", "2", "3"], stacks.Select(s => s.Key));
        Assert.All(stacks, s => Assert.Empty(s.MissingChapters));
        Assert.Equal(0, r.MissingChapterCount);
        Assert.Equal([(6, 6), (6, 6), (7, 7)], stacks.Select(s => (s.ChapterCount!.Value, s.ChaptersPresent!.Value)));
        // The extras after N.5 are extras, not parts: 6.5 and 6.6 in volume 1.
        var first = stacks[0].Members.Where(m => m.Chapter is 6.5m or 6.6m).ToList();
        Assert.Equal(2, first.Count);
        Assert.All(first, m => Assert.True(m.IsExtra));
        Assert.DoesNotContain(VolumeGrouping.Slots(stacks[0]), s => s.Kind == VolumeSlotKind.Missing);
    }

    [Fact]
    public void Reach_HoldsEveryVolumeAsChapters_ForTheVolumeCovers()
    {
        var reach = SeriesReach.Of(Rows(), Map());

        Assert.Equal([1, 2, 3], reach.HeldAsChapters.Order());
        Assert.Empty(reach.PartialVolumes);
        Assert.Equal(19, reach.ReachChapter);
    }

    [Fact]
    public void Trackers_CountASplitChapterRead_WhenItsPartsAreRead_ItsExtrasUnread()
    {
        // Volume 1's files (1-5, 6.1, 6.2, 6.5 "Extras", 6.6 "Bonus"), all read except the extras: chapter 6 is read, so volume 1 is too.
        var rows = Rows().Where(r => r.Name.Contains("Vol. 001", StringComparison.Ordinal))
            .Select(r => new TrackerProgressRow(r, Read: !IsExtra(r.Name))).ToList();
        Assert.Equal(9, rows.Count);
        Assert.Equal(1, TrackerProgress.Compute(rows, Map(), false).Automatic.Volumes);

        // A part unread keeps its chapter (and so the volume) out.
        var partUnread = rows.Select(r => r.Row.Name.StartsWith("Ch. 0006.20", StringComparison.Ordinal) ? r with { Read = false } : r).ToList();
        Assert.Null(TrackerProgress.Compute(partUnread, Map(), false).Automatic.Volumes);
    }

    private static bool IsExtra(string name) =>
        name.Contains("(Extras", StringComparison.Ordinal) || name.Contains("(Bonus", StringComparison.Ordinal)
        || name.Contains("(Afterword", StringComparison.Ordinal);
}
