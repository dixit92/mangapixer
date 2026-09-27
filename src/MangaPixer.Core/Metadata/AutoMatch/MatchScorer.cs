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
/// Format (novel / artbook / audio), origin vs the category folder (agreement only, 1.27.0), tall strips vs a print record,
/// counts (volumes vs volumes, chapters vs chapters - never chapters vs volumes), earliest file year
/// vs start year, one-shot shape, ComicInfo series, creator tags.</item>
/// <item><b>Vetoes</b> demote auto to review: any corroboration conflict, a related top pair the
/// number-aware title does not separate, and - mandatory for archive-level works - an author
/// conflict (the archive's creator tags name none of the record's authors).</item>
/// <item><b>Bands</b> from <see cref="MatchThresholds"/>: auto = raw title &gt;= AutoTitle, adjusted lead
/// &gt;= Margin over the next distinct record, no veto, and an auto-capable class; a
/// <see cref="WorkClass.OneShot"/> folder additionally needs raw &gt;= <see cref="OneShotAutoTitle"/> (a code
/// constant, owner decision 13). A single archive may hold a one-shot, one volume or a whole multi-volume series
/// (owner, 2026-09-26), so the record's volume count never blocks auto; a one-shot record only breaks ties. Review = raw &gt;= ReviewFloor.</item>
/// <item><b>ToPersist</b> (decision 8): candidates within <see cref="PersistWindow"/> of the top, at most
/// <see cref="PersistMax"/>; only the top when it leads the next by <see cref="PersistClearLead"/> or more.</item>
/// </list>
/// </summary>
public sealed class MatchScorer : IMatchScorer
{
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

    /// <summary>A count conflict: the local unit number &gt; <c>CountFactor x published + CountSlack</c>.</summary>
    public const double CountFactor = 1.5;
    public const int CountSlack = 2;


    public const double PersistWindow = 0.15;
    public const int PersistMax = 5;
    public const double PersistClearLead = 0.30;

    /// <summary>Reasons that veto auto (a conflict between local evidence and the record).</summary>
    public const MatchReason VetoReasons = MatchReason.CountConflict | MatchReason.YearConflict | MatchReason.TypeConflict
        | MatchReason.RelatedPair | MatchReason.AuthorConflict;

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
        var scored = distinct.Select(c => ScoreOne(c, variants, ctx)).ToList();

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

        // "Close second" only means something for a top that could be reviewed (1.27.0): below the floor the work
        // is unmatched, and a chip about two equally poor candidates only confuses.
        var margin = top.AdjustedScore - (second?.AdjustedScore ?? 0);
        if (margin < thresholds.Margin && top.TitleScore >= thresholds.ReviewFloor)
            reasons |= MatchReason.CloseSecond;

        var autoClass = IsAutoCapable(ctx.Class);
        if (!autoClass)
            reasons |= MatchReason.ReviewOnlyClass;

        var oneShotOk = ctx.Class != WorkClass.OneShot || top.TitleScore >= OneShotAutoTitle;

        var band = autoClass && oneShotOk
            && top.TitleScore >= thresholds.AutoTitle
            && margin >= thresholds.Margin
            && (reasons & VetoReasons) == 0
                ? MatchBand.Auto
                : top.TitleScore >= thresholds.ReviewFloor ? MatchBand.NeedsReview : MatchBand.Unmatched;

        top = top with { Reasons = reasons };
        ranked[0] = top;

        return new MatchOutcome(band, ranked, band == MatchBand.Unmatched ? [] : ChoosePersisted(ranked));
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

    private static ScoredCandidate ScoreOne(MatchCandidate c, List<PreparedVariant> variants, MatchContext ctx)
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
        var titleNumbers = titles.Select(TitleNormalizer.NumberTokens).ToList();
        // "Title: Long Subtitle", "Title ~Subtitle~" and "Title - Subtitle" records also compare by the part
        // before the break, capped (1.26.1 colon; 1.27.0 tilde and spaced dash).
        var heads = titles
            .Select(TitleNormalizer.SubtitleHead)
            .Where(h => h is not null && !titles.Contains(h, StringComparer.OrdinalIgnoreCase))
            .Select(h => h!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
                    raw = TitleSimilarity.Score(v.Text, titles[i]);
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
        if (origin is { } o && AutoMatchText.OriginsForCategory(ctx.CategoryHint) is { } allowed
            && (allowed.Contains(o) || (c.Webtoon == true && allowed.Contains(MetadataOrigin.Korea))))
        {
            delta += OriginAgree;
        }
        if (ctx.TallStrips)
        {
            if (c.Webtoon == true || origin is MetadataOrigin.Korea or MetadataOrigin.ChinaTaiwan)
                delta += OriginAgree;
            else if (c.Webtoon == false && origin == MetadataOrigin.Japan)
            {
                delta += Conflict;
                reasons |= MatchReason.TypeConflict;
            }
        }

        // Counts: volumes vs volumes, chapters vs chapters (the E3 fix); unknown -> no signal. 1.27.0: the local side
        // is the highest unit NUMBER the names state (extras and x.5 chapters do not inflate it); the published side
        // is the largest number any source states - the latest chapter (it restarts per season on renumbered
        // webtoons), the status total, the English publisher's totals. A folder that mixes volume and chapter
        // archives gives no count signal at all (no subtraction heuristics).
        if (!(ctx.VolumeLikeCount > 0 && ctx.ChapterLikeCount > 0))
        {
            var localVolumes = ctx.VolumeLikeCount > 0 ? ctx.LocalVolumes ?? ctx.VolumeLikeCount : 0;
            var localChapters = ctx.ChapterLikeCount > 0 ? ctx.LocalChapters ?? ctx.ChapterLikeCount : 0;
            var volumes = Math.Max(c.Volumes ?? 0, c.EnglishVolumes ?? 0);
            var chapters = Math.Max(Math.Max(c.LatestChapter ?? 0, c.TotalChapters ?? 0), c.EnglishChapters ?? 0);
            if (localVolumes > 0 && volumes > 0)
            {
                if (localVolumes > CountFactor * volumes + CountSlack) { delta += Conflict; reasons |= MatchReason.CountConflict; }
                else delta += CountAgree;
            }
            if (localChapters > 0 && chapters > 0)
            {
                if (localChapters > CountFactor * chapters + CountSlack) { delta += Conflict; reasons |= MatchReason.CountConflict; }
                else delta += CountAgree;
            }
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

        return new ScoredCandidate(c, title, title + delta, reasons);
    }

    private static bool IsOneShotRecord(MatchCandidate c) =>
        c.Volumes == 1 || (c.Volumes is null && c.LatestChapter == 1);

    private static bool AreRelated(MatchCandidate a, MatchCandidate b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.Ordinal)
        && ((a.Relations ?? []).Any(r => string.Equals(r.ExternalId, b.ExternalId, StringComparison.Ordinal))
            || (b.Relations ?? []).Any(r => string.Equals(r.ExternalId, a.ExternalId, StringComparison.Ordinal)));

    private static List<ScoredCandidate> ChoosePersisted(List<ScoredCandidate> ranked)
    {
        var top = ranked[0];
        if (ranked.Count == 1 || top.AdjustedScore - ranked[1].AdjustedScore >= PersistClearLead)
            return [top];
        return ranked
            .TakeWhile(s => top.AdjustedScore - s.AdjustedScore <= PersistWindow + 1e-9)
            .Take(PersistMax)
            .ToList();
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
