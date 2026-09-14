namespace com.lifepixer.mangaplex.Server.Persistence;

using System.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Set-based maintenance of <see cref="Entities.CatalogNodeEntity.LatestDescendantAddedAt"/>
/// (1.12.0). Recomputes a folder's value as the MAX <c>CreatedAt</c> over its non-tombstoned
/// descendant ARCHIVES at any depth, or NULL when it has none — the invariant the browse
/// <c>recentlyUpdated</c> sort and the home stacking depend on.
///
/// Both entry points issue a SINGLE <c>WITH RECURSIVE … UPDATE</c> statement (the recursive
/// descendant walk mirrors <c>CatalogBrowseService.ResolveFolderCoversAsync</c>), so callers
/// never fall into a per-node N+1. <see cref="RecomputeFoldersAsync"/> is bounded to the given
/// affected folder roots (scan-time bubble-up / tombstone recompute); the migration backfill
/// inlines the same statement over every folder.
///
/// <c>CreatedAt</c> is stored as an order-preserving binary long
/// (<c>DateTimeOffsetToBinaryConverter</c>), so <c>MAX()</c> yields the chronologically latest
/// instant and copies verbatim into <c>LatestDescendantAddedAt</c> (identically encoded) with no
/// decode/encode round trip.
/// </summary>
public static class LatestDescendantAddedAtMaintenance
{
    /// <summary>
    /// Recomputes <c>LatestDescendantAddedAt</c> for every folder in the database. Equivalent
    /// to the AddLatestDescendantAddedAt migration backfill; exposed for reuse and testing.
    /// </summary>
    public static Task RecomputeAllFoldersAsync(MangaPlexDbContext db, CancellationToken ct = default)
        => ExecuteAsync(
            db,
            seed: "SELECT Id, Id FROM catalog_nodes WHERE Kind = 0",
            updateWhere: "catalog_nodes.Kind = 0",
            ct);

    /// <summary>
    /// Recomputes <c>LatestDescendantAddedAt</c> for exactly the given folder ids (the affected
    /// path of a scan). No-op when the set is empty. Non-folder ids are harmless (they resolve
    /// to a NULL aggregate and are skipped by the folder filter on the update).
    /// </summary>
    public static Task RecomputeFoldersAsync(
        MangaPlexDbContext db,
        IReadOnlyCollection<long> folderIds,
        CancellationToken ct = default)
    {
        if (folderIds is null || folderIds.Count == 0)
            return Task.CompletedTask;

        var ids = string.Join(",", folderIds);
        return ExecuteAsync(
            db,
            seed: $"SELECT Id, Id FROM catalog_nodes WHERE Kind = 0 AND Id IN ({ids})",
            updateWhere: $"catalog_nodes.Kind = 0 AND catalog_nodes.Id IN ({ids})",
            ct);
    }

    private static async Task ExecuteAsync(
        MangaPlexDbContext db,
        string seed,
        string updateWhere,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE descendants(RootId, NodeId) AS (
                    {seed}
                    UNION ALL
                    SELECT d.RootId, cn.Id FROM descendants d
                    JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                ),
                maxes AS (
                    SELECT d.RootId AS RootId, MAX(a.CreatedAt) AS MaxCreated
                    FROM descendants d
                    JOIN catalog_nodes a ON a.Id = d.NodeId AND a.Kind = 1 AND a.Availability != 5
                    GROUP BY d.RootId
                )
                UPDATE catalog_nodes
                SET LatestDescendantAddedAt = (SELECT MaxCreated FROM maxes WHERE maxes.RootId = catalog_nodes.Id)
                WHERE {updateWhere};
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }
}
