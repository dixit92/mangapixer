namespace com.lifepixer.mangapixer.Server.Features.Recommendations;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// Content-based, local-only recommendations (1.53.0 lane C; design note "MangaPixer - Recommendations", owner decisions 2026-11-10).
/// Nothing leaves the server and nothing but series ON this server is recommended.
/// <list type="bullet">
/// <item><b>Home</b> (<see cref="ForUserAsync"/>): seeds are the viewer's own signals - a rating 9-10 (weight 1.0), 7-8 (0.6), 1-4 (-0.5, a
/// negative seed), a favorite (0.6; the max with a positive rating, not added), a series the viewer finished (0.4, only when it carries no
/// rating). A rated, starred, read or started node maps to its work at the node or its nearest ancestor that is a work location (the Info
/// panel's rule). Candidates: works with a visible location in the viewer's scope (Incognito-aware, series information shown), minus
/// libraries the viewer hid from Home, minus every work the viewer rated, starred, read or started.</item>
/// <item><b>More like this</b> (<see cref="SimilarAsync"/>): the node's own work (its location's values) is the one seed; read works are
/// allowed, after the unread ones.</item>
/// </list>
/// Scoring: <see cref="RecommendationScorer"/> over the scope's <see cref="RecommendationIndex"/>. Caching: the index per (generation,
/// scope) (viewer-independent); a Home answer per (user, scope, generation, the user's signal stamp, the Home-hidden libraries, limit) -
/// any rating, favourite, read mark or progress change changes the stamp, so the next answer is fresh; a similar answer per (location,
/// scope, generation, limit) - viewer-independent. Cards are enriched per request (read state, star, own rating, (i)).
/// </summary>
public sealed class RecommendationService
{
    public const int DefaultLimit = 12;

    public static readonly TimeSpan AnswerTimeToLive = TimeSpan.FromMinutes(10);

    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;
    private readonly FacetViewerScopeResolver _scopes;
    private readonly IFacetIndexGeneration _generation;
    private readonly RecommendationIndexCache _indexes;
    private readonly CatalogBrowseService _browse;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        MangaPixerDbContext db, LibraryAuthorizationService auth, FacetViewerScopeResolver scopes, IFacetIndexGeneration generation,
        RecommendationIndexCache indexes, CatalogBrowseService browse, IMemoryCache cache, ILogger<RecommendationService> logger)
    {
        _db = db;
        _auth = auth;
        _scopes = scopes;
        _generation = generation;
        _indexes = indexes;
        _browse = browse;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>A pick before its card: the node to open (internal id) and its reason.</summary>
    internal sealed record PickedNode(long NodeId, RecommendationReasonDto Reason);

    private sealed record HomeKey(long UserId, string ScopeHash, long Generation, string Stamp, string Excluded, int Limit);

    private sealed record SimilarKey(long LocationNodeId, string ScopeHash, long Generation, int Limit);

    /// <summary>The seed weight of a 1-10 rating (null = neutral, 5-6).</summary>
    public static double? RatingWeight(int score) => score switch
    {
        >= 9 => 1.0,
        >= 7 => 0.6,
        <= 4 => -0.5,
        _ => null,
    };

    public const double FavoriteWeight = 0.6;
    public const double FinishedWeight = 0.4;

    // ------------------------------------------------------------------------------------------------------------------- Home

    /// <summary>The Home row: the viewer's best <paramref name="limit"/> picks (empty without a seed or above-floor score).</summary>
    public async Task<RecommendationsDto> ForUserAsync(long userId, bool incognito, int limit, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        limit = Math.Clamp(limit, 1, RecommendationScorer.MaxLimit);
        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        if (scope.IsEmpty)
            return new RecommendationsDto { Items = [] };

        var excluded = (await _db.HomeExcludedLibraries.AsNoTracking().Where(h => h.UserId == userId)
            .Select(h => h.LibraryId).ToListAsync(ct)).Distinct().Order().ToList();
        var stamp = await SignalStampAsync(userId, ct);
        var generation = _generation.Current; // before any query of the index
        var key = new HomeKey(userId, scope.Hash, generation, stamp, string.Join(",", excluded), limit);

        var cached = _cache.TryGetValue(key, out List<PickedNode>? picks) && picks is not null;
        var seedCount = 0;
        long indexMs = 0, scoreMs = 0;
        if (!cached)
        {
            var phase = Stopwatch.GetTimestamp();
            var index = await _indexes.GetAsync(_db, scope, generation, ct);
            indexMs = (long)Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            phase = Stopwatch.GetTimestamp();
            (picks, seedCount) = await ScoreHomeAsync(index, scope, userId, excluded.ToHashSet(), limit, ct);
            scoreMs = (long)Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            _cache.SetSized(key, picks!, AnswerTimeToLive, SharedMemoryCache.Kib(256L + picks!.Count * 256L));
        }

        var items = await CardsAsync(picks!, userId, incognito, ct);
        _logger.LogDebug(LogEvents.Recommendations.HomeAnswered,
            "Recommendations for user {UserId}: {Seeds} seeds, {Picks} picks, cached {Cached}, index {IndexMs} ms, seeds and score {ScoreMs} ms, in {ElapsedMs} ms",
            userId, seedCount, items.Count, cached, indexMs, scoreMs, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new RecommendationsDto { Items = items };
    }

    /// <summary>
    /// The viewer's signal stamp: counts, latest times and score / state sums of their ratings, favourites, read marks and reading
    /// progress. Any set, change or removal changes it (a cleared rating lowers a count; a re-rating moves its time and the sum).
    /// </summary>
    internal async Task<string> SignalStampAsync(long userId, CancellationToken ct)
    {
        var rows = await QuerySql.ReadAsync(_db, """
            SELECT
              (SELECT COUNT(*) FROM user_ratings WHERE UserId = @u),
              (SELECT IFNULL(MAX(RatedAt), 0) FROM user_ratings WHERE UserId = @u),
              (SELECT TOTAL(Score) FROM user_ratings WHERE UserId = @u),
              (SELECT COUNT(*) FROM favorites WHERE UserId = @u),
              (SELECT IFNULL(MAX(CreatedAt), 0) FROM favorites WHERE UserId = @u),
              (SELECT COUNT(*) FROM read_marks WHERE UserId = @u),
              (SELECT IFNULL(MAX(MarkedAt), 0) FROM read_marks WHERE UserId = @u),
              (SELECT COUNT(*) FROM reading_progress WHERE UserId = @u),
              (SELECT IFNULL(MAX(UpdatedAt), 0) FROM reading_progress WHERE UserId = @u),
              (SELECT TOTAL(State) FROM reading_progress WHERE UserId = @u)
            """, new Dictionary<string, object?> { ["@u"] = userId },
            r => string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))), ct);
        return rows.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>A node the viewer signalled, mapped to the work location at it or its nearest ancestor (internal ids).</summary>
    private sealed record Signal(int Kind, long NodeId, int Score);

    private async Task<(List<PickedNode> Picks, int Seeds)> ScoreHomeAsync(
        RecommendationIndex index, FacetViewerScope scope, long userId, HashSet<long> excludedLibraries, int limit, CancellationToken ct)
    {
        if (index.WorkCount == 0)
            return ([], 0);

        // The viewer's signals: 0 rating (with its score), 1 favourite, 2 read mark, 3 reading progress.
        var signals = await QuerySql.ReadAsync(_db, """
            SELECT 0, CatalogNodeId, Score FROM user_ratings WHERE UserId = @u
            UNION ALL SELECT 1, CatalogNodeId, 0 FROM favorites WHERE UserId = @u
            UNION ALL SELECT 2, ItemId, 0 FROM read_marks WHERE UserId = @u
            UNION ALL SELECT 3, ItemId, 0 FROM reading_progress WHERE UserId = @u
            """, new Dictionary<string, object?> { ["@u"] = userId }, r => new Signal(r.GetInt32(0), r.GetInt64(1), r.GetInt32(2)), ct);
        if (signals.Count == 0)
            return ([], 0);

        var locationOf = await NearestLocationsAsync(userId, ct);

        var ratingWeights = new Dictionary<int, List<double?>>();
        var favorite = new HashSet<int>();
        var touched = new HashSet<int>();
        var marked = new HashSet<int>(); // touched by a read mark: only these can be finished (Read needs every archive marked)
        foreach (var signal in signals)
        {
            if (!locationOf.TryGetValue(signal.NodeId, out var location))
                continue;
            foreach (var work in index.WorksAtNode(location))
            {
                switch (signal.Kind)
                {
                    case 0:
                        if (!ratingWeights.TryGetValue(work, out var list))
                            ratingWeights[work] = list = [];
                        list.Add(RatingWeight(signal.Score));
                        break;
                    case 1:
                        favorite.Add(work);
                        break;
                    case 2:
                        touched.Add(work);
                        marked.Add(work);
                        break;
                    default:
                        touched.Add(work);
                        break;
                }
            }
        }

        // Finished: a touched work whose visible archives the viewer has all read (the tag view's work read rule).
        var finished = new HashSet<int>();
        if (marked.Count > 0)
        {
            var rollups = await QueryWorkReadState.OfWorksAsync(_db, marked.Select(w => index.WorkIds[w]).ToList(), scope, userId, ct);
            foreach (var w in marked)
            {
                if (rollups.TryGetValue(index.WorkIds[w], out var rollup) && rollup == FolderReadRollup.Read)
                    finished.Add(w);
            }
        }

        var seeds = new List<RecommendationScorer.Seed>();
        foreach (var work in ratingWeights.Keys.Union(favorite).Union(finished).Order())
        {
            double weight;
            if (ratingWeights.TryGetValue(work, out var weights))
            {
                // An explicit rating decides (the highest one when several nodes of the work are rated); a favourite lifts it to 0.6.
                var rated = weights.Where(x => x is not null).Select(x => x!.Value).ToList();
                var r = rated.Count == 0 ? 0 : rated.Max();
                weight = favorite.Contains(work) ? Math.Max(r, FavoriteWeight) : r;
            }
            else
            {
                weight = favorite.Contains(work) ? FavoriteWeight : FinishedWeight;
            }
            if (Math.Abs(weight) > 1e-12)
                seeds.Add(new RecommendationScorer.Seed(index.WorkValues[work], weight, index.RecordIds[work], work));
        }
        if (!seeds.Any(s => s.Weight > 0))
            return ([], seeds.Count);

        var known = ratingWeights.Keys.Concat(favorite).Concat(touched).ToHashSet();
        bool IsCandidate(int w) => !known.Contains(w) && index.Locations[w].Any(l => !excludedLibraries.Contains(l.LibraryId));
        var scored = RecommendationScorer.Score(index, seeds, IsCandidate, limit);
        var picks = await ReasonsAsync(index, seeds, scored, similar: false,
            w => index.Locations[w].First(l => !excludedLibraries.Contains(l.LibraryId)).NodeId, ct);
        return (picks, seeds.Count);
    }

    /// <summary>
    /// Every node the viewer signalled (rated, starred, read, in progress), mapped to the nearest node at or above it that is a work
    /// location in the tag index (any library; the caller keeps only locations of the scope's index).
    /// </summary>
    private async Task<Dictionary<long, long>> NearestLocationsAsync(long userId, CancellationToken ct)
    {
        var rows = await QuerySql.ReadAsync(_db, """
            WITH RECURSIVE sig(NodeId) AS (
                SELECT CatalogNodeId FROM user_ratings WHERE UserId = @u
                UNION SELECT CatalogNodeId FROM favorites WHERE UserId = @u
                UNION SELECT ItemId FROM read_marks WHERE UserId = @u
                UNION SELECT ItemId FROM reading_progress WHERE UserId = @u
            ),
            up(StartId, Id, Depth, Hit) AS (
                SELECT s.NodeId, s.NodeId, 0, EXISTS (SELECT 1 FROM facet_entries e WHERE e.LocationNodeId = s.NodeId)
                FROM sig s
                UNION ALL
                SELECT up.StartId, c.ParentId, up.Depth + 1, EXISTS (SELECT 1 FROM facet_entries e WHERE e.LocationNodeId = c.ParentId)
                FROM up JOIN catalog_nodes c ON c.Id = up.Id
                WHERE up.Hit = 0 AND c.ParentId IS NOT NULL AND up.Depth < 64
            )
            SELECT StartId, Id FROM up WHERE Hit = 1
            """, new Dictionary<string, object?> { ["@u"] = userId }, r => (Start: r.GetInt64(0), Location: r.GetInt64(1)), ct);
        var result = new Dictionary<long, long>(rows.Count);
        foreach (var row in rows)
            result.TryAdd(row.Start, row.Location);
        return result;
    }

    // ------------------------------------------------------------------------------------------------------------------- similar

    /// <summary>
    /// "More like this" for a node: null when the node is unknown, tombstoned or not accessible (404 - a direct URL, reachable under
    /// Incognito like the Info panel); empty when its library shows no series information or it is no work location (nor below one).
    /// </summary>
    public async Task<RecommendationsDto?> SimilarAsync(long userId, bool incognito, string nodePublicId, int limit, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        limit = Math.Clamp(limit, 1, RecommendationScorer.MaxLimit);
        var node = await _db.CatalogNodes.AsNoTracking().Where(n => n.PublicId == nodePublicId)
            .Select(n => new { n.Id, n.LibraryId, n.Availability }).FirstOrDefaultAsync(ct);
        if (node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            return null;
        if (!await _auth.CanAccessLibraryAsync(userId, node.LibraryId, ct))
            return null;
        var empty = new RecommendationsDto { Items = [] };
        if (await _scopes.IsHiddenAsync(node.LibraryId, ct))
            return empty;
        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        if (scope.IsEmpty)
            return empty;

        var location = await NearestLocationAsync(node.Id, ct);
        if (location is null)
            return empty;

        var generation = _generation.Current;
        var key = new SimilarKey(location.Value, scope.Hash, generation, limit);
        var cached = _cache.TryGetValue(key, out List<PickedNode>? picks) && picks is not null;
        if (!cached)
        {
            var index = await _indexes.GetAsync(_db, scope, generation, ct);
            picks = await ScoreSimilarAsync(index, location.Value, limit, ct);
            _cache.SetSized(key, picks, AnswerTimeToLive, SharedMemoryCache.Kib(256L + picks.Count * 256L));
        }

        var items = await CardsAsync(picks!, userId, incognito, ct);
        // Read works come after the unread ones (a browse aid, not a to-read list); the rank order is kept inside each group.
        items = items.Where(i => !IsRead(i.Node)).Concat(items.Where(i => IsRead(i.Node))).ToList();
        _logger.LogDebug(LogEvents.Recommendations.SimilarAnswered,
            "Similar for node {NodeId}: {Picks} picks, cached {Cached}, in {ElapsedMs} ms",
            node.Id, items.Count, cached, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new RecommendationsDto { Items = items };
    }

    private static bool IsRead(CatalogNodeDto node) => node.IsRead || node.ReadRollup == FolderReadRollup.Read;

    /// <summary>The node itself when it is a work location, else its nearest ancestor that is one (null: none).</summary>
    private async Task<long?> NearestLocationAsync(long nodeId, CancellationToken ct)
    {
        var rows = await QuerySql.ReadAsync(_db, """
            WITH RECURSIVE up(Id, Depth) AS (
                SELECT @node, 0
                UNION ALL
                SELECT c.ParentId, up.Depth + 1 FROM catalog_nodes c JOIN up ON c.Id = up.Id
                WHERE c.ParentId IS NOT NULL AND up.Depth < 64
            )
            SELECT up.Id FROM up
            WHERE EXISTS (SELECT 1 FROM facet_entries e WHERE e.LocationNodeId = up.Id)
            ORDER BY up.Depth LIMIT 1
            """, new Dictionary<string, object?> { ["@node"] = nodeId }, r => r.GetInt64(0), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<List<PickedNode>> ScoreSimilarAsync(RecommendationIndex index, long location, int limit, CancellationToken ct)
    {
        if (index.WorkCount == 0)
            return [];

        // The location's own values (the Info panel's "values of that location"), whatever its library: the seed is the page itself.
        var rows = await QuerySql.ReadAsync(_db, """
            SELECT DISTINCT e.WorkId, e.ValueId, w.RecordId
            FROM facet_entries e JOIN facet_works w ON w.Id = e.WorkId
            WHERE e.LocationNodeId = @loc
            """, new Dictionary<string, object?> { ["@loc"] = location },
            r => (WorkId: r.GetInt64(0), ValueId: r.GetInt64(1), RecordId: r.IsDBNull(2) ? (long?)null : r.GetInt64(2)), ct);
        if (rows.Count == 0)
            return [];

        var seedWorks = rows.Select(r => r.WorkId).ToHashSet();
        var values = rows.Select(r => index.TryValue(r.ValueId, out var v) ? v : -1).Where(v => v >= 0).Distinct().Order().ToList();
        var record = rows.Where(r => r.RecordId is not null).OrderBy(r => r.WorkId).Select(r => r.RecordId).FirstOrDefault();
        var seedWork = seedWorks.Order().Select(id => index.TryWork(id, out var w) ? (int?)w : null).FirstOrDefault(w => w is not null);
        var seeds = new List<RecommendationScorer.Seed> { new(values, 1.0, record, seedWork) };

        bool IsCandidate(int w) => !seedWorks.Contains(index.WorkIds[w]);
        var scored = RecommendationScorer.Score(index, seeds, IsCandidate, limit);
        // The seed's title for a relation reason: the page's own work title.
        var seedTitle = await _db.FacetWorks.AsNoTracking().Where(w => seedWorks.Contains(w.Id)).OrderBy(w => w.Id)
            .Select(w => w.Title).FirstOrDefaultAsync(ct);
        var seedNode = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == location).Select(n => n.PublicId).FirstOrDefaultAsync(ct);
        return await ReasonsAsync(index, seeds, scored, similar: true, w => index.Locations[w][0].NodeId, ct,
            similarSeed: (seedTitle ?? string.Empty, seedNode));
    }

    // ------------------------------------------------------------------------------------------------------------------- reasons + cards

    /// <summary>The picks with their reason lines (value displays and seed titles read once for the whole answer).</summary>
    private async Task<List<PickedNode>> ReasonsAsync(
        RecommendationIndex index, IReadOnlyList<RecommendationScorer.Seed> seeds, IReadOnlyList<RecommendationScorer.Pick> scored,
        bool similar, Func<int, long> nodeOf, CancellationToken ct, (string Title, string? NodePublicId)? similarSeed = null)
    {
        if (scored.Count == 0)
            return [];

        var valueIds = scored.SelectMany(p => p.Reason.ValueIds).Distinct().ToList();
        var displays = valueIds.Count == 0 ? [] : await _db.FacetValues.AsNoTracking().Where(v => valueIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => v.Display, ct);

        // Seed names (Home): the seed work's title and its best visible location's node.
        var seedWorks = scored.Where(p => p.Reason.SeedIndex is not null && seeds[p.Reason.SeedIndex.Value].Work is not null)
            .Select(p => seeds[p.Reason.SeedIndex!.Value].Work!.Value).Distinct().ToList();
        var seedWorkIds = seedWorks.Select(w => index.WorkIds[w]).ToList();
        var titles = seedWorkIds.Count == 0 ? [] : await _db.FacetWorks.AsNoTracking().Where(w => seedWorkIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Title, ct);
        var seedNodeIds = seedWorks.Select(w => index.Locations[w][0].NodeId).Distinct().ToList();
        var seedNodes = seedNodeIds.Count == 0 ? [] : await _db.CatalogNodes.AsNoTracking().Where(n => seedNodeIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, n => n.PublicId, ct);

        (string Title, string? Node) SeedName(int? seedIndex)
        {
            if (similar && similarSeed is { } page)
                return page;
            if (seedIndex is not { } s || seeds[s].Work is not { } w)
                return (string.Empty, null);
            return (titles.GetValueOrDefault(index.WorkIds[w]) ?? string.Empty, seedNodes.GetValueOrDefault(index.Locations[w][0].NodeId));
        }

        var picks = new List<PickedNode>(scored.Count);
        foreach (var pick in scored)
        {
            var spec = pick.Reason;
            var names = spec.ValueIds.Select(id => displays.GetValueOrDefault(id)).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
            var (title, node) = SeedName(spec.SeedIndex);
            var shared = string.Join(", ", names);
            var reason = spec.Kind switch
            {
                RecommendationScorer.ReasonKind.Relation => new RecommendationReasonDto
                {
                    Kind = RecommendationReasonKind.Relation,
                    Display = $"{RecommendationScorer.RelationWords(spec.Relation ?? string.Empty, spec.SeedListsPick)} {title}".Trim(),
                    RelatedNodeId = node,
                },
                RecommendationScorer.ReasonKind.Creator => new RecommendationReasonDto
                {
                    Kind = RecommendationReasonKind.Creator,
                    Display = names.Count > 0 ? $"Same author: {names[0]}" : "Same author",
                },
                _ => new RecommendationReasonDto
                {
                    Kind = RecommendationReasonKind.SharedValues,
                    Display = similar || title.Length == 0 ? shared : (shared.Length == 0 ? $"Like {title}" : $"Like {title}: {shared}"),
                    RelatedNodeId = similar ? null : node,
                },
            };
            picks.Add(new PickedNode(nodeOf(pick.Work), reason));
        }
        return picks;
    }

    /// <summary>
    /// The picks' cards through browse's enrichment (covers, read state / rollup, the favourite star and own rating, the (i) flag), per
    /// library, in the picks' order. A pick whose node is gone, tombstoned or no longer visible is dropped.
    /// </summary>
    private async Task<List<RecommendationDto>> CardsAsync(List<PickedNode> picks, long userId, bool incognito, CancellationToken ct)
    {
        if (picks.Count == 0)
            return [];
        var ids = picks.Select(p => p.NodeId).Distinct().ToList();
        var visible = (await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct)).ToHashSet();
        var rows = await _db.CatalogNodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new
            {
                n.LibraryId,
                Row = new CatalogBrowseService.BrowseRow
                {
                    Id = n.PublicId,
                    ParentId = n.Parent != null ? n.Parent.PublicId : "",
                    LibraryId = n.Library != null ? n.Library.PublicId : "",
                    Kind = n.Kind,
                    DisplayName = n.DisplayName,
                    Availability = n.Availability,
                    PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                    InternalId = n.Id,
                    CreatedAt = n.CreatedAt,
                    SortKey = n.SortKey,
                },
            })
            .ToListAsync(ct);

        var cards = new Dictionary<long, CatalogNodeDto>();
        foreach (var group in rows.Where(r => visible.Contains(r.LibraryId)).GroupBy(r => r.LibraryId).OrderBy(g => g.Key))
        {
            var groupRows = group.Select(g => g.Row).ToList();
            var enriched = await _browse.EnrichAsync(groupRows, userId, group.Key, ct);
            var byPublic = enriched.ToDictionary(n => n.Id, StringComparer.Ordinal);
            foreach (var row in groupRows)
            {
                if (byPublic.TryGetValue(row.Id, out var card))
                    cards[row.InternalId] = card;
            }
        }

        // An archive card's context line (1.50.0): the folder it belongs to, as on Favorites.
        var archives = cards.Where(c => c.Value.Kind == CatalogNodeKind.Archive).Select(c => c.Key).ToList();
        if (archives.Count > 0)
        {
            var contexts = await ArchiveContextNames.ResolveAsync(_db, archives, visible, ct);
            foreach (var (id, context) in contexts)
            {
                if (cards.TryGetValue(id, out var card))
                    cards[id] = card with { ContextName = context };
            }
        }

        var items = new List<RecommendationDto>(picks.Count);
        var seen = new HashSet<long>();
        foreach (var pick in picks)
        {
            if (seen.Add(pick.NodeId) && cards.TryGetValue(pick.NodeId, out var card))
                items.Add(new RecommendationDto { Node = card, Reason = pick.Reason });
        }
        return items;
    }
}
