namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for <see cref="MatchScorer"/> (stage 2, design section 2 + decisions 6, 8, 13): the
/// number-aware title, corroboration as re-ranking only, vetoes, the related-pair rule, bands from
/// the thresholds, review-only classes and the adaptive persisted set. Synthetic records only.
/// </summary>
public sealed class MatchScorerTests
{
    private readonly MatchScorer _scorer = new();

    private static MatchCandidate Rec(string id, string title, string[]? alt = null, MetadataFormat? format = MetadataFormat.Comic,
        string? origin = "Manga", int? year = null, int? volumes = null, int? chapter = null, string[]? authors = null,
        (string Id, string Rel)[]? related = null, bool? webtoon = null) =>
        new("mangaupdates", id, title, alt ?? [], format, origin, year, volumes, chapter, authors ?? [],
            (related ?? []).Select(r => new CandidateRelation(r.Id, r.Rel)).ToList(), webtoon);

    private static MatchQuery Query(string[] titles, WorkClass cls = WorkClass.Series, int archives = 5, int volumes = 0,
        int chapters = 0, int? earliestYear = null, string? category = null, bool tall = false, string[]? authors = null,
        string? comicInfo = null, QueryVariantKind firstKind = QueryVariantKind.Primary) =>
        new(titles.Select((t, i) => new QueryVariant(t, i == 0 ? firstKind : QueryVariantKind.EnglishTitle)).ToList(),
            new MatchContext(cls, archives, volumes, chapters, earliestYear, category, tall, authors ?? [], comicInfo));

    private MatchOutcome Score(MatchQuery q, params MatchCandidate[] c) => _scorer.Score(q, c, MatchThresholds.Default);

    [Fact]
    public void ExactTitle_ClearLead_IsAuto()
    {
        var o = Score(Query(["Some Series"]), Rec("1", "Some Series"), Rec("2", "Completely Different"));

        Assert.Equal(MatchBand.Auto, o.Band);
        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(1.0, o.Ranked[0].TitleScore, 6);
    }

    [Fact]
    public void AltTitle_MatchesTheEnglishVariant()
    {
        var o = Score(Query(["Romaji Name Here", "English Name Here"]), Rec("1", "Romaji Name Here Official", ["English Name Here"]));

        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void NoCandidates_IsUnmatched_WithNothingPersisted()
    {
        var o = Score(Query(["Some Series"]));

        Assert.Equal(MatchBand.Unmatched, o.Band);
        Assert.Empty(o.Ranked);
        Assert.Empty(o.ToPersist);
    }

    [Fact]
    public void WeakTitle_IsUnmatched_MediumIsReview()
    {
        Assert.Equal(MatchBand.Unmatched, Score(Query(["Some Series"]), Rec("1", "Nothing Alike At All")).Band);
        Assert.Equal(MatchBand.NeedsReview, Score(Query(["Some Series Name"]), Rec("1", "Some Series Name Gaiden")).Band);
    }

    [Fact]
    public void ExactTie_IsReview_WithCloseSecond()
    {
        var o = Score(Query(["Some Series"]), Rec("1", "Some Series"), Rec("2", "Some Series"));

        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.CloseSecond));
        Assert.Equal(2, o.ToPersist.Count);
    }

    [Fact]
    public void NovelTwin_IsPushedDown_ByTheFormatConflict()
    {
        var o = Score(Query(["Some Series"]), Rec("n", "Some Series", format: MetadataFormat.Novel, origin: "Novel"), Rec("m", "Some Series"));

        Assert.Equal(MatchBand.Auto, o.Band);
        Assert.Equal("m", o.Ranked[0].Candidate.ExternalId);
        Assert.True(o.Ranked[1].Reasons.HasFlag(MatchReason.TypeConflict));
    }

    [Fact]
    public void CategoryOrigin_BreaksATie_ButNeverLiftsTheRawScore()
    {
        var o = Score(Query(["Some Series"], category: "Manhwa"), Rec("jp", "Some Series", origin: "Manga"), Rec("kr", "Some Series", origin: "Manhwa"));

        Assert.Equal("kr", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(1.0, o.Ranked[0].TitleScore, 6);
        Assert.True(o.Ranked[1].Reasons.HasFlag(MatchReason.TypeConflict));
        Assert.Equal(MatchBand.Auto, o.Band); // 1.02 vs 0.90: lead 0.12
    }

    [Fact]
    public void OriginNameOfTheEnum_IsAcceptedToo()
    {
        var o = Score(Query(["Some Series"], category: "Manhwa"), Rec("kr", "Some Series", origin: nameof(MetadataOrigin.Korea)));

        Assert.False(o.Ranked[0].Reasons.HasFlag(MatchReason.TypeConflict));
    }

    [Fact]
    public void TallStrips_ConflictWithAPrintRecord()
    {
        var o = Score(Query(["Some Series"], tall: true), Rec("1", "Some Series", origin: "Manga", webtoon: false));

        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.TypeConflict));
        Assert.Equal(MatchBand.NeedsReview, o.Band);
    }

    [Fact]
    public void Chapters_AreComparedWithChapters_NotVolumes()
    {
        // E3: 150 chapter archives of a 20-volume series is NOT a conflict.
        var ok = Score(Query(["Some Series"], chapters: 150), Rec("1", "Some Series", volumes: 20, chapter: 160));
        Assert.Equal(MatchBand.Auto, ok.Band);
        Assert.False(ok.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));

        var tooMany = Score(Query(["Some Series"], volumes: 40), Rec("1", "Some Series", volumes: 20));
        Assert.True(tooMany.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.Equal(MatchBand.NeedsReview, tooMany.Band);
    }

    [Fact]
    public void SeasonRenumberedWebtoon_ComparesChaptersWithTheStatedTotal()
    {
        // The latest chapter number restarts per season; the status line states the total.
        var q = Query(["Some Series"], chapters: 600, category: "Manhwa");

        Assert.True(Score(q, Rec("1", "Some Series", origin: "Manhwa", chapter: 235)).Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        var withTotal = Score(q, Rec("1", "Some Series", origin: "Manhwa", chapter: 235) with { TotalChapters = 652 });
        Assert.False(withTotal.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.Equal(MatchBand.Auto, withTotal.Band);
    }

    [Fact]
    public void FileYearBeforeTheStart_IsAYearConflict()
    {
        var o = Score(Query(["Some Series"], earliestYear: 2001), Rec("1", "Some Series", year: 2015));

        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.YearConflict));
        Assert.Equal(MatchBand.NeedsReview, o.Band);
    }

    [Fact]
    public void SequelNumber_Disagreement_IsPenalized()
    {
        var o = Score(Query(["Some Series Part 3 - Subtitle"]),
            Rec("p3", "Some Series Part 3: Subtitle"), Rec("p4", "Some Series Part 4: Other Subtitle"), Rec("all", "Some Series"));

        Assert.Equal("p3", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, o.Band);
        Assert.True(o.Ranked.Single(r => r.Candidate.ExternalId == "p4").Reasons.HasFlag(MatchReason.NumberMismatch));
    }

    [Fact]
    public void SequelSplitVariant_StillPenalizesTheMissingNumber()
    {
        // "Title 2" must not auto-link to "Title" through its retrieval-only split variant.
        var q = new MatchQuery(
            [new QueryVariant("Some Series 2", QueryVariantKind.Primary), new QueryVariant("Some Series", QueryVariantKind.SequelNumberSplit)],
            new MatchContext(WorkClass.Series, 5, 0, 0, null, null, false, []));
        var o = _scorer.Score(q, [Rec("1", "Some Series")], MatchThresholds.Default);

        Assert.NotEqual(MatchBand.Auto, o.Band);
        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.NumberMismatch));
    }

    [Fact]
    public void RelatedPair_ForcesReview_UnlessTheTitleSeparatesThem()
    {
        var main = Rec("main", "Some Series", related: [("spin", "Spin-Off")]);
        var spin = Rec("spin", "Some Series!");
        var o = Score(Query(["Some Series"]), main, spin);
        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.RelatedPair));

        var farSpin = Rec("spin", "Some Series Side Story Collection");
        var o2 = Score(Query(["Some Series"]), main, farSpin);
        Assert.Equal(MatchBand.Auto, o2.Band);
        Assert.False(o2.Ranked[0].Reasons.HasFlag(MatchReason.RelatedPair));
    }

    [Theory]
    [InlineData(WorkClass.Mixed)]
    [InlineData(WorkClass.Ambiguous)]
    [InlineData(WorkClass.CollectionContainer)]
    public void ReviewOnlyClasses_AreNeverAuto(WorkClass cls)
    {
        var o = Score(Query(["Some Series"], cls: cls), Rec("1", "Some Series"));

        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.ReviewOnlyClass));
    }

    [Fact]
    public void OneShotFolder_NeedsAOneShotRecord()
    {
        Assert.Equal(MatchBand.Auto, Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story", volumes: 1)).Band);
        Assert.Equal(MatchBand.NeedsReview, Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story")).Band);

        var big = Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story", volumes: 12));
        Assert.Equal(MatchBand.NeedsReview, big.Band);
        Assert.True(big.Ranked[0].Reasons.HasFlag(MatchReason.OneShotMismatch));
    }

    [Fact]
    public void ArchiveLevel_AuthorConflict_VetoesAuto()
    {
        var q = Query(["Short Story"], cls: WorkClass.CollectionLeaf, archives: 1, authors: ["Some Artist"]);

        var conflict = Score(q, Rec("1", "Short Story", authors: ["Other Person"]));
        Assert.Equal(MatchBand.NeedsReview, conflict.Band);
        Assert.True(conflict.Ranked[0].Reasons.HasFlag(MatchReason.AuthorConflict));

        var agree = Score(q, Rec("1", "Short Story", authors: ["ARTIST Some"]));
        Assert.Equal(MatchBand.Auto, agree.Band);

        // Undecidable (no authors on the record): no veto.
        Assert.Equal(MatchBand.Auto, Score(q, Rec("1", "Short Story")).Band);
    }

    [Fact]
    public void FolderLevel_AuthorMismatch_IsNoVeto_AgreementIsATieBreak()
    {
        var q = Query(["Some Series"], authors: ["Some Author"]);

        Assert.Equal(MatchBand.Auto, Score(q, Rec("1", "Some Series", authors: ["Other Person"])).Band);

        var o = Score(q, Rec("a", "Some Series", authors: ["Other Person"]), Rec("b", "Some Series", authors: ["Some Author"]));
        Assert.Equal("b", o.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public void ComicInfoSeries_BreaksATie()
    {
        var o = Score(Query(["Some Series"], comicInfo: "Some Series Deluxe"),
            Rec("a", "Some Series"), Rec("b", "Some Series", ["Some Series Deluxe"]));

        Assert.Equal("b", o.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public void Thresholds_ComeFromSettings()
    {
        var q = Query(["Some Series Name"]);
        var rec = Rec("1", "Some Series Name!!");
        var close = Rec("1", "Some Serie Name");

        var strict = _scorer.Score(q, [close], new MatchThresholds(0.99, 0.10, 0.60));
        var loose = _scorer.Score(q, [close], new MatchThresholds(0.85, 0.10, 0.60));
        Assert.Equal(MatchBand.NeedsReview, strict.Band);
        Assert.Equal(MatchBand.Auto, loose.Band);
        Assert.Equal(MatchBand.Auto, _scorer.Score(q, [rec], new MatchThresholds(0.99, 0.10, 0.60)).Band);

        Assert.Throws<ArgumentOutOfRangeException>(() => _scorer.Score(q, [rec], new MatchThresholds(0.5, 0.1, 0.6)));
    }

    [Fact]
    public void ToPersist_OnlyTheTop_WhenItLeadsClearly()
    {
        var o = Score(Query(["Some Series Name"], cls: WorkClass.Ambiguous),
            Rec("1", "Some Series Name"), Rec("2", "Unrelated Words Entirely"), Rec("3", "Other Thing"));

        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.Equal(["1"], o.ToPersist.Select(p => p.Candidate.ExternalId));
    }

    [Fact]
    public void ToPersist_KeepsCandidatesWithinTheWindow_AtMostFive()
    {
        var recs = Enumerable.Range(1, 7).Select(i => Rec(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "Some Series")).ToArray();
        var o = Score(Query(["Some Series"]), recs);

        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.Equal(5, o.ToPersist.Count);
        Assert.Equal(7, o.Ranked.Count);
    }

    [Fact]
    public void DuplicateCandidates_AreMerged_KeepingTheRicherEntry()
    {
        var o = Score(Query(["Some Series"]), Rec("1", "Some Series"), Rec("1", "Some Series", authors: ["A B"], volumes: 3));

        Assert.Single(o.Ranked);
        Assert.Equal(3, o.Ranked[0].Candidate.Volumes);
        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void Score_IsDeterministic_RegardlessOfInputOrder()
    {
        var a = Rec("a", "Some Series");
        var b = Rec("b", "Some Series");
        var o1 = Score(Query(["Some Series"]), a, b);
        var o2 = Score(Query(["Some Series"]), b, a);

        Assert.Equal(o1.Ranked.Select(r => r.Candidate.ExternalId), o2.Ranked.Select(r => r.Candidate.ExternalId));
    }
}
