namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The archive "context line" (1.50.0, lane W): the name of the folder an archive belongs to, shown as a muted second line under the
/// archive's own name wherever the archive is listed outside its folder (search results, quick results, Continue reading, Favorites).
/// A renamer or a library layout that drops the series title from file names (<c>Berserk/Vol. 012.cbz</c>, <c>Series/Ch. 0106.00 Vol. 006.cbz</c>)
/// leaves a found archive unrecognisable without it.
/// <para>
/// The rule: the nearest ancestor FOLDER whose name is not a plain unit / kind folder (<see cref="AutoMatchText.IsUnitFolderName"/> -
/// <c>Volumes</c>, <c>Vol 3</c>, <c>Chapters</c>, <c>Ch 1-50</c>, <c>Extras</c>, <c>Season 2</c>, <c>Part 3</c>, <c>12</c> -
/// the one list the work detector, the Volumes view and Home's stacks already use). <c>Series/Volumes/Vol 1.cbz</c> gives
/// <c>Series</c>; <c>Series/Vol 1.cbz</c> gives <c>Series</c>. An archive directly in the library root, a folder, an unknown id and an
/// archive below nothing but kind folders have no context (absent from the answer).
/// </para>
/// <para>
/// Two refinements (integrator decisions on the owner's answer 14): a CATEGORY folder (<see cref="AutoMatchText.IsCategoryFolderName"/> -
/// <c>Manga</c>, <c>Manhwa</c>, <c>Comics</c>, <c>Doujinshi</c> ..., the one category list the work detector and the tree snapshot use) is skipped like a kind
/// folder (<c>Manga/One-shot.cbz</c> has no context; <c>Artist/Manga/One-shot.cbz</c> gives <c>Artist</c>); and a context that equals the archive's own name
/// after the catalog's folding (extension removed, <see cref="TitleNormalizer.ScoringForm"/>: <c>Series/Series.cbz</c>) is left out, so the same text never shows twice.
/// </para>
/// <para>
/// Batched: ONE recursive upward query (at most <see cref="SeriesInfoResolver.MaxWalkDepth"/> levels) whatever the number of archives.
/// An ancestor lives in the archive's own library, so restricting the START nodes to <c>visibleLibraryIds</c> adds no access surface;
/// the answer is a display name only, never a path.
/// </para>
/// </summary>
public static class ArchiveContextNames
{
    /// <summary>More start ids than this are answered in several queries (the statement carries the ids as literals).</summary>
    internal const int ChunkSize = 400;

    /// <summary>
    /// The context name of each archive in <paramref name="archiveNodeIds"/> (internal catalog node ids) that has one. Archives outside
    /// <paramref name="visibleLibraryIds"/> (pass the viewer's visible libraries, Incognito applied), folders, tombstone-agnostic
    /// (the caller filters what it lists) and archives without a non-kind ancestor are left out of the dictionary.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, string>> ResolveAsync(
        MangaPixerDbContext db,
        IEnumerable<long> archiveNodeIds,
        IReadOnlyCollection<long> visibleLibraryIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archiveNodeIds);
        ArgumentNullException.ThrowIfNull(visibleLibraryIds);
        var result = new Dictionary<long, string>();
        var decided = new HashSet<long>();
        var ids = archiveNodeIds.Distinct().ToList();
        if (ids.Count == 0 || visibleLibraryIds.Count == 0)
            return result;
        var libraries = string.Join(",", visibleLibraryIds.Distinct());

        var connection = db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            foreach (var chunk in ids.Chunk(ChunkSize))
            {
                using var command = connection.CreateCommand();
                command.CommandText = StatementFor(string.Join(",", chunk), libraries);

                // Rows come ordered by archive then depth (nearest ancestor first): the first non-kind name of each archive wins.
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var archiveId = reader.GetInt64(0);
                    if (decided.Contains(archiveId))
                        continue;
                    var name = reader.GetString(1);
                    if (!IsContextName(name))
                        continue;
                    // The nearest real ancestor decides; when it only repeats the archive's own name there is nothing to add (no climbing further).
                    decided.Add(archiveId);
                    if (!RepeatsOwnName(reader.GetString(2), name))
                        result[archiveId] = name.Trim();
                }
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>The context name of one archive, or null.</summary>
    public static async Task<string?> ResolveAsync(
        MangaPixerDbContext db, long archiveNodeId, IReadOnlyCollection<long> visibleLibraryIds, CancellationToken ct = default) =>
        (await ResolveAsync(db, [archiveNodeId], visibleLibraryIds, ct)).GetValueOrDefault(archiveNodeId);

    /// <summary>True when an ancestor folder's name can be the context line: not empty, not a plain unit / kind folder and not a category folder.</summary>
    internal static bool IsContextName(string? folderName) =>
        !string.IsNullOrWhiteSpace(folderName) && !AutoMatchText.IsUnitFolderName(folderName) && !AutoMatchText.IsCategoryFolderName(folderName);

    /// <summary>True when the context equals the archive's own name after the catalog's folding (extension removed, <see cref="TitleNormalizer.ScoringForm"/>).</summary>
    internal static bool RepeatsOwnName(string archiveName, string contextName)
    {
        var own = TitleNormalizer.ScoringForm(TitleNormalizer.StripArchiveExtension(archiveName));
        return own.Length > 0 && own == TitleNormalizer.ScoringForm(contextName);
    }

    /// <summary>The statement: the folders above each archive (nearest first), archives only, start nodes in the visible libraries.</summary>
    internal static string StatementFor(string archiveIds, string libraryIds) => $"""
        WITH RECURSIVE up(ArchiveId, NodeId, ParentId, Depth) AS (
            SELECT Id, Id, ParentId, 0 FROM catalog_nodes
            WHERE Id IN ({archiveIds}) AND Kind = {(int)CatalogNodeKind.Archive} AND LibraryId IN ({libraryIds})
            UNION ALL
            SELECT u.ArchiveId, p.Id, p.ParentId, u.Depth + 1
            FROM up u
            JOIN catalog_nodes p ON p.Id = u.ParentId
            WHERE u.Depth < {SeriesInfoResolver.MaxWalkDepth}
        )
        SELECT u.ArchiveId, n.DisplayName, a.DisplayName
        FROM up u
        JOIN catalog_nodes n ON n.Id = u.NodeId
        JOIN catalog_nodes a ON a.Id = u.ArchiveId
        WHERE u.Depth > 0 AND n.Kind = {(int)CatalogNodeKind.Folder}
        ORDER BY u.ArchiveId, u.Depth;
        """;
}
