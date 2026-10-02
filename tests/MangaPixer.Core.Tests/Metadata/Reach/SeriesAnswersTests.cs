namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Reach;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using Xunit;

/// <summary>
/// Unit tests for the one answer of the Completion tab (1.32.0, owner-approved): "Finished - you have it all", "Finished - missing
/// some", "Everything released so far", "Missing some", "Can't tell", and the engine rules behind them (finished needs the origin run
/// to have ended; Cancelled is ended; a volumes folder is judged by the volume edition; Official chapters; the one-shot). Synthetic names.
/// </summary>
public sealed class SeriesAnswersTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static List<GroupingRow> VolumeFiles(int from, int to) => Enumerable.Range(from, to - from + 1).Select(v => Archive($"Series v{v:00}")).ToList();

    private static List<GroupingRow> ChapterFiles(int from, int to) => Enumerable.Range(from, to - from + 1).Select(c => Archive($"Series c{c:000}")).ToList();

    private static IReadOnlyList<decimal> Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => (decimal)n).ToList();

    private static VolumeMapInput EvenMap(int volumes, int per, bool ongoing) =>
        new(Enumerable.Range(1, volumes).Select(k => new VolumeMapVolume(k, Range(per * (k - 1) + 1, per * k))).ToList(),
            per, volumes, ongoing, VolumeListSource.MangaDex);

    private static ProgressFacts English(
        MetadataOriginStatus? origin, int? originVolumes = null, int? official = null, MetadataOriginStatus? officialStatus = null,
        int? latest = null, bool? scanComplete = null) =>
        new("en", MetadataOrigin.Japan, origin, originVolumes, null, official is null ? null : "Synthetic Press", official, null,
            officialStatus, true, latest, scanComplete);

    private static (SeriesAnswer, SeriesAnswerReason) AnswerOf(ProgressResult r) => (r.Answer, r.AnswerReason);

    [Fact]
    public void FinishedOfficialEdition_HeldWhole_HaveItAll()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14), null,
            English(MetadataOriginStatus.Complete, 14, 14, MetadataOriginStatus.Complete));

        Assert.Equal((SeriesAnswer.HaveItAll, SeriesAnswerReason.None), AnswerOf(r));
        Assert.Equal(CompletionBasis.OfficialVolumes, r.CompletionBasis);
        Assert.Equal(SeriesAnswer.HaveItAll, SeriesProgress.ToDto(r).Answer);
    }

    [Fact]
    public void AFinishedEnglishEdition_OfARunningSeries_IsNotFinished()
    {
        // E1: only the original run says nothing more will come.
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 14), null,
            English(MetadataOriginStatus.Ongoing, 14, 14, MetadataOriginStatus.Complete));

        Assert.Equal(SeriesCompletion.None, r.Completion);
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.Running), AnswerOf(r));
    }

    [Fact]
    public void CancelledInTheOrigin_IsEnded_ForEveryChapterReleased()
    {
        // E2: "completely released" + Cancelled is finished by chapters (it was Complete only before 1.32.0).
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, MetadataOriginStatus.Cancelled, LatestChapter: 80, ScanlationComplete: true);

        var held = SeriesProgress.Evaluate(ChapterFiles(1, 80), null, facts);
        Assert.Equal((SeriesAnswer.HaveItAll, CompletionBasis.AllChapters), (held.Answer, held.CompletionBasis));

        var gap = SeriesProgress.Evaluate(ChapterFiles(1, 70), null, facts);
        Assert.Equal((SeriesAnswer.FinishedMissing, SeriesCompletion.FinishedNotHeld), (gap.Answer, gap.Completion));
    }

    [Fact]
    public void AnotherLanguage_IsFinishedWhenItsReleasedListReachesTheLastChapter()
    {
        // E2: no hard-coded English - the French list reaching the origin's last chapter finishes the French chapter release.
        var facts = new ProgressFacts("fr", MetadataOrigin.Japan, MetadataOriginStatus.Complete, OriginChapters: 50,
            ReleasedChapters: Range(1, 50).ToHashSet());

        var held = SeriesProgress.Evaluate(ChapterFiles(1, 50), null, facts);
        Assert.Equal((SeriesAnswer.HaveItAll, CompletionBasis.AllChapters), (held.Answer, held.CompletionBasis));

        var some = SeriesProgress.Evaluate(ChapterFiles(1, 45), null, facts);
        Assert.Equal((SeriesAnswer.FinishedMissing, 50, 45), (some.Answer, some.CompletionTarget, some.CompletionHeld));

        var partial = SeriesProgress.Evaluate(ChapterFiles(1, 40), null, facts with { ReleasedChapters = Range(1, 40).ToHashSet() });
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.WaitingForLanguage), AnswerOf(partial));
    }

    [Fact]
    public void AVolumesFolder_IsJudgedByTheVolumeEdition_NotByTheChapterRelease()
    {
        // E4 (rule V): ended in Japan (12 volumes), every chapter out in English, the English edition at 10 volumes and still coming.
        var map = EvenMap(12, 8, ongoing: false);
        var withEdition = SeriesProgress.Evaluate(VolumeFiles(1, 10), map,
            English(MetadataOriginStatus.Complete, 12, 10, MetadataOriginStatus.Ongoing, latest: 96, scanComplete: true));
        Assert.Equal(SeriesCompletion.None, withEdition.Completion);
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.WaitingForLanguage), AnswerOf(withEdition));

        // Without a known English volume edition the chapter release decides: the last volumes' chapters are out, not here.
        var withoutEdition = SeriesProgress.Evaluate(VolumeFiles(1, 10), map,
            English(MetadataOriginStatus.Complete, 12, latest: 96, scanComplete: true));
        Assert.Equal((SeriesAnswer.FinishedMissing, CompletionBasis.AllChapters), (withoutEdition.Answer, withoutEdition.CompletionBasis));
    }

    [Fact]
    public void AnOfficialChapterRelease_IsNamedOfficialChapters()
    {
        // E5: an English publisher that releases chapter by chapter (MANGA Plus-style) covers every chapter.
        var facts = new ProgressFacts("en", MetadataOrigin.Japan, MetadataOriginStatus.Complete, OfficialPublisher: "Synthetic Plus",
            OfficialChapters: 60, Licensed: true, LatestChapter: 60, ScanlationComplete: true);
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 60), null, facts);

        Assert.Equal((SeriesAnswer.HaveItAll, CompletionBasis.OfficialChapters), (r.Answer, r.CompletionBasis));

        var fan = SeriesProgress.Evaluate(ChapterFiles(1, 60), null, facts with { OfficialPublisher = null, OfficialChapters = null });
        Assert.Equal(CompletionBasis.AllChapters, fan.CompletionBasis);
    }

    [Fact]
    public void AOneShot_WithoutANumber_IsHeldWhole_OnlyWhenTheRecordSaysOneVolumeAndItEnded()
    {
        var file = new List<GroupingRow> { Archive("Synthetic Short Story") };

        var oneShot = SeriesProgress.Evaluate(file, null, English(MetadataOriginStatus.Complete, 1));
        Assert.Equal((SeriesAnswer.HaveItAll, SeriesAnswerReason.OneShot), AnswerOf(oneShot));
        Assert.Equal((SeriesCompletion.CompleteCollection, CompletionBasis.OriginRun, 1, 1),
            (oneShot.Completion, oneShot.CompletionBasis, oneShot.CompletionTarget, oneShot.CompletionHeld));

        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NoNumbers),
            AnswerOf(SeriesProgress.Evaluate(file, null, English(MetadataOriginStatus.Ongoing, 1))));
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NoNumbers),
            AnswerOf(SeriesProgress.Evaluate(file, null, English(MetadataOriginStatus.Complete, 3))));
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NoNumbers),
            AnswerOf(SeriesProgress.Evaluate([], null, English(MetadataOriginStatus.Complete, 1))));
    }

    [Fact]
    public void NumberingRestarts_CantTell()
    {
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 10), null, English(MetadataOriginStatus.Complete, 5), restarts: true);
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NumberingRestarts), AnswerOf(r));
    }

    [Fact]
    public void NothingKnownAboutReleasesInTheLanguage_CantTell()
    {
        var facts = new ProgressFacts("fr", MetadataOrigin.Japan, MetadataOriginStatus.Ongoing, 22);
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NothingKnownReleased),
            AnswerOf(SeriesProgress.Evaluate(ChapterFiles(1, 10), null, facts)));
    }

    [Theory]
    [InlineData(MetadataOriginStatus.Ongoing, SeriesAnswerReason.Running)]
    [InlineData(MetadataOriginStatus.Hiatus, SeriesAnswerReason.OnHiatus)]
    [InlineData(null, SeriesAnswerReason.StatusUnknown)]
    public void ARunningSeries_WithReleasedChaptersNotHere_IsMissingSome(MetadataOriginStatus? status, SeriesAnswerReason reason)
    {
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, status, LatestChapter: 150, ScanlationComplete: false);
        Assert.Equal((SeriesAnswer.MissingSome, reason), AnswerOf(SeriesProgress.Evaluate(ChapterFiles(1, 148), null, facts)));
    }

    [Theory]
    [InlineData(MetadataOriginStatus.Ongoing, SeriesAnswerReason.Running)]
    [InlineData(MetadataOriginStatus.Hiatus, SeriesAnswerReason.OnHiatus)]
    [InlineData(null, SeriesAnswerReason.StatusUnknown)]
    public void ARunningSeries_WithEverythingReleasedHere_IsEverythingSoFar(MetadataOriginStatus? status, SeriesAnswerReason reason)
    {
        var facts = new ProgressFacts("en", MetadataOrigin.Korea, status, LatestChapter: 150, ScanlationComplete: false);
        Assert.Equal((SeriesAnswer.UpToDate, reason), AnswerOf(SeriesProgress.Evaluate(ChapterFiles(1, 150), null, facts)));
    }

    [Fact]
    public void AVolumesFolderOfARunningSeries_WithNoVolumeTotal_CantTell()
    {
        // Nothing was compared: "everything released so far" would be a guess (132 such folders live at the design measurement).
        var none = SeriesProgress.Evaluate(VolumeFiles(1, 5), null, English(MetadataOriginStatus.Ongoing, 22, latest: 100));
        Assert.Equal((SeriesAnswer.CantTell, SeriesAnswerReason.NoVolumeTotal), AnswerOf(none));

        var known = SeriesProgress.Evaluate(VolumeFiles(1, 5), null, English(MetadataOriginStatus.Ongoing, 22, 5, MetadataOriginStatus.Ongoing, latest: 100));
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.Running), AnswerOf(known));
    }

    [Fact]
    public void ADroppedEnglishEdition_HeldWhole_IsEverythingSoFar_NotHaveItAll()
    {
        var r = SeriesProgress.Evaluate(VolumeFiles(1, 7), null,
            English(MetadataOriginStatus.Complete, 14, 7, MetadataOriginStatus.Cancelled));

        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.LanguageEditionDropped), AnswerOf(r));
    }

    [Fact]
    public void AnEndedSeries_WithReleasedChaptersNotHere_IsFinishedMissingSome_WithoutAnEdition()
    {
        // English chapters stop at 40 and are not complete; 36-40 are out and not here.
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 35), null, English(MetadataOriginStatus.Complete, 5, latest: 40, scanComplete: false));

        Assert.Equal((SeriesAnswer.FinishedMissing, SeriesAnswerReason.None), AnswerOf(r));
        Assert.Null(r.CompletionBasis);
    }

    [Fact]
    public void AnEndedSeries_WhoseLanguageReleaseIsNotFinished_IsEverythingSoFar_WaitingForTheLanguage()
    {
        var r = SeriesProgress.Evaluate(ChapterFiles(1, 40), null, English(MetadataOriginStatus.Complete, 5, latest: 40, scanComplete: false));
        Assert.Equal((SeriesAnswer.UpToDate, SeriesAnswerReason.WaitingForLanguage), AnswerOf(r));
    }

    [Fact]
    public void AVolumesFolder_CollectsVolumes_UnlessChapterFilesGoPastThem()
    {
        var map = EvenMap(3, 3, ongoing: true);
        Assert.True(SeriesAnswers.CollectsVolumes(SeriesReach.Of(VolumeFiles(1, 2), map)));
        // Chapter files a volume file here already holds do not make it a chapters folder.
        Assert.True(SeriesAnswers.CollectsVolumes(SeriesReach.Of([.. VolumeFiles(1, 2), Archive("Series c005")], map)));
        Assert.False(SeriesAnswers.CollectsVolumes(SeriesReach.Of([.. VolumeFiles(1, 2), Archive("Series c007")], map)));
        Assert.False(SeriesAnswers.CollectsVolumes(SeriesReach.Of(ChapterFiles(1, 6), map)));
    }
}
