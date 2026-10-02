namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Scores provider candidates for one work and bands the result (metadata stage 2, design
/// section 2 + owner decisions 6, 8 and 13). Pure and deterministic.
///
/// <list type="bullet">
/// <item><b>Title</b>: the stage-1 <see cref="TitleSimilarity"/> over every (variant, record title /
/// alt title) pair - the MAIN title's trailing <c>(disambiguator)</c> also counts stripped - minus <see cref="NumberPenalty"/> when the pair disagrees on a sequel / part
/// number (<see cref="TitleNormalizer.NumberTokens"/>). Retrieval-only variants (subtitle and
/// sequel-number splits) are compared with the numbers of the name they came from and carry a
/// small <see cref="DerivedVariantDiscount"/>, so a full-name match always wins a tie.</item>
/// <item><b>Corroboration</b> re-ranks only: small agreements and conflict penalties change the
/// ADJUSTED score (ordering and margin), never the raw title score the auto threshold reads.
/// Format (novel / artbook / audio), origin vs the category folder (agreement only, 1.27.0) or - stronger, both ways, never a
/// veto - vs the folder's declared type (1.30.0), tall strips vs a print record,
/// counts (volumes vs volumes, chapters vs chapters - never chapters vs volumes), earliest file year
/// vs start year, one-shot shape, ComicInfo series, creator tags.</item>
/// <item><b>Vetoes</b> demote auto to review: any corroboration conflict, a related top pair the
/// number-aware title does not separate, a top that only the folder's subtitle separates from a record of its own series
/// family (1.30.0, <see cref="MatchReason.SubtitleFamily"/>), and - mandatory for archive-level works - an author
/// conflict (the archive's creator tags name none of the record's authors).</item>
/// <item><b>Bands</b> from <see cref="MatchThresholds"/>: auto = raw title &gt;= AutoTitle, adjusted lead
/// &gt;= Margin over the next distinct record, no veto, and an auto-capable class; a
/// <see cref="WorkClass.OneShot"/> folder additionally needs raw &gt;= <see cref="OneShotAutoTitle"/> (a code
/// constant, owner decision 13). A single archive may hold a one-shot, one volume or a whole multi-volume series
/// (owner, 2026-09-26), so the record's volume count never blocks auto; a one-shot record only breaks ties. Review = raw &gt;= ReviewFloor.</item>
/// <item><b>ToPersist</b> (decision 8): candidates within <see cref="PersistWindow"/> of the top, at most
/// <see cref="PersistMax"/>; only the top when it leads the next by <see cref="PersistClearLead"/> or more - plus, always, a
/// record of the top's series family that only the folder's subtitle set apart (1.30.0), so the review shows both.</item>
/// </list>
/// </summary>
public sealed class MatchScorer : IMatchScorer
{
    /// <summary>
    /// 1.32.0 (lane B, research 4.3): evidence for COMICS records only (<see cref="ComicsEvidenceRules.IsComicsProvider"/>), adjusted
    /// score only - the raw title score and the auto bands are unchanged, and a manga record never reads it. The Grand Comics Database
    /// lists one series per edition and translation (Blacksad: 38 series), so the start year of the name, the language, the shape
    /// (issues or collected books) and the publisher tell same-named editions apart.
    /// </summary>
    public const double ComicsStartYearExact = 0.05;

    /// <summary>The comics record starts one year before or after the name's <c>(YYYY)</c>.</summary>
    public const double ComicsStartYearNear = 0.02;

    public const double ComicsLanguageAgree = 0.03;
    public const double ComicsLanguageMismatch = -0.05;
    public const double ComicsShapeAgree = 0.03;
    public const double ComicsShapeMismatch = -0.05;
    public const double ComicsPublisherAgree = 0.03;

    /// <summary>Subtracted from a title pair that disagrees on a sequel / part number.</summary>
    public const double NumberPenalty = 0.15;

    /// <summary>Subtracted from pairs scored with a retrieval-only (derived) variant.</summary>
    public const double DerivedVariantDiscount = 0.03;

    /// <summary>One-shot folders need at least this raw title score (code constant, decision 13).</summary>
    public const double OneShotAutoTitle = 0.95;

    public const double FormatConflict = -0.30;
    public const double Conflict = -0.10;
    public const double OriginAgree = 0.02;
    public const double CountAgree = 0.01;
    public const double YearAgree = 0.01;
    public const double OneShotAgree = 0.02;
    public const double ComicInfoAgree = 0.05;
    public const double AuthorAgree = 0.05;

    /// <summary>
    /// The record fits the folder's DECLARED type and its implied origin (1.30.0, owner: a strong hint; adjusted score only).
    /// With <see cref="DeclaredTypeMismatch"/> it spans exactly the default margin: a declaration can settle a TIE on the title
    /// between records of different origins, but a record the declaration contradicts is only ever overtaken by one that
    /// matches the title at least as well - a wrong declaration never beats a better title into an automatic link.
    /// </summary>
    public const double DeclaredTypeAgree = 0.05;

    /// <summary>The record contradicts the folder's declared type (1.30.0): lowers the adjusted score, never a veto.</summary>
    public const double DeclaredTypeMismatch = -0.05;

    /// <summary>The candidate's cover is the same as the local cover (<see cref="CoverEvidence"/>, 1.28.0; adjusted score only).</summary>
    public const double CoverAgree = 0.05;

    /// <summary>
    /// A creator hint from the name (<c>Title [Family Given]</c>) names the record's author or its
    /// <c>(AUTHOR Name)</c> disambiguator (1.26.1): enough to separate same-titled records by the margin.
    /// Includes <see cref="AuthorAgree"/> when both apply (the total author bonus is this value).
    /// </summary>
    public const double CreatorHintAgree = 0.10;

    /// <summary>
    /// The title score of a record whose title, up to its subtitle break, EQUALS the searched name (<c>Title</c> vs
    /// <c>Title: Long Subtitle</c>, 1.26.1; <c>Title ~Subtitle~</c> and <c>Title - Subtitle</c>, 1.27.0), or that
    /// starts, word for word, with a searched name of at least <see cref="LeadingPartMinWords"/> words (the
    /// leading part of a long title, 1.27.0): below the lowest allowed auto threshold, so such a match only ranks
    /// the record for review and never links it on its own.
    /// </summary>
    public const double SubtitleHeadCap = 0.80;

    /// <summary>A title pair whose only shared tokens are numbers keeps this share of its similarity (1.27.0).</summary>
    public const double DigitOnlyOverlapFactor = 0.5;

    /// <summary>Related top two need at least this raw title gap to stay auto.</summary>
    public const double RelatedSeparation = 0.10;

    /// <summary>A count conflict: the local unit number &gt; <c>CountFactor x published + CountSlack</c> (<see cref="CountEvidence"/>).</summary>
    public const double CountFactor = CountEvidence.Factor;
    public const int CountSlack = CountEvidence.Slack;


    /// <summary>Rounding tolerance of score comparisons against a threshold.</summary>
    private const double ScoreTolerance = 1e-9;

    public const double PersistWindow = 0.15;
    public const int PersistMax = 5;
    public const double PersistClearLead = 0.30;

    /// <summary>Reasons that veto auto (a conflict between local evidence and the record).</summary>
    public const MatchReason VetoReasons = MatchReason.CountConflict | MatchReason.YearConflict | MatchReason.TypeConflict
        | MatchReason.RelatedPair | MatchReason.AuthorConflict | MatchReason.SubtitleFamily;

    public MatchOutcome Score(MatchQuery query, IReadOnlyList<MatchCandidate> candidates, MatchThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!thresholds.IsValid)
            throw new ArgumentOutOfRangeException(nameof(thresholds), "Match thresholds are outside their bounds.");

        var distinct = Distinct(candidates);
        if (distinct.Count == 0)
            return new MatchOutcome(MatchBand.Unmatched, [], []);

        var ctx = query.Context;
        var variants = PrepareVariants(query.Variants);
        var (scored, capped) = SubtitleOutweighsHead(distinct.Select(c => ScoreOne(c, variants, ctx)).ToList(), variants, thresholds);

        var ranked = scored
            .OrderByDescending(s => s.AdjustedScore)
            .ThenByDescending(s => s.TitleScore)
            .ThenBy(s => s.Candidate.Provider, StringComparer.Ordinal)
            .ThenBy(s => s.Candidate.ExternalId, StringComparer.Ordinal)
            .ToList();

        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1] : null;
        var reasons = top.Reasons;

        if (second is not null && AreRelated(top.Candidate, second.Candidate)
            && top.TitleScore - second.TitleScore < RelatedSeparation)
            reasons |= MatchReason.RelatedPair;

        // 1.30.0 (owner: "not automatic - keep it in review"): the subtitle cap decided between the top and a record of its own
        // series family that matched the name's head and would otherwise have been a close second (a main series vs its spin-off).
        // The rival is stored with the review candidates even outside the persist window, so the admin sees both records.
        var subtitleRivals = capped.Count == 0 ? [] : ranked.Skip(1)
            .Where(s => capped.TryGetValue(s.Candidate.ExternalId, out var uncapped)
                && uncapped >= top.TitleScore - RelatedSeparation - ScoreTolerance
                && SeriesFamilies.AreFamily(top.Candidate, s.Candidate))
            .ToList();
        if (subtitleRivals.Count > 0)
            reasons |= MatchReason.SubtitleFamily;

        // 1.30.0: another candidate worth reviewing is the top's series family - a chip on review and Auto-linked rows, never a veto.
        if (ranked.Skip(1).Any(s => s.TitleScore >= thresholds.ReviewFloor - ScoreTolerance && SeriesFamilies.AreFamily(top.Candidate, s.Candidate)))
            reasons |= MatchReason.SeriesFamily;

        // "Close second" only means something for a top that could be reviewed (1.27.0): below the floor the work
        // is unmatched, and a chip about two equally poor candidates only confuses.
        // Compared with a rounding tolerance (1.30.0): evidence sized to span the margin exactly (a declared type's +0.05 / -0.05)
        // must not depend on how the adjusted scores' sums happen to round.
        var margin = top.AdjustedScore - (second?.AdjustedScore ?? 0);
        var leads = margin >= thresholds.Margin - ScoreTolerance;
        if (!leads && top.TitleScore >= thresholds.ReviewFloor)
            reasons |= MatchReason.CloseSecond;

        var autoClass = IsAutoCapable(ctx.Class);
        if (!autoClass)
            reasons |= MatchReason.ReviewOnlyClass;

        var oneShotOk = ctx.Class != WorkClass.OneShot || top.TitleScore >= OneShotAutoTitle;

        var band = autoClass && oneShotOk
            && top.TitleScore >= thresholds.AutoTitle
            && leads
            && (reasons & VetoReasons) == 0
                ? MatchBand.Auto
                : top.TitleScore >= thresholds.ReviewFloor ? MatchBand.NeedsReview : MatchBand.Unmatched;

        top = top with { Reasons = reasons };
        ranked[0] = top;

        return new MatchOutcome(band, ranked, band == MatchBand.Unmatched ? [] : ChoosePersisted(ranked, subtitleRivals));
    }

    /// <summary>Classes whose works may be auto-linked (folder-level series and archive-level collections).</summary>
    public static bool IsAutoCapable(WorkClass cls) => cls is WorkClass.Series or WorkClass.SeriesWithUnits
        or WorkClass.OneShot or WorkClass.CollectionLeaf or WorkClass.ArtistCollection;

    /// <summary>Archive-level classes: the author-conflict veto applies.</summary>
    public static bool IsArchiveLevel(WorkClass cls) => cls is WorkClass.CollectionLeaf or WorkClass.ArtistCollection;

    private sealed record PreparedVariant(string Text, IReadOnlyList<string> Numbers, bool Derived, string NumberSource, QueryVariantKind Kind)
    {
        /// <summary>The scoring form (compared with heads and record-title prefixes).</summary>
        public string Form { get; } = TitleNormalizer.ScoringForm(Text);

        public int Words { get; } = TitleNormalizer.ScoringForm(Text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>A name must have at least this many words to count as the leading part of a longer record title.</summary>
    public const int LeadingPartMinWords = 3;

    private static bool IsLeadingPart(PreparedVariant v, string titleForm) =>
        v.Words >= LeadingPartMinWords && titleForm.Length > v.Form.Length
        && titleForm.StartsWith(v.Form + " ", StringComparison.Ordinal);

    /// <summary>
    /// The pair agrees on sequel / part numbers: the same numbers, or every number only one side reads as a
    /// sequel number still stands in the other side's text (1.27.0: <c>Title Level 99</c> vs a record
    /// <c>Title Level 99 ~Subtitle~</c> is the same work, not a sequel mismatch).
    /// </summary>
    private static bool NumbersAgree(PreparedVariant v, string title, IReadOnlyList<string> titleNumbers)
    {
        if (v.Numbers.SequenceEqual(titleNumbers, StringComparer.Ordinal))
            return true;
        return v.Numbers.Except(titleNumbers, StringComparer.Ordinal).All(n => TitleNormalizer.ContainsNumber(title, n))
            && titleNumbers.Except(v.Numbers, StringComparer.Ordinal).All(n => TitleNormalizer.ContainsNumber(v.NumberSource, n));
    }

    private static List<PreparedVariant> PrepareVariants(IReadOnlyList<QueryVariant> variants)
    {
        // Derived variants compare numbers with the name they came from: the primary, else the
        // first full variant.
        var source = variants.FirstOrDefault(v => v.Kind == QueryVariantKind.Primary)
            ?? variants.FirstOrDefault(v => !IsDerived(v.Kind));
        var sourceNumbers = TitleNormalizer.NumberTokens(source?.Text);
        return variants
            .Where(v => !string.IsNullOrWhiteSpace(v.Text))
            .Select(v => IsDerived(v.Kind)
                ? new PreparedVariant(v.Text, sourceNumbers, true, source?.Text ?? v.Text, v.Kind)
                : new PreparedVariant(v.Text, TitleNormalizer.NumberTokens(v.Text), false, v.Text, v.Kind))
            .ToList();
    }

    private static bool IsDerived(QueryVariantKind kind) =>
        kind is QueryVariantKind.SubtitleSplit or QueryVariantKind.SequelNumberSplit or QueryVariantKind.CreatorSplit;

    /// <summary>Variants that are the folder's (or group's) own name, not a bracket or a split.</summary>
    private static bool IsOwnName(QueryVariantKind kind) =>
        kind is QueryVariantKind.Primary or QueryVariantKind.ComicInfoSeries or QueryVariantKind.ArchiveDerivedTitle;

    /// <summary>
    /// 1.30.0 (backlog: "a folder name's subtitle should favour the spin-off record"): when the work's own name states a subtitle
    /// (<c>Series - Subtitle</c>) and a candidate at the review floor has that subtitle as its own (<c>Series: Subtitle</c>), a record
    /// that matched only the bare head (<c>Series</c>, through the retrieval-only subtitle split) is capped at
    /// <see cref="SubtitleHeadCap"/> like a record-side head: the explicit subtitle outweighs the main-title match. Also returns the
    /// capped records with their title score before the cap: when one of them is the top's series family, the subtitle alone
    /// decided and the work goes to review (<see cref="MatchReason.SubtitleFamily"/>).
    /// </summary>
    private static (List<ScoredCandidate> Scored, Dictionary<string, double> Capped) SubtitleOutweighsHead(List<(ScoredCandidate Scored, bool ViaSubtitleSplit)> scored,
        List<PreparedVariant> variants, MatchThresholds thresholds)
    {
        var subtitles = variants.Where(v => IsOwnName(v.Kind) || v.Kind == QueryVariantKind.EnglishTitle)
            .Select(v => TitleNormalizer.NameSubtitle(v.Text))
            .OfType<string>()
            .Select(TitleNormalizer.ScoringForm)
            .Where(f => f.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var capped = new Dictionary<string, double>(StringComparer.Ordinal);
        if (subtitles.Count == 0)
            return (scored.Select(s => s.Scored).ToList(), capped);
        bool Carries(MatchCandidate c) => new[] { c.Title }.Concat(c.AltTitles ?? [])
            .Select(t => AutoMatchText.WithoutDisambiguator(t) ?? t)
            .Select(TitleNormalizer.SubtitleTail).OfType<string>()
            .Any(t => subtitles.Contains(TitleNormalizer.ScoringForm(t)));
        var carriers = scored.Where(s => s.Scored.TitleScore >= thresholds.ReviewFloor && Carries(s.Scored.Candidate))
            .Select(s => s.Scored.Candidate.ExternalId).ToHashSet(StringComparer.Ordinal);
        if (carriers.Count == 0)
            return (scored.Select(s => s.Scored).ToList(), capped);
        var result = scored.Select(s =>
        {
            var x = s.Scored;
            if (!s.ViaSubtitleSplit || carriers.Contains(x.Candidate.ExternalId) || x.TitleScore <= SubtitleHeadCap)
                return x;
            capped[x.Candidate.ExternalId] = x.TitleScore;
            var drop = x.TitleScore - SubtitleHeadCap;
            return x with { TitleScore = SubtitleHeadCap, AdjustedScore = x.AdjustedScore - drop };
        }).ToList();
        return (result, capped);
    }

    private static (ScoredCandidate Scored, bool ViaSubtitleSplit) ScoreOne(MatchCandidate c, List<PreparedVariant> variants, MatchContext ctx)
    {
        var reasons = MatchReason.None;
        var titles = new List<string> { c.Title };
        titles.AddRange(c.AltTitles ?? []);
        titles = titles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        // Only the MAIN title's trailing "(disambiguator)" counts stripped (1.27.0): MangaUpdates names same-titled
        // records "Word (AUTHOR Name)", so the stripped main title is the plain name. An ALT or hit title
        // "Word (Other Name)" is another record's name for a different work - stripped, it scored a false 1.00.
        if (AutoMatchText.WithoutDisambiguator(c.Title) is { } stripped && !titles.Contains(stripped, StringComparer.OrdinalIgnoreCase))
            titles.Add(stripped);
        // 1.29.0 (owner): an ALT title whose disambiguator names THIS record's author ("Fly Me to the Moon (HATA Kenjiro)" on
        // "Tonikaku Kawaii") is the record's own name - stripped, it counts in full; any other stripped alias (a search hit
        // before its authors are known, or a tag naming someone else) counts at DisambiguatedAliasFactor, never alone an
        // automatic link.
        var factors = titles.Select(_ => 1.0).ToList();
        foreach (var (alias, factor) in AutoMatchText.DisambiguatedAliases(titles.Skip(1).ToList(), c.Authors))
        {
            if (titles.Contains(alias, StringComparer.OrdinalIgnoreCase))
                continue;
            titles.Add(alias);
            factors.Add(factor);
        }
        var titleNumbers = titles.Select(TitleNormalizer.NumberTokens).ToList();
        // "Title: Long Subtitle", "Title ~Subtitle~" and "Title - Subtitle" records also compare by the part
        // before the break, capped (1.26.1 colon; 1.27.0 tilde and spaced dash).
        var heads = titles
            .Select(TitleNormalizer.SubtitleHead)
            .Where(h => h is not null && !titles.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // An ALT title that is just the head of the record's own main title ("Title" on a record named
        // "Title - Spin-off Name") is the franchise's short name, not this record's full name (1.27.0, found in the
        // golden recordings: a spin-off listed the series name as an alias and scored a clean 1.00). It counts like
        // a head: capped, review only.
        if (TitleNormalizer.SubtitleHead(c.Title) is { } mainHead)
        {
            var headForm = TitleNormalizer.ScoringForm(mainHead);
            foreach (var alias in titles.Skip(1).Where(t => TitleNormalizer.ScoringForm(t) == headForm).ToList())
            {
                var at = titles.IndexOf(alias, 1);
                titles.RemoveAt(at);
                factors.RemoveAt(at);
                titleNumbers.RemoveAt(at); // (kept aligned with titles - before 1.29.0 a removed alias shifted the numbers by one)
                if (!heads.Contains(alias, StringComparer.OrdinalIgnoreCase))
                    heads.Add(alias);
            }
        }
        var capped = titles.Count;
        var fullForms = titles.Select(TitleNormalizer.ScoringForm).ToList();
        titles.AddRange(heads);
        titleNumbers.AddRange(heads.Select(TitleNormalizer.NumberTokens));

        // Creator hints that name this record (its authors, or its "(AUTHOR Name)" disambiguator).
        var authors = (c.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var hints = (ctx.CreatorHints ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        var hintNamesRecord = hints.Count > 0 && hints.Any(h => authors
            .Concat(new[] { c.Title }.Concat(c.AltTitles ?? []).Select(AutoMatchText.DisambiguatorTag).OfType<string>())
            .Any(n => AutoMatchText.NamesEqual(h, n)));

        // A trailing "[Two Words]" is read both as an English title and as a creator hint (1.27.0): as a title it
        // may carry an auto link only when the folder's own name also resembles the record, so an author's name
        // can never auto-link a record that merely has that name as its title.
        var ownNameResembles = new Lazy<bool>(() => variants.Where(v => IsOwnName(v.Kind))
            .Any(v => titles.Take(capped).Any(t => TitleSimilarity.Score(v.Text, t) >= TitleSimilarity.PossibleThreshold)));

        var best = 0.0;
        var bestPenalized = false;
        var bestViaSubtitleSplit = false;
        foreach (var v in variants)
        {
            var reviewOnly = (v.Kind == QueryVariantKind.CreatorSplit && !hintNamesRecord)
                || (v.Kind == QueryVariantKind.EnglishTitle && hints.Any(h => AutoMatchText.NamesEqual(h, v.Text)) && !ownNameResembles.Value);
            for (var i = 0; i < titles.Count; i++)
            {
                double raw;
                if (i >= capped)
                {
                    // Only a head EQUAL to the searched name counts ("Title" vs "Title: Subtitle"), never
                    // a merely similar one - that is how spin-offs ("Title: Side Story") look.
                    if (v.Form != TitleNormalizer.ScoringForm(titles[i]))
                        continue;
                    raw = SubtitleHeadCap;
                }
                else
                {
                    raw = TitleSimilarity.Score(v.Text, titles[i]) * factors[i];
                    // A shared number alone is no title evidence (1.27.0: "Title 99" vs an unrelated "... 99").
                    if (TitleSimilarity.SharesOnlyDigitTokens(v.Text, titles[i]))
                        raw *= DigitOnlyOverlapFactor;
                    // The leading part of a long title (1.27.0): a name of at least three words that the record
                    // title starts with, word for word, is the same cap - review only, never auto on its own.
                    if (raw < SubtitleHeadCap && IsLeadingPart(v, fullForms[i]))
                        raw = SubtitleHeadCap;
                }
                if (raw <= 0) continue;
                if (reviewOnly)
                    raw = Math.Min(raw, SubtitleHeadCap);
                var penalized = !NumbersAgree(v, titles[i], titleNumbers[i]);
                var s = raw - (penalized ? NumberPenalty : 0) - (v.Derived ? DerivedVariantDiscount : 0);
                if (s > best)
                {
                    best = s;
                    bestPenalized = penalized;
                    bestViaSubtitleSplit = v.Kind == QueryVariantKind.SubtitleSplit;
                }
            }
        }
        var title = Math.Clamp(best, 0, 1);
        if (bestPenalized)
            reasons |= MatchReason.NumberMismatch;

        var delta = 0.0;

        // Format: tier 2 filters these out; a local tier or a lifted filter may still return them.
        if (c.Format is MetadataFormat.Novel or MetadataFormat.Artbook or MetadataFormat.Audio)
        {
            delta += FormatConflict;
            reasons |= MatchReason.TypeConflict;
        }

        // Origin vs the category folder: agreement only (owner option a', 2026-09-27). A manhwa filed under a
        // "Manga" folder is common, so a mismatch is neutral - no penalty, no veto. Tall strips vs a print record
        // stay a conflict (they measure the pages, not a folder name).
        var origin = AutoMatchText.ParseOrigin(c.Origin);
        if (ctx.DeclaredType is { } declared)
        {
            // An admin's declaration is a strong hint both ways (1.30.0, owner) and takes the place of the folder word.
            switch (DeclaredFactsComparer.TypeSignal(declared, origin, c.Format, c.Webtoon))
            {
                case DeclaredTypeSignal.Agree:
                    delta += DeclaredTypeAgree;
                    reasons |= MatchReason.DeclaredTypeAgree;
                    break;
                case DeclaredTypeSignal.Mismatch:
                    delta += DeclaredTypeMismatch;
                    reasons |= MatchReason.DeclaredTypeMismatch;
                    break;
            }
        }
        else if (origin is { } o && AutoMatchText.OriginsForCategory(ctx.CategoryHint) is { } allowed
            && (allowed.Contains(o) || (c.Webtoon == true && allowed.Contains(MetadataOrigin.Korea))))
        {
            delta += OriginAgree;
        }
        // Tall strips are a hint, never a blocker (owner, 1.27.0 review: there are Japanese vertical manga): they
        // favour webtoon / Korean / Chinese records and say nothing against a print record.
        if (ctx.TallStrips && (c.Webtoon == true || origin is MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan))
            delta += OriginAgree;

        // Counts: volumes vs volumes, chapters vs chapters, unit NUMBERS (not file counts); a mixed folder gives no signal,
        // and the latest tracked chapter alone never conflicts with a record that counts its run in volumes. The rule
        // lives in CountEvidence (1.29.0) so Identify's warning reads the same.
        var count = CountEvidence.Compare(CountEvidence.FromContext(ctx), PublishedUnitCounts.Of(c));
        foreach (var signal in new[] { count.Volumes, count.Chapters })
        {
            if (signal == CountSignal.Conflict) { delta += Conflict; reasons |= MatchReason.CountConflict; }
            else if (signal == CountSignal.Agree) delta += CountAgree;
        }

        // Year: a file cannot predate the series (English release years bound it from above).
        if (ctx.EarliestYear is { } year && c.StartYear is { } start)
        {
            if (year < start - 1) { delta += Conflict; reasons |= MatchReason.YearConflict; }
            else delta += YearAgree;
        }

        // One-shot shape: a one-shot folder, or a single archive of a collection that does not
        // name a volume or chapter. A one-shot record gets a small tie-break; a multi-volume record is
        // NOT a conflict - one archive can hold a whole series (owner, 2026-09-26), and linking a lone
        // volume or an omnibus to its series record is right either way.
        if ((ctx.Class == WorkClass.OneShot
             || (IsArchiveLevel(ctx.Class) && ctx.ArchiveCount == 1 && ctx.VolumeLikeCount == 0 && ctx.ChapterLikeCount == 0))
            && IsOneShotRecord(c))
        {
            delta += OneShotAgree;
        }

        // ComicInfo series names the record.
        if (!string.IsNullOrWhiteSpace(ctx.ComicInfoSeries))
        {
            var key = TitleNormalizer.ScoringForm(ctx.ComicInfoSeries);
            if (key.Length > 0 && titles.Any(t => TitleNormalizer.ScoringForm(t) == key))
                delta += ComicInfoAgree;
        }

        // Creator tags: a tie-break everywhere; a veto at archive level when they name none of
        // the record's authors (undecidable when either side is empty).
        var tags = (ctx.AuthorTags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var authorBonus = 0.0;
        if (tags.Count > 0 && authors.Count > 0)
        {
            if (tags.Any(t => authors.Any(a => AutoMatchText.NamesEqual(t, a))))
                authorBonus = AuthorAgree;
            else if (IsArchiveLevel(ctx.Class))
                reasons |= MatchReason.AuthorConflict;
        }

        // Creator hints from the name: positive only. The record's authors come from a full read;
        // its "(AUTHOR Name)" disambiguator is on every search hit, so ties are broken without a read.
        if (hintNamesRecord)
            authorBonus = CreatorHintAgree;
        delta += authorBonus;

        // Comics records (1.32.0): start year, language, shape and publisher of the edition.
        if (ctx.ComicsEvidence is { } comics && ComicsEvidenceRules.IsComicsProvider(c.Provider))
            delta += ComicsDelta(c, comics, ref reasons);

        // Cover comparison (1.28.0): positive only, and only on the adjusted score - a tie can be broken, a weak
        // title never becomes an automatic link.
        if (ctx.CoverMatches is { Count: > 0 } covers && covers.Contains(c.ExternalId))
        {
            delta += CoverAgree;
            reasons |= MatchReason.CoverMatch;
        }

        return (new ScoredCandidate(c, title, title + delta, reasons), bestViaSubtitleSplit);
    }

    /// <summary>The comics evidence of one comics record (1.32.0); every part is skipped when either side is unknown.</summary>
    private static double ComicsDelta(MatchCandidate c, ComicsEvidence comics, ref MatchReason reasons)
    {
        var delta = 0.0;
        if (comics.StartYear is { } named && c.StartYear is { } start && Math.Abs(named - start) <= 1)
        {
            delta += named == start ? ComicsStartYearExact : ComicsStartYearNear;
            reasons |= MatchReason.ComicsStartYear;
        }
        if (ComicsEvidenceRules.LanguageCode(comics.Language) is { } wanted && ComicsEvidenceRules.LanguageCode(c.Language) is { } edition)
        {
            if (wanted == edition)
                delta += ComicsLanguageAgree;
            else
            {
                delta += ComicsLanguageMismatch;
                reasons |= MatchReason.ComicsLanguageMismatch;
            }
        }
        if (comics.Shape != ComicsShape.Unknown && c.Shape != ComicsShape.Unknown)
        {
            if (comics.Shape == c.Shape)
                delta += ComicsShapeAgree;
            else
            {
                delta += ComicsShapeMismatch;
                reasons |= MatchReason.ComicsShapeMismatch;
            }
        }
        if (comics.Publisher is { } publisher && (c.Publishers ?? []).Any(p => ComicsEvidenceRules.PublishersEqual(publisher, p)))
        {
            delta += ComicsPublisherAgree;
            reasons |= MatchReason.ComicsPublisherAgree;
        }
        return delta;
    }

    private static bool IsOneShotRecord(MatchCandidate c) =>
        c.Volumes == 1 || (c.Volumes is null && c.LatestChapter == 1);

    private static bool AreRelated(MatchCandidate a, MatchCandidate b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.Ordinal)
        && ((a.Relations ?? []).Any(r => string.Equals(r.ExternalId, b.ExternalId, StringComparison.Ordinal))
            || (b.Relations ?? []).Any(r => string.Equals(r.ExternalId, a.ExternalId, StringComparison.Ordinal)));

    /// <summary>The adaptive review set (decision 8), plus the records <paramref name="alsoKeep"/> names (1.30.0), in rank order.</summary>
    private static List<ScoredCandidate> ChoosePersisted(List<ScoredCandidate> ranked, IReadOnlyList<ScoredCandidate> alsoKeep)
    {
        var top = ranked[0];
        List<ScoredCandidate> chosen = ranked.Count == 1 || top.AdjustedScore - ranked[1].AdjustedScore >= PersistClearLead
            ? [top]
            : ranked.TakeWhile(s => top.AdjustedScore - s.AdjustedScore <= PersistWindow + ScoreTolerance).Take(PersistMax).ToList();
        var extra = alsoKeep.Where(a => !chosen.Contains(a)).ToList();
        if (extra.Count == 0)
            return chosen;
        var keep = chosen.Take(Math.Max(1, PersistMax - extra.Count)).Concat(extra)
            .Select(s => s.Candidate.ExternalId).ToHashSet(StringComparer.Ordinal);
        return ranked.Where(s => keep.Contains(s.Candidate.ExternalId)).Take(PersistMax).ToList();
    }

    /// <summary>One entry per (provider, external id); a duplicate keeps the entry with more data.</summary>
    private static List<MatchCandidate> Distinct(IReadOnlyList<MatchCandidate> candidates)
    {
        var byKey = new Dictionary<(string, string), MatchCandidate>();
        var order = new List<(string, string)>();
        foreach (var c in candidates)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.ExternalId) || string.IsNullOrWhiteSpace(c.Title))
                continue;
            var key = (c.Provider ?? string.Empty, c.ExternalId);
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = c;
                order.Add(key);
            }
            else if (Richness(c) > Richness(existing))
            {
                byKey[key] = c;
            }
        }
        return order.Select(k => byKey[k]).ToList();
    }

    private static int Richness(MatchCandidate c) =>
        (c.AltTitles?.Count ?? 0) + (c.Authors?.Count ?? 0) + (c.Relations?.Count ?? 0)
        + (c.Volumes is null ? 0 : 1) + (c.LatestChapter is null ? 0 : 1) + (c.StartYear is null ? 0 : 1);
}
