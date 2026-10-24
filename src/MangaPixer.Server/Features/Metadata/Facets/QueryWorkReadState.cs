namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>
/// The viewer's read state of a WORK (1.47.0, lane P - the tag view's read filter). A work is read by the browse folder rule, as if it
/// were one folder holding its locations: the readable (not tombstoned) archives at or below every location of the work the viewer may
/// see (<see cref="FacetViewerScope"/>: the viewer's libraries, minus their Private libraries while Incognito is on, minus libraries with
/// "Show series information" off), each counted once, classified by <see cref="FolderReadRollupRules.Classify"/> - Read when every one
/// carries the viewer's read mark, Reading when some is read or in progress, Unread otherwise. A location in a library the viewer cannot
/// see never counts, so a work's state never reveals that location. A work with nothing readable has no rollup (Unread, as an empty
/// folder in browse).
/// </summary>
internal static class QueryWorkReadState
{
    /// <summary>The rollup of each work with at least one readable archive at a visible location (absent = nothing readable).</summary>
    public static async Task<Dictionary<long, FolderReadRollup>> OfWorksAsync(
        MangaPixerDbContext db, IReadOnlyCollection<long> workIds, FacetViewerScope scope, long userId, CancellationToken ct)
    {
        var result = new Dictionary<long, FolderReadRollup>();
        if (workIds.Count == 0 || scope.IsEmpty)
            return result;

        foreach (var chunk in workIds.Distinct().Chunk(QuerySql.Chunk))
        {
            // UNION (not UNION ALL) in the walk: a location inside another location of the same work is walked once, so an archive is
            // counted once per work.
            var rows = await QuerySql.ReadAsync(db, $"""
                WITH RECURSIVE located(WorkId, NodeId) AS (
                    SELECT e.WorkId, e.LocationNodeId
                    FROM facet_entries e
                    JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
                    WHERE e.WorkId IN ({QuerySql.List(chunk)})
                    AND e.LibraryId IN ({scope.SqlList})
                    UNION
                    SELECT l.WorkId, c.Id FROM located l
                    JOIN catalog_nodes c ON c.ParentId = l.NodeId
                )
                SELECT l.WorkId,
                       COUNT(*) AS Total,
                       SUM(CASE WHEN rm.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS ReadCount,
                       SUM(CASE WHEN rm.ItemId IS NULL AND rp.State = {(int)ReadingState.InProgress} THEN 1 ELSE 0 END) AS InProgressCount
                FROM located l
                JOIN catalog_nodes a ON a.Id = l.NodeId AND a.Kind = {(int)CatalogNodeKind.Archive} AND a.Availability != {QuerySql.Tombstoned}
                LEFT JOIN read_marks rm ON rm.ItemId = a.Id AND rm.UserId = @user
                LEFT JOIN reading_progress rp ON rp.ItemId = a.Id AND rp.UserId = @user
                GROUP BY l.WorkId
                """, new Dictionary<string, object?> { ["@user"] = userId },
                r => (WorkId: r.GetInt64(0), Total: r.GetInt32(1), Read: r.GetInt32(2), InProgress: r.GetInt32(3)), ct);
            foreach (var row in rows)
            {
                if (FolderReadRollupRules.Classify(row.Total, row.Read, row.InProgress) is { } rollup)
                    result[row.WorkId] = rollup;
            }
        }
        return result;
    }

    /// <summary>Whether a work with this rollup passes the filter (browse's folder mapping: Unread also keeps a work with nothing readable).</summary>
    public static bool Matches(IReadOnlyDictionary<long, FolderReadRollup> rollups, long workId, BrowseReadStateFilter readState)
    {
        var has = rollups.TryGetValue(workId, out var rollup);
        return readState switch
        {
            BrowseReadStateFilter.Read => has && rollup == FolderReadRollup.Read,
            BrowseReadStateFilter.Reading => has && rollup == FolderReadRollup.Reading,
            BrowseReadStateFilter.Unread => !has || rollup == FolderReadRollup.Unread,
            _ => true,
        };
    }
}
