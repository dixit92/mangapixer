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
    public void OneShotFolder_NamedEdition_MatchesTheSeriesRecord()
    {
        // A whole series in one archive, named after a re-release: the planner drops the edition
        // phrase, so the series record leads its spin-offs instead of all scoring alike.
        var planner = new MatchQueryPlanner();
        var shape = new FolderShape("SOME TITLE! Master Edition", 1, ["SOME TITLE! Master Edition.cbz"], []);
        var q = planner.PlanFolder(shape, new WorkDetector().Classify(shape));

        var o = _scorer.Score(q,
            [Rec("1", "Some Title!", volumes: 10), Rec("2", "Some Title! Academy and So On"), Rec("3", "Some Title 2")],
            MatchThresholds.Default);

        Assert.Equal(WorkClass.OneShot, q.Context.Class);
        Assert.Equal("SOME TITLE", q.Variants[0].Text);
        // MangaUpdates search treats a trailing "!" as significant: the name as written is the second search.
        Assert.Equal(new QueryVariant("SOME TITLE!", QueryVariantKind.Primary), q.Variants[1]);
        Assert.NotEqual(MatchBand.Unmatched, o.Band);
        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
    }

    [Fact]
    public void Disambiguator_IsStrippedFromTheMainTitleOnly_NeverFromAnAltTitle()
    {
        // Live run (B): the right record is "Sprout (FAMILY Given)"; another record carries the ALT title
        // "Sprout (OTHER Person)" (its main title is a different name). Stripped, that alt scored a false 1.00
        // and tied the right record.
        var o = Score(Query(["Sprout"]), Rec("1", "Sprout (FAMILY Given)"), Rec("2", "Hana no Me", alt: ["Sprout (OTHER Person)"]));

        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(1.0, o.Ranked[0].TitleScore, 3);
        Assert.True(o.Ranked[1].TitleScore < 0.92, $"alt with disambiguator scored {o.Ranked[1].TitleScore:0.000}");
        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void AltTitleDisambiguator_NamingTheRecordsOwnAuthor_CountsInFull_OtherwiseCapped()
    {
        // Owner RC review (1.29.0): a folder named by a series' English title; the right record's main title is the original one
        // and its English alias carries MangaUpdates' author tag ("Fly Me to the Moon (HATA Kenjiro)"). Synthetic lookalikes.
        var own = Score(Query(["Moon Letter"]), Rec("1", "Moon Letter"),
            Rec("2", "Tsuki no Tegami", alt: ["Moon Letter (SATO Hana)"], authors: ["SATO Hana"]));
        Assert.Equal(1.0, own.Ranked.Single(r => r.Candidate.ExternalId == "2").TitleScore, 3);
        Assert.NotEqual(MatchBand.Auto, own.Band); // two works share the name: review, the right one among the top

        // Before the record is fetched (a search hit: no authors yet) the stripped alias is evidence, never alone an auto link.
        var hit = Score(Query(["Moon Letter"]), Rec("2", "Tsuki no Tegami", alt: ["Moon Letter (SATO Hana)"]));
        Assert.Equal(AutoMatchText.DisambiguatedAliasFactor, hit.Ranked[0].TitleScore, 3);
        Assert.NotEqual(MatchBand.Auto, hit.Band);
        Assert.NotEqual(MatchBand.Unmatched, hit.Band);
    }

    [Fact]
    public void TildeSubtitle_AndATitleNumber_ReachReviewWithoutANumberPenalty()
    {
        // Live run (V): folder "<Two Words> Level 99"; the record's English alt writes the subtitle
        // "~Subtitle~" with no space after the tilde, its main title "<Romaji> Level 99: <Subtitle>".
        var o = Score(Query(["Alpha Beta Level 99"]),
            Rec("1", "Arufa Beta Reberu 99: Hidden Subtitle Words", alt: ["Alpha Beta Level 99 ~Long Subtitle Words Here~"]));

        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[0].TitleScore, 3);
        Assert.False(o.Ranked[0].Reasons.HasFlag(MatchReason.NumberMismatch));
        Assert.Equal(MatchBand.NeedsReview, o.Band);
    }

    [Fact]
    public void SpacedDashSubtitle_HeadEqualToTheName_IsReviewOnly()
    {
        var o = Score(Query(["Alpha Beta"]), Rec("1", "Alpha Beta - The Long Subtitle of It"));

        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[0].TitleScore, 3);
        Assert.Equal(MatchBand.NeedsReview, o.Band);
    }

    [Fact]
    public void SequelNumber_StillPenalized_WhenTheRecordTitleLacksIt()
    {
        var o = Score(Query(["Alpha Beta 2"]), Rec("1", "Alpha Beta"));
        Assert.True(o.Ranked[0].Reasons.HasFlag(MatchReason.NumberMismatch));
    }

    [Fact]
    public void LeadingWordsOfALongTitle_AreReviewOnly_EvenAtTheLoosestThresholds()
    {
        // Live run (R): the folder is the first three words of a long romaji title.
        var q = Query(["Alpha to Beta Gamma"]);
        var record = Rec("1", "Alpha to Beta Gamma Delta Epsilon Zeta Eta Theta Iota Kappa Lambda Mu");
        var o = Score(q, record);
        var loosest = _scorer.Score(q, [record], new MatchThresholds(MatchThresholds.AutoTitleMin, MatchThresholds.MarginMin, MatchThresholds.ReviewFloorMin));

        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[0].TitleScore, 3);
        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.Equal(MatchBand.NeedsReview, loosest.Band);
    }

    [Fact]
    public void LeadingPart_NeedsThreeWholeWords()
    {
        // Two words, or a prefix that ends inside a word, is not "the leading part".
        var two = Score(Query(["Alpha Beta"]), Rec("1", "Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota"));
        var partial = Score(Query(["Alpha Beta Gam"]), Rec("1", "Alpha Beta Gamma Delta Epsilon Zeta Eta Theta Iota"));

        Assert.True(two.Ranked[0].TitleScore < MatchScorer.SubtitleHeadCap);
        Assert.True(partial.Ranked[0].TitleScore < MatchScorer.SubtitleHeadCap);
    }

    [Fact]
    public void CloseSecond_IsOnlyRaised_WhenTheTopReachesTheReviewFloor()
    {
        var poor = Score(Query(["Alpha to Beta Gamma"]), Rec("1", "Unrelated Words Here"), Rec("2", "Other Unrelated Words"));
        Assert.Equal(MatchBand.Unmatched, poor.Band);
        Assert.False(poor.Ranked[0].Reasons.HasFlag(MatchReason.CloseSecond));

        var tied = Score(Query(["Sprout"]), Rec("1", "Sprout (OTHER Person)"), Rec("2", "Sprout (THIRD Person)"));
        Assert.True(tied.Ranked[0].Reasons.HasFlag(MatchReason.CloseSecond));
    }

    [Fact]
    public void SharedNumberAlone_IsDamped()
    {
        var damped = Score(Query(["Alpha Beta 99"]), Rec("1", "Kappa Lambda 99"));
        var plain = TitleSimilarity.Score("Alpha Beta 99", "Kappa Lambda 99");

        Assert.True(TitleSimilarity.SharesOnlyDigitTokens("Alpha Beta 99", "Kappa Lambda 99"));
        Assert.False(TitleSimilarity.SharesOnlyDigitTokens("Alpha Beta 99", "Alpha Kappa 99"));
        Assert.Equal(plain * MatchScorer.DigitOnlyOverlapFactor, damped.Ranked[0].TitleScore, 6);
    }

    [Fact]
    public void CreatorSplit_IsReviewOnly_UnlessTheNamedAuthorWroteTheRecord()
    {
        // "Family Given - Sprout Garden": the title part is searched, but alone it never links.
        var planner = new MatchQueryPlanner();
        var shape = new FolderShape("Family Given - Sprout Garden", 2, ["Family Given - Sprout Garden v01.cbz", "Family Given - Sprout Garden v02.cbz"], []);
        var q = planner.PlanFolder(shape, new WorkDetector().Classify(shape));
        Assert.Contains(q.Variants, v => v.Kind == QueryVariantKind.CreatorSplit && v.Text == "Sprout Garden");

        var stranger = _scorer.Score(q, [Rec("1", "Sprout Garden", authors: ["Other Person"])], MatchThresholds.Default);
        Assert.Equal(MatchScorer.SubtitleHeadCap - MatchScorer.DerivedVariantDiscount, stranger.Ranked[0].TitleScore, 3);
        Assert.Equal(MatchBand.NeedsReview, stranger.Band);

        var author = _scorer.Score(q, [Rec("1", "Sprout Garden", authors: ["GIVEN Family"])], MatchThresholds.Default);
        Assert.True(author.Ranked[0].TitleScore >= 0.92, $"title {author.Ranked[0].TitleScore:0.000}");
        Assert.Equal(MatchBand.Auto, author.Band);
    }

    [Fact]
    public void TrailingTwoWordBracket_AsATitle_NeverAutoLinksOnItsOwn()
    {
        // "Sprout [Family Given]": the bracket is both an English-title variant and a creator hint. A record
        // TITLED "Family Given" must not auto-link through it; a record whose title the folder name resembles may.
        var planner = new MatchQueryPlanner();
        var shape = new FolderShape("Sprout [Family Given]", 2, ["Sprout v01.cbz", "Sprout v02.cbz"], []);
        var q = planner.PlanFolder(shape, new WorkDetector().Classify(shape));
        Assert.Contains(q.Variants, v => v.Kind == QueryVariantKind.EnglishTitle && v.Text == "Family Given");

        var titled = _scorer.Score(q, [Rec("1", "Family Given")], MatchThresholds.Default);
        Assert.NotEqual(MatchBand.Auto, titled.Band);
        var loose = _scorer.Score(q, [Rec("1", "Family Given")], new MatchThresholds(MatchThresholds.AutoTitleMin, MatchThresholds.MarginMin, MatchThresholds.ReviewFloorMin));
        Assert.NotEqual(MatchBand.Auto, loose.Band);

        var real = _scorer.Score(q, [Rec("1", "Sprout", authors: ["Family Given"])], MatchThresholds.Default);
        Assert.Equal(MatchBand.Auto, real.Band);
    }

    [Fact]
    public void RomajiWithEnglishBracket_StillAutoLinks_ByTheEnglishTitle()
    {
        // "Romaji [English Title]" (F02 shape): the folder's own name resembles the record, so the bracket may carry it.
        var q = new MatchQuery(
            [new QueryVariant("Kappa Meshi", QueryVariantKind.Primary), new QueryVariant("Delicious Kappa", QueryVariantKind.EnglishTitle)],
            new MatchContext(WorkClass.Series, 5, 5, 0, null, null, false, [], CreatorHints: ["Delicious Kappa"]));
        var o = _scorer.Score(q, [Rec("1", "Kappa Meshi", alt: ["Delicious Kappa"])], MatchThresholds.Default);

        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void AnAliasThatIsTheMainTitlesHead_IsReviewOnly()
    {
        // A spin-off "Alpha Beta - Side Name" lists "Alpha Beta" as an alias; the series "Alpha Beta - Main
        // Subtitle" is only reached by its head. Neither may auto-link the name both share.
        var o = Score(Query(["Alpha Beta"]),
            Rec("spin", "Alpha Beta - Side Name Diary", alt: ["Alpha Beta"]),
            Rec("main", "Alpha Beta - Main Subtitle Words", alt: ["Alpha Beta ~Main Subtitle Words~"]));

        Assert.All(o.Ranked, r => Assert.Equal(MatchScorer.SubtitleHeadCap, r.TitleScore, 3));
        Assert.Equal(MatchBand.NeedsReview, o.Band);

        // A plain alias of a record whose main title has no subtitle is still a full match.
        var plain = Score(Query(["Alpha Beta"]), Rec("1", "Arufa Beta", alt: ["Alpha Beta"]));
        Assert.Equal(MatchBand.Auto, plain.Band);
    }

    private static MatchQuery WithUnits(MatchQuery q, int volumeLike, int chapterLike, int? localVolumes, int? localChapters) =>
        q with { Context = q.Context with { VolumeLikeCount = volumeLike, ChapterLikeCount = chapterLike, LocalVolumes = localVolumes, LocalChapters = localChapters } };

    [Fact]
    public void Count_ComparesTheHighestUnitNumber_NotTheFileCount()
    {
        // 12 archives, but they are volumes 1-6 plus six "x.5" extras: no conflict with a 6-volume record.
        var extras = Score(WithUnits(Query(["Some Series"]), 12, 0, localVolumes: 6, localChapters: null), Rec("1", "Some Series", volumes: 6));
        Assert.False(extras.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));

        // 150 archives that are chapters 950-1100: a 200-chapter record conflicts, by the number.
        var late = Score(WithUnits(Query(["Some Series"]), 0, 150, null, localChapters: 1100), Rec("1", "Some Series", chapter: 200));
        Assert.True(late.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
    }

    [Fact]
    public void Count_MixedVolumeAndChapterArchives_GiveNoCountSignal()
    {
        var o = Score(WithUnits(Query(["Some Series"]), 40, 300, 40, 300), Rec("1", "Some Series", volumes: 2, chapter: 10));

        Assert.False(o.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.Equal(1.0, o.Ranked[0].AdjustedScore, 6); // neither an agreement nor a conflict
    }

    [Fact]
    public void Count_PublishedSide_IsTheLargestNumberOfAnySource()
    {
        // Season-renumbered webtoon: latest chapter 18, status total 195 (live run, T).
        var season = Score(WithUnits(Query(["Some Series"]), 0, 195, null, 195),
            Rec("1", "Some Series", chapter: 18) with { TotalChapters = 195 });
        Assert.False(season.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.Equal(MatchBand.Auto, season.Band);

        // The English publisher's totals bound it too.
        var english = Score(WithUnits(Query(["Some Series"]), 30, 0, 30, null),
            Rec("1", "Some Series", volumes: 12) with { EnglishVolumes = 30, EnglishChapters = 250 });
        Assert.False(english.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        var none = Score(WithUnits(Query(["Some Series"]), 30, 0, 30, null), Rec("1", "Some Series", volumes: 12));
        Assert.True(none.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
    }

    [Fact]
    public void Count_ChapterFolder_OfAVolumeRecord_WithOnlyALatestChapter_IsNoConflict()
    {
        // Backlog (1.29.0, spin-off shape): 58 chapter archives against a record with 10 volumes whose latest tracked chapter
        // is 12 - chapters have no total to be compared with, so no Count penalty (the latest release may lag a licence).
        var spinOff = Score(WithUnits(Query(["Some Series"]), 0, 58, null, 58), Rec("1", "Some Series", volumes: 10, chapter: 12));
        Assert.False(spinOff.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.Equal(MatchBand.Auto, spinOff.Band);

        // A stated chapter total still bounds it.
        var total = Score(WithUnits(Query(["Some Series"]), 0, 58, null, 58),
            Rec("1", "Some Series", volumes: 10, chapter: 12) with { TotalChapters = 20 });
        Assert.True(total.Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
    }

    [Fact]
    public void Count_UsesThePlannersUnits_OverTheArchiveCounts()
    {
        // 40 archives in a Volumes subfolder that are volumes 1-10 (and their x.5 extras): the numbers count.
        var units = new LocalUnitCounts(40, 0, 1, 10, null, null);
        var q = Query(["Some Series"], volumes: 40) is var b ? b with { Context = b.Context with { Units = units } } : null;
        Assert.False(Score(q!, Rec("1", "Some Series", volumes: 10)).Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
        Assert.True(Score(Query(["Some Series"], volumes: 40), Rec("1", "Some Series", volumes: 10)).Ranked[0].Reasons.HasFlag(MatchReason.CountConflict));
    }

    private static MatchQuery WithHints(MatchQuery q, params string[] hints) => q with { Context = q.Context with { CreatorHints = hints } };

    [Fact]
    public void CreatorHint_SeparatesSameTitledRecords_ByTheirDisambiguator()
    {
        // "Sprout [Family Given].cbz" in a one-shot collection: three records score 1.00 on the title.
        var q = WithHints(Query(["Sprout"], WorkClass.CollectionLeaf, archives: 1), "Family Given");
        var o = Score(q, Rec("1", "Sprout (OTHER Person)"), Rec("2", "Sprout", volumes: 15), Rec("3", "Sprout (FAMILY Given)"));

        Assert.Equal("3", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void CreatorHint_MatchesRecordAuthors_InEitherNameOrder_AndNeverVetoes()
    {
        var q = WithHints(Query(["Some Series"]), "Family Given");
        var agree = Score(q, Rec("1", "Some Series", authors: ["Given Family"]), Rec("2", "Some Series 2nd"));
        var none = Score(WithHints(Query(["Some Series"]), "English Words"), Rec("1", "Some Series", authors: ["Given Family"]));

        Assert.Equal(1.0 + MatchScorer.CreatorHintAgree, agree.Ranked[0].AdjustedScore, 6);
        Assert.Equal(1.0, none.Ranked[0].AdjustedScore, 6);
        Assert.Equal(MatchReason.None, none.Ranked[0].Reasons & MatchScorer.VetoReasons);
    }

    [Fact]
    public void RecordTitleWithSubtitle_RanksByTheHeadBeforeTheColon_ButNeverAutoLinks()
    {
        var o = Score(Query(["Fake Hero of the Year"]),
            Rec("1", "Fake Hero of the Year: Ideal Hero? Sorry, a Fake"), Rec("2", "The Year of Nothing"));
        var exact = Score(Query(["Some Saga"]), Rec("1", "Some Saga"), Rec("2", "Some Saga: Before the Fall"));

        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[0].TitleScore, 6);
        Assert.Equal(MatchBand.NeedsReview, o.Band);
        Assert.Equal(("1", MatchBand.Auto), (exact.Ranked[0].Candidate.ExternalId, exact.Band));
    }

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
        // Positive-only (1.27.0, owner option a'): the other origin is neutral, so the lead is only the +0.02.
        Assert.False(o.Ranked[1].Reasons.HasFlag(MatchReason.TypeConflict));
        Assert.Equal(MatchBand.NeedsReview, o.Band);
    }

    [Fact]
    public void CategoryOrigin_Mismatch_IsNeutral_NoPenaltyAndNoVeto()
    {
        // A manhwa filed under a "Manga" folder still auto-links (live run, owner option a').
        var o = Score(Query(["Some Series"], category: "Manga"), Rec("kr", "Some Series", origin: "Manhwa", webtoon: true));

        Assert.Equal(MatchBand.Auto, o.Band);
        Assert.Equal(MatchReason.None, o.Ranked[0].Reasons);
        Assert.Equal(1.0, o.Ranked[0].AdjustedScore, 6);
    }

    [Theory]
    [InlineData("Manga", true)]
    [InlineData(" manhwa ", true)]
    [InlineData("WEBTOONS", true)]
    [InlineData("Manga Collection", false)] // whole name only
    [InlineData("Ongoing", false)] // a shelf word is never a category hint
    [InlineData(null, false)]
    public void CategoryFolderName_IsTheExactWholeName(string? name, bool expected) =>
        Assert.Equal(expected, AutoMatchText.IsCategoryFolderName(name));

    [Fact]
    public void CategoryAndShelfWords_AreOneStopList_ForCreatorNames()
    {
        Assert.True(AutoMatchText.IsCategoryWord("Manhwa"));
        Assert.True(AutoMatchText.IsCategoryWord("Light Novels"));
        Assert.False(AutoMatchText.IsAuthorLike("Manga", requireTwoTokens: false));
        Assert.False(AutoMatchText.IsAuthorLike("Light Novels", requireTwoTokens: true));
        Assert.Empty(AutoMatchText.CategoryFolderWords.Intersect(AutoMatchText.ShelfWords, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void OriginNameOfTheEnum_IsAcceptedToo()
    {
        var o = Score(Query(["Some Series"], category: "Manhwa"), Rec("kr", "Some Series", origin: nameof(MetadataOrigin.Korea)));

        Assert.False(o.Ranked[0].Reasons.HasFlag(MatchReason.TypeConflict));
    }

    [Fact]
    public void TallStrips_AreAHintOnly_APrintRecordIsNotAConflict()
    {
        // Owner, 1.27.0 review: Japanese vertical manga exist - tall pages favour webtoon records but never block.
        var o = Score(Query(["Some Series"], tall: true), Rec("1", "Some Series", origin: "Manga", webtoon: false));

        Assert.False(o.Ranked[0].Reasons.HasFlag(MatchReason.TypeConflict));
        Assert.Equal(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void TallStrips_FavourAWebtoonRecord_OverAPrintRecordOfTheSameTitle()
    {
        var o = Score(Query(["Some Series"], tall: true),
            Rec("jp", "Some Series", origin: "Manga", webtoon: false), Rec("kr", "Some Series", origin: "Manhwa"));

        Assert.Equal("kr", o.Ranked[0].Candidate.ExternalId);
        Assert.True(o.Ranked[0].AdjustedScore > o.Ranked[1].AdjustedScore);
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
    public void OneShotFolder_AutoLinks_WhateverTheRecordsVolumeCount()
    {
        // One archive can be a one-shot, one volume or a whole multi-volume series (owner, 2026-09-26):
        // the record's volume count never blocks auto; only the stricter title score does.
        Assert.Equal(MatchBand.Auto, Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story", volumes: 1)).Band);
        Assert.Equal(MatchBand.Auto, Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story")).Band);

        var wholeSeries = Score(Query(["Short Story"], cls: WorkClass.OneShot, archives: 1), Rec("1", "Short Story", volumes: 12));
        Assert.Equal(MatchBand.Auto, wholeSeries.Band);
        Assert.False(wholeSeries.Ranked[0].Reasons.HasFlag(MatchReason.OneShotMismatch));
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

    private static MatchQuery Planned(string folderName, int chapters)
    {
        var shape = new FolderShape(folderName, 2,
            Enumerable.Range(1, chapters).Select(i => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{folderName} - Chapter {i:000}.cbz")).ToList(), []);
        return new MatchQueryPlanner().PlanFolder(shape, new WorkDetector().Classify(shape));
    }

    [Fact]
    public void AFolderSubtitle_ThatIsASpinOffsSubtitle_OutweighsTheBareMainTitle()
    {
        // 1.30.0 (backlog, owner fixture shape "<Series> - <Subtitle> [<Note>]", 58 chapter archives): the main record matched only
        // the head through the subtitle split (0.97) and stayed a close, related second. It is now capped like a record-side head.
        var q = Planned("Alpha Garden - Before the Frost", 58);
        var main = Rec("1", "Alpha Garden", volumes: 30, related: [("2", "Spin-off")]);
        var spinOff = Rec("2", "Alpha Garden - Before the Frost", volumes: 10, related: [("1", "Main Story")]);

        var o = Score(q, main, spinOff);

        Assert.Equal("2", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[1].TitleScore, 3);
        Assert.Equal(MatchBand.Auto, o.Band);
        Assert.Equal(MatchReason.None, o.Ranked[0].Reasons & MatchScorer.VetoReasons);
    }

    [Fact]
    public void AFolderSubtitle_NoCandidateHas_LeavesTheHeadMatchAlone()
    {
        // "Title Words - Something" where no record carries "Something": the franchise record keeps its derived score.
        var q = Planned("Alpha Garden - Before the Frost", 20);

        var o = Score(q, Rec("1", "Alpha Garden", volumes: 30));

        Assert.Equal(1.0 - MatchScorer.DerivedVariantDiscount, o.Ranked[0].TitleScore, 3);
    }

    [Fact]
    public void AFolderSubtitle_TheMainRecordCarriesItself_IsNotCapped()
    {
        // The subtitle is the main record's own English subtitle (its alt "Alpha Garden: Before the Frost"): nothing to outweigh.
        var q = Planned("Alpha Garden - Before the Frost", 20);

        var o = Score(q, Rec("1", "Arufa Gaaden", alt: ["Alpha Garden: Before the Frost"], volumes: 30), Rec("3", "Alpha Garden Other"));

        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.True(o.Ranked[0].TitleScore > 0.95);
    }
}
