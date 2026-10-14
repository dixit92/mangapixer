namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// The progress rules of tracker sync (1.43.0, owner answers of 2026-10-13): read marks only; the highest WHOLE chapter marked (extras and
/// <c>.5</c> never raise it, a hole does not hold it back); volumes only from marked volume archives through an EXACT list; no volumes for
/// chapters-only series or a declared edition; bulk / import marks counted apart (held for "Send"). Synthetic names only.
/// </summary>
public sealed class TrackerProgressTests
{
    private static TrackerProgressRow Row(string name, bool read = true, bool held = false, string? container = null) =>
        new(new GroupingRow("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant(), container), read, held);

    private static VolumeMapInput Exact(VolumeListSource source = VolumeListSource.MangaDex, bool chaptersOnly = false) => new(
        [new VolumeMapVolume(1, [1m, 2m, 3m, 3.5m, 4m]), new VolumeMapVolume(2, [5m, 6m, 7m, 8m]), new VolumeMapVolume(3, [9m, 10m, 11m, 12m])],
        4.0, 3, true, source, ChaptersOnly: chaptersOnly);

    [Fact]
    public void Chapters_AreTheHighestWholeChapterMarked_AHoleDoesNotHoldThemBack()
    {
        var result = TrackerProgress.Compute([Row("Chapter 001"), Row("Chapter 002", read: false), Row("Chapter 007")], null, false);

        Assert.Equal(new TrackerNumbers(7, null), result.Automatic);
        Assert.Equal(result.Automatic, result.All);
    }

    [Fact]
    public void ExtrasAndHalfChapters_NeverRaiseChapters_ARangeCountsItsEnd()
    {
        Assert.Equal(new TrackerNumbers(4, null), TrackerProgress.Compute([Row("Chapter 004"), Row("Chapter 004.5")], null, false).Automatic);
        Assert.Equal(TrackerNumbers.None, TrackerProgress.Compute([Row("Chapter 012.5")], null, false).Automatic);
        Assert.Equal(new TrackerNumbers(15, null), TrackerProgress.Compute([Row("Chapter 010-015")], null, false).Automatic);
    }

    [Fact]
    public void Unread_SendsNothing()
    {
        Assert.True(TrackerProgress.Compute([Row("Chapter 001", read: false), Row("Chapter 002", read: false)], Exact(), false).All.IsEmpty);
    }

    [Fact]
    public void AVolumeArchive_CountsItsChaptersAndItself_OnlyThroughAnExactList()
    {
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute([Row("Volume 02")], Exact(), false).Automatic);
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute([Row("Volume 02")], Exact(VolumeListSource.Wikipedia), false).Automatic);
        // The AniList ratio (or file names alone) is an estimate: neither volumes nor chapters come from it.
        var ratio = new VolumeMapInput([], 4.0, 3, true, VolumeListSource.AniList);
        Assert.Equal(TrackerNumbers.None, TrackerProgress.Compute([Row("Volume 02")], ratio, false).Automatic);
        Assert.Equal(TrackerNumbers.None, TrackerProgress.Compute([Row("Volume 02")], null, false).Automatic);
    }

    [Fact]
    public void AChapterArchiveThatStatesItsVolume_CountsItsChapter_NotTheVolume()
    {
        Assert.Equal(new TrackerNumbers(17, null), TrackerProgress.Compute([Row("Vol. 03 Chapter 017")], Exact(), false).Automatic);
    }

    [Fact]
    public void AChaptersOnlySeries_SendsNoVolumes()
    {
        var result = TrackerProgress.Compute([Row("Volume 02"), Row("Chapter 020")], Exact(chaptersOnly: true), false);
        Assert.Equal(new TrackerNumbers(20, null), result.Automatic);
    }

    [Fact]
    public void ADeclaredEdition_NeverSendsVolumeNumbers_NorMapsItsVolumesToChapters()
    {
        var result = TrackerProgress.Compute([Row("Volume 02"), Row("Chapter 006")], Exact(), declaredEdition: true);
        Assert.Equal(new TrackerNumbers(6, null), result.Automatic);
    }

    [Fact]
    public void HeldMarks_CountOnlyInAll()
    {
        var result = TrackerProgress.Compute([Row("Chapter 003"), Row("Chapter 009", held: true), Row("Volume 03", held: true)], Exact(), false);

        Assert.Equal(new TrackerNumbers(3, null), result.Automatic);
        Assert.Equal(new TrackerNumbers(12, 3), result.All);
    }

    [Fact]
    public void ABareNumberInAVolumesFolder_IsAVolume()
    {
        Assert.Equal(new TrackerNumbers(8, 2), TrackerProgress.Compute([Row("02", container: "Volumes")], Exact(), false).Automatic);
    }
}
