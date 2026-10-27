namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>One suggested value: its public id (the tag view's <c>f</c> value is <c>token:PublicId</c>), name and how many works carry it.</summary>
public sealed record SuggestedValue(string PublicId, FacetBucket Bucket, string Display, int Count);

/// <summary>
/// Values for the quick results (1.49.0, lane O): the creators, tags, genres (and, for a typed field, any other bucket's values) whose
/// name matches the typed words, with their work counts - the facet query side's viewer scope first (<see cref="FacetViewerScope"/>:
/// the libraries the viewer may list, Incognito applied, series information hidden = nothing), so a value with no entry at a place the
/// viewer may see is never named. Words are matched like the plain search matches names (<see cref="SearchText"/>: accents and case
/// folded, every word of 3+ characters inside the name, a 1-2 character word starting a word). Ranked exact, starts with, a word
/// starts, contains; then the more works the better, then the name. A merged creator is never listed (its live creator is).
/// </summary>
public sealed class FacetSuggestService
{
    /// <summary>The most creator candidates counted for one request (counting is per value; the rest of a long list is never shown).</summary>
    internal const int MaxCandidates = 60;

    /// <summary>The most values read when no word was typed (a typed field with an empty value: the most used first).</summary>
    internal const int EmptyTextLimit = 40;

    private readonly MangaPixerDbContext _db;
    private readonly FacetCountService _counts;

    public FacetSuggestService(MangaPixerDbContext db, FacetCountService counts)
    {
        _db = db;
        _counts = counts;
    }

    /// <summary>
    /// The best <paramref name="limit"/> values of <paramref name="buckets"/> for <paramref name="query"/> (an empty query lists the most
    /// used). Nothing for an empty scope.
    /// </summary>
    public async Task<IReadOnlyList<SuggestedValue>> ValuesAsync(
        FacetViewerScope scope, IReadOnlyCollection<FacetBucket> buckets, SearchQuery query, int limit, CancellationToken ct = default)
    {
        if (scope.IsEmpty || buckets.Count == 0 || limit <= 0)
            return [];

        var found = new List<(SuggestedValue Value, int Tier)>();
        var others = buckets.Where(b => b != FacetBucket.Creators).ToList();
        if (others.Count > 0)
            found.AddRange(await BucketValuesAsync(scope, others, query, ct));
        if (buckets.Contains(FacetBucket.Creators))
            found.AddRange(await CreatorValuesAsync(scope, query, ct));

        return found
            .OrderBy(f => f.Tier).ThenByDescending(f => f.Value.Count).ThenBy(f => f.Value.Display, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Value.PublicId, StringComparer.Ordinal)
            .Take(limit)
            .Select(f => f.Value)
            .ToList();
    }

    /// <summary>
    /// Tags, genres and the other plain buckets: one grouped count over the values whose name passes a cheap SQL narrowing (each ASCII word
    /// inside the name, or a non-ASCII character anywhere - C# then decides after folding), counted in the viewer's scope only.
    /// </summary>
    private async Task<List<(SuggestedValue Value, int Tier)>> BucketValuesAsync(
        FacetViewerScope scope, List<FacetBucket> buckets, SearchQuery query, CancellationToken ct)
    {
        var list = string.Join(",", buckets.Select(b => ((int)b).ToString(CultureInfo.InvariantCulture)));
        var words = query.AllWords.Select(w => w.Folded).Where(w => w.All(char.IsAsciiLetterOrDigit)).Distinct(StringComparer.Ordinal).ToList();
        var narrowing = string.Join(" ", words.Select(w => $"AND (v.Display LIKE '%{w}%' OR v.Display GLOB '*[^ -~]*')"));
        var tail = query.IsEmpty ? $"ORDER BY 5 DESC, v.Display LIMIT {EmptyTextLimit}" : "";
        var rows = await QuerySql.ReadAsync(_db, $"""
            SELECT v.Id, v.PublicId, v.Bucket, v.Display, COUNT(DISTINCT e.WorkId), v.CreatorId
            FROM facet_values v
            JOIN facet_entries e ON e.ValueId = v.Id AND e.LibraryId IN ({scope.SqlList})
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            WHERE v.Bucket IN ({list})
            {narrowing}
            GROUP BY v.Id
            {tail}
            """, null, r => (Id: r.GetInt64(0), PublicId: r.GetString(1), Bucket: (FacetBucket)r.GetInt32(2), Display: r.GetString(3),
                Count: r.GetInt32(4), CreatorId: r.IsDBNull(5) ? (long?)null : r.GetInt64(5)), ct);

        var matched = new List<(SuggestedValue Value, int Tier, long? CreatorId)>();
        foreach (var row in rows)
        {
            if (ValueTier(query, row.Display) is { } tier)
                matched.Add((new SuggestedValue(row.PublicId, row.Bucket, row.Display, row.Count), tier, row.CreatorId));
        }
        return (await WithoutMergedCreatorsAsync(matched, ct)).Select(m => (m.Value, m.Tier)).ToList();
    }

    /// <summary>
    /// Creators: the spellings (<c>creator_names</c>, so "Urasawa Naoki" and "Naoki Urasawa" are one creator) that hold every typed word,
    /// each creator ranked by its best spelling, merged creators followed to their live creator, the best candidates counted in scope.
    /// </summary>
    private async Task<List<(SuggestedValue Value, int Tier)>> CreatorValuesAsync(FacetViewerScope scope, SearchQuery query, CancellationToken ct)
    {
        if (query.IsEmpty)
            return await BucketValuesAsync(scope, [FacetBucket.Creators], query, ct);

        var words = query.AllWords.Select(w => w.Folded).Distinct(StringComparer.Ordinal).Take(6).ToList();
        var parameters = new Dictionary<string, object?>();
        var where = string.Join(" AND ", words.Select((w, i) =>
        {
            parameters["@w" + i.ToString(CultureInfo.InvariantCulture)] = "%" + w + "%";
            return $"n.NameKey LIKE @w{i.ToString(CultureInfo.InvariantCulture)}";
        }));
        var names = await QuerySql.ReadAsync(_db, $"SELECT n.CreatorId, n.NameKey FROM creator_names n WHERE {where}", parameters,
            r => (CreatorId: r.GetInt64(0), Key: r.GetString(1)), ct);

        var sortedTyped = string.Join(' ', words.OrderBy(w => w, StringComparer.Ordinal));
        var tiers = new Dictionary<long, int>();
        foreach (var (creatorId, key) in names)
        {
            var tier = key == sortedTyped ? SearchText.RankExact
                : words.All(w => SearchText.HasWordStartingWith(key, w)) ? SearchText.RankWordStarts
                : SearchText.RankContains;
            if (!tiers.TryGetValue(creatorId, out var best) || tier < best)
                tiers[creatorId] = tier;
        }
        if (tiers.Count == 0)
            return [];

        // A merged creator answers with the creator it was merged into; that creator's value is the one listed.
        var live = await QueryValues.LiveCreatorsAsync(_db, tiers.Keys.ToList(), ct);
        var liveTier = new Dictionary<long, int>();
        foreach (var (creatorId, tier) in tiers)
        {
            var target = live.GetValueOrDefault(creatorId, creatorId);
            if (!liveTier.TryGetValue(target, out var best) || tier < best)
                liveTier[target] = tier;
        }
        var values = await QueryValues.CreatorValuesAsync(_db, liveTier.Keys.ToList(), ct);
        var candidates = liveTier
            .Where(t => values.ContainsKey(t.Key))
            .Select(t => (Value: values[t.Key], Tier: t.Value))
            .OrderBy(c => c.Tier).ThenBy(c => c.Value.Display, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Value.Id)
            .Take(MaxCandidates)
            .ToList();

        var counts = await _counts.CountAsync(scope, candidates.Select(c => c.Value.Id).ToList(), ct);
        return candidates
            .Where(c => counts.GetValueOrDefault(c.Value.Id) > 0)
            .Select(c => (new SuggestedValue(c.Value.PublicId, FacetBucket.Creators, c.Value.Display, counts[c.Value.Id]), c.Tier))
            .ToList();
    }

    /// <summary>The rank tier of a value's name for the typed words, or null when it does not match.</summary>
    internal static int? ValueTier(SearchQuery query, string display)
    {
        if (query.IsEmpty)
            return SearchText.RankExact;
        var folded = SearchText.Fold(display);
        foreach (var word in query.AllWords)
        {
            if (word.Length >= SearchText.TrigramLength && !folded.Contains(word.Folded, StringComparison.Ordinal))
                return null;
        }
        return SearchText.Rank(query, folded);
    }

    /// <summary>Drops the values of merged creators (the live creator's own value is listed instead).</summary>
    private async Task<List<(SuggestedValue Value, int Tier, long? CreatorId)>> WithoutMergedCreatorsAsync(
        List<(SuggestedValue Value, int Tier, long? CreatorId)> values, CancellationToken ct)
    {
        var creatorIds = values.Where(v => v.CreatorId is not null).Select(v => v.CreatorId!.Value).ToList();
        if (creatorIds.Count == 0)
            return values;
        var live = await QueryValues.LiveCreatorsAsync(_db, creatorIds, ct);
        return values.Where(v => v.CreatorId is not { } c || live.GetValueOrDefault(c, c) == c).ToList();
    }
}
