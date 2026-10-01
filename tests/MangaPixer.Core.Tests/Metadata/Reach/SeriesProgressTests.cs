namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using Xunit;

/// <summary>
/// Unit tests for a series folder's reach and progress (1.30.0): volume files and chapter files merged through the stored
/// volume -> chapter list, the per-kind comparison with what is released in the preferred language, the upgrades (official
/// volumes held only as chapters) and the completion mark. The cases are the worked examples of the lane R design. Synthetic names.
/// </summary>
public sealed class SeriesProgressTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static IEnumerable<GroupingRow> VolumeFiles(int from, int to) => Enumerable.Range(from, to - from + 1).Select(v => Archive($"Series v{v:00}"));

    private static IEnumerable<GroupingRow> ChapterFiles(int from, int to) => Enumerable.Range(from, to - from + 1).Select(c => Archive($"Series c{c:000}"));

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    /// <summary>Volumes 1..<paramref name="volumes"/>, <paramref name="per"/> chapters each (volume k = chapters per*(k-1)+1 .. per*k).</summary>
    private static VolumeMapInput EvenMap(int volumes, int per, bool ongoing = true) =>
        new(Enumerable.Range(1, volumes).Select(k => new VolumeMapVolume(k, Range(per * (k - 1) + 1, per * k))).ToList(),
            per, volumes, ongoing, VolumeListSource.MangaDex);

    private static ProgressFacts English(
        MetadataOriginStatus? origin = MetadataOriginStatus.Ongoing, int? originVolumes = null, int? official = null,
        MetadataOriginStatus? officialStatus = null, int? latest = null, bool? scanComplete = null, int? originChapters = null) =>
        new("en", MetadataOrigin.Japan, origin, originVolumes, originChapters, official is null ? null : "Synthetic Press", official, null,
            officialStatus, true, latest, scanComplete);

    [Fact]
    public void OwnerExample_VolumesThenChapterStacks_IsUpToDate_AndReachesTheLatestChapter()
    {
        var rows = VolumeFiles(1, 14).Concat(ChapterFiles(43, 57)).ToList();
        var r = SeriesProgress.Evaluate(rows, EvenMap(19, 3), English(originVolumes: 22, official: 14, officialStatus: MetadataOriginStatus.Ongoing, latest: 57));

        Assert.Equal(57, r.Reach.ReachChapter);
        Assert.Equal(19, r.Reach.ReachVolume); // volumes 15-19 held whole as chapters
        Assert.Empty(r.Reach.Overlap);
        Assert.Empty(r.MissingVolumes);
        Assert.Equal(0, r.MissingChapterCount);
        Assert.Empty(r.UpgradeVolumes);
        Assert.Equal(SeriesCompletion.None, r.Completion);

        var dto = SeriesProgress.ToDto(r);
        Assert.Equal([new UnitSpanDto { From = 1, To = 14 }], dto.Reach!.VolumeFiles);
        Assert.Equal([new UnitSpanDto { From = 43, To = 57 }], dto.Reach.Chapters);
        Assert.True(dto.ReleaseKnown);
        Assert.Equal("Synthetic Press", dto.Trackers.OfficialPublisher);
    }

    [Fact]
    public void OwnerExample_WhenTheEnglishVolume15IsOut_ItIsAnUpgrade_NeverMissing_AndItsStackSaysSo()
    {
        var rows = VolumeFiles(1, 14).Concat(ChapterFiles(43, 57)).ToList();
        var map = EvenMap(19, 3);
        var r = SeriesProgress.Evaluate(rows, map, English(originVolumes: 22, official: 15, officialStatus: MetadataOriginStatus.Ongoing, latest: 57));

        Assert.Equal([15], r.UpgradeVolumes);
        Assert.Empty(r.MissingVolumes);
        Assert.Equal(0, r.MissingChapterCount);

        var grouping = VolumeGrouping.Group(rows, map with { ReleasedVolumeCount = 15, ReleasedLanguage = "en" }, markMissingVolumes: true);
        var stacks = grouping.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).Select(e => e.Stack!).ToDictionary(s => s.Key);
        Assert.Equal("en", stacks["15"].OfficialRelease);
        Assert.Null(stacks["16"].OfficialRelease);
    }

    [Fact]
    public void MixedFolder_OverlapIsCountedOnce_AndTheOverlappingChapterFilesAreMarked()
    {
        var rows = VolumeFiles(1, 10).Concat(ChapterFiles(85, 120)).ToList();
        var r = SeriesProgress.Evaluate(rows, EvenMap(10, 9), English(originVolumes: 14, official: 10, officialStatus: MetadataOriginStatus.Ongoing, latest: 120));

        Assert.Equal(120, r.Reach.ReachChapter);
        Assert.Equal(Enumerable.Range(85, 6), r.Reach.Overlap.Order());
        Assert.Equal(ReachResolution.VolumeList, r.Reach.Resolution);
        Assert.Equal("10", r.Reach.AlsoInVolume["id:Series c085"]);
        Assert.Equal("10", r.Reach.AlsoInVolume["id:Series c090"]);
        Assert.False(r.Reach.AlsoInVolume.ContainsKey("id:Series c091"));
        Assert.Equal([new UnitSpanDto { From = 91, To = 120 }], SeriesReach.ToDto(r.Reach)!.Chapters);
        Assert.Equal(0, r.MissingChapterCount);
        Assert.Empty(r.MissingVolumes);
    }

    [Fact]
    public void WithoutAMap_ChapterNamesThatStateTheirVolume_StillResolveTheOverlap()
    {
        var rows = new List<GroupingRow> { Archive("Series v10") };
        rows.AddRange(Enumerable.Range(85, 6).Select(c => Archive($"Series v10 c{c:000}")));
        rows.AddRange(ChapterFiles(91, 95));
        var reach = SeriesReach.Of(rows, null);

        Assert.Equal(ReachResolution.FileNames, reach.Resolution);
        Assert.Equal(6, reach.Overlap.Count);
        Assert.Equal("10", reach.AlsoInVolume["id:Series v10 c085"]);
        Assert.Equal([91, 92, 93, 94, 95], SeriesReach.ChaptersOutsideVolumes(reach));
    }

    [Fact]
    public void EstimatedVolumeChapters_CountInTheReach_ButNoOverlapIsClaimed()
    {
        var rows = VolumeFiles(1, 3).Concat(ChapterFiles(25, 40)).ToList();
        var ratioOnly = new VolumeMapInput([], 10, 12, true, VolumeListSource.AniList);
        var reach = SeriesReach.Of(rows, ratioOnly);

        Assert.Equal(ReachResolution.Estimated, reach.Resolution);
        Assert.Contains(1, reach.Covered);
        Assert.Empty(reach.Overlap);
        Assert.Empty(reach.AlsoInVolume);
        Assert.Equal(Enumerable.Range(25, 16), SeriesReach.ChaptersOutsideVolumes(reach));
    }

    [Fact]
    public void ChaptersOnlyWebtoon_HolesAndChaptersReleasedAfterTheLastOneHereAreMissing()
    {
        var rows = ChapterFiles(1, 148).Where(r => r.Name != "Series c100").ToList();
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, MetadataOriginStatus.Ongoing, OriginChapters: 150, LatestChapter: 150, ScanlationComplete: false);
        var r = SeriesProgress.Evaluate(rows, null, facts);

        Assert.Equal(148, r.Reach.ReachChapter);
        Assert.Null(r.Reach.ReachVolume);
        Assert.Equal([100m], r.ChapterHoles);
        Assert.Equal(2, r.ChaptersBehind);
        Assert.Equal(3, r.MissingChapterCount);
        Assert.Equal(MissingTotalSource.LatestChapter, r.ChapterTotalSource);
    }

    [Fact]
    public void VolumesOnly_NotFullyHeld_MissingVolumes_AndAPromptWhenFinishedInEnglish_NeverBehindAScanlation()
    {
        var facts = English(MetadataOriginStatus.Complete, 14, 14, MetadataOriginStatus.Complete, latest: 150);
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 12).ToList(), null, facts);

        Assert.Equal([13, 14], r.MissingVolumes);
        Assert.Equal(2, r.VolumesBehind);
        Assert.Equal(0, r.MissingChapterCount); // no chapter files: the scanlation is not compared
        Assert.Equal(SeriesCompletion.FinishedNotHeld, r.Completion);
        Assert.Equal(CompletionBasis.OfficialVolumes, r.CompletionBasis);
        Assert.Equal((14, 12), (r.CompletionTarget, r.CompletionHeld));
    }

    [Fact]
    public void CompleteSeries_FullyHeld_IsACompleteCollection()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14).ToList(), null,
            English(MetadataOriginStatus.Complete, 14, 14, MetadataOriginStatus.Complete));

        Assert.Equal(SeriesCompletion.CompleteCollection, r.Completion);
        Assert.Equal(CompletionBasis.OfficialVolumes, r.CompletionBasis);
    }

    [Fact]
    public void OfficialStatusUnknown_IsInferredComplete_WhenTheOriginIsCompleteAndEveryOriginVolumeIsOut()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14).ToList(), null, English(MetadataOriginStatus.Complete, 14, 14, officialStatus: null));

        Assert.Equal(SeriesCompletion.CompleteCollection, r.Completion);
        Assert.Equal(CompletionBasis.OfficialVolumes, r.CompletionBasis);
    }

    [Fact]
    public void CompleteChapterRuns_CountAsHoldingTheirVolume()
    {
        var rows = VolumeFiles(1, 12).Concat(ChapterFiles(37, 42)).ToList();
        var r = SeriesProgress.Evaluate(rows, EvenMap(14, 3, ongoing: false), English(MetadataOriginStatus.Complete, 14, 14, MetadataOriginStatus.Complete));

        Assert.Equal(SeriesCompletion.CompleteCollection, r.Completion);
        Assert.Equal([13, 14], r.UpgradeVolumes); // still worth getting as volumes
    }

    [Fact]
    public void FinishedScanlation_FullyHeld_IsACompleteCollection_ByChapters()
    {
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, MetadataOriginStatus.Complete, LatestChapter: 120, ScanlationComplete: true);
        var held = SeriesProgress.Evaluate(ChapterFiles(1, 120).ToList(), null, facts);
        var gap = SeriesProgress.Evaluate(ChapterFiles(1, 118).ToList(), null, facts);

        Assert.Equal((SeriesCompletion.CompleteCollection, CompletionBasis.AllChapters, true), (held.Completion, held.CompletionBasis, held.CompletionInChapters));
        Assert.Equal((SeriesCompletion.FinishedNotHeld, 120, 118), (gap.Completion, gap.CompletionTarget, gap.CompletionHeld));
    }

    [Fact]
    public void FinishedScanlation_WithChaptersReleasedPastTheLatestRelease_IsNotComplete()
    {
        // 1.30.0 soak-test shape: MangaUpdates' latest release says 51, the English release list names chapters to 55.
        var facts = new ProgressFacts("en", MetadataOrigin.Japan, MetadataOriginStatus.Complete, LatestChapter: 51, ScanlationComplete: true,
            ReleasedChapters: Enumerable.Range(1, 55).Select(c => (decimal)c).ToHashSet());
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 51).ToList(), null, facts);

        Assert.True(r.MissingChapterCount > 0);
        Assert.NotEqual(SeriesCompletion.CompleteCollection, r.Completion); // never "Complete collection" next to a missing count
        Assert.Equal((SeriesCompletion.FinishedNotHeld, 55, 51), (r.Completion, r.CompletionTarget, r.CompletionHeld));
    }

    [Fact]
    public void OriginOnlyFinished_FullyHeldIsComplete_NotHeldIsNoPrompt()
    {
        var facts = English(MetadataOriginStatus.Complete, 14, 10, MetadataOriginStatus.Ongoing);

        var all = SeriesProgress.Evaluate(VolumeFiles(1, 14).ToList(), null, facts);
        Assert.Equal((SeriesCompletion.CompleteCollection, CompletionBasis.OriginRun), (all.Completion, all.CompletionBasis));

        var some = SeriesProgress.Evaluate(VolumeFiles(1, 12).ToList(), null, facts);
        Assert.Equal(SeriesCompletion.None, some.Completion);
    }

    [Fact]
    public void OriginRun_LastVolumeListedOnlyByItsTranslatedChapters_IsNotComplete()
    {
        // 1.30.1 owner live check: the volume list (built from translations) names volume 8 as chapters 36-38; the origin run has 40
        // chapters. Volumes 1-7 as files + chapters 36-38 hold the list, not the run.
        var volumes = Enumerable.Range(1, 7).Select(k => new VolumeMapVolume(k, Range(5 * (k - 1) + 1, 5 * k)))
            .Append(new VolumeMapVolume(8, Range(36, 38))).ToList();
        var map = new VolumeMapInput(volumes, 4.75, 8, false, VolumeListSource.MangaDex);
        var rows = VolumeFiles(1, 7).Concat(ChapterFiles(36, 38)).ToList();

        var r = SeriesProgress.Evaluate(rows, map, English(MetadataOriginStatus.Complete, 8, latest: 38, scanComplete: false, originChapters: 40));
        Assert.Contains(8, r.Reach.HeldAsChapters); // still shown as held by the list
        Assert.Equal(SeriesCompletion.None, r.Completion);

        // Once the chapters here reach the origin's last chapter, the run is held whole.
        var whole = SeriesProgress.Evaluate(VolumeFiles(1, 7).Concat(ChapterFiles(36, 40)).ToList(), map,
            English(MetadataOriginStatus.Complete, 8, latest: 40, scanComplete: true, originChapters: 40));
        Assert.Equal(SeriesCompletion.CompleteCollection, whole.Completion);
    }

    [Fact]
    public void VolumesHeldByAnEstimate_NeverMakeACompleteCollection()
    {
        // 1.30.1 owner live check: only volume 1 is listed (chapters 1-4); volumes 2-4 come from the chapters-per-volume ratio.
        // Chapters 1-36 "hold" all four estimated volumes, but that is no proof the folder holds the run.
        var map = new VolumeMapInput([new VolumeMapVolume(1, Range(1, 4))], 4, 4, false, VolumeListSource.MangaDex);
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 36).ToList(), map, English(MetadataOriginStatus.Complete, 4, scanComplete: false));

        Assert.NotEmpty(r.Reach.HeldByEstimate);
        Assert.NotEqual(SeriesCompletion.CompleteCollection, r.Completion);
    }

    [Fact]
    public void VolumeFiles_AreWholeByThemselves_WhateverTheChapterExtent()
    {
        // A volume list that stops early never blocks volume FILES: volumes 1-14 on disk are the whole official edition.
        var map = EvenMap(10, 5, ongoing: false);
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14).ToList(), map,
            English(MetadataOriginStatus.Complete, 14, 14, MetadataOriginStatus.Complete, latest: 120, originChapters: 130));

        Assert.Equal((SeriesCompletion.CompleteCollection, CompletionBasis.OfficialVolumes), (r.Completion, r.CompletionBasis));
    }

    [Fact]
    public void Hiatus_IsNeverFinished()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14).ToList(), null, English(MetadataOriginStatus.Hiatus, 14, 14, MetadataOriginStatus.Hiatus));
        Assert.Equal(SeriesCompletion.None, r.Completion);
    }

    [Fact]
    public void APartialVolume_IsAnUpgrade_AndItsMissingChapterIsMissing()
    {
        var rows = VolumeFiles(1, 14).Concat([Archive("Series c043"), Archive("Series c045")]).ToList();
        var r = SeriesProgress.Evaluate(rows, EvenMap(19, 3), English(originVolumes: 22, official: 15, officialStatus: MetadataOriginStatus.Ongoing));

        Assert.Equal([15], r.UpgradeVolumes);
        Assert.Equal([15], r.Reach.PartialVolumes);
        Assert.Equal([44m], r.ChapterHoles);
    }

    [Fact]
    public void OfficialVolumes_MakeTheirChaptersReleased()
    {
        var map = SeriesProgress.WithOfficialChapters(EvenMap(3, 3) with { ReleasedVolumeCount = 2 });
        Assert.Equal(Range(1, 6), map.ReleasedChapters!.Order());
        Assert.Same(VolumeMapInput.Empty, SeriesProgress.WithOfficialChapters(VolumeMapInput.Empty));
    }

    [Fact]
    public void FrenchPreferred_ComparesWithTheReleasedList_AndNoEnglishFact()
    {
        var released = Range(1, 87).ToHashSet();
        var facts = new ProgressFacts("fr", MetadataOrigin.Japan, MetadataOriginStatus.Ongoing, 22, ReleasedChapters: released);
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 87).ToList(), null, facts);

        Assert.Equal(0, r.MissingChapterCount);
        Assert.True(SeriesProgress.ToDto(r).ReleaseKnown);
        Assert.Equal(87, SeriesProgress.ToDto(r).Trackers.ReleasedChapter);
        Assert.Empty(r.UpgradeVolumes);
    }

    [Fact]
    public void Restarts_GiveNoReach_ButKeepTheTrackers()
    {
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 10).ToList(), null, English(originVolumes: 5), restarts: true);
        var dto = SeriesProgress.ToDto(r);

        Assert.Null(dto.Reach);
        Assert.Equal(5, dto.Trackers.OriginVolumes);
        Assert.Equal(SeriesCompletion.None, dto.Completion);
    }

    [Fact]
    public void Spans_CompactAndBounded()
    {
        Assert.Equal(
            [new UnitSpanDto { From = 1, To = 3 }, new UnitSpanDto { From = 7, To = 7 }, new UnitSpanDto { From = 9, To = 10 }],
            SeriesReach.Spans([10, 1, 2, 3, 7, 9, 3]));
        Assert.Equal(2, SeriesReach.Spans([1, 3, 5, 7], max: 2).Count);
    }
}
