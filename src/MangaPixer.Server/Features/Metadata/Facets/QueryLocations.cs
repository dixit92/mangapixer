namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>
/// Locations of works as display-name breadcrumbs (1.45.0, lane Q): only locations in the viewer's shown libraries, never a path.
/// Batched: one query for the locations of a page of works, one recursive query for every trail.
/// </summary>
internal static class QueryLocations
{
    /// <summary>A location of a work: its node, library and sort position (library name, then the node's sort key).</summary>
    public sealed record Location(long WorkId, long NodeId, string NodePublicId, long LibraryId, string LibraryPublicId, string LibraryName, string SortKey);

    /// <summary>The visible, non-tombstoned locations of each work, ordered by library name then sort key.</summary>
    public static async Task<Dictionary<long, List<Location>>> OfWorksAsync(
        MangaPixerDbContext db, IReadOnlyCollection<long> workIds, FacetViewerScope scope, CancellationToken ct)
    {
        var byWork = new Dictionary<long, List<Location>>();
        if (workIds.Count == 0 || scope.IsEmpty)
            return byWork;

        foreach (var chunk in workIds.Chunk(QuerySql.Chunk))
        {
            var rows = await QuerySql.ReadAsync(db, $"""
                SELECT DISTINCT e.WorkId, n.Id, n.PublicId, l.Id, l.PublicId, l.DisplayName, n.SortKey
                FROM facet_entries e
                JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                JOIN libraries l ON l.Id = e.LibraryId
                WHERE e.WorkId IN ({QuerySql.List(chunk)})
                AND e.LibraryId IN ({scope.SqlList})
                """, null, r => new Location(
                    r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6)), ct);
            foreach (var row in rows)
            {
                if (!byWork.TryGetValue(row.WorkId, out var list))
                    byWork[row.WorkId] = list = [];
                list.Add(row);
            }
        }

        foreach (var list in byWork.Values)
        {
            list.Sort((a, b) =>
            {
                var byLibrary = string.Compare(a.LibraryName, b.LibraryName, StringComparison.OrdinalIgnoreCase);
                if (byLibrary != 0)
                    return byLibrary;
                var byKey = string.CompareOrdinal(a.SortKey, b.SortKey);
                return byKey != 0 ? byKey : a.NodeId.CompareTo(b.NodeId);
            });
        }
        return byWork;
    }

    /// <summary>Breadcrumb DTOs for these locations: the trail from the library's top-level folder down to the node itself.</summary>
    public static async Task<Dictionary<long, FacetLocationDto>> ToDtosAsync(
        MangaPixerDbContext db, IEnumerable<Location> locations, CancellationToken ct)
    {
        var list = locations.DistinctBy(l => l.NodeId).ToList();
        var result = new Dictionary<long, FacetLocationDto>();
        if (list.Count == 0)
            return result;

        // Every node on the way up from each location (64 levels at most, as the series-info walk).
        var nodes = new Dictionary<long, (long? ParentId, string PublicId, string DisplayName)>();
        foreach (var chunk in list.Select(l => l.NodeId).Chunk(QuerySql.Chunk))
        {
            var rows = await QuerySql.ReadAsync(db, $"""
                WITH RECURSIVE up(Id, Depth) AS (
                    SELECT Id, 0 FROM catalog_nodes WHERE Id IN ({QuerySql.List(chunk)})
                    UNION
                    SELECT c.ParentId, up.Depth + 1 FROM catalog_nodes c JOIN up ON c.Id = up.Id
                    WHERE c.ParentId IS NOT NULL AND up.Depth < 64
                )
                SELECT DISTINCT n.Id, n.ParentId, n.PublicId, n.DisplayName
                FROM up JOIN catalog_nodes n ON n.Id = up.Id
                """, null, r => (Id: r.GetInt64(0), ParentId: r.IsDBNull(1) ? (long?)null : r.GetInt64(1), PublicId: r.GetString(2), Name: r.GetString(3)), ct);
            foreach (var row in rows)
                nodes[row.Id] = (row.ParentId, row.PublicId, row.Name);
        }

        foreach (var location in list)
        {
            var trail = new List<BreadcrumbEntry>();
            long? current = location.NodeId;
            var guard = 0;
            while (current is { } id && nodes.TryGetValue(id, out var node) && guard++ <= 64)
            {
                trail.Add(new BreadcrumbEntry { Id = node.PublicId, DisplayName = node.DisplayName });
                current = node.ParentId;
            }
            trail.Reverse();
            result[location.NodeId] = new FacetLocationDto
            {
                NodeId = location.NodePublicId,
                LibraryId = location.LibraryPublicId,
                LibraryName = location.LibraryName,
                Trail = trail,
            };
        }
        return result;
    }
}
