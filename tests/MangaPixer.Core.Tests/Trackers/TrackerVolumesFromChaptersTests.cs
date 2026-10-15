namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// Owner E1 (1.43.0, "NOW in 1.43.0"): a volume counts as read when EVERY whole chapter the EXACT list (MangaDex aggregate / Wikipedia)
/// gives it is marked read in chapter files - never through the AniList ratio or an estimate, never for a declared edition or a chapters-only
/// series. Golden cases on a synthetic list; names are synthetic.
/// </summary>
public sealed class TrackerVolumesFromChaptersTests
{
    private static TrackerProgressRow Row(string name, bool read = true, bool held = false) =>
        new(new GroupingRow("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant()), read, held);

    // Volume 1: 1-4 and the extra 3.5; volume 2: 5-8; volume 3: 9-12; volume 4 lists only an extra (12.5).
    private static VolumeMapInput List(VolumeListSource source = VolumeListSource.MangaDex, bool chaptersOnly = false) => new(
        [
            new VolumeMapVolume(1, [1m, 2m, 3m, 3.5m, 4m]),
            new VolumeMapVolume(2, [5m, 6m, 7m, 8m]),
            new VolumeMapVolume(3, [9m, 10m, 11m, 12m]),
            new VolumeMapVolume(4, [12.5m]),
        ],
        4.0, 4, true, source, ChaptersOnly: chaptersOnly);

    private static IEnumerable<TrackerProgressRow> Chapters(int from, int to, bool held = false) =>
        Enumerable.Range(from, to - from + 1).Select(c => Row($"Chapter {c:000}", held: held));

    [Fact]
    public void EveryChapterOfAVolumeMarked_CountsTheVolume()
    {
        var result = TrackerProgress.Compute([.. Chapters(1, 8)], List(), false);
        Assert.Equal(new TrackerNumbers(8, 2), result.Automatic);
    }

    [Fact]
    public void TheExtraOfAVolume_IsNeverRequired()
    {
        // 3.5 is not on disk: volume 1 still counts.
        Assert.Equal(new TrackerNumbers(4, 1), TrackerProgress.Compute([.. Chapters(1, 4)], List(), false).Automatic);
    }

    [Fact]
    public void OneChapterUnread_HoldsThatVolumeBack()
    {
        var rows = Chapters(1, 8).Select(r => r.Row.Name == "Chapter 007" ? r with { Read = false } : r).ToList();
        Assert.Equal(new TrackerNumbers(8, 1), TrackerProgress.Compute(rows, List(), false).Automatic);
    }

    [Fact]
    public void AHoleInAnEarlierVolume_DoesNotHoldBackALaterCompleteOne()
    {
        // Chapter 2 is missing on disk; volume 2 (5-8) is complete: the highest complete volume counts, as for chapters.
        var rows = Chapters(1, 8).Where(r => r.Row.Name != "Chapter 002").ToList();
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute(rows, List(), false).Automatic);
    }

    [Fact]
    public void AVolumeListingOnlyAnExtra_NeverCountsThisWay()
    {
        var rows = Chapters(1, 12).Append(Row("Chapter 012.5")).ToList();
        Assert.Equal(new TrackerNumbers(12, 3), TrackerProgress.Compute(rows, List(), false).Automatic);
    }

    [Fact]
    public void ARangeFile_CoversItsRange()
    {
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute([.. Chapters(1, 4), Row("Chapter 005-008")], List(), false).Automatic);
    }

    [Fact]
    public void ASplitChapter_CountsWhenAllItsPartsAreMarked()
    {
        var complete = Chapters(5, 8).Where(r => r.Row.Name != "Chapter 006").Concat([Row("Chapter 006.1"), Row("Chapter 006.2")]).ToList();
        Assert.Equal(2, TrackerProgress.Compute(complete, List(), false).Automatic.Volumes);

        var partUnread = complete.Select(r => r.Row.Name == "Chapter 006.2" ? r with { Read = false } : r).ToList();
        Assert.Null(TrackerProgress.Compute(partUnread, List(), false).Automatic.Volumes);
    }

    [Fact]
    public void HeldMarks_CountTheVolumeOnlyInAll()
    {
        var result = TrackerProgress.Compute([.. Chapters(1, 4), .. Chapters(5, 8, held: true)], List(), false);
        Assert.Equal(new TrackerNumbers(4, 1), result.Automatic);
        Assert.Equal(new TrackerNumbers(8, 2), result.All);
    }

    [Fact]
    public void WikipediaIsExact_TheAniListRatioAndChaptersOnlyAndADeclaredEditionAreNot()
    {
        Assert.Equal(2, TrackerProgress.Compute([.. Chapters(1, 8)], List(VolumeListSource.Wikipedia), false).Automatic.Volumes);
        Assert.Null(TrackerProgress.Compute([.. Chapters(1, 8)], List(VolumeListSource.AniList), false).Automatic.Volumes);
        Assert.Null(TrackerProgress.Compute([.. Chapters(1, 8)], List(chaptersOnly: true), false).Automatic.Volumes);
        Assert.Null(TrackerProgress.Compute([.. Chapters(1, 8)], List(), declaredEdition: true).Automatic.Volumes);
        Assert.Null(TrackerProgress.Compute([.. Chapters(1, 8)], null, false).Automatic.Volumes);
    }

    [Fact]
    public void AVolumeFileAndChapterFiles_TheHigherVolumeWins()
    {
        // Volume 1 as a file, volume 2 as its chapters.
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute([Row("Volume 01"), .. Chapters(5, 8)], List(), false).Automatic);
    }
}
