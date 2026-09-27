namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A folder's cover is the cover of its first non-tombstoned descendant archive by SortKey - the
/// same rule browse uses, shared with the metadata review list and the identify dialog (1.26.x).
/// </summary>
internal static class FolderCovers
{
    /// <summary>
    /// Resolves the cover archive of each folder in one recursive CTE (no N+1): folder internal id ->
    /// cover archive public id. Folders without a readable descendant archive are omitted.
    /// </summary>
    public static async Task<Dictionary<long, string>> ResolveAsync(MangaPixerDbContext db, IReadOnlyCollection<long> folderInternalIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (folderInternalIds.Count == 0)
            return result;

        var ids = string.Join(",", folderInternalIds);
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE descendants(RootId, NodeId, Kind, SortKey, Availability) AS (
                    SELECT r.Id, cn.Id, cn.Kind, cn.SortKey, cn.Availability
                    FROM catalog_nodes r
                    JOIN catalog_nodes cn ON cn.ParentId = r.Id
                    WHERE r.Id IN ({ids})
                    UNION ALL
                    SELECT d.RootId, cn.Id, cn.Kind, cn.SortKey, cn.Availability
                    FROM descendants d
                    JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                ),
                ranked AS (
                    SELECT d.RootId, cn.PublicId AS CoverPublicId,
                           ROW_NUMBER() OVER (PARTITION BY d.RootId ORDER BY d.SortKey, d.NodeId) AS rn
                    FROM descendants d
                    JOIN catalog_nodes cn ON d.NodeId = cn.Id
                    WHERE d.Kind = 1 AND d.Availability != 5
                )
                SELECT RootId, CoverPublicId FROM ranked WHERE rn = 1;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt64(0)] = reader.GetString(1);
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>The cover URL of an archive (by public id).</summary>
    public static string ArchiveCoverUrl(string archivePublicId) => $"/api/v1/items/{archivePublicId}/cover";
}
