namespace com.lifepixer.mangapixer.Tests.Core.Trackers;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using Xunit;

/// <summary>
/// The one-shot rule (1.43.0, owner bug on the review instance): an archive whose name states no number and that is the ONLY archive of
/// the series' scope counts as chapter 1 / volume 1 - for MangaPixer's numbers and for "read on the tracker". Unnumbered archives next to
/// other archives stay uncounted. Golden cases; synthetic names.
/// </summary>
public sealed class TrackerOneShotTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static GroupingRow Folder(string name) => new("f:" + name, GroupingRowKind.Folder, name, name.ToLowerInvariant());

    private static readonly VolumeMapInput ChaptersOnly = new([new VolumeMapVolume(1, [1m])], null, 1, false, VolumeListSource.MangaDex,
        ChaptersOnly: true);

    [Fact]
    public void TheOnlyUnnumberedArchive_IsAOneShot_ChapterOneVolumeOne()
    {
        var units = TrackerUnits.Of([Archive("Lonely Story")]);
        Assert.Equal(new TrackerUnit(TrackerUnits.OneShotUnits, true), Assert.Single(units));
        Assert.Equal((1m, 1m), (units[0].Units.Chapter!.Value, units[0].Units.Volume!.Value));

        // Folders around it do not count as archives.
        Assert.True(TrackerUnits.Of([Folder("Extras folder"), Archive("Lonely Story")])[1].OneShot);
    }

    [Fact]
    public void NextToAnotherArchive_OrWithANumber_ItIsNot()
    {
        var two = TrackerUnits.Of([Archive("Chapter 001"), Archive("Extras")]);
        Assert.All(two, u => Assert.False(u.OneShot));
        Assert.True(two[1].Units.IsEmpty);
        Assert.False(TrackerUnits.Of([Archive("Volume 03")])[0].OneShot);
        Assert.False(TrackerUnits.Of([Archive("Lonely Story"), Archive("000")])[0].OneShot);
    }

    [Fact]
    public void Progress_CountsAReadOneShot_AsChapterOneVolumeOne()
    {
        Assert.Equal(new TrackerNumbers(1, 1), TrackerProgress.Compute([new TrackerProgressRow(Archive("Lonely Story"), true)], null, false).Automatic);
        Assert.True(TrackerProgress.Compute([new TrackerProgressRow(Archive("Lonely Story"), false)], null, false).All.IsEmpty);
        Assert.Equal(new TrackerNumbers(1, null), TrackerProgress.Compute([new TrackerProgressRow(Archive("Lonely Story"), true)], ChaptersOnly, false).Automatic);
        Assert.Equal(new TrackerNumbers(1, null), TrackerProgress.Compute([new TrackerProgressRow(Archive("Lonely Story"), true)], null, true).Automatic);
        var held = TrackerProgress.Compute([new TrackerProgressRow(Archive("Lonely Story"), true, Held: true)], null, false);
        Assert.Equal((TrackerNumbers.None, new TrackerNumbers(1, 1)), (held.Automatic, held.All));
        // Two archives: the unnumbered one still counts nothing.
        Assert.Equal(TrackerNumbers.None,
            TrackerProgress.Compute([new TrackerProgressRow(Archive("Chapter 001"), false), new TrackerProgressRow(Archive("Extras"), true)], null, false).All);
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(null, 1, true)]
    [InlineData(3, 2, true)]
    [InlineData(0, 0, false)]
    [InlineData(null, null, false)]
    public void AOneShot_IsReadOnTheTracker_ByEitherNumber(int? chapters, int? volumes, bool expected)
    {
        Assert.Equal(expected, TrackerReadOn.Covers(TrackerUnits.OneShotUnits, new TrackerNumbers(chapters, volumes), null, false, oneShot: true));
        // Without the flag, an unnumbered file is never read this way.
        Assert.False(TrackerReadOn.Covers(default, new TrackerNumbers(chapters, volumes), null, false));
    }
}
