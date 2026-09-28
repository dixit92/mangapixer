namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Data;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The batched "has series information" flag behind the (i) and the hover summary
/// (extracted from <c>CatalogBrowseService</c> in 1.28.0). Two rules, both a fixed
/// number of queries whatever the card count, never a per-card resolver call:
/// <list type="bullet">
/// <item><description><b>Own</b> (browse, Search, Favorites): a node's own confirmed / auto
/// web link, an archive's own parsed ComicInfo (current content version), or a folder with
/// parsed ComicInfo on its child archives (depth 1) or grandchild archives (depth 2).
/// Inherited links and "Don't match" never count.</description></item>
/// <item><description><b>Anchored</b> (Home, 1.28.0): a folder uses the own rule; an ARCHIVE
/// follows <see cref="SeriesInfoResolver"/> - the nearest link walking self -&gt; ancestors
/// (bounded <see cref="SeriesInfoResolver.MaxWalkDepth"/>, needs-review skipped, "Don't
/// match" stops it) or its own parsed ComicInfo. So an archive inside a linked series folder
/// shows the series' (i), exactly what <c>GET /nodes/{id}/series-info</c> returns for it.</description></item>
/// </list>
/// The public-id variants apply "Show series information" (globally and per node's library).
/// </summary>
public sealed class SeriesInfoFlagService
{
    private readonly MangaPixerDbContext _db;

    public SeriesInfoFlagService(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The public ids among <paramref name="publicIds"/> with their OWN series information,
    /// in libraries where "Show series information" is on. One settings check, one id lookup,
    /// then <see cref="NodesWithOwnSeriesInfoAsync"/>.
    /// </summary>
    public Task<HashSet<string>> WithOwnSeriesInfoAsync(IEnumerable<string> publicIds, CancellationToken ct = default) =>
        ByPublicIdAsync(publicIds, anchored: false, ct);

    /// <summary>
    /// The public ids among <paramref name="publicIds"/> whose (i) has something to show under
    /// the ANCHOR rule (see the type summary), in libraries where "Show series information" is
    /// on. Folders: the own rule. Archives: nearest ancestor link or own ComicInfo.
    /// </summary>
    public Task<HashSet<string>> WithAnchoredSeriesInfoAsync(IEnumerable<string> publicIds, CancellationToken ct = default) =>
        ByPublicIdAsync(publicIds, anchored: true, ct);

    private async Task<HashSet<string>> ByPublicIdAsync(IEnumerable<string> publicIds, bool anchored, CancellationToken ct)
    {
        var ids = publicIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var globallyHidden = await _db.AppSettings
            .AnyAsync(s => s.Id == AppSettingsEntity.SingletonId && s.MetadataSeriesInfoHidden, ct);
        if (globallyHidden)
            return new HashSet<string>(StringComparer.Ordinal);

        var rows = await _db.CatalogNodes
            .Where(n => ids.Contains(n.PublicId) && !n.Library!.MetadataSeriesInfoHidden)
            .Select(n => new { n.Id, n.PublicId, n.Kind })
            .ToListAsync(ct);
        if (rows.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var nodes = rows.Select(r => (r.Id, r.Kind)).ToList();
        var has = anchored
            ? await NodesWithAnchoredSeriesInfoAsync(nodes, ct)
            : await NodesWithOwnSeriesInfoAsync(nodes, ct);

        return rows.Where(r => has.Contains(r.Id)).Select(r => r.PublicId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The internal ids among <paramref name="nodes"/> that have their OWN series
    /// information (the own rule above); the caller has already applied "Show series
    /// information". At most four queries whatever the count.
    /// </summary>
    public async Task<HashSet<long>> NodesWithOwnSeriesInfoAsync(
        IReadOnlyList<(long InternalId, int Kind)> nodes, CancellationToken ct = default)
    {
        var pageIds = nodes.Select(r => r.InternalId).ToList();
        var has = (await _db.NodeSeriesLinks
            .Where(l => pageIds.Contains(l.NodeId) && l.RecordId != null
                && (l.State == (int)SeriesLinkState.Confirmed || l.State == (int)SeriesLinkState.Auto))
            .Select(l => l.NodeId)
            .ToListAsync(ct)).ToHashSet();

        var archiveIds = nodes.Where(r => r.Kind == (int)CatalogNodeKind.Archive).Select(r => r.InternalId).ToList();
        if (archiveIds.Count > 0)
            has.UnionWith(await ArchivesWithOwnComicInfoAsync(archiveIds, ct));

        var folderIds = nodes.Where(r => r.Kind == (int)CatalogNodeKind.Folder).Select(r => r.InternalId).ToList();
        if (folderIds.Count > 0)
        {
            var depth1 = await (
                from n in _db.CatalogNodes
                join a in _db.ArchiveItems on n.Id equals a.NodeId
                join e in _db.EmbeddedMetadata on n.Id equals e.NodeId
                where n.ParentId != null && folderIds.Contains(n.ParentId.Value)
                    && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned
                    && e.State == 1 && e.ContentVersion == a.ContentVersion
                select n.ParentId!.Value).Distinct().ToListAsync(ct);
            has.UnionWith(depth1);

            var remaining = folderIds.Where(id => !has.Contains(id)).ToList();
            if (remaining.Count > 0)
            {
                has.UnionWith(await (
                    from child in _db.CatalogNodes
                    join n in _db.CatalogNodes on child.Id equals n.ParentId
                    join a in _db.ArchiveItems on n.Id equals a.NodeId
                    join e in _db.EmbeddedMetadata on n.Id equals e.NodeId
                    where child.ParentId != null && remaining.Contains(child.ParentId.Value)
                        && child.Kind == (int)CatalogNodeKind.Folder && child.Availability != (int)CatalogNodeAvailability.Tombstoned
                        && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != (int)CatalogNodeAvailability.Tombstoned
                        && e.State == 1 && e.ContentVersion == a.ContentVersion
                    select child.ParentId!.Value).Distinct().ToListAsync(ct));
            }
        }

        return has;
    }

    /// <summary>
    /// The internal ids among <paramref name="nodes"/> with series information under the
    /// anchor rule; the caller has already applied "Show series information". Folders go
    /// through <see cref="NodesWithOwnSeriesInfoAsync"/>; archives cost two more queries
    /// (one recursive ancestor walk for the nearest link, one ComicInfo lookup).
    /// </summary>
    public async Task<HashSet<long>> NodesWithAnchoredSeriesInfoAsync(
        IReadOnlyList<(long InternalId, int Kind)> nodes, CancellationToken ct = default)
    {
        var folders = nodes.Where(r => r.Kind != (int)CatalogNodeKind.Archive).ToList();
        var has = folders.Count > 0 ? await NodesWithOwnSeriesInfoAsync(folders, ct) : new HashSet<long>();

        var archiveIds = nodes.Where(r => r.Kind == (int)CatalogNodeKind.Archive).Select(r => r.InternalId).Distinct().ToList();
        if (archiveIds.Count == 0)
            return has;

        has.UnionWith(await ArchivesWithNearestWebLinkAsync(archiveIds, ct));
        var rest = archiveIds.Where(id => !has.Contains(id)).ToList();
        if (rest.Count > 0)
            has.UnionWith(await ArchivesWithOwnComicInfoAsync(rest, ct));
        return has;
    }

    private async Task<List<long>> ArchivesWithOwnComicInfoAsync(List<long> archiveIds, CancellationToken ct) =>
        await (
            from e in _db.EmbeddedMetadata
            join a in _db.ArchiveItems on e.NodeId equals a.NodeId
            where archiveIds.Contains(e.NodeId) && e.State == 1 && e.ContentVersion == a.ContentVersion
            select e.NodeId).ToListAsync(ct);

    /// <summary>
    /// The resolver's link walk, batched: for each start node, the nearest link row on
    /// self -&gt; ancestors (at most <see cref="SeriesInfoResolver.MaxWalkDepth"/> levels up,
    /// needs-review rows skipped) and keep the node when that row is a confirmed / auto link
    /// with a record. "Don't match" is the nearest row, so it stops inheritance here too.
    /// One recursive CTE.
    /// </summary>
    private async Task<HashSet<long>> ArchivesWithNearestWebLinkAsync(List<long> nodeIds, CancellationToken ct)
    {
        var result = new HashSet<long>();
        var ids = string.Join(",", nodeIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            // State: 0 Confirmed, 1 Auto, 2 NeedsReview (skipped), 3 DontMatch.
            command.CommandText = $"""
                WITH RECURSIVE up(StartId, NodeId, ParentId, Depth) AS (
                    SELECT Id, Id, ParentId, 0 FROM catalog_nodes WHERE Id IN ({ids})
                    UNION ALL
                    SELECT u.StartId, p.Id, p.ParentId, u.Depth + 1
                    FROM up u
                    JOIN catalog_nodes p ON p.Id = u.ParentId
                    WHERE u.Depth < {SeriesInfoResolver.MaxWalkDepth}
                ),
                ranked AS (
                    SELECT u.StartId, l.State, l.RecordId,
                           ROW_NUMBER() OVER (PARTITION BY u.StartId ORDER BY u.Depth) AS rn
                    FROM up u
                    JOIN node_series_links l ON l.NodeId = u.NodeId AND l.State != 2
                )
                SELECT StartId FROM ranked
                WHERE rn = 1 AND State IN (0, 1) AND RecordId IS NOT NULL;
                """;

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(reader.GetInt64(0));
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }
}
