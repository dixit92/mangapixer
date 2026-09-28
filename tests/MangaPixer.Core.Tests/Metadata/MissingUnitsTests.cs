namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata.Missing;
using Xunit;

/// <summary>
/// Unit tests for the missing volumes / chapters calculator (1.28.0): highest number vs the stored totals (English
/// first, then origin, then latest chapter), holes, range archives, volumes and chapters kept apart, mixed folders,
/// and the fall-through when a total is below what is on disk. Synthetic names only.
/// </summary>
public sealed class MissingUnitsTests
{
    private static IReadOnlyList<string>[] One(params string[] names) => [names];

    [Fact]
    public void Volumes_BehindTheEnglishTotal()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02", "Synthetic v03", "Synthetic v04", "Synthetic v05", "Synthetic v06", "Synthetic v07"),
            new PublishedTotals(EnglishVolumes: 10, OriginVolumes: 14));

        Assert.Equal(MissingVerdict.Behind, r.Verdict);
        var v = Assert.IsType<MissingUnitGap>(r.Volumes);
        Assert.Equal((1, 7, 10, 3), (v.Lowest, v.Have, v.Available, v.BehindBy));
        Assert.Equal(MissingTotalSource.English, v.Source);
        Assert.Equal(MissingConfidence.High, v.Confidence);
        Assert.Empty(v.Missing);
        Assert.Null(r.Chapters);
    }

    [Fact]
    public void Volumes_FallBackToTheOriginTotal_WhenNoEnglishTotal()
    {
        var r = MissingUnits.Evaluate(One("Synthetic Vol. 1", "Synthetic Vol. 2"), new PublishedTotals(OriginVolumes: 14));
        Assert.Equal((14, MissingTotalSource.Origin, MissingConfidence.Medium, 12),
            (r.Volumes!.Available, r.Volumes.Source, r.Volumes.Confidence, r.Volumes.BehindBy));
    }

    [Fact]
    public void Holes_AreListed_AndBeatUpToDate()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02", "Synthetic v05", "Synthetic v06"), new PublishedTotals(EnglishVolumes: 6));
        Assert.Equal(MissingVerdict.Holes, r.Verdict);
        Assert.Equal([3, 4], r.Volumes!.Missing);
        Assert.Equal(2, r.Volumes.MissingCount);
        Assert.Equal(0, r.Volumes.BehindBy);
    }

    [Fact]
    public void RangeArchives_CoverTheirWholeRange()
    {
        var r = MissingUnits.Evaluate(One("Synthetic Vol. 01-05", "Synthetic v06"), new PublishedTotals(EnglishVolumes: 6));
        Assert.Equal(MissingVerdict.UpToDate, r.Verdict);
        Assert.Equal(2, r.Volumes!.ArchiveCount);
        Assert.Empty(r.Volumes.Missing);
    }

    [Fact]
    public void Chapters_UseTheOriginTotal_ThenTheLatestChapter()
    {
        var names = One("Synthetic - Chapter 001", "Synthetic - Chapter 002", "Synthetic - Chapter 003");
        var origin = MissingUnits.Evaluate(names, new PublishedTotals(OriginChapters: 195, LatestChapter: 120.5));
        Assert.Equal((195, MissingTotalSource.Origin), (origin.Chapters!.Available, origin.Chapters.Source));

        var latest = MissingUnits.Evaluate(names, new PublishedTotals(LatestChapter: 120.5, OriginVolumes: 9));
        Assert.Equal((120, MissingTotalSource.LatestChapter, MissingConfidence.Low, 117),
            (latest.Chapters!.Available, latest.Chapters.Source, latest.Chapters.Confidence, latest.Chapters.BehindBy));
        Assert.Null(latest.Volumes); // chapters are never compared with a volume total
    }

    [Fact]
    public void TotalBelowWhatIsOnDisk_GivesWayToTheNextSource()
    {
        // English edition still catching up (5), the origin says 14: the copies are compared with the origin.
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02", "Synthetic v03", "Synthetic v04", "Synthetic v05", "Synthetic v06", "Synthetic v07", "Synthetic v08"),
            new PublishedTotals(EnglishVolumes: 5, OriginVolumes: 14));
        Assert.Equal((14, MissingTotalSource.Origin, 6), (r.Volumes!.Available, r.Volumes.Source, r.Volumes.BehindBy));

        // No source covers it: the English total stays, not behind.
        var ahead = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02", "Synthetic v03"), new PublishedTotals(EnglishVolumes: 2, OriginVolumes: 2));
        Assert.Equal(MissingVerdict.UpToDate, ahead.Verdict);
        Assert.Equal((2, MissingTotalSource.English, 0), (ahead.Volumes!.Available, ahead.Volumes.Source, ahead.Volumes.BehindBy));
    }

    [Fact]
    public void MixedFolder_GivesNoVerdict_ButSeparateFoldersAreComparedSeparately()
    {
        var mixed = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic - Chapter 010"), new PublishedTotals(EnglishVolumes: 5, LatestChapter: 30));
        Assert.Equal(MissingVerdict.Mixed, mixed.Verdict);
        Assert.Equal(1, mixed.MixedFolders);
        Assert.Null(mixed.Volumes);

        // Volumes/ and Chapters/ subfolders: each kind on its own; chapter holes count from the lowest chapter
        // on disk (chapters after the last volume), not from 1.
        IReadOnlyList<string>[] folders =
        [
            ["Synthetic v01", "Synthetic v02"],
            ["Synthetic - Chapter 021", "Synthetic - Chapter 022", "Synthetic - Chapter 024"],
        ];
        var split = MissingUnits.Evaluate(folders, new PublishedTotals(EnglishVolumes: 2, LatestChapter: 24));
        Assert.Equal(MissingVerdict.Holes, split.Verdict);
        Assert.Equal(0, split.Volumes!.BehindBy);
        Assert.Equal([23], split.Chapters!.Missing);
    }

    [Fact]
    public void NoNumbers_OrNoTotal()
    {
        Assert.Equal(MissingVerdict.NoUnits, MissingUnits.Evaluate(One("Synthetic Artbook"), new PublishedTotals(EnglishVolumes: 3)).Verdict);
        Assert.Equal(MissingVerdict.NoTotal, MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02"), new PublishedTotals()).Verdict);
    }

    [Fact]
    public void Extras_DoNotInflate_AndLongHoleListsAreCapped()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v02.5", "Synthetic v03", "Synthetic v200"), new PublishedTotals(EnglishVolumes: 200));
        Assert.Equal(200, r.Volumes!.Have);
        Assert.Equal(MissingUnits.MaxListed, r.Volumes.Missing.Count);
        Assert.Equal(197, r.Volumes.MissingCount); // 1 and 4..199
    }
}
