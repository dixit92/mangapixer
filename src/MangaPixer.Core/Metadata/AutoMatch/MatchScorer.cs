namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Scores provider candidates for one work and bands the result (metadata stage 2, design
/// section 2 + owner decisions 6, 8 and 13). Pure and deterministic.
///
/// <list type="bullet">
/// <item><b>Title</b>: the stage-1 <see cref="TitleSimilarity"/> over every (variant, record title /
/// alt title) pair - a title's trailing <c>(disambiguator)</c> also counts stripped - minus <see cref="NumberPenalty"/> when the pair disagrees on a sequel / part
/// number (<see cref="TitleNormalizer.NumberTokens"/>). Retrieval-only variants (subtitle and
/// sequel-number splits) are compared with the numbers of the name they came from and carry a
/// small <see cref="DerivedVariantDiscount"/>, so a full-name match always wins a tie.</item>
/// <item><b>Corroboration</b> re-ranks only: small agreements and conflict penalties change the
/// ADJUSTED score (ordering and margin), never the raw title score the auto threshold reads.
/// Format (novel / artbook / audio), origin vs the category folder, tall strips vs a print record,
/// counts (volumes vs volumes, chapters vs chapters - never chapters vs volumes), earliest file year
/// vs start year, one-shot shape, ComicInfo series, creator tags.</item>
/// <item><b>Vetoes</b> demote auto to review: any corroboration conflict, a related top pair the
/// number-aware title does not separate, and - mandatory for archive-level works - an author
/// conflict (the archive's creator tags name none of the record's authors).</item>
/// <item><b>Bands</b> from <see cref="MatchThresholds"/>: auto = raw title &gt;= AutoTitle, adjusted lead
/// &gt;= Margin over the next distinct record, no veto, and an auto-capable class; a
/// <see cref="WorkClass.OneShot"/> folder additionally needs raw &gt;= <see cref="OneShotAutoTitle"/> and a
/// one-shot / 1-volume record (a code constant, owner decision 13). Review = raw &gt;= ReviewFloor.</item>
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

    /// <summary>Related top two need at least this raw title gap to stay auto.</summary>
    public const double RelatedSeparation = 0.10;

    /// <summary>A count conflict: local units &gt; <c>CountFactor x published + CountSlack</c>.</summary>
    public const double CountFactor = 1.5;
    public const int CountSlack = 2;

    /// <summary>One-shot conflict: the record has at least this many volumes.</summary>
    public const int OneShotMaxVolumes = 3;

    public const double PersistWindow = 0.15;
    public const int PersistMax = 5;
    public const double PersistClearLead = 0.30;

    /// <summary>Reasons that veto auto (a conflict between local evidence and the record).</summary>
    public const MatchReason VetoReasons = MatchReason.CountConflict | MatchReason.YearConflict | MatchReason.TypeConflict
        | MatchReason.RelatedPair | MatchReason.OneShotMismatch | MatchReason.AuthorConflict;

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

        var margin = top.AdjustedScore - (second?.AdjustedScore ?? 0);
        if (margin < thresholds.Margin)
            reasons |= MatchReason.CloseSecond;

        var autoClass = IsAutoCapable(ctx.Class);
        if (!autoClass)
            reasons |= MatchReason.ReviewOnlyClass;

        var oneShotOk = ctx.Class != WorkClass.OneShot
            || (top.TitleScore >= OneShotAutoTitle && IsOneShotRecord(top.Candidate));

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

    private sealed record PreparedVariant(string Text, IReadOnlyList<string> Numbers, bool Derived);

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
                ? new PreparedVariant(v.Text, sourceNumbers, true)
                : new PreparedVariant(v.Text, TitleNormalizer.NumberTokens(v.Text), false))
            .ToList();
    }

    private static bool IsDerived(QueryVariantKind kind) =>
        kind is QueryVariantKind.SubtitleSplit or QueryVariantKind.SequelNumberSplit;

    private static ScoredCandidate ScoreOne(MatchCandidate c, List<PreparedVariant> variants, MatchContext ctx)
    {
        var reasons = MatchReason.None;
        var titles = new List<string> { c.Title };
        titles.AddRange(c.AltTitles ?? []);
        titles = titles.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        foreach (var stripped in titles.Select(AutoMatchText.WithoutDisambiguator).OfType<string>().ToList())
        {
            if (!titles.Contains(stripped, StringComparer.OrdinalIgnoreCase))
                titles.Add(stripped);
        }
        var titleNumbers = titles.Select(TitleNormalizer.NumberTokens).ToList();

        var best = 0.0;
        var bestPenalized = false;
        foreach (var v in variants)
        {
            for (var i = 0; i < titles.Count; i++)
            {
                var raw = TitleSimilarity.Score(v.Text, titles[i]);
                if (raw <= 0) continue;
                var penalized = !v.Numbers.SequenceEqual(titleNumbers[i], StringComparer.Ordinal);
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

        // Origin vs the category folder, and tall strips vs a print record.
        var origin = AutoMatchText.ParseOrigin(c.Origin);
        if (origin is { } o && AutoMatchText.OriginsForCategory(ctx.CategoryHint) is { } allowed)
        {
            if (allowed.Contains(o) || (c.Webtoon == true && allowed.Contains(MetadataOrigin.Korea)))
                delta += OriginAgree;
            else
            {
                delta += Conflict;
                reasons |= MatchReason.TypeConflict;
            }
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

        // Counts: volumes vs volumes, chapters vs chapters (the E3 fix); unknown -> no signal. The
        // latest chapter number restarts per season on renumbered webtoons, so a stated total wins.
        if (ctx.VolumeLikeCount > 0 && c.Volumes is { } vols && vols > 0)
        {
            if (ctx.VolumeLikeCount > CountFactor * vols + CountSlack) { delta += Conflict; reasons |= MatchReason.CountConflict; }
            else delta += CountAgree;
        }
        if (ctx.ChapterLikeCount > 0 && Math.Max(c.LatestChapter ?? 0, c.TotalChapters ?? 0) is var chapters && chapters > 0)
        {
            if (ctx.ChapterLikeCount > CountFactor * chapters + CountSlack) { delta += Conflict; reasons |= MatchReason.CountConflict; }
            else delta += CountAgree;
        }

        // Year: a file cannot predate the series (English release years bound it from above).
        if (ctx.EarliestYear is { } year && c.StartYear is { } start)
        {
            if (year < start - 1) { delta += Conflict; reasons |= MatchReason.YearConflict; }
            else delta += YearAgree;
        }

        // One-shot shape: a one-shot folder, or a single archive of a collection that does not
        // name a volume or chapter (a lone "Title v01" is one unit of a longer work).
        if (ctx.Class == WorkClass.OneShot
            || (IsArchiveLevel(ctx.Class) && ctx.ArchiveCount == 1 && ctx.VolumeLikeCount == 0 && ctx.ChapterLikeCount == 0))
        {
            if (IsOneShotRecord(c)) delta += OneShotAgree;
            else if (c.Volumes is >= OneShotMaxVolumes) { delta += Conflict; reasons |= MatchReason.OneShotMismatch; }
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
        var authors = (c.Authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (tags.Count > 0 && authors.Count > 0)
        {
            if (tags.Any(t => authors.Any(a => AutoMatchText.NamesEqual(t, a))))
                delta += AuthorAgree;
            else if (IsArchiveLevel(ctx.Class))
                reasons |= MatchReason.AuthorConflict;
        }

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
