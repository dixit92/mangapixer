namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The nearest series link of a node (self -> ancestors): where it sits, its state and its record.</summary>
/// <param name="LinkNodeId">The node that carries the link (the node itself or an ancestor).</param>
/// <param name="Depth">0 = the node's own link.</param>
/// <param name="State">A <see cref="SeriesLinkState"/>; <see cref="SeriesLinkState.DontMatch"/> stops everything automatic.</param>
public readonly record struct NearestLink(long LinkNodeId, int Depth, SeriesLinkState State, long? RecordId)
{
    /// <summary>A Confirmed or Auto link to a record.</summary>
    public bool IsLinked => RecordId is not null && State is SeriesLinkState.Confirmed or SeriesLinkState.Auto;

    public bool IsDontMatch => State == SeriesLinkState.DontMatch;
}

/// <summary>
/// Batched "nearest series link" walk for the cover layer - the same self -> ancestors rule (bounded 64, needs-review rows
/// skipped, the nearest row wins, Don't match stops) as <c>SeriesInfoResolver</c>, for many nodes in one recursive CTE.
/// </summary>
internal static class CoverLinks
{
    public static async Task<Dictionary<long, NearestLink>> NearestAsync(MangaPixerDbContext db, IReadOnlyCollection<long> nodeIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, NearestLink>();
        if (nodeIds.Count == 0)
            return result;

        var ids = string.Join(",", nodeIds.Distinct().Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE up(StartId, NodeId, ParentId, Depth) AS (
                    SELECT cn.Id, cn.Id, cn.ParentId, 0 FROM catalog_nodes cn WHERE cn.Id IN ({ids})
                    UNION ALL
                    SELECT u.StartId, p.Id, p.ParentId, u.Depth + 1
                    FROM up u JOIN catalog_nodes p ON p.Id = u.ParentId
                    WHERE u.Depth < 64
                ),
                hits AS (
                    SELECT u.StartId, u.NodeId, u.Depth, l.State, l.RecordId,
                           ROW_NUMBER() OVER (PARTITION BY u.StartId ORDER BY u.Depth) AS rn
                    FROM up u JOIN node_series_links l ON l.NodeId = u.NodeId
                    WHERE l.State != {(int)SeriesLinkState.NeedsReview}
                )
                SELECT StartId, NodeId, Depth, State, RecordId FROM hits WHERE rn = 1;
                """;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result[reader.GetInt64(0)] = new NearestLink(reader.GetInt64(1), reader.GetInt32(2), (SeriesLinkState)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4));
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }
}
