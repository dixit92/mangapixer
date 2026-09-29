namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A folder's FILE cover is the cover of its first non-tombstoned descendant archive by SortKey (ordinal, then id) - the one
/// rule, in two forms: <see cref="ResolveAsync"/> over the database (behind <c>ICoverResolver</c> for every card) and
/// <see cref="FirstArchiveBelow"/> over the matcher's in-memory tree snapshot (the cover comparison compares file covers).
/// </summary>
internal static class FolderCovers
{
    /// <summary>
    /// Resolves the cover archive of each folder in one recursive CTE (no N+1): folder internal id ->
    /// cover archive public id. Folders without a readable descendant archive are omitted.
    /// </summary>
    public static async Task<Dictionary<long, string>> ResolveAsync(MangaPixerDbContext db, IReadOnlyCollection<long> folderInternalIds, CancellationToken ct) =>
        (await ResolveArchivesAsync(db, folderInternalIds, ct)).ToDictionary(kv => kv.Key, kv => kv.Value.PublicId);

    /// <summary>
    /// The same rule with the cover archive's internal id too (the cover layer resolves the archive's own layer):
    /// folder internal id -> (archive internal id, archive public id).
    /// </summary>
    public static async Task<Dictionary<long, (long Id, string PublicId)>> ResolveArchivesAsync(MangaPixerDbContext db,
        IReadOnlyCollection<long> folderInternalIds, CancellationToken ct)
    {
        var result = new Dictionary<long, (long Id, string PublicId)>();
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
                    SELECT d.RootId, cn.Id AS CoverId, cn.PublicId AS CoverPublicId,
                           ROW_NUMBER() OVER (PARTITION BY d.RootId ORDER BY d.SortKey, d.NodeId) AS rn
                    FROM descendants d
                    JOIN catalog_nodes cn ON d.NodeId = cn.Id
                    WHERE d.Kind = 1 AND d.Availability != 5
                )
                SELECT RootId, CoverId, CoverPublicId FROM ranked WHERE rn = 1;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetString(2));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>
    /// The same rule over a <see cref="LibraryTreeSnapshot"/> (live nodes only): the archive with the ordinal-smallest SortKey
    /// anywhere below <paramref name="folderId"/>, ties by id; null when the subtree holds no archive.
    /// </summary>
    public static long? FirstArchiveBelow(LibraryTreeSnapshot tree, long folderId)
    {
        ArgumentNullException.ThrowIfNull(tree);
        LibraryTreeSnapshot.Node? best = null;
        var stack = new Stack<long>([folderId]);
        while (stack.Count > 0)
        {
            foreach (var child in tree.ChildrenOf(stack.Pop()))
            {
                if (child.IsFolder)
                    stack.Push(child.Id);
                else if (best is null || string.CompareOrdinal(child.SortKey, best.SortKey) < 0
                    || (string.Equals(child.SortKey, best.SortKey, StringComparison.Ordinal) && child.Id < best.Id))
                    best = child;
            }
        }
        return best?.Id;
    }

    /// <summary>The cover URL of an archive (by public id).</summary>
    public static string ArchiveCoverUrl(string archivePublicId) => $"/api/v1/items/{archivePublicId}/cover";
}
