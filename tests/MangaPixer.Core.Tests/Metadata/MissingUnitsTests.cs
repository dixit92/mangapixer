namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
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
        Assert.Equal((2, 6), (r.Volumes!.ArchiveCount, r.Volumes.UnitCount));
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
        Assert.Equal(198, r.Volumes.MissingCount); // 1, 2 and 4..199: the extra 2.5 fills no number (1.29.0)
    }

    private static MissingFolder Folder(string? name, params string[] archives) => new(name, archives);

    [Fact]
    public void SeasonSubfolders_ThatContinueTheNumbering_AreOneRun()
    {
        // The live 1.28.0 finding's shape: a loose prologue next to Season 1 / Season 2 that continue the numbering.
        var r = MissingUnits.Evaluate(
        [
            Folder(null, "000.cbz"),
            Folder("Season 1", "Synthetic - Chapter 001", "Synthetic - Chapter 002", "Synthetic - Chapter 003"),
            Folder("Season 2", "Synthetic - Chapter 004", "Synthetic - Chapter 006"),
        ], new PublishedTotals(OriginChapters: 8));

        Assert.Equal(MissingVerdict.Behind, r.Verdict);
        Assert.Equal((0, 6, 8, 2), (r.Chapters!.Lowest, r.Chapters.Have, r.Chapters.Available, r.Chapters.BehindBy));
        Assert.Equal([5], r.Chapters.Missing);
    }

    [Fact]
    public void NumberingThatRestartsPerSubfolder_GivesNoVerdict()
    {
        var r = MissingUnits.Evaluate(
        [
            Folder(null, "000.cbz"),
            Folder("Season 1", "Synthetic - Chapter 001", "Synthetic - Chapter 002", "Synthetic - Chapter 003"),
            Folder("Season 2", "Synthetic - Chapter 001", "Synthetic - Chapter 002"),
        ], new PublishedTotals(OriginChapters: 223));

        Assert.Equal(MissingVerdict.Restarts, r.Verdict);
        Assert.Null(r.Chapters); // never "chapter 3 of 223"

        // A second season that has only just started (one chapter, numbered 1 again) restarts too.
        Assert.Equal(MissingVerdict.Restarts, MissingUnits.Evaluate(
            [Folder("Part 1", "Synthetic v01", "Synthetic v02"), Folder("Part 2", "Synthetic v01")], new PublishedTotals(OriginVolumes: 9)).Verdict);

        // One number at a boundary shared by folders that start apart is a duplicate, not a restart.
        Assert.Equal(MissingVerdict.UpToDate, MissingUnits.Evaluate(
            [Folder("Season 1", "Synthetic - Chapter 001", "Synthetic - Chapter 002"), Folder("Season 2", "Synthetic - Chapter 002", "Synthetic - Chapter 003")],
            new PublishedTotals(OriginChapters: 3)).Verdict);
    }

    [Fact]
    public void RestartsInOneUnit_KeepTheOtherUnitsNumbers()
    {
        var r = MissingUnits.Evaluate(
        [
            Folder("Volumes", "Synthetic v01", "Synthetic v02"),
            Folder("Season 1", "Synthetic - Chapter 001", "Synthetic - Chapter 002"),
            Folder("Season 2", "Synthetic - Chapter 001"),
        ], new PublishedTotals(EnglishVolumes: 4));

        Assert.Equal(MissingVerdict.Restarts, r.Verdict);
        Assert.Equal(2, r.Volumes!.BehindBy);
        Assert.Null(r.Chapters);
    }

    [Fact]
    public void ALoneChapterZero_IsNotProgress()
    {
        var r = MissingUnits.Evaluate(One("000.cbz"), new PublishedTotals(OriginChapters: 223));

        Assert.Equal(MissingVerdict.NoUnits, r.Verdict);
        Assert.Null(r.Chapters); // never "You have chapter 0 of 223 - 223 behind"
    }

    [Fact]
    public void Extras_AreNeverMissing_AndNeverFillANumber()
    {
        var r = MissingUnits.Evaluate(One("Synthetic c001", "Synthetic c002", "Synthetic c003.5", "Synthetic c004"), new PublishedTotals(OriginChapters: 4));

        Assert.Equal(MissingVerdict.Holes, r.Verdict);
        Assert.Equal([3], r.Chapters!.Missing); // 3.5 does not fill 3; no 1.5 or 2.5 is ever missing
        Assert.Equal((4, 3), (r.Chapters.ArchiveCount, r.Chapters.UnitCount));
    }

    [Fact]
    public void SplitChapterParts_FillTheirChapter()
    {
        var r = MissingUnits.Evaluate(
            One("Synthetic c001", "Synthetic c002.1", "Synthetic c002.2", "Synthetic c003", "Synthetic c003.2", "Synthetic c004"),
            new PublishedTotals(OriginChapters: 4));

        Assert.Equal(MissingVerdict.UpToDate, r.Verdict);
        Assert.Empty(r.Chapters!.Missing);
        Assert.Equal((6, 4), (r.Chapters.ArchiveCount, r.Chapters.UnitCount));
    }

    private static UnitNumbers Ch(decimal c) => new(null, null, c, null, decimal.Truncate(c) != c);

    [Fact]
    public void SplitsOf_TellsPartsFromExtras()
    {
        // 2.1 + 2.2: parts; 3 + 3.2: the file 3 is the first part; 5.4 + 5.5: .5 continues .4; 6.1 + 6.3: 6.2 is missing.
        var s = MissingUnits.SplitsOf([Ch(2.1m), Ch(2.2m), Ch(3m), Ch(3.2m), Ch(5.4m), Ch(5.5m), Ch(6.1m), Ch(6.3m)]);
        Assert.Equal([2, 3, 5, 6], s.Chapters.Order());
        Assert.Equal([2.1m, 2.2m, 3.2m, 5.4m, 5.5m, 6.1m, 6.3m], s.Parts.Order());
        Assert.Equal([5.1m, 5.2m, 5.3m, 6.2m], s.MissingParts);

        // Extras stay extras: a lone .5 (with or without its whole), a lone .2, a .1 next to its whole file, 12.25.
        var extras = MissingUnits.SplitsOf([Ch(10m), Ch(10.5m), Ch(11.5m), Ch(12.2m), Ch(13m), Ch(13.1m), Ch(14.25m), Ch(14.75m)]);
        Assert.Empty(extras.Chapters);
        Assert.Empty(extras.Parts);
        Assert.Empty(extras.MissingParts);
    }

    [Fact]
    public void VolumesFolder_ReadsBareNumbersAsVolumes()
    {
        var r = MissingUnits.Evaluate([Folder("Volumes", "01.cbz", "02.cbz", "04.cbz")], new PublishedTotals(EnglishVolumes: 5, OriginChapters: 40));

        Assert.Null(r.Chapters);
        Assert.Equal((4, 5, 1), (r.Volumes!.Have, r.Volumes.Available, r.Volumes.BehindBy));
        Assert.Equal([3], r.Volumes.Missing);
    }

    [Fact]
    public void Holes_ForAVirtualVolume()
    {
        // Volume 1 holds chapters 1-10 (a mapping); chapter 8 is missing; 4.5 is an extra and never missing.
        var units = new[] { "c001", "c002", "c003", "c004", "c004.5", "c005", "c006", "c007", "c009-010" }
            .Select(n => com.lifepixer.mangapixer.Core.Metadata.AutoMatch.AutoMatchText.UnitsOf("Synthetic " + n)).ToList();

        Assert.Equal([8], MissingUnits.Holes(units, MissingUnitKind.Chapter, 1, 10));
        Assert.Equal([8], MissingUnits.Holes(units, MissingUnitKind.Chapter, [1m, 2m, 4.5m, 8m, 9m, 10m, 11.5m]));
        Assert.Equal(Enumerable.Range(1, 10), MissingUnits.NumbersOf(units, MissingUnitKind.Chapter).Where(n => n != 8).Append(8).Order());
        Assert.Empty(MissingUnits.NumbersOf(units, MissingUnitKind.Volume));
    }

    [Fact]
    public void ChaptersPerVolume_ConvertsTheEnglishTotal_WhenTheSameUnitHasNone()
    {
        // English "18 Volumes" only; chapters on disk; AniList's finished entry has 9.5 chapters per volume.
        var chapters = MissingUnits.Evaluate(One("Synthetic - Chapter 001", "Synthetic - Chapter 002"),
            new PublishedTotals(EnglishVolumes: 18, LatestChapter: 200, ChaptersPerVolume: 9.5));
        Assert.Equal((171, MissingTotalSource.Converted, MissingConfidence.Medium),
            (chapters.Chapters!.Available, chapters.Chapters.Source, chapters.Chapters.Confidence));

        // A stated chapter total beats the conversion; no ratio, no conversion.
        Assert.Equal(MissingTotalSource.English, MissingUnits.Evaluate(One("Synthetic - Chapter 001"),
            new PublishedTotals(EnglishVolumes: 18, EnglishChapters: 60, ChaptersPerVolume: 9.5)).Chapters!.Source);
        Assert.Equal(MissingTotalSource.LatestChapter, MissingUnits.Evaluate(One("Synthetic - Chapter 001"),
            new PublishedTotals(EnglishVolumes: 18, LatestChapter: 200)).Chapters!.Source);

        // Volumes from English chapters, before the origin volume count.
        var volumes = MissingUnits.Evaluate(One("Synthetic v01"), new PublishedTotals(EnglishChapters: 95, OriginVolumes: 20, ChaptersPerVolume: 9.5));
        Assert.Equal((10, MissingTotalSource.Converted), (volumes.Volumes!.Available, volumes.Volumes.Source));
    }

    // --- 1.29.0 RC: "missing" = released in the preferred language ---

    [Fact]
    public void Language_English_ComparesWithTheEnglishTotal_AndKeepsTheOriginAsContext()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02"), new PublishedTotals(EnglishVolumes: 4, OriginVolumes: 14, Language: "en"));

        Assert.Equal(MissingVerdict.Behind, r.Verdict);
        Assert.Equal((4, MissingTotalSource.English, 2, 14), (r.Volumes!.Available, r.Volumes.Source, r.Volumes.BehindBy, r.Volumes.OriginTotal));
    }

    [Fact]
    public void Language_NeverFallsBackToTheOriginTotal()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v02"), new PublishedTotals(OriginVolumes: 14, Language: "en"));

        Assert.Equal(MissingVerdict.NoTotal, r.Verdict);
        Assert.Null(r.Volumes!.Available);
        Assert.Equal(0, r.Volumes.BehindBy);
        Assert.Equal(14, r.Volumes.OriginTotal);
    }

    [Fact]
    public void Language_Other_IgnoresTheEnglishTotals_AndUsesTheReleasedChapters()
    {
        var volumes = MissingUnits.Evaluate(One("Synthetic v01"), new PublishedTotals(EnglishVolumes: 9, Language: "fr"));
        Assert.Equal(MissingVerdict.NoTotal, volumes.Verdict);

        var chapters = MissingUnits.Evaluate(One("Synthetic - Chapter 001", "Synthetic - Chapter 002"),
            new PublishedTotals(EnglishChapters: 50, LatestChapter: 40, Language: "fr", ReleasedChapters: 6));
        Assert.Equal((6, MissingTotalSource.Released, 4), (chapters.Chapters!.Available, chapters.Chapters.Source, chapters.Chapters.BehindBy));
    }

    [Fact]
    public void Language_HolesBelowTheHighestNumber_StayMissing_WithoutAnyTotal()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01", "Synthetic v03"), new PublishedTotals(Language: "de"));

        Assert.Equal(MissingVerdict.Holes, r.Verdict);
        Assert.Equal([2], r.Volumes!.Missing);
    }

    [Fact]
    public void WithoutALanguage_TheOriginStillCounts_As1280()
    {
        var r = MissingUnits.Evaluate(One("Synthetic v01"), new PublishedTotals(OriginVolumes: 3));

        Assert.Equal((3, MissingTotalSource.Origin), (r.Volumes!.Available, r.Volumes.Source));
        Assert.Null(r.Volumes.OriginTotal);
    }
}
