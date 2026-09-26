namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The result of looking up one work: the scorer's outcome plus every record fetched on the way.</summary>
public sealed record WorkLookupResult(
    MatchOutcome Outcome,
    WorkClassification Classification,
    IReadOnlyDictionary<string, ProviderSeriesRecord> Fetched,
    IReadOnlyDictionary<string, string?> HitImages);

/// <summary>
/// Looks one work up (stage 2, section 2 retrieval tiers) and scores it through the
/// matcher-core contract (<see cref="IMatchQueryPlanner"/>, <see cref="IMatchScorer"/>):
/// - tier 0: a MangaUpdates id from the work's ComicInfo <c>Web</c> URLs -> one GET, no
///   name sent;
/// - tier 2: automatic searches over the planner's variants (at most
///   <see cref="AutoMatchPolicy.MaxSearchesPerWork"/>, always with the fixed type
///   filter, doujinshi allowed below a "Doujinshi &amp; adult one-shots" folder), each
///   followed by a GET of the best hit (and the runner-up when it is close), stopping
///   at the first confident variant.
/// Every call goes through <see cref="MetadataGateway"/> as <see cref="MetadataCallOrigin.Automatic"/>;
/// a refusal propagates as <see cref="MetadataGatewayException"/> (the worker waits).
/// Nothing here writes to the database or logs text.
/// </summary>
public sealed class AutoMatchLookup
{
    private const string Provider = MetadataIdentifyService.DefaultProvider;
    private const double TallRatio = 2.0;
    private const int MaxMeasuredPages = 400;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IMatchQueryPlanner _planner;
    private readonly IMatchScorer _scorer;

    public AutoMatchLookup(MangaPixerDbContext db, MetadataGateway gateway, IMatchQueryPlanner planner, IMatchScorer scorer)
    {
        _db = db;
        _gateway = gateway;
        _planner = planner;
        _scorer = scorer;
    }

    public async Task<WorkLookupResult> LookupAsync(
        LibraryTreeSnapshot tree, DetectedWork work, WorkClassification classification, MatchThresholds thresholds,
        bool allowDoujinshi, MetadataCallContext call, CancellationToken ct)
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

        var candidates = new Dictionary<string, MatchCandidate>(StringComparer.Ordinal);
        var fetched = new Dictionary<string, ProviderSeriesRecord>(StringComparer.Ordinal);
        var images = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Tier 0: a provider id the work's own ComicInfo points at (id only, no name).
        if (await ComicInfoReferenceAsync(archiveIds, ct) is { } reference
            && await _gateway.GetSeriesAsync(reference.Provider, tree.LibraryId, reference.ExternalId, ct, call) is { } hinted)
        {
            fetched[hinted.ExternalId] = hinted;
            candidates[hinted.ExternalId] = ToCandidate(hinted);
            var hintedOutcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);
            if (hintedOutcome.Band == MatchBand.Auto)
                return new WorkLookupResult(hintedOutcome, classification, fetched, images);
        }

        // Tier 2: automatic name searches.
        var variants = query.Variants
            .Select(v => MetadataGateway.NormalizeQuery(v.Text))
            .Where(t => t.Length is > 0 and <= MetadataGateway.MaxQueryLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(AutoMatchPolicy.MaxSearchesPerWork)
            .ToList();
        var scoringTexts = query.Variants.Select(v => v.Text).ToList();
        foreach (var text in variants)
        {
            var page = await _gateway.SearchAutomaticAsync(Provider, tree.LibraryId, text, allowDoujinshi, call, ct);
            var ranked = page.Hits
                .Select((hit, index) => (Hit: hit, Index: index, Score: TitleSimilarity.Best(scoringTexts, new[] { hit.Title, hit.HitTitle }.OfType<string>())))
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Index)
                .ToList();
            foreach (var (hit, _, _) in ranked)
            {
                images.TryAdd(hit.ExternalId, hit.ImageRemoteUrl);
                if (!fetched.ContainsKey(hit.ExternalId))
                    candidates[hit.ExternalId] = ToCandidate(hit);
            }

            // GET the best hit, and the runner-up when it is close (alt titles, authors, counts).
            for (var i = 0; i < Math.Min(2, ranked.Count); i++)
            {
                if (i == 1 && ranked[0].Score - ranked[1].Score > AutoMatchPolicy.SecondFetchWithin)
                    break;
                await FetchIntoAsync(ranked[i].Hit.ExternalId, tree.LibraryId, candidates, fetched, call, ct);
            }

            var interim = _scorer.Score(query, candidates.Values.ToList(), thresholds);
            if (interim.Ranked.Count > 0 && interim.Ranked[0].TitleScore >= AutoMatchPolicy.ConfidentTitle)
                break;
        }

        var outcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);

        // An automatic link needs the full record of the chosen candidate.
        if (outcome.Band == MatchBand.Auto && outcome.Ranked.Count > 0 && !fetched.ContainsKey(outcome.Ranked[0].Candidate.ExternalId))
        {
            await FetchIntoAsync(outcome.Ranked[0].Candidate.ExternalId, tree.LibraryId, candidates, fetched, call, ct);
            outcome = _scorer.Score(query, candidates.Values.ToList(), thresholds);
        }
        return new WorkLookupResult(outcome, classification, fetched, images);
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
        r.ProviderType,
        r.StartYear,
        r.OriginVolumes,
        r.LatestChapter is { } chapter ? (int)Math.Floor(chapter) : null,
        r.Creators.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        r.Relations.Select(x => new CandidateRelation(x.ExternalId, x.Relation)).ToList());

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

    private async Task<ProviderRef?> ComicInfoReferenceAsync(IReadOnlyList<long> archiveIds, CancellationToken ct)
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
                if (_gateway.ParseReference(Provider, url) is { } reference)
                    return reference;
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
