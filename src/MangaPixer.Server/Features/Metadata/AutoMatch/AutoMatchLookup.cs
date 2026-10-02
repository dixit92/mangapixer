namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The result of looking up one work: the scorer's outcome plus every record fetched on the way, and how many
/// candidate covers were compared (1.28.0; 0 when no comparison ran).
/// </summary>
public sealed record WorkLookupResult(
    MatchOutcome Outcome,
    WorkClassification Classification,
    IReadOnlyDictionary<string, ProviderSeriesRecord> Fetched,
    IReadOnlyDictionary<string, string?> HitImages,
    int CoversCompared = 0,
    string CoverCheck = AutoMatch.CoverCheck.NotConfigured);

/// <summary>
/// A comics work that must wait for the Grand Comics Database (1.32.0): its bucket has no token to spare for automatic work (25 an
/// hour, two kept for admins) or GCD asked to slow down. The queue row is deferred until <see cref="Until"/> - no attempt is counted
/// - and the worker goes on with the next work, so manga never wait behind a comic.
/// </summary>
public sealed class AutoMatchDeferredException : Exception
{
    public AutoMatchDeferredException(string code, DateTimeOffset until)
        : base("Automatic lookup deferred: " + code)
    {
        Code = code;
        Until = until;
    }

    public string Code { get; }
    public DateTimeOffset Until { get; }
}

/// <summary>
/// Looks one work up (stage 2, section 2 retrieval tiers) and scores it through the
/// matcher-core contract (<see cref="IMatchQueryPlanner"/>, <see cref="IMatchScorer"/>):
/// - tier 0: a MangaUpdates id from the work's ComicInfo <c>Web</c> URLs -> one GET, no
///   name sent;
/// - tier 2: automatic searches over the planner's variants (at most
///   <see cref="AutoMatchPolicy.MaxSearchesPerWork"/>, always with the fixed type
///   filter, doujinshi allowed below a "Doujinshi &amp; adult one-shots" folder), each
///   followed by a GET of the best hit (and the runner-up when it is close), stopping
///   at the first confident variant; and at most ONE more GET per work, of the best hit whose matched title carries an
///   author tag (1.30.0), so the tag can be checked against the record's authors.
/// - cover comparison (1.28.0, optional <see cref="AutoMatchCoverComparer"/>): when the final score is a tie on
///   the title for a volume-shaped work, the covers of the two tied candidates are compared with the work's local
///   cover and a matching one gets a small adjusted-score bonus (never the raw title score).
/// - comics routing (1.32.0, owner 2026-10-02): a work whose local signs route to comics (<see cref="ComicsSignal.RoutesToComics"/>)
///   is searched on the Grand Comics Database first - at most <see cref="MaxComicsSearchesPerWork"/> searches, with the start year of
///   its name - and on MangaUpdates only when GCD has no candidate at the review floor; both sites' candidates are scored on one scale
///   (<see cref="ComicsEvidence"/> tells GCD editions apart). Other works: MangaUpdates only, exactly as before. With GCD removed from
///   the allowlist, comics are searched on MangaUpdates only. A GCD id in ComicInfo is a tier-0 GET like a MangaUpdates one.
///   GCD's slow bucket is never waited for: <see cref="AutoMatchDeferredException"/> defers the work instead.
/// Every call goes through <see cref="MetadataGateway"/> as <see cref="MetadataCallOrigin.Automatic"/>;
/// a refusal propagates as <see cref="MetadataGatewayException"/> (the worker waits).
/// Nothing here writes to the database or logs text.
/// </summary>
public sealed class AutoMatchLookup
{
    private const string Provider = MetadataIdentifyService.DefaultProvider;
    private const string ComicsProvider = GcdMapping.ProviderId;

    /// <summary>
    /// GCD searches per comics work (1.32.0): the name with its start year, the longest part of a name with a title separator
    /// (GCD's search is a literal substring match), then the name without its year or the next variant - every request counts at
    /// 25 an hour, and a GCD search answers with whole series, so no GET follows.
    /// </summary>
    public const int MaxComicsSearchesPerWork = 3;

    /// <summary>How long a comics work waits when GCD refused without saying until when (one token period).</summary>
    public static readonly TimeSpan DefaultDeferral = TimeSpan.FromSeconds(144);

    private const double TallRatio = 2.0;
    private const int MaxMeasuredPages = 400;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IMatchQueryPlanner _planner;
    private readonly IMatchScorer _scorer;
    private readonly AutoMatchCoverComparer? _covers;
    private readonly GcdDetails? _gcd;

    public AutoMatchLookup(MangaPixerDbContext db, MetadataGateway gateway, IMatchQueryPlanner planner, IMatchScorer scorer,
        AutoMatchCoverComparer? covers = null, GcdDetails? gcd = null)
    {
        _db = db;
        _gateway = gateway;
        _planner = planner;
        _scorer = scorer;
        _covers = covers;
        _gcd = gcd;
    }

    public async Task<WorkLookupResult> LookupAsync(
        LibraryTreeSnapshot tree, DetectedWork work, WorkClassification classification, MatchThresholds thresholds,
        bool allowDoujinshi, MetadataCallContext call, CancellationToken ct, DeclaredFacts? declared = null)
    {
        var shape = tree.ShapeOf(work.FolderId);
        var archiveIds = ArchivesOf(tree, work);
        var comicInfoSeries = await ComicInfoMajorityAsync(archiveIds, ct);

        MatchQuery query;
        if (work.Level == MatchLevel.Archive)
        {
            var group = GroupOf(tree, work, classification)
                ?? new ArchiveGroup(tree.Find(work.AnchorNodeId)?.Name ?? string.Empty, []);
            query = _planner.PlanArchiveGroup(shape, classification, group);
        }
        else
        {
            query = _planner.PlanFolder(shape, classification, comicInfoSeries);
        }
        if (await TallStripsAsync(archiveIds, ct) is { } tall && tall != query.Context.TallStrips)
            query = query with { Context = query.Context with { TallStrips = tall } };
        // What an admin declared for the work's folder (1.28.0; the type a strong hint since 1.30.0): scoring evidence, never sent.
        query = DeclaredHints.Apply(query, declared);
        // The work's local comics signs (1.32.0): which site is searched first, and scoring evidence. Never sent.
        query = await ComicsSignalReader.ApplyAsync(_db, query, shape, archiveIds, ct);

        // 1.32.0: GCD is asked only while it is on the allowlist (and every other switch is on); removed, comics go to MangaUpdates.
        var gcdOpen = _gcd is not null && await _gateway.CheckSwitchesAsync(tree.LibraryId, MetadataCallOrigin.Automatic, ComicsProvider, ct) is null;
        var comicsFirst = gcdOpen && query.Context.Comics?.RoutesToComics == true;
        if (comicsFirst)
        {
            var names = archiveIds.Select(id => tree.Find(id)?.Name).OfType<string>().ToList();
            var evidence = await ComicsEvidenceReader.ReadAsync(_db, tree.Find(work.AnchorNodeId)?.Name, names, archiveIds, ct);
            query = query with { Context = query.Context with { ComicsEvidence = evidence } };
        }

        var found = new Retrieval();

        // Tier 0: a provider id the work's own ComicInfo points at (id only, no name) - MangaUpdates, or (1.32.0) a GCD series.
        if (await ComicInfoReferenceAsync(archiveIds, gcdOpen, ct) is { } reference
            && await GetHintedAsync(reference, tree.LibraryId, call, ct) is { } hinted)
        {
            found.Fetched[hinted.ExternalId] = hinted;
            found.Candidates[hinted.ExternalId] = ToCandidate(hinted);
            var hintedOutcome = _scorer.Score(query, found.Candidates.Values.ToList(), thresholds);
            if (hintedOutcome.Band == MatchBand.Auto)
                return new WorkLookupResult(await WithComicsDetailsAsync(hintedOutcome, found, tree.LibraryId, call, ct),
                    classification, found.Fetched, found.Images);
        }

        var coverArchive = AutoMatchCoverComparer.CoverArchiveOf(tree, work);
        MatchOutcome? outcome = null;
        if (comicsFirst)
        {
            outcome = await RetrieveComicsAsync(query, tree.LibraryId, thresholds, call, found, ct, coverArchive);
            if (outcome.Ranked.Count == 0 || outcome.Ranked[0].TitleScore < thresholds.ReviewFloor)
                outcome = null; // Nothing close on GCD: MangaUpdates, as for any work (its candidates join GCD's on one scale).
        }
        outcome ??= await RetrieveAsync(query, tree.LibraryId, thresholds, allowDoujinshi, call, found, ct, coverArchive);
        outcome = await WithComicsDetailsAsync(outcome, found, tree.LibraryId, call, ct);
        return new WorkLookupResult(outcome, classification, found.Fetched, found.Images, found.CoversCompared, found.CoverCheck);
    }

    /// <summary>The tier-0 GET; a GCD one may defer the work (its slow bucket).</summary>
    private Task<ProviderSeriesRecord?> GetHintedAsync(ProviderRef reference, long libraryId, MetadataCallContext call, CancellationToken ct) =>
        reference.Provider == ComicsProvider
            ? DeferOnSlowAsync(() => _gateway.GetSeriesAsync(reference.Provider, libraryId, reference.ExternalId, ct, call))
            : _gateway.GetSeriesAsync(reference.Provider, libraryId, reference.ExternalId, ct, call);

    /// <summary>
    /// The GCD searches of a comics work (1.32.0): the name with the start year of its <c>(YYYY)</c>, then - while nothing reached the
    /// review floor - the longest part of a name with a title separator (<see cref="GcdMapping.LongestNamePart"/>), the name without
    /// the year, the next name variant - at most <see cref="MaxComicsSearchesPerWork"/>. Each
    /// hit is a whole series (no GET). A tie on the title between two GCD editions of a volume-shaped work may compare their first
    /// issues' cover thumbnails with the local cover (Automatic matching's consent; best effort, never deferred).
    /// </summary>
    private async Task<MatchOutcome> RetrieveComicsAsync(MatchQuery query, long libraryId, MatchThresholds thresholds, MetadataCallContext call,
        Retrieval found, CancellationToken ct, long? coverArchiveId)
    {
        var texts = query.Variants
            .Select(v => MetadataGateway.NormalizeQuery(v.Text))
            .Where(t => t.Length is > 0 and <= MetadataGateway.MaxQueryLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (texts.Count == 0)
            return _scorer.Score(query, [], thresholds);
        var year = query.Context.ComicsEvidence?.StartYear;
        var attempts = new List<(string Text, int? Year)> { (texts[0], year) };
        if (GcdMapping.LongestNamePart(texts[0]) is { } part)
            attempts.Add((part, year));
        if (year is not null)
            attempts.Add((texts[0], null));
        attempts.AddRange(texts.Skip(1).Select(t => (t, (int?)null)));

        var searches = 0;
        foreach (var (text, startYear) in attempts)
        {
            if (searches >= MaxComicsSearchesPerWork)
                break;
            var interim = _scorer.Score(query, found.Candidates.Values.ToList(), thresholds);
            var best = interim.Ranked.Count > 0 ? interim.Ranked[0].TitleScore : 0;
            if (best >= AutoMatchPolicy.ConfidentTitle || (searches > 0 && best >= thresholds.ReviewFloor))
                break;
            searches++;
            var page = await DeferOnSlowAsync(() => _gateway.SearchAutomaticAsync(
                ComicsProvider, libraryId, text, allowDoujinshi: false, call, ct, startYear: startYear));
            foreach (var hit in page.Hits)
            {
                if (hit.Record is not { } record)
                    continue;
                found.Fetched[record.ExternalId] = record;
                found.Candidates[record.ExternalId] = ToCandidate(record);
                found.Images.TryAdd(record.ExternalId, null);
            }
        }

        var outcome = _scorer.Score(query, found.Candidates.Values.ToList(), thresholds);
        if (_covers is null || _gcd is null)
            return outcome;
        found.CoverCheck = !CoverEvidence.IsTie(outcome, thresholds) ? CoverCheck.NoTie
            : !CoverEvidence.IsVolumeShaped(query.Context) ? CoverCheck.NotVolumeShaped
            : coverArchiveId is null ? CoverCheck.NoCoverArchive
            : found.CoverCheck;
        if (coverArchiveId is { } coverArchive
            && CoverEvidence.TiedPair(outcome, query.Context, thresholds) is { Count: 2 } pair
            && pair.All(p => p.Candidate.Provider == ComicsProvider))
        {
            var images = new List<(string ExternalId, string? ImageUrl)>();
            foreach (var p in pair)
            {
                var issue = found.Fetched.TryGetValue(p.Candidate.ExternalId, out var record)
                    ? await _gcd.FirstIssueAsync(record, libraryId, call, ct)
                    : null;
                images.Add((p.Candidate.ExternalId, issue?.CoverUrl));
            }
            var comparison = await _covers.CompareAsync(coverArchive, libraryId, ComicsProvider, images, call, ct);
            found.CoversCompared = comparison.ImagesCompared;
            found.CoverCheck = comparison.Code;
            if (comparison.Matches.Count > 0)
            {
                query = query with { Context = query.Context with { CoverMatches = comparison.Matches } };
                outcome = _scorer.Score(query, found.Candidates.Values.ToList(), thresholds);
            }
        }
        return outcome;
    }

    /// <summary>
    /// A GCD record about to be linked automatically gets its publisher's name (best effort: one request only when a token is
    /// spare; the link never waits for it).
    /// </summary>
    private async Task<MatchOutcome> WithComicsDetailsAsync(MatchOutcome outcome, Retrieval found, long libraryId, MetadataCallContext call,
        CancellationToken ct)
    {
        if (_gcd is not null && outcome.Band == MatchBand.Auto && outcome.Ranked.Count > 0
            && outcome.Ranked[0].Candidate.Provider == ComicsProvider
            && found.Fetched.TryGetValue(outcome.Ranked[0].Candidate.ExternalId, out var record))
            found.Fetched[record.ExternalId] = await _gcd.WithPublisherAsync(record, libraryId, call, ct);
        return outcome;
    }

    /// <summary>GCD's slow bucket or its backoff: the work is deferred, never waited for (1.32.0).</summary>
    private static async Task<T> DeferOnSlowAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (MetadataGatewayException ex) when (ex.Code is "provider_busy" or "provider_backoff")
        {
            throw new AutoMatchDeferredException(ex.Code, ex.RetryAt ?? DateTimeOffset.UtcNow + DefaultDeferral);
        }
    }

    /// <summary>
    /// Tier 2 alone, for a query that is already planned: the automatic name searches, the GETs and the final
    /// score, exactly as <see cref="LookupAsync"/> runs them (the golden set replays recorded provider answers
    /// through this, so it measures the production retrieval loop and the production mapping).
    /// </summary>
    public async Task<WorkLookupResult> SearchAndScoreAsync(
        MatchQuery query, WorkClassification classification, long libraryId, MatchThresholds thresholds, bool allowDoujinshi,
        MetadataCallContext call, CancellationToken ct, long? coverArchiveId = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var found = new Retrieval();
        var outcome = await RetrieveAsync(query, libraryId, thresholds, allowDoujinshi, call, found, ct, coverArchiveId);
        return new WorkLookupResult(outcome, classification, found.Fetched, found.Images, found.CoversCompared, found.CoverCheck);
    }

    /// <summary>What one lookup has collected: candidates by id, full records fetched, hit images.</summary>
    private sealed class Retrieval
    {
        public Dictionary<string, MatchCandidate> Candidates { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ProviderSeriesRecord> Fetched { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> Images { get; } = new(StringComparer.Ordinal);
        public int CoversCompared { get; set; }

        /// <summary>The one extra GET of a work (1.30.0) was spent on a hit whose matched title carries an author tag.</summary>
        public bool TagFetchUsed { get; set; }
        public string CoverCheck { get; set; } = AutoMatch.CoverCheck.NotConfigured;
    }

    private async Task<MatchOutcome> RetrieveAsync(
        MatchQuery query, long libraryId, MatchThresholds thresholds, bool allowDoujinshi, MetadataCallContext call,
        Retrieval found, CancellationToken ct, long? coverArchiveId)
    {
        var candidates = found.Candidates;
        var fetched = found.Fetched;

        // Tier 2: automatic name searches.
        var variants = query.Variants
            .Select(v => MetadataGateway.NormalizeQuery(v.Text))
            .Where(t => t.Length is > 0 and <= MetadataGateway.MaxQueryLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(AutoMatchPolicy.MaxSearchesPerWork)
            .ToList();
        // Page 1 of each variant in order, stopping at the first confident one.
        var searches = 0;
        var withPageTwo = new List<string>();
        foreach (var text in variants)
        {
            searches++;
            var page = await _gateway.SearchAutomaticAsync(Provider, libraryId, text, allowDoujinshi, call, ct);
            await TakePageAsync(query, page, libraryId, thresholds, found, call, ct);
            if (page.Hits.Count >= MetadataGateway.SearchPageSize && page.TotalHits > page.Hits.Count)
                withPageTwo.Add(text);

            var interim = _scorer.Score(query, candidates.Values.ToList(), thresholds);
            if (interim.Ranked.Count > 0 && interim.Ranked[0].TitleScore >= AutoMatchPolicy.ConfidentTitle)
                break;
        }

        // Then page 2 of the same texts (1.27.0), while the top two are tied or nothing reached the review
        // floor, inside the same per-work search bound: MangaUpdates ranks short look-alike titles first, so
        // the right record of a one-word or partial name can sit on page 2.
        foreach (var text in withPageTwo)
        {
            if (searches >= AutoMatchPolicy.MaxSearchesPerWork
                || !NeedsPageTwo(_scorer.Score(query, candidates.Values.ToList(), thresholds), thresholds))
                break;
            searches++;
            var page = await _gateway.SearchAutomaticAsync(Provider, libraryId, text, allowDoujinshi, call, ct, page: 2);
            await TakePageAsync(query, page, libraryId, thresholds, found, call, ct);
        }

        var outcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);

        // A tie on the title for a volume-shaped work: compare the two tied candidates' covers with the local cover.
        if (_covers is not null)
            found.CoverCheck = !CoverEvidence.IsTie(outcome, thresholds) ? CoverCheck.NoTie
                : !CoverEvidence.IsVolumeShaped(query.Context) ? CoverCheck.NotVolumeShaped
                : coverArchiveId is null ? CoverCheck.NoCoverArchive
                : found.CoverCheck;
        if (_covers is not null && coverArchiveId is { } coverArchive
            && CoverEvidence.TiedPair(outcome, query.Context, thresholds) is { Count: 2 } pair)
        {
            // The search hit's small thumbnail first (enough for a 32x32 hash), else the record's image.
            var images = pair.Select(p => (p.Candidate.ExternalId,
                found.Images.GetValueOrDefault(p.Candidate.ExternalId)
                    ?? (fetched.TryGetValue(p.Candidate.ExternalId, out var record) ? record.ImageRemoteUrl : null))).ToList();
            var comparison = await _covers.CompareAsync(coverArchive, libraryId, Provider, images, call, ct);
            found.CoversCompared = comparison.ImagesCompared;
            found.CoverCheck = comparison.Code;
            if (comparison.Matches.Count > 0)
            {
                query = query with { Context = query.Context with { CoverMatches = comparison.Matches } };
                outcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);
            }
        }

        // An automatic link needs the full record of the chosen candidate.
        if (outcome.Band == MatchBand.Auto && outcome.Ranked.Count > 0 && !fetched.ContainsKey(outcome.Ranked[0].Candidate.ExternalId))
        {
            await FetchIntoAsync(outcome.Ranked[0].Candidate.ExternalId, libraryId, candidates, fetched, call, ct);
            outcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);
        }
        return outcome;
    }

    /// <summary>Adds a page's hits as candidates and GETs the best one or two (see the loop in <see cref="RetrieveAsync"/>).</summary>
    private async Task TakePageAsync(MatchQuery query, ProviderSearchPage page, long libraryId, MatchThresholds thresholds,
        Retrieval found, MetadataCallContext call, CancellationToken ct)
    {
        var candidates = found.Candidates;
        foreach (var hit in page.Hits)
        {
            found.Images.TryAdd(hit.ExternalId, hit.ImageRemoteUrl);
            if (!found.Fetched.ContainsKey(hit.ExternalId))
                candidates[hit.ExternalId] = ToCandidate(hit);
        }

        // GET the best hit, and the runner-up when its title score is close (alt titles, authors, counts).
        // Hits are ranked by the scorer (1.27.0; before, by plain similarity, so a disambiguated or
        // "Title: Subtitle" record lost the GET to a look-alike, and the year / type evidence on the hit
        // was ignored), and only hits that reach the review floor are fetched - a search that found
        // nothing usable costs no GET.
        var pageCandidates = page.Hits.Select(h => h.ExternalId).Distinct(StringComparer.Ordinal)
            .Select(id => candidates.GetValueOrDefault(id)).OfType<MatchCandidate>().ToList();
        var ranked = _scorer.Score(query, pageCandidates, thresholds).Ranked;
        for (var i = 0; i < Math.Min(2, ranked.Count); i++)
        {
            if (ranked[i].TitleScore < thresholds.ReviewFloor)
                break;
            if (i == 1 && ranked[0].TitleScore - ranked[1].TitleScore > AutoMatchPolicy.SecondFetchWithin)
                break;
            await FetchIntoAsync(ranked[i].Candidate.ExternalId, libraryId, candidates, found.Fetched, call, ct);
        }

        // 1.30.0 (owner-confirmed): a hit that matched through a title with a trailing author tag ("Fly Me to the Moon (HATA
        // Kenjiro)" on "Tonikaku Kawaii") scores only DisambiguatedAliasFactor until its authors are known. The best such hit that
        // reaches the review floor and was not fetched above gets ONE GET per work, so the tag can be checked against the record's
        // authors (in full when it names one of them, else it stays at the factor).
        if (!found.TagFetchUsed
            && TaggedAliasHit(query, ranked, found.Fetched, thresholds) is { } tagged)
        {
            found.TagFetchUsed = true;
            await FetchIntoAsync(tagged.Candidate.ExternalId, libraryId, candidates, found.Fetched, call, ct);
        }
    }

    /// <summary>
    /// The best-ranked unfetched hit at the review floor whose title score comes from an author-tagged alias (an alternative
    /// title whose trailing <c>(disambiguator)</c> names a person, <see cref="AutoMatchText.IsPersonTag"/>, scored at <see cref="AutoMatchText.DisambiguatedAliasFactor"/>) - the score that
    /// would rise if the tag named the record's author (1.30.0). Null when there is none.
    /// </summary>
    internal static ScoredCandidate? TaggedAliasHit(MatchQuery query, IReadOnlyList<ScoredCandidate> ranked,
        IReadOnlyDictionary<string, ProviderSeriesRecord> fetched, MatchThresholds thresholds)
    {
        var texts = query.Variants.Select(v => v.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return ranked.FirstOrDefault(r => r.TitleScore >= thresholds.ReviewFloor
            && r.TitleScore < 1.0 - 1e-9
            && !fetched.ContainsKey(r.Candidate.ExternalId)
            && (r.Candidate.AltTitles ?? []).Any(t => AutoMatchText.IsPersonTag(AutoMatchText.DisambiguatorTag(t))
                && AutoMatchText.WithoutDisambiguator(t) is { } alias
                && AutoMatchText.DisambiguatedAliasFactor * TitleSimilarity.Best(texts, [alias]) >= r.TitleScore - 0.02));
    }

    /// <summary>Page 1 left the top two tied, or nothing at the review floor (1.27.0).</summary>
    internal static bool NeedsPageTwo(MatchOutcome interim, MatchThresholds thresholds)
    {
        if (interim.Ranked.Count == 0 || interim.Ranked[0].TitleScore < thresholds.ReviewFloor)
            return true;
        if (interim.Ranked.Count < 2)
            return false;
        var (top, second) = (interim.Ranked[0], interim.Ranked[1]);
        return second.TitleScore >= top.TitleScore - AutoMatchPolicy.PageTwoTieWithin
            && top.AdjustedScore - second.AdjustedScore < thresholds.Margin;
    }

    private async Task FetchIntoAsync(string externalId, long libraryId, Dictionary<string, MatchCandidate> candidates,
        Dictionary<string, ProviderSeriesRecord> fetched, MetadataCallContext call, CancellationToken ct)
    {
        if (fetched.ContainsKey(externalId))
            return;
        var record = await _gateway.GetSeriesAsync(Provider, libraryId, externalId, ct, call);
        if (record is null)
        {
            candidates.Remove(externalId); // Gone at the provider: never link to it.
            return;
        }
        fetched[externalId] = record;
        candidates[externalId] = ToCandidate(record);
    }

    /// <summary>The group of an archive work, re-derived from the current classification (null when it is gone).</summary>
    public static ArchiveGroup? GroupOf(LibraryTreeSnapshot tree, DetectedWork work, WorkClassification classification)
    {
        var archives = tree.ChildArchives(work.FolderId);
        var index = archives.ToList().FindIndex(a => a.Id == work.AnchorNodeId);
        return index < 0 ? null : classification.ArchiveGroups.FirstOrDefault(g => g.ArchiveIndexes.Contains(index));
    }

    /// <summary>The archives of a work: the group's archives, or up to 500 archives below a folder.</summary>
    public static List<long> ArchivesOf(LibraryTreeSnapshot tree, DetectedWork work)
    {
        if (work.Level == MatchLevel.Archive)
            return [work.AnchorNodeId, .. work.MemberNodeIds];
        var result = new List<long>();
        var stack = new Stack<long>();
        stack.Push(work.FolderId);
        while (stack.Count > 0 && result.Count < AutoMatchPolicy.MaxLocalArchives)
        {
            var id = stack.Pop();
            foreach (var child in tree.ChildrenOf(id))
            {
                if (child.IsFolder)
                    stack.Push(child.Id);
                else if (result.Count < AutoMatchPolicy.MaxLocalArchives)
                    result.Add(child.Id);
            }
        }
        return result;
    }

    internal static MatchCandidate ToCandidate(ProviderSeriesRecord r) => new(
        r.Provider,
        r.ExternalId,
        r.Title,
        r.AltTitles,
        r.Format,
        // A comics record's provider type is free text in the edition's language ("série continue en cours"): its origin is the name.
        r.Provider == ComicsProvider ? r.Origin?.ToString() : r.ProviderType,
        r.StartYear,
        r.OriginVolumes,
        r.LatestChapter is { } chapter ? (int)Math.Floor(chapter) : null,
        r.Creators.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        r.Relations.Select(x => new CandidateRelation(x.ExternalId, x.Relation)).ToList(),
        r.Webtoon,
        r.TotalChapters,
        r.EnglishVolumes,
        r.EnglishChapters,
        r.Language,
        r.Shape,
        r.Publishers.Select(p => p.Name).ToList());

    internal static MatchCandidate ToCandidate(ProviderSearchHit hit) => new(
        Provider,
        hit.ExternalId,
        hit.Title,
        hit.HitTitle is { } alt ? [alt] : [],
        Providers.MangaUpdates.MangaUpdatesMapping.FormatOf(hit.ProviderType),
        hit.ProviderType,
        hit.Year,
        null,
        null,
        [],
        []);

    /// <summary>The ComicInfo <c>Series</c> at least 60% of the work's parsed items agree on (the resolver's majority rule).</summary>
    private async Task<string?> ComicInfoMajorityAsync(IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        if (archiveIds.Count == 0)
            return null;
        var names = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1)
            .Select(e => e.Series)
            .ToListAsync(ct);
        if (names.Count == 0)
            return null;
        var top = names.Where(n => !string.IsNullOrWhiteSpace(n))
            .GroupBy(n => n!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return top is not null && top.Count() >= names.Count * SeriesInfoResolver.MajorityShare ? top.Key : null;
    }

    private async Task<ProviderRef?> ComicInfoReferenceAsync(IReadOnlyList<long> archiveIds, bool gcdOpen, CancellationToken ct)
    {
        if (archiveIds.Count == 0)
            return null;
        var webJson = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1 && e.WebUrlsJson != null)
            .OrderBy(e => e.NodeId)
            .Select(e => e.WebUrlsJson)
            .Take(50)
            .ToListAsync(ct);
        foreach (var json in webJson)
            foreach (var url in MetadataJson.ReadList<string>(json))
            {
                if (_gateway.ParseReference(Provider, url) is { } reference)
                    return reference;
                if (gcdOpen && _gateway.ParseReference(ComicsProvider, url) is { } comics)
                    return comics;
            }
        return null;
    }

    /// <summary>Tall strips (webtoon-shaped pages) over up to 400 measured pages; null when fewer than 20 are measured.</summary>
    private async Task<bool?> TallStripsAsync(IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        if (archiveIds.Count == 0)
            return null;
        var dims = await _db.PageEntries.AsNoTracking()
            .Where(p => archiveIds.Contains(p.ItemId) && p.Width != null && p.Height != null && p.Width > 0)
            .OrderBy(p => p.ItemId).ThenBy(p => p.Ordinal)
            .Select(p => new { p.Width, p.Height })
            .Take(MaxMeasuredPages)
            .ToListAsync(ct);
        return dims.Count < 20 ? null : dims.Count(d => d.Height >= d.Width * TallRatio) * 2 >= dims.Count;
    }
}
