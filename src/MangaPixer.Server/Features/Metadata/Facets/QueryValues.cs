namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Tag values and creator redirects for the tag query (1.45.0, lane Q).</summary>
internal static class QueryValues
{
    public sealed record Value(long Id, string PublicId, FacetBucket Bucket, string ValueKey, string Display, long? CreatorId);

    public static async Task<Dictionary<long, Value>> ByIdAsync(MangaPixerDbContext db, IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, Value>();
        foreach (var chunk in ids.Distinct().Chunk(QuerySql.Chunk))
        {
            var rows = await db.FacetValues.AsNoTracking()
                .Where(v => chunk.Contains(v.Id))
                .Select(v => new { v.Id, v.PublicId, v.Bucket, v.ValueKey, v.Display, v.CreatorId })
                .ToListAsync(ct);
            foreach (var v in rows)
                result[v.Id] = new Value(v.Id, v.PublicId, (FacetBucket)v.Bucket, v.ValueKey, v.Display, v.CreatorId);
        }
        return result;
    }

    /// <summary>
    /// The live creator each creator id stands for: itself, or - for a creator an admin merged into another - the end of its
    /// <c>MergedIntoId</c> chain (at most 16 hops; a cycle stops where it started). Unknown ids map to themselves.
    /// </summary>
    public static async Task<Dictionary<long, long>> LiveCreatorsAsync(MangaPixerDbContext db, IReadOnlyCollection<long> creatorIds, CancellationToken ct)
    {
        var result = creatorIds.Distinct().ToDictionary(id => id, id => id);
        if (result.Count == 0)
            return result;

        var mergedInto = new Dictionary<long, long?>();
        var frontier = result.Keys.ToList();
        for (var hop = 0; hop < 16 && frontier.Count > 0; hop++)
        {
            var rows = await db.Creators.AsNoTracking()
                .Where(c => frontier.Contains(c.Id))
                .Select(c => new { c.Id, c.MergedIntoId })
                .ToListAsync(ct);
            foreach (var row in rows)
                mergedInto[row.Id] = row.MergedIntoId;
            frontier = rows.Where(r => r.MergedIntoId is { } next && !mergedInto.ContainsKey(next)).Select(r => r.MergedIntoId!.Value).ToList();
        }

        foreach (var id in result.Keys.ToList())
        {
            var current = id;
            for (var hop = 0; hop < 16; hop++)
            {
                if (!mergedInto.TryGetValue(current, out var next) || next is not { } target || target == id)
                    break;
                current = target;
            }
            result[id] = current;
        }
        return result;
    }

    /// <summary>The creator value (bucket Creators) of each live creator id, when the index has one.</summary>
    public static async Task<Dictionary<long, Value>> CreatorValuesAsync(MangaPixerDbContext db, IReadOnlyCollection<long> creatorIds, CancellationToken ct)
    {
        var result = new Dictionary<long, Value>();
        foreach (var chunk in creatorIds.Distinct().Chunk(QuerySql.Chunk))
        {
            var rows = await db.FacetValues.AsNoTracking()
                .Where(v => v.Bucket == (int)FacetBucket.Creators && v.CreatorId != null && chunk.Contains(v.CreatorId!.Value))
                .Select(v => new { v.Id, v.PublicId, v.Bucket, v.ValueKey, v.Display, v.CreatorId })
                .ToListAsync(ct);
            foreach (var v in rows.OrderBy(v => v.Id))
                result.TryAdd(v.CreatorId!.Value, new Value(v.Id, v.PublicId, (FacetBucket)v.Bucket, v.ValueKey, v.Display, v.CreatorId));
        }
        return result;
    }

    /// <summary>The values among <paramref name="valueIds"/> with at least one entry the viewer may see (shown library, not tombstoned).</summary>
    public static async Task<HashSet<long>> VisibleAsync(MangaPixerDbContext db, IReadOnlyCollection<long> valueIds, FacetViewerScope scope, CancellationToken ct)
    {
        var visible = new HashSet<long>();
        if (valueIds.Count == 0 || scope.IsEmpty)
            return visible;
        foreach (var chunk in valueIds.Distinct().Chunk(QuerySql.Chunk))
        {
            var rows = await QuerySql.ReadAsync(db, $"""
                SELECT DISTINCT e.ValueId
                FROM facet_entries e
                JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                WHERE e.ValueId IN ({QuerySql.List(chunk)})
                AND e.LibraryId IN ({scope.SqlList})
                """, null, r => r.GetInt64(0), ct);
            visible.UnionWith(rows);
        }
        return visible;
    }
}
