namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>The count rule extracted from the scorer (1.29.0): local unit numbers vs published totals. Synthetic names only.</summary>
public sealed class CountEvidenceTests
{
    private static PublishedUnitCounts Published(int? volumes = null, int? englishVolumes = null, int? statusChapters = null,
        int? englishChapters = null, int? latest = null) => new(volumes, englishVolumes, statusChapters, englishChapters, latest);

    private static ChildFolderShape Sub(string name, params string[] archives) => new(name, archives.Length, archives);

    [Fact]
    public void LocalOf_ReadsLooseNames_AsHighestNumbers()
    {
        var local = CountEvidence.LocalOf(["Some Series v01.cbz", "Some Series v02.cbz", "Some Series v02.5.cbz", "Some Series v06.cbz"], null);

        Assert.Equal(new LocalUnitCounts(4, 0, 1, 6, null, null), local);
        Assert.False(local.IsMixed);
    }

    [Fact]
    public void LocalOf_SeasonSubfolders_AddTheirNumbers_SideFoldersDoNot()
    {
        var local = CountEvidence.LocalOf(["000.cbz"],
        [
            Sub("Season 1", "Some Series - Chapter 001.cbz", "Some Series - Chapter 002.cbz"),
            Sub("Season 2", "Some Series - Chapter 003.cbz", "Some Series - Chapter 004.5.cbz"),
            Sub("Extras", "Some Series - Chapter 900.cbz"),
            Sub("Another Work", "Another Work - Chapter 700.cbz"), // not a unit subfolder: a separate work
        ]);

        Assert.Equal(new LocalUnitCounts(0, 5, null, null, 0, 4), local);
    }

    [Fact]
    public void LocalOf_VolumesSubfolder_ReadsBareNumbers_AsVolumes()
    {
        var local = CountEvidence.LocalOf([], [Sub("Volumes", "01.cbz", "02.cbz", "03 [Final].cbz")]);

        Assert.Equal(new LocalUnitCounts(3, 0, 1, 3, null, null), local);
    }

    [Fact]
    public void LocalOf_VolumesNextToChapters_IsMixed()
    {
        var local = CountEvidence.LocalOf(["Some Series Ch 015.cbz"], [Sub("Volumes", "Some Series v01.cbz")]);

        Assert.True(local.IsMixed);
        Assert.Equal(CountComparison.None, CountEvidence.Compare(local, Published(volumes: 1, statusChapters: 2)));
    }

    [Theory]
    [InlineData("Extras", true)]
    [InlineData("Side Stories", true)]
    [InlineData("Colored", true)]
    [InlineData("Oneshots", true)]
    [InlineData("Season 2", false)]
    [InlineData("Volumes", false)]
    [InlineData("One Piece of Work", false)] // not a unit folder at all
    public void IsSideFolderName_KnowsTheSideMaterialWords(string name, bool expected) =>
        Assert.Equal(expected, CountEvidence.IsSideFolderName(name));

    [Fact]
    public void Compare_VolumesWithVolumeTotals_Only()
    {
        var volumes = new LocalUnitCounts(12, 0, 1, 12, null, null);

        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(volumes, Published(volumes: 10)).Volumes);
        Assert.Equal(CountSignal.Conflict, CountEvidence.Compare(volumes, Published(volumes: 6)).Volumes); // 12 > 1.5 x 6 + 2
        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(volumes, Published(volumes: 6, englishVolumes: 12)).Volumes);
        // A chapter total says nothing about volumes.
        Assert.Equal(CountComparison.None with { PublishedChapters = 3 }, CountEvidence.Compare(volumes, Published(statusChapters: 3)));
    }

    [Fact]
    public void Compare_ChaptersWithChapterTotals_TheLatestChapterAloneNeverConflictsWithAVolumeRecord()
    {
        var chapters = new LocalUnitCounts(0, 58, null, null, 1, 58);

        // The spin-off shape: a record with 10 volumes and a latest tracked chapter of 12.
        Assert.Equal(CountSignal.None, CountEvidence.Compare(chapters, Published(volumes: 10, latest: 12)).Chapters);
        Assert.Equal(CountSignal.None, CountEvidence.Compare(chapters, Published(volumes: 10)).Chapters);
        // The latest chapter still agrees, and bounds a record with no volume total (a web serial).
        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(chapters, Published(volumes: 10, latest: 60)).Chapters);
        Assert.Equal(CountSignal.Conflict, CountEvidence.Compare(chapters, Published(latest: 12)).Chapters);
        // A stated total bounds it; the larger of total and latest counts (season-renumbered webtoons).
        Assert.Equal(CountSignal.Conflict, CountEvidence.Compare(chapters, Published(volumes: 10, statusChapters: 20)).Chapters);
        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(chapters, Published(statusChapters: 20, latest: 50)).Chapters);
        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(chapters, Published(englishChapters: 60)).Chapters);
    }

    [Fact]
    public void Describe_NamesTheUnitAndItsRange()
    {
        Assert.Equal("chapters 1-43", CountEvidence.Describe(new LocalUnitCounts(0, 43, null, null, 1, 43), volumes: false));
        Assert.Equal("volume 7", CountEvidence.Describe(new LocalUnitCounts(1, 0, 7, 7, null, null), volumes: true));
        Assert.Null(CountEvidence.Describe(LocalUnitCounts.Empty, volumes: true));
    }

    [Fact]
    public void FromContext_PrefersThePlannersUnits_ElseTheOlderFields()
    {
        var context = new MatchContext(WorkClass.Series, 5, 5, 0, null, null, false, [], LocalVolumes: 4);
        Assert.Equal(new LocalUnitCounts(5, 0, null, 4, null, null), CountEvidence.FromContext(context));

        var units = new LocalUnitCounts(5, 0, 2, 9, null, null);
        Assert.Same(units, CountEvidence.FromContext(context with { Units = units }));
    }

    [Fact]
    public void ChapterNames_StatingTheirVolume_AreComparedWithTheVolumeTotal()
    {
        // 1.29.0 (owner): "Title v09 c060" tells how many volumes the run has reached - a volume signal, never "mixed".
        var local = CountEvidence.LocalOf(["Some Title v01 c001.cbz", "Some Title v05 c030.cbz", "Some Title v09 c060.cbz"], null);
        Assert.Equal((0, 3, 9), (local.VolumeArchives, local.ChapterArchives, local.HighestNamedVolume));
        Assert.False(local.IsMixed);
        Assert.Equal(CountSignal.Agree, CountEvidence.Compare(local, new PublishedUnitCounts(10, null, null, null, null)).Volumes);
        Assert.Equal(CountSignal.Conflict, CountEvidence.Compare(local, new PublishedUnitCounts(3, null, null, null, null)).Volumes);
        Assert.Equal("chapters up to volume 9", CountEvidence.Describe(local, volumes: true));

        // Plain chapter names state no volume: no volume signal, as before.
        var plain = CountEvidence.LocalOf(["Some Title - Chapter 001.cbz", "Some Title - Chapter 060.cbz"], null);
        Assert.Null(plain.HighestNamedVolume);
        Assert.Equal(CountSignal.None, CountEvidence.Compare(plain, new PublishedUnitCounts(3, null, null, null, null)).Volumes);
    }
}
