namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using Xunit;

/// <summary>
/// Unit tests for the edition override and "Track completion: off" in the progress engine (1.39.0, owner 2026-10-09): with "Volumes in
/// this edition: N" the volume answers (total, missing, upgrades, completion) count the edition's volumes 1..N by volume files and never
/// the regular edition's list, while chapter answers stay as they were; with tracking off nothing is compared. Synthetic names.
/// </summary>
public sealed class SeriesEditionProgressTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static List<GroupingRow> VolumeFiles(params int[] volumes) => volumes.Select(v => Archive($"Series v{v:00}")).ToList();

    private static IEnumerable<GroupingRow> ChapterFiles(int from, int to) => Enumerable.Range(from, to - from + 1).Select(c => Archive($"Series c{c:000}"));

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    /// <summary>The regular edition: 30 volumes of 10 chapters.</summary>
    private static VolumeMapInput Regular() =>
        new(Enumerable.Range(1, 30).Select(k => new VolumeMapVolume(k, Range((10 * (k - 1)) + 1, 10 * k))).ToList(),
            10, 30, false, VolumeListSource.MangaDex);

    /// <summary>A finished series with a finished 30-volume English edition, plus what an admin declared on the folder.</summary>
    private static ProgressFacts Finished(int? volumes = null, DeclaredEdition? edition = null, bool trackingOff = false, int? latest = null,
        bool? scanComplete = null) =>
        new("en", MetadataOrigin.Japan, MetadataOriginStatus.Complete, 30, 300, "Synthetic Press", 30, null, MetadataOriginStatus.Complete,
            true, latest, scanComplete, VolumeOverride: volumes, Edition: edition, TrackingOff: trackingOff);

    [Fact]
    public void Override_AllEditionVolumesHere_IsAWholeFinishedCollection()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(Enumerable.Range(1, 10).ToArray()), Regular(), Finished(10, DeclaredEdition.Omnibus));

        Assert.Empty(r.MissingVolumes);
        Assert.Equal((10, MissingTotalSource.Declared), (r.VolumeTotal, r.VolumeTotalSource));
        Assert.Equal((SeriesCompletion.CompleteCollection, CompletionBasis.Edition, 10, 10),
            (r.Completion, r.CompletionBasis, r.CompletionTarget, r.CompletionHeld));
        Assert.Equal(SeriesAnswer.HaveItAll, r.Answer);

        var dto = SeriesProgress.ToDto(r);
        Assert.Equal((10, DeclaredEdition.Omnibus, false), (dto.VolumeTotalOverride, dto.Edition, dto.TrackingOff));
        var missing = SeriesProgress.ToMissing(r, 10, 0);
        Assert.Equal(MissingVerdict.UpToDate, missing.Verdict);
        Assert.Equal((10, 0, MissingConfidence.High), (missing.Volumes!.Available, missing.Volumes.BehindBy, missing.Volumes.Confidence));
    }

    [Fact]
    public void WithoutOverride_TheSameFolderIsMeasuredAgainstTheRegularEdition()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(Enumerable.Range(1, 10).ToArray()), Regular(), Finished());

        Assert.Equal(20, r.MissingVolumes.Count);
        Assert.Equal(SeriesAnswer.FinishedMissing, r.Answer);
        Assert.Null(SeriesProgress.ToDto(r).VolumeTotalOverride);
    }

    [Fact]
    public void Override_HolesAndBehindAreTheEditionsOwn_AndFinishedNotHeldNamesTheEdition()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 2, 4), Regular(), Finished(6, DeclaredEdition.Master));

        Assert.Equal([3, 5, 6], r.MissingVolumes);
        Assert.Equal(2, r.VolumesBehind);
        Assert.Equal((SeriesCompletion.FinishedNotHeld, CompletionBasis.Edition, 6, 3), (r.Completion, r.CompletionBasis, r.CompletionTarget, r.CompletionHeld));
        Assert.Equal(SeriesAnswer.FinishedMissing, r.Answer);
    }

    [Fact]
    public void Override_NoUpgrades_AndChapterAnswersAreUnchanged()
    {
        // Volume files 1-2 and chapters 21-35: the regular list places chapters 21-30 in volume 3 (an official volume held as chapters).
        var rows = VolumeFiles(1, 2).Concat(ChapterFiles(21, 35)).ToList();
        var plain = SeriesProgress.Evaluate(rows, Regular(), Finished(latest: 40));
        var edition = SeriesProgress.Evaluate(rows, Regular(), Finished(5, DeclaredEdition.Omnibus, latest: 40));

        Assert.NotEmpty(plain.UpgradeVolumes);
        Assert.Empty(edition.UpgradeVolumes);
        // Chapters: the same holes, the same "behind" and the same total either way.
        Assert.Equal(plain.ChapterHoles, edition.ChapterHoles);
        Assert.Equal((plain.ChaptersBehind, plain.ChapterTotal), (edition.ChaptersBehind, edition.ChapterTotal));
        // Volumes: only the volume files count for the edition (3, 4, 5 missing); the regular reach is not read for them.
        Assert.Equal([3, 4, 5], edition.MissingVolumes);
        var gap = SeriesProgress.ToMissing(edition, 2, 15).Volumes!;
        Assert.Equal((2, 5, 3), (gap.Have, gap.Available!.Value, gap.BehindBy));
    }

    [Fact]
    public void Override_ChaptersOnlyFolder_HasNoVolumeAnswer()
    {
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 40).ToList(), Regular(), Finished(5, latest: 40));

        Assert.Empty(r.MissingVolumes);
        Assert.Empty(r.UpgradeVolumes);
        Assert.Null(SeriesProgress.ToMissing(r, 0, 40).Volumes);
        Assert.Null(r.CompletionBasis); // neither the regular edition's volumes nor an edition the folder holds no file of
        Assert.Equal(SeriesAnswer.UpToDate, r.Answer);
    }

    [Fact]
    public void Override_RunningSeries_HeldWhole_IsEverythingReleasedSoFar()
    {
        var facts = Finished(4) with { OriginStatus = MetadataOriginStatus.Ongoing, OfficialStatus = MetadataOriginStatus.Ongoing };
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 2, 3, 4), Regular() with { Ongoing = true }, facts);

        Assert.Empty(r.MissingVolumes);
        Assert.Equal(SeriesCompletion.None, r.Completion);
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.Running), (r.Answer, r.AnswerReason));
    }

    [Fact]
    public void TrackingOff_KeepsTheReach_ButGivesNoAnswer()
    {
        var rows = VolumeFiles(1, 2).Concat(ChapterFiles(21, 35)).ToList();
        var r = SeriesProgress.Evaluate(rows, Regular(), Finished(trackingOff: true, latest: 40));

        Assert.Equal(35, r.Reach.ReachChapter);
        Assert.Empty(r.MissingVolumes);
        Assert.Equal(0, r.MissingChapterCount);
        Assert.Empty(r.UpgradeVolumes);
        Assert.Equal(SeriesCompletion.None, r.Completion);
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NotTracked), (r.Answer, r.AnswerReason));
        var dto = SeriesProgress.ToDto(r);
        Assert.True(dto.TrackingOff);
        Assert.Equal(30, dto.Trackers.OfficialVolumes); // the trackers stay
        Assert.Equal(MissingVerdict.NoUnits, SeriesProgress.ToMissing(r, 2, 15).Verdict);

        var restarts = SeriesProgress.Evaluate(rows, Regular(), Finished(trackingOff: true), restarts: true);
        Assert.Equal(SeriesAnswerReason.NotTracked, restarts.AnswerReason);
    }
}
