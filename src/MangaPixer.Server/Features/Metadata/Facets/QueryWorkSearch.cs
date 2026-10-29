namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>A malformed <c>f</c> parameter (not <c>&lt;bucket&gt;:fv_...</c>) - answered 400 <c>invalid_filter</c>.</summary>
public sealed class FacetFilterFormatException(string message) : Exception(message);

/// <summary>
/// The tag view = a filtered search (1.45.0, lane Q; D1 2.6, owner Q12 / Q13): repeatable <c>f=&lt;bucket&gt;:&lt;value id&gt;</c> (AND)
/// and typed field syntax in <c>q</c>, resolved on the server to value ids among values with an entry the viewer may see. Results are
/// WORK cards, keyset-paged by (work sort key, id), each with its visible locations as display-name breadcrumbs. Free text left in
/// <c>q</c> next to a filter narrows the works. A filter that matches no visible value - unknown, or present only where the
/// viewer cannot look - gives an empty page with no count and no name: the two cases answer alike (never 403).
/// <para>
/// 1.49.0 (lane T, owner decision 9 a): tags that combine. The <c>f</c> grammar (the 1.45.0 form stays valid):
/// <c>&lt;bucket&gt;:fv_a</c> = must carry a; <c>&lt;bucket&gt;:fv_a|fv_b</c> = must carry a OR b (any of, one bucket, at most
/// <see cref="MaxAlternatives"/> ids); <c>-&lt;bucket&gt;:fv_a</c> (also with <c>|</c>) = must carry none of them. Typed fields follow
/// <see cref="FacetSearchSyntax"/> (<c>-genre:horror</c>, <c>genre:horror|mystery</c>, <c>year:2010..2015</c>, <c>tag:</c>,
/// <c>series:</c>). Only filters with no positive value filter: the works the viewer may see. Free text (S5): every word must match the
/// work's title, one of its record's alternative titles, or the name of one of its locations the viewer may see (a leading <c>-</c> = must
/// not). The first page carries <see cref="SearchResultsDto.Refine"/>: the tags of all the results with their counts (cached per viewer
/// scope and exact statement in <see cref="FacetCountCache"/>).
/// </para>
/// </summary>
public sealed class FacetSearchService
{
    /// <summary>
    /// The most filters (chips + typed fields) one search applies; further ones are ignored. Kept at 10 in 1.49.0 (lane T): each filter
    /// is one sub-select, alternatives inside a filter (<c>|</c>) do not count, so 10 covers any combination a person builds by hand.
    /// </summary>
    public const int MaxFilters = 10;

    /// <summary>1.49.0: the most value ids one <c>f</c> parameter lists (<c>genre:fv_a|fv_b|...</c>).</summary>
    public const int MaxAlternatives = 20;

    /// <summary>Locations listed on a work card.</summary>
    public const int MaxCardLocations = 5;

    /// <summary>1.49.0: Refine chips per bucket (the rest is counted in <see cref="FacetBucketDto.More"/>).</summary>
    public const int RefineChipsPerBucket = 12;

    /// <summary>
    /// 1.50.0 (lane U): the most values one expanded Refine bucket lists ("+N more" opened). Tags is the biggest bucket (about 3 000 values
    /// on a large library); the cap keeps a pathological index from sending an unbounded list, and the bucket's <c>more</c> still counts
    /// what is left out.
    /// </summary>
    public const int MaxExpandedChips = 5000;

    /// <summary>1.49.0: the buckets the Refine list offers, in the panel's order.</summary>
    public static readonly IReadOnlyList<FacetBucket> RefineBuckets =
    [
        FacetBucket.Creators, FacetBucket.Magazine, FacetBucket.Publisher, FacetBucket.Year, FacetBucket.Genres, FacetBucket.Type,
        FacetBucket.Tags,
    ];

    private readonly MangaPixerDbContext _db;
    private readonly FacetViewerScopeResolver _scopes;
    private readonly CatalogBrowseService _browse;
    private readonly FacetCountCache _cache;
    private readonly IFacetIndexGeneration _generation;
    private readonly ILogger<FacetSearchService> _logger;

    public FacetSearchService(MangaPixerDbContext db, FacetViewerScopeResolver scopes, CatalogBrowseService browse, FacetCountCache cache,
        IFacetIndexGeneration generation, ILogger<FacetSearchService> logger)
    {
        _db = db;
        _scopes = scopes;
        _browse = browse;
        _cache = cache;
        _generation = generation;
        _logger = logger;
    }

    /// <summary>Whether this search is a tag view (any <c>f</c> parameter, or a typed field in <c>q</c>); otherwise search is unchanged.</summary>
    public static bool IsFacetSearch(string? query, IReadOnlyCollection<string>? filters) =>
        (filters is not null && filters.Any(f => !string.IsNullOrWhiteSpace(f))) || FacetSearchSyntax.Parse(query).Fields.Count > 0;

    /// <summary>
    /// A resolved filter: the echo, the matched value ids (a value filter) or the title words (a <c>series:</c> field), and whether it
    /// excludes.
    /// </summary>
    private sealed record Resolved(SearchFilterDto Dto, HashSet<long> ValueIds, bool Negated = false, IReadOnlyList<TagViewText.Word>? TitleWords = null)
    {
        public bool IsTitle => TitleWords is not null;
    }

    /// <summary>A parsed <c>f</c> parameter.</summary>
    internal sealed record ChipRequest(FacetBucket Bucket, IReadOnlyList<string> ValueIds, bool Negated, string Raw);

    public async Task<SearchResultsDto> SearchAsync(
        long userId,
        bool incognito,
        string? query,
        IReadOnlyCollection<string>? filters,
        long? libraryId,
        string? cursor,
        int pageSize,
        BrowseReadStateFilter readState = BrowseReadStateFilter.All,
        FacetBucket? expand = null,
        CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        pageSize = Math.Clamp(pageSize, 1, CatalogBrowseService.MaxSearchPageSize);
        var parsed = FacetSearchSyntax.Parse(query);
        var chips = (filters ?? Array.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        var chipRequests = chips.Select(ParseChip).ToList(); // throws FacetFilterFormatException before any query

        var scope = await _scopes.ResolveAsync(userId, incognito, ct);
        if (libraryId is { } lib)
            scope = scope.Narrow(lib);

        var resolved = new List<Resolved>();
        foreach (var chip in chipRequests.Take(MaxFilters))
            resolved.Add(await ResolveChipAsync(chip, scope, ct));
        foreach (var field in parsed.Fields.Take(Math.Max(0, MaxFilters - resolved.Count)))
            resolved.Add(field.Bucket == FacetBucket.Series ? SeriesField(field) : await ResolveTypedAsync(field, scope, ct));

        var echo = resolved.Select(r => r.Dto).ToList();
        SearchResultsDto Empty() => new()
        {
            Query = query ?? "",
            Items = [],
            TotalCount = 0,
            HasMore = false,
            Filters = echo,
            Works = [],
        };

        // A positive filter that matched nothing visible: nothing can match. An exclusion that matched nothing excludes nothing.
        if (scope.IsEmpty || resolved.Count == 0 || resolved.Any(r => !r.Negated && !r.IsTitle && r.ValueIds.Count == 0))
        {
            Log(echo.Count, 0, started);
            return Empty();
        }

        var parameters = new Dictionary<string, object?>();
        var conditions = new List<string>();
        var positive = resolved.Any(r => !r.Negated && !r.IsTitle);
        if (!positive)
        {
            // Only exclusions / title words: start from every work the viewer may see.
            conditions.Add($"""
                w.Id IN (SELECT e.WorkId FROM facet_entries e
                         JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                         WHERE e.LibraryId IN ({scope.SqlList}))
                """);
        }
        foreach (var r in resolved)
        {
            if (r.TitleWords is { } words)
            {
                // series:"x" - one condition for the field: all of its words (a negated field: not all of them).
                var all = words.Select(word => TitleCondition(word.Text, parameters, scope)).ToList();
                if (all.Count > 0)
                    conditions.Add((r.Negated ? "NOT " : "") + "(" + string.Join(" AND ", all) + ")");
                continue;
            }
            if (r.ValueIds.Count == 0)
                continue; // an exclusion of nothing
            conditions.Add($"""
                w.Id {(r.Negated ? "NOT IN" : "IN")} (SELECT e.WorkId FROM facet_entries e
                         JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                         WHERE e.ValueId IN ({QuerySql.List(r.ValueIds)}) AND e.LibraryId IN ({scope.SqlList}))
                """);
        }
        // S5 (1.49.0): every free-text word matches the title, an alternative title or a visible location's name.
        foreach (var word in TagViewText.Words(parsed.FreeText))
            conditions.Add((word.Negated ? "NOT " : "") + TitleCondition(word.Text, parameters, scope));

        // 1.47.0 (lane P): the read filter - the viewer's read state of each matching WORK over its visible locations
        // (QueryWorkReadState), resolved in a pre-pass and folded in as an id list, so the page, the cursor and the count agree.
        if (readState != BrowseReadStateFilter.All)
            conditions.Add(await ReadStateConditionAsync(string.Join("\nAND ", conditions), parameters, scope, userId, readState, ct));
        var where = string.Join("\nAND ", conditions);
        var textParameters = new Dictionary<string, object?>(parameters);

        var position = ParseCursor(cursor);
        var cursorWhere = "";
        if (position is { } at)
        {
            cursorWhere = "AND (w.SortKey > @cursorKey OR (w.SortKey = @cursorKey AND w.Id > @cursorId))";
            parameters["@cursorKey"] = at.SortKey;
            parameters["@cursorId"] = at.Id;
        }
        parameters["@limit"] = pageSize + 1;

        var rows = await QuerySql.ReadAsync(_db, $"""
            SELECT w.Id, w.Title, w.SortKey
            FROM facet_works w
            WHERE {where}
            {cursorWhere}
            ORDER BY w.SortKey, w.Id
            LIMIT @limit
            """, parameters, r => (Id: r.GetInt64(0), Title: r.GetString(1), SortKey: r.GetString(2)), ct);
        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows = rows.Take(pageSize).ToList();

        var total = (await QuerySql.ReadAsync(_db, $"""
            SELECT COUNT(*) FROM facet_works w WHERE {where}
            """, textParameters, r => r.GetInt32(0), ct)).Single();

        IReadOnlyList<FacetBucketDto>? refine = null;
        if (position is null)
        {
            var used = resolved.SelectMany(r => r.ValueIds).ToHashSet();
            refine = total == 0 ? [] : await RefineAsync(where, textParameters, scope, used, expand, ct);
        }

        var works = await CardsAsync(userId, rows.Select(r => (r.Id, r.Title)).ToList(), scope, ct);
        Log(echo.Count, total, started);
        return new SearchResultsDto
        {
            Query = query ?? "",
            Items = [],
            TotalCount = total,
            NextCursor = hasMore && rows.Count > 0 ? EncodeCursor(rows[^1].Id, rows[^1].SortKey) : null,
            HasMore = hasMore,
            Filters = echo,
            Works = works,
            Refine = refine,
        };
    }

    /// <summary>
    /// S5 (1.49.0): one free-text word (or quoted phrase) as a condition - the work's title, one of its record's alternative titles
    /// (read with <c>json_each</c>, so non-Latin titles compare as text), or the display name of one of its locations the viewer may see.
    /// Case-insensitive for ASCII letters (SQLite <c>LIKE</c>); the text goes in as a parameter.
    /// </summary>
    private static string TitleCondition(string word, Dictionary<string, object?> parameters, FacetViewerScope scope)
    {
        var name = "@t" + parameters.Count(p => p.Key.StartsWith("@t", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture);
        parameters[name] = "%" + EscapeLike(word) + "%";
        return $"""
            (w.Title LIKE {name} ESCAPE '\'
             OR EXISTS (SELECT 1 FROM metadata_records r, json_each(CASE WHEN json_valid(r.AltTitlesJson) THEN r.AltTitlesJson ELSE '[]' END) j
                        WHERE r.Id = w.RecordId AND j.type = 'text' AND j.value LIKE {name} ESCAPE '\')
             OR EXISTS (SELECT 1 FROM facet_entries e
                        JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                        WHERE e.WorkId = w.Id AND e.LibraryId IN ({scope.SqlList}) AND n.DisplayName LIKE {name} ESCAPE '\'))
            """;
    }

    /// <summary>A <c>series:</c> field: its words (a quoted value is one phrase) as title words; always "matched" (it is text, not a value).</summary>
    private static Resolved SeriesField(TypedFacetField field)
    {
        IReadOnlyList<TagViewText.Word> words = field.Exact ? [new TagViewText.Word(field.Value, false)] : TagViewText.Words(field.Value).Select(w => w with { Negated = false }).ToList();
        return new Resolved(new SearchFilterDto
        {
            Bucket = FacetBucket.Series,
            Source = SearchFilterSource.Typed,
            Text = field.Value,
            Display = field.Value,
            Matched = words.Count > 0,
            ValueIds = [],
            Negated = field.Negated,
        }, [], field.Negated, words);
    }

    /// <summary>
    /// 1.49.0 (lane T, owner decision 9 a): the Refine list - for each value of <see cref="RefineBuckets"/>, how many of ALL the results
    /// (<paramref name="where"/>) carry it at a location the viewer may see; values already filtered on and merged creators left out; top
    /// <see cref="RefineChipsPerBucket"/> per bucket. Cached in <see cref="FacetCountCache"/> under (generation, shown-set hash, statement
    /// hash) - the statement names the shown libraries, every resolved value and the read filter's kept works, so a cached list is exactly
    /// what the same statement computes.
    /// 1.50.0 (lane U): <paramref name="expand"/> names one bucket whose list is complete (up to <see cref="MaxExpandedChips"/>, "+N more"
    /// opened) - the values are the ones already counted for the same statement, only the cut is lifted; the expanded list is cached under
    /// its own key, so a plain search never reads it.
    /// </summary>
    private async Task<IReadOnlyList<FacetBucketDto>> RefineAsync(
        string where, IReadOnlyDictionary<string, object?> parameters, FacetViewerScope scope, HashSet<long> used, FacetBucket? expand,
        CancellationToken ct)
    {
        var generation = _generation.Current; // read BEFORE querying (QueryGeneration's contract)
        var statement = where + "\u0001" + string.Join("\u0001", parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)))
            + (expand is { } open && RefineBuckets.Contains(open) ? "\u0001expand=" + (int)open : "");
        var key = new FacetCountCache.RefineKey(generation, scope.Hash,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(statement))));
        if (_cache.TryGet(key, out var cached) && cached is not null)
            return cached;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var buckets = string.Join(",", RefineBuckets.Select(b => ((int)b).ToString(CultureInfo.InvariantCulture)));
        var rows = await QuerySql.ReadAsync(_db, $"""
            SELECT v.Id, v.Bucket, v.PublicId, v.Display, v.CreatorId, COUNT(DISTINCT e.WorkId)
            FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            JOIN facet_values v ON v.Id = e.ValueId AND v.Bucket IN ({buckets})
            WHERE e.LibraryId IN ({scope.SqlList})
            AND e.WorkId IN (SELECT w.Id FROM facet_works w WHERE {where})
            GROUP BY v.Id
            """, parameters, r => (Id: r.GetInt64(0), Bucket: (FacetBucket)r.GetInt32(1), PublicId: r.GetString(2), Display: r.GetString(3),
                CreatorId: r.IsDBNull(4) ? (long?)null : r.GetInt64(4), Count: r.GetInt32(5)), ct);
        rows = rows.Where(r => !used.Contains(r.Id)).ToList();

        // A merged creator's value is left out: its works carry the live creator's value too once the index is rebuilt, and a chip never
        // names a merged creator (the Info panel's rule).
        var creatorIds = rows.Where(r => r.CreatorId is not null).Select(r => r.CreatorId!.Value).ToList();
        if (creatorIds.Count > 0)
        {
            var live = await QueryValues.LiveCreatorsAsync(_db, creatorIds, ct);
            rows = rows.Where(r => r.CreatorId is not { } c || live.GetValueOrDefault(c, c) == c).ToList();
        }

        var result = rows
            .GroupBy(r => r.Bucket)
            .OrderBy(g => (int)g.Key)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(r => r.Count).ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).ToList();
                var token = FacetBucketTokens.Token(g.Key);
                var keep = expand == g.Key ? MaxExpandedChips : RefineChipsPerBucket;
                return new FacetBucketDto
                {
                    Bucket = g.Key,
                    Chips = ordered.Take(keep).Select(r => new FacetChipDto
                    {
                        Id = r.PublicId,
                        Display = r.Display,
                        Count = r.Count,
                        Filter = token + ":" + r.PublicId,
                    }).ToList(),
                    More = Math.Max(0, ordered.Count - keep),
                };
            })
            .ToList();
        _cache.Set(key, result);
        _logger.LogDebug(LogEvents.Metadata.FacetRefineComputed, "Tag search refine: {Values} values in {Buckets} buckets, in {ElapsedMs} ms",
            rows.Count, result.Count, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }

    /// <summary>The works among those matching <paramref name="where"/> whose read state passes <paramref name="readState"/>, as a condition.</summary>
    private async Task<string> ReadStateConditionAsync(
        string where, IReadOnlyDictionary<string, object?> parameters, FacetViewerScope scope, long userId, BrowseReadStateFilter readState,
        CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var matching = await QuerySql.ReadAsync(_db, $"""
            SELECT w.Id FROM facet_works w WHERE {where}
            """, parameters, r => r.GetInt64(0), ct);
        var rollups = await QueryWorkReadState.OfWorksAsync(_db, matching, scope, userId, ct);
        var kept = matching.Where(id => QueryWorkReadState.Matches(rollups, id, readState)).ToList();
        _logger.LogDebug(LogEvents.Metadata.FacetWorkReadStateFiltered,
            "Tag search read filter {ReadState}: {Kept} of {Matching} works, in {ElapsedMs} ms",
            readState, kept.Count, matching.Count, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return kept.Count == 0 ? "0" : $"w.Id IN ({QuerySql.List(kept)})";
    }

    private void Log(int filters, int works, long started) =>
        _logger.LogDebug(LogEvents.Metadata.FacetSearchServed, "Tag search: {Filters} filters, {Works} works, in {ElapsedMs} ms",
            filters, works, (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    /// <summary>Work cards: the first visible location as a normal search card, plus the visible locations as breadcrumbs.</summary>
    private async Task<List<FacetWorkDto>> CardsAsync(long userId, List<(long Id, string Title)> works, FacetViewerScope scope, CancellationToken ct)
    {
        var locations = await QueryLocations.OfWorksAsync(_db, works.Select(w => w.Id).ToList(), scope, ct);
        var listed = locations.Values.SelectMany(l => l.Take(MaxCardLocations)).ToList();
        var crumbs = await QueryLocations.ToDtosAsync(_db, listed, ct);
        var cards = await _browse.SearchCardsAsync(userId, listed.Select(l => l.NodeId).Distinct().ToList(), ct);

        var result = new List<FacetWorkDto>(works.Count);
        foreach (var (id, title) in works)
        {
            if (!locations.TryGetValue(id, out var at) || at.Count == 0 || !cards.TryGetValue(at[0].NodeId, out var node))
                continue; // cannot happen for a work the filter found (it has a visible location); never shows an empty card
            result.Add(new FacetWorkDto
            {
                Title = title,
                Node = node,
                CoverUrl = node.CoverUrl,
                LocationCount = at.Count,
                Locations = at.Take(MaxCardLocations).Select(l => crumbs[l.NodeId]).ToList(),
            });
        }
        return result;
    }

    /// <summary>
    /// <c>&lt;bucket&gt;:fv_...</c>, 1.49.0 also <c>&lt;bucket&gt;:fv_a|fv_b</c> (any of, at most <see cref="MaxAlternatives"/>) and a leading
    /// <c>-</c> (exclude); anything else is malformed.
    /// </summary>
    internal static ChipRequest ParseChip(string filter)
    {
        var negated = filter.StartsWith('-');
        var body = negated ? filter[1..] : filter;
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !FacetBucketTokens.TryParse(body[..colon], out var bucket))
            throw Malformed();
        var raw = body[(colon + 1)..];
        var ids = raw.Split('|');
        if (ids.Length > MaxAlternatives || ids.Any(id => !FacetPublicIds.IsValueId(id)))
            throw Malformed();
        return new ChipRequest(bucket, ids.Distinct(StringComparer.Ordinal).ToList(), negated, raw);

        static FacetFilterFormatException Malformed() => new(
            "A filter is [-]<bucket>:<value id>[|<value id>...], e.g. creator:fv_abc, genre:fv_a|fv_b or -genre:fv_c.");
    }

    private static Resolved Unmatched(FacetBucket bucket, SearchFilterSource source, string? id, string? text, bool negated) => new(new SearchFilterDto
    {
        Bucket = bucket,
        Source = source,
        Id = id,
        Text = text,
        Matched = false,
        ValueIds = [],
        Negated = negated,
    }, [], negated);

    private async Task<Resolved> ResolveChipAsync(ChipRequest chip, FacetViewerScope scope, CancellationToken ct)
    {
        var rows = await _db.FacetValues.AsNoTracking()
            .Where(v => chip.ValueIds.Contains(v.PublicId))
            .Select(v => new { v.Id, v.PublicId, v.Bucket, v.ValueKey, v.Display, v.CreatorId })
            .ToListAsync(ct);
        var byPublic = rows.Where(v => v.Bucket == (int)chip.Bucket).ToDictionary(v => v.PublicId, StringComparer.Ordinal);

        // In the order sent; a merged creator's value answers with its target's value (the redirect - bookmarked views keep working).
        var shown = new List<QueryValues.Value>();
        string? redirectedFrom = null;
        foreach (var publicId in chip.ValueIds)
        {
            if (!byPublic.TryGetValue(publicId, out var value))
                continue;
            var one = new QueryValues.Value(value.Id, value.PublicId, (FacetBucket)value.Bucket, value.ValueKey, value.Display, value.CreatorId);
            if (value.CreatorId is { } creator)
            {
                var live = (await QueryValues.LiveCreatorsAsync(_db, [creator], ct))[creator];
                if (live != creator && (await QueryValues.CreatorValuesAsync(_db, [live], ct)).TryGetValue(live, out var target))
                {
                    one = target;
                    if (chip.ValueIds.Count == 1)
                        redirectedFrom = publicId;
                }
            }
            if (shown.All(s => s.Id != one.Id))
                shown.Add(one);
        }

        var visible = await QueryValues.VisibleAsync(_db, shown.Select(s => s.Id).ToList(), scope, ct);
        shown = shown.Where(s => visible.Contains(s.Id)).ToList();
        if (shown.Count == 0)
        {
            // Never name a value the viewer cannot see; a redirect that turned out invisible is not echoed either.
            return Unmatched(chip.Bucket, SearchFilterSource.Chip, chip.Raw, null, chip.Negated);
        }

        return new Resolved(new SearchFilterDto
        {
            Bucket = chip.Bucket,
            Source = SearchFilterSource.Chip,
            Id = chip.Raw,
            Display = string.Join(" or ", shown.Select(s => s.Display)),
            Matched = true,
            ValueIds = shown.Select(s => s.PublicId).ToList(),
            RedirectedFrom = redirectedFrom,
            Negated = chip.Negated,
        }, shown.Select(s => s.Id).ToHashSet(), chip.Negated);
    }

    /// <summary>
    /// A typed field: <c>creator:</c> by <c>creator_names.NameKey</c> (every spelling of a creator, merged creators followed to their
    /// target); the other buckets by the value's key or display name under the same folding. Only values with a visible entry count.
    /// 1.49.0 (lane T, S4): each alternative (<c>|</c>) matches its exact values; an UNQUOTED alternative with no exact value matches the
    /// values that have all of its words (<c>creator:urasawa</c> finds "Naoki Urasawa"); a quoted value matches exactly only; <c>year:</c>
    /// takes a range.
    /// </summary>
    private async Task<Resolved> ResolveTypedAsync(TypedFacetField field, FacetViewerScope scope, CancellationToken ct)
    {
        var alternatives = field.Alternatives
            .Select(a => (Text: a, Key: DeclaredFactsComparer.NameKey(a)))
            .Where(a => a.Key.Length > 0)
            .ToList();
        if (alternatives.Count == 0 || scope.IsEmpty)
            return Unmatched(field.Bucket, SearchFilterSource.Typed, null, field.Value, field.Negated);

        var candidates = new List<QueryValues.Value>();
        if (field.Bucket == FacetBucket.Creators)
        {
            var creatorIds = new HashSet<long>();
            foreach (var (_, key) in alternatives)
            {
                var exact = await _db.CreatorNames.AsNoTracking().Where(n => n.NameKey == key).Select(n => n.CreatorId).Distinct().ToListAsync(ct);
                if (exact.Count == 0 && !field.Exact)
                {
                    // Every word of the typed name is a word of a spelling (NameKey words are sorted, so order does not matter).
                    var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var longest = words.OrderByDescending(w => w.Length).First();
                    var like = "%" + EscapeLike(longest) + "%";
                    var near = await _db.CreatorNames.AsNoTracking()
                        .Where(n => EF.Functions.Like(n.NameKey, like, "\\"))
                        .Select(n => new { n.CreatorId, n.NameKey })
                        .ToListAsync(ct);
                    exact = near.Where(n => HasAllWords(n.NameKey, words)).Select(n => n.CreatorId).Distinct().ToList();
                }
                creatorIds.UnionWith(exact);
            }
            var live = await QueryValues.LiveCreatorsAsync(_db, creatorIds, ct);
            candidates = (await QueryValues.CreatorValuesAsync(_db, live.Values.Distinct().ToList(), ct)).Values.ToList();
        }
        else
        {
            var bucketValues = await QuerySql.ReadAsync(_db, $"""
                SELECT v.Id, v.PublicId, v.Bucket, v.ValueKey, v.Display, v.CreatorId
                FROM facet_values v
                WHERE v.Bucket = @bucket
                AND EXISTS (SELECT 1 FROM facet_entries e
                            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                            WHERE e.ValueId = v.Id AND e.LibraryId IN ({scope.SqlList}))
                """, new Dictionary<string, object?> { ["@bucket"] = (int)field.Bucket },
                r => new QueryValues.Value(r.GetInt64(0), r.GetString(1), (FacetBucket)r.GetInt32(2), r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5)), ct);
            foreach (var (text, key) in alternatives)
            {
                if (field.Bucket == FacetBucket.Year && FacetSearchSyntax.YearRange(text) is { } range)
                {
                    candidates.AddRange(bucketValues.Where(v => YearOf(v.ValueKey) is { } y && y >= range.From && y <= range.To));
                    continue;
                }
                var exact = bucketValues.Where(v =>
                        string.Equals(v.ValueKey, text, StringComparison.OrdinalIgnoreCase)
                        || DeclaredFactsComparer.NameKey(v.ValueKey) == key
                        || DeclaredFactsComparer.NameKey(v.Display) == key)
                    .ToList();
                if (exact.Count == 0 && !field.Exact)
                {
                    var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    exact = bucketValues.Where(v => HasAllWords(DeclaredFactsComparer.NameKey(v.Display), words)).ToList();
                }
                candidates.AddRange(exact);
            }
        }

        var visible = await QueryValues.VisibleAsync(_db, candidates.Select(c => c.Id).Distinct().ToList(), scope, ct);
        var matched = candidates.Where(c => visible.Contains(c.Id)).DistinctBy(c => c.Id).OrderBy(c => c.Id).ToList();
        if (matched.Count == 0)
            return Unmatched(field.Bucket, SearchFilterSource.Typed, null, field.Value, field.Negated);

        return new Resolved(new SearchFilterDto
        {
            Bucket = field.Bucket,
            Source = SearchFilterSource.Typed,
            Text = field.Value,
            Display = matched.Count == 1 ? matched[0].Display : field.Value,
            Matched = true,
            ValueIds = matched.Select(m => m.PublicId).ToList(),
            Negated = field.Negated,
        }, matched.Select(m => m.Id).ToHashSet(), field.Negated);
    }

    /// <summary>Whether a folded name (space-separated words) has every one of <paramref name="words"/> as a whole word.</summary>
    private static bool HasAllWords(string nameKey, IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return false;
        var have = nameKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return words.All(have.Contains);
    }

    /// <summary>The year of a Year value key (<c>y:2004</c>), or null.</summary>
    private static int? YearOf(string valueKey) =>
        valueKey.StartsWith("y:", StringComparison.Ordinal)
        && int.TryParse(valueKey.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>The works keyset cursor: <c>w:&lt;work id&gt;:&lt;SortKey&gt;</c>.</summary>
    internal static string EncodeCursor(long id, string sortKey) => "w:" + id.ToString(CultureInfo.InvariantCulture) + ":" + sortKey;

    /// <summary>The position after which a works page starts; null (first page) for no cursor or a malformed one.</summary>
    internal static (string SortKey, long Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !cursor.StartsWith("w:", StringComparison.Ordinal))
            return null;
        var rest = cursor[2..];
        var sep = rest.IndexOf(':', StringComparison.Ordinal);
        return sep > 0 && long.TryParse(rest.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (rest[(sep + 1)..], id)
            : null;
    }
}
