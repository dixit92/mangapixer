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
/// <c>q</c> next to a filter narrows the works by title. A filter that matches no visible value - unknown, or present only where the
/// viewer cannot look - gives an empty page with no count and no name: the two cases answer alike (never 403).
/// </summary>
public sealed class FacetSearchService
{
    /// <summary>The most filters (chips + typed fields) one search applies; further ones are ignored.</summary>
    public const int MaxFilters = 10;

    /// <summary>Locations listed on a work card.</summary>
    public const int MaxCardLocations = 5;

    private readonly MangaPixerDbContext _db;
    private readonly FacetViewerScopeResolver _scopes;
    private readonly CatalogBrowseService _browse;
    private readonly ILogger<FacetSearchService> _logger;

    public FacetSearchService(MangaPixerDbContext db, FacetViewerScopeResolver scopes, CatalogBrowseService browse, ILogger<FacetSearchService> logger)
    {
        _db = db;
        _scopes = scopes;
        _browse = browse;
        _logger = logger;
    }

    /// <summary>Whether this search is a tag view (any <c>f</c> parameter, or a typed field in <c>q</c>); otherwise search is unchanged.</summary>
    public static bool IsFacetSearch(string? query, IReadOnlyCollection<string>? filters) =>
        (filters is not null && filters.Any(f => !string.IsNullOrWhiteSpace(f))) || FacetSearchSyntax.Parse(query).Fields.Count > 0;

    private sealed record Resolved(SearchFilterDto Dto, HashSet<long> ValueIds);

    public async Task<SearchResultsDto> SearchAsync(
        long userId,
        bool incognito,
        string? query,
        IReadOnlyCollection<string>? filters,
        long? libraryId,
        string? cursor,
        int pageSize,
        BrowseReadStateFilter readState = BrowseReadStateFilter.All,
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
        foreach (var (bucket, id) in chipRequests.Take(MaxFilters))
            resolved.Add(await ResolveChipAsync(bucket, id, scope, ct));
        foreach (var field in parsed.Fields.Take(Math.Max(0, MaxFilters - resolved.Count)))
            resolved.Add(await ResolveTypedAsync(field, scope, ct));

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

        if (scope.IsEmpty || resolved.Count == 0 || resolved.Any(r => r.ValueIds.Count == 0))
        {
            Log(echo.Count, 0, started);
            return Empty();
        }

        // Each filter: the work has an entry with one of its values at a location the viewer may see (AND across filters).
        var conditions = resolved.Select(r => $"""
            w.Id IN (SELECT e.WorkId FROM facet_entries e
                     JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                     WHERE e.ValueId IN ({QuerySql.List(r.ValueIds)}) AND e.LibraryId IN ({scope.SqlList}))
            """).ToList();
        var parameters = new Dictionary<string, object?>();
        if (parsed.FreeText.Length > 0)
        {
            conditions.Add("w.Title LIKE @text ESCAPE '\\'");
            parameters["@text"] = "%" + EscapeLike(parsed.FreeText) + "%";
        }
        // 1.47.0 (lane P): the read filter - the viewer's read state of each matching WORK over its visible locations
        // (QueryWorkReadState), resolved in a pre-pass and folded in as an id list, so the page, the cursor and the count agree.
        if (readState != BrowseReadStateFilter.All)
            conditions.Add(await ReadStateConditionAsync(string.Join("\nAND ", conditions), parameters, scope, userId, readState, ct));
        var where = string.Join("\nAND ", conditions);

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

        var countParameters = parameters.Where(p => p.Key == "@text").ToDictionary(p => p.Key, p => p.Value);
        var total = (await QuerySql.ReadAsync(_db, $"""
            SELECT COUNT(*) FROM facet_works w WHERE {where}
            """, countParameters, r => r.GetInt32(0), ct)).Single();

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
        };
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

    /// <summary><c>&lt;bucket&gt;:fv_...</c>; anything else is malformed.</summary>
    internal static (FacetBucket Bucket, string ValueId) ParseChip(string filter)
    {
        var colon = filter.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !FacetBucketTokens.TryParse(filter[..colon], out var bucket) || !FacetPublicIds.IsValueId(filter[(colon + 1)..]))
            throw new FacetFilterFormatException("A filter is <bucket>:<value id>, e.g. creator:fv_abc.");
        return (bucket, filter[(colon + 1)..]);
    }

    private static Resolved Unmatched(FacetBucket bucket, SearchFilterSource source, string? id, string? text) => new(new SearchFilterDto
    {
        Bucket = bucket,
        Source = source,
        Id = id,
        Text = text,
        Matched = false,
        ValueIds = [],
    }, []);

    private async Task<Resolved> ResolveChipAsync(FacetBucket bucket, string publicId, FacetViewerScope scope, CancellationToken ct)
    {
        var value = await _db.FacetValues.AsNoTracking()
            .Where(v => v.PublicId == publicId)
            .Select(v => new { v.Id, v.PublicId, v.Bucket, v.ValueKey, v.Display, v.CreatorId })
            .FirstOrDefaultAsync(ct);
        if (value is null || value.Bucket != (int)bucket)
            return Unmatched(bucket, SearchFilterSource.Chip, publicId, null);

        // A merged creator's value answers with its target's value (the redirect - bookmarked views keep working).
        var shown = new QueryValues.Value(value.Id, value.PublicId, (FacetBucket)value.Bucket, value.ValueKey, value.Display, value.CreatorId);
        string? redirectedFrom = null;
        if (value.CreatorId is { } creator)
        {
            var live = (await QueryValues.LiveCreatorsAsync(_db, [creator], ct))[creator];
            if (live != creator && (await QueryValues.CreatorValuesAsync(_db, [live], ct)).TryGetValue(live, out var target))
            {
                shown = target;
                redirectedFrom = publicId;
            }
        }

        if (!(await QueryValues.VisibleAsync(_db, [shown.Id], scope, ct)).Contains(shown.Id))
            return Unmatched(bucket, SearchFilterSource.Chip, publicId, null);

        return new Resolved(new SearchFilterDto
        {
            Bucket = bucket,
            Source = SearchFilterSource.Chip,
            Id = publicId,
            Display = shown.Display,
            Matched = true,
            ValueIds = [shown.PublicId],
            RedirectedFrom = redirectedFrom,
        }, [shown.Id]);
    }

    /// <summary>
    /// A typed field: <c>creator:</c> by <c>creator_names.NameKey</c> (every spelling of a creator, merged creators followed to their
    /// target); the other buckets by the value's key or display name under the same folding. Only values with a visible entry count.
    /// </summary>
    private async Task<Resolved> ResolveTypedAsync(TypedFacetField field, FacetViewerScope scope, CancellationToken ct)
    {
        var key = DeclaredFactsComparer.NameKey(field.Value);
        if (key.Length == 0 || scope.IsEmpty)
            return Unmatched(field.Bucket, SearchFilterSource.Typed, null, field.Value);

        List<QueryValues.Value> candidates;
        if (field.Bucket == FacetBucket.Creators)
        {
            var creatorIds = await _db.CreatorNames.AsNoTracking()
                .Where(n => n.NameKey == key)
                .Select(n => n.CreatorId)
                .Distinct()
                .ToListAsync(ct);
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
            candidates = bucketValues.Where(v =>
                    string.Equals(v.ValueKey, field.Value, StringComparison.OrdinalIgnoreCase)
                    || DeclaredFactsComparer.NameKey(v.ValueKey) == key
                    || DeclaredFactsComparer.NameKey(v.Display) == key)
                .ToList();
        }

        var visible = await QueryValues.VisibleAsync(_db, candidates.Select(c => c.Id).ToList(), scope, ct);
        var matched = candidates.Where(c => visible.Contains(c.Id)).OrderBy(c => c.Id).ToList();
        if (matched.Count == 0)
            return Unmatched(field.Bucket, SearchFilterSource.Typed, null, field.Value);

        return new Resolved(new SearchFilterDto
        {
            Bucket = field.Bucket,
            Source = SearchFilterSource.Typed,
            Text = field.Value,
            Display = matched.Count == 1 ? matched[0].Display : field.Value,
            Matched = true,
            ValueIds = matched.Select(m => m.PublicId).ToList(),
        }, matched.Select(m => m.Id).ToHashSet());
    }

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
