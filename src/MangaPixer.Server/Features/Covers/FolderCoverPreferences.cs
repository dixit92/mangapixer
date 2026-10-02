namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The nearest folder cover preference of a node: the folder that carries the row (the node itself or an ancestor) and its value.</summary>
public readonly record struct NearestCoverPreference(long SourceNodeId, FolderCoverPreference Preference);

/// <summary>
/// Batched "nearest folder cover preference" walk (1.32.0): for many nodes in ONE recursive query, the first row of
/// <c>folder_cover_preferences</c> on the node itself or an ancestor (bounded 64), the same self -> ancestors rule as the folder reader
/// defaults. Nodes without a row anywhere above are omitted (they inherit the library's "Show web covers" switch - see
/// <see cref="FolderCoverRules"/>). The cover layer, the stack covers and the background decisions all read it through here.
/// </summary>
internal static class FolderCoverPreferences
{
    public static async Task<Dictionary<long, NearestCoverPreference>> NearestAsync(MangaPixerDbContext db, IReadOnlyCollection<long> nodeIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, NearestCoverPreference>();
        // Most libraries have no row at all: one cheap probe instead of the recursive walk on every browse page.
        if (nodeIds.Count == 0 || !await db.FolderCoverPreferences.AsNoTracking().AnyAsync(ct))
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
                    SELECT u.StartId, u.NodeId, f.Preference,
                           ROW_NUMBER() OVER (PARTITION BY u.StartId ORDER BY u.Depth) AS rn
                    FROM up u JOIN folder_cover_preferences f ON f.NodeId = u.NodeId
                )
                SELECT StartId, NodeId, Preference FROM hits WHERE rn = 1;
                """;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt64(0)] = new NearestCoverPreference(reader.GetInt64(1), (FolderCoverPreference)reader.GetInt32(2));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>The nearest preference of one node, or null (inherits the library).</summary>
    public static async Task<NearestCoverPreference?> OfAsync(MangaPixerDbContext db, long nodeId, CancellationToken ct)
    {
        var all = await NearestAsync(db, [nodeId], ct);
        return all.TryGetValue(nodeId, out var p) ? p : null;
    }
}
