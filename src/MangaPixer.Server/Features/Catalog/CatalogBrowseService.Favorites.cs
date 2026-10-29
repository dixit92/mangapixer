namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using Microsoft.EntityFrameworkCore;

// 1.50.0 lane F - the Favorites listing, moved unchanged out of CatalogBrowseService.cs into its own partial so the Favorites view
// (sorts, filters, server-side archive covers) is built here without touching the shared browse file.

public sealed partial class CatalogBrowseService
{
    /// <summary>
    /// Lists the current user's favorites (1.21.0), keyset-paged like browse and ordered
    /// recently-favorited (newest first). Visibility flows through the SAME
    /// <see cref="LibraryAuthorizationService.GetVisibleLibraryIdsAsync"/> chokepoint +
    /// X-Incognito signal as browse/search, so a favorite pointing into the viewer's own
    /// Private library is hidden while Incognito is on. Returns
    /// <see cref="CatalogNodeDto"/>s with folder covers and per-user read state resolved
    /// exactly as browse does. Tombstoned nodes are excluded.
    ///
    /// Stacking (1.27.0): starred archives that share a direct parent folder collapse
    /// into ONE item for that folder (<see cref="CatalogNodeDto.FavoriteStackCount"/> =
    /// how many), placed at its newest favorite (see <see cref="FavoriteStacks"/>). The
    /// grouping runs over the viewer's VISIBLE favorites only, so a count never reveals
    /// hidden content. <c>TotalCount</c> counts items (cards), not raw favorites. A
    /// single item carries <c>IsFavorite = true</c>; a stack carries the folder's own
    /// star. A fixed number of queries per page: keys, page nodes, covers, read state (1.48.0: and the folder read rollups).
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> ListFavoritesAsync(
        long userId,
        string? cursor = null,
        int pageSize = 50,
        bool incognito = false,
        CancellationToken ct = default)
    {
        if (pageSize < 1)
            pageSize = 50;

        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
        {
            return new PageResponse<CatalogNodeDto>
            {
                Items = [],
                TotalCount = 0,
                NextCursor = null,
                HasMore = false,
            };
        }

        // The viewer's visible favorites as narrow key rows (favorites joined to their
        // visible, non-tombstoned nodes), grouped into stacks in memory.
        var keys = await _db.Favorites
            .Where(f => f.UserId == userId)
            .Join(_db.CatalogNodes, f => f.CatalogNodeId, n => n.Id, (f, n) => new { f, n })
            .Where(x => visibleLibs.Contains(x.n.LibraryId))
            .Where(x => x.n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(x => new { FavoriteId = x.f.Id, x.f.CreatedAt, NodeId = x.n.Id, x.n.ParentId, x.n.Kind })
            .ToListAsync(ct);
        var entries = FavoriteStacks.Group(
            keys.Select(k => new FavoriteStacks.Key(k.FavoriteId, k.CreatedAt, k.NodeId, k.ParentId, k.Kind)));
        var starredNodeIds = keys.Select(k => k.NodeId).ToHashSet();

        // Keyset on each item's representative favorite: (FavoritedAt desc, favorite Id
        // desc), a stack's newest member. The cursor carries the last item's pair so
        // paging is stable even when several favorites share a timestamp.
        IEnumerable<FavoriteStacks.Entry> remaining = entries;
        if (!string.IsNullOrEmpty(cursor) && TryDecodeFavoriteCursor(cursor, out var curAt, out var curId))
            remaining = FavoriteStacks.After(entries, curAt.UtcTicks, curId);

        var pageEntries = remaining.Take(pageSize + 1).ToList();
        var hasMore = pageEntries.Count > pageSize;
        if (hasMore)
            pageEntries = pageEntries.Take(pageSize).ToList();

        var pageNodeIds = pageEntries.Select(e => e.NodeId).Distinct().ToList();
        var rowsById = await _db.CatalogNodes
            .Where(n => pageNodeIds.Contains(n.Id))
            .Select(n => new FavoriteRow
            {
                InternalId = n.Id,
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library!.PublicId,
                Kind = n.Kind,
                DisplayName = n.DisplayName,
                Availability = n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
            })
            .ToDictionaryAsync(r => r.InternalId, ct);

        // A page item whose node vanished between the two queries is skipped.
        var rows = pageEntries
            .Where(e => rowsById.ContainsKey(e.NodeId))
            .Select(e => (Entry: e, Row: rowsById[e.NodeId]))
            .ToList();

        var nodes = rows.Select(x => new CatalogNodeDto
        {
            Id = x.Row.Id,
            ParentId = x.Row.ParentId,
            LibraryId = x.Row.LibraryId,
            Kind = (CatalogNodeKind)x.Row.Kind,
            DisplayName = x.Row.DisplayName,
            Availability = (CatalogNodeAvailability)x.Row.Availability,
            PageCount = x.Row.PageCount,
            IsFavorite = x.Entry.StackCount is null || starredNodeIds.Contains(x.Entry.NodeId),
            FavoriteStackCount = x.Entry.StackCount,
        }).ToList();

        // Folder covers (single starred folders AND stacks) - the same resolver as browse/search.
        var folderRows = rowsById.Values.Where(r => r.Kind == (int)CatalogNodeKind.Folder).ToList();
        if (folderRows.Count > 0)
            nodes = await ApplyCoversAsync(nodes, folderRows.Select(r => new CoverTarget(r.InternalId, r.Id, true)), ct);

        // Per-user read state for archive favorites (sticky read-mark + progress),
        // mirroring the browse enrichment so the favorites view renders identical badges.
        var archiveIds = nodes.Where(n => n.Kind == CatalogNodeKind.Archive).Select(n => n.Id).ToList();
        if (archiveIds.Count > 0)
        {
            var readPublicIds = (await _db.ReadMarks
                .Where(m => m.UserId == userId)
                .Join(_db.CatalogNodes, m => m.ItemId, n => n.Id, (m, n) => n.PublicId)
                .Where(pid => archiveIds.Contains(pid))
                .ToListAsync(ct)).ToHashSet();

            var progressStates = await _db.ReadingProgress
                .Where(p => p.UserId == userId)
                .Join(_db.CatalogNodes, p => p.ItemId, n => n.Id, (p, n) => new { n.PublicId, p.State, p.Ordinal })
                .Where(x => archiveIds.Contains(x.PublicId))
                .ToDictionaryAsync(x => x.PublicId, x => new { x.State, x.Ordinal }, ct);

            if (readPublicIds.Count > 0 || progressStates.Count > 0)
            {
                nodes = nodes.Select(n =>
                {
                    if (n.Kind != CatalogNodeKind.Archive)
                        return n;
                    var isRead = readPublicIds.Contains(n.Id);
                    if (progressStates.TryGetValue(n.Id, out var p))
                        return n with { IsRead = isRead, ReadingState = (ReadingState)p.State, LastReadPage = p.Ordinal };
                    return isRead ? n with { IsRead = true } : n;
                }).ToList();
            }
        }

        // Folder read rollup (1.48.0): a favorited folder, and a stack (which stands for its parent folder), carry the same
        // Read / Reading badge as a browse card. One batched query for the page; a stack's rollup covers the WHOLE folder
        // (not only its starred members), exactly as browse. Only the viewer's own marks count.
        var folderInternalIdByPublicId = rowsById.Values
            .Where(r => r.Kind == (int)CatalogNodeKind.Folder)
            .ToDictionary(r => r.Id, r => r.InternalId, StringComparer.Ordinal);
        if (folderInternalIdByPublicId.Count > 0)
        {
            var rollups = await ResolveFolderReadRollupsAsync(folderInternalIdByPublicId.Values.ToList(), userId, ct);
            if (rollups.Count > 0)
            {
                nodes = nodes.Select(n => n.Kind == CatalogNodeKind.Folder
                    && folderInternalIdByPublicId.TryGetValue(n.Id, out var internalId)
                    && rollups.TryGetValue(internalId, out var rollup)
                        ? n with { ReadRollup = rollup }
                        : n).ToList();
            }
        }

        // Series information (1.27.0): the browse (i) flag, batched for the page. 1.48.0: ANCHORED to the series, as the home
        // Continue row - an archive favorite inside a linked series gets that series' (i); "Show series information" off still wins.
        nodes = await ApplyHasSeriesInfoByPublicIdAsync(nodes, anchored: true, ct);

        string? nextCursor = null;
        if (hasMore && pageEntries.Count > 0)
            nextCursor = EncodeFavoriteCursor(pageEntries[^1].RepresentativeTicks, pageEntries[^1].RepresentativeId);

        return new PageResponse<CatalogNodeDto>
        {
            Items = nodes,
            TotalCount = entries.Count,
            NextCursor = nextCursor,
            HasMore = hasMore,
        };
    }

    /// <summary>Intermediate favorites-list projection carrying the node DTO fields of one
    /// page item (a favorite's node, or a stack's folder).</summary>
    private sealed record FavoriteRow
    {
        public required long InternalId { get; init; }
        public required string Id { get; init; }
        public required string ParentId { get; init; }
        public required string LibraryId { get; init; }
        public required int Kind { get; init; }
        public required string DisplayName { get; init; }
        public required int Availability { get; init; }
        public int? PageCount { get; init; }
    }

    /// <summary>
    /// Encodes a recently-favorited keyset cursor as <c>f:{utcTicks}:{favoriteId}</c>.
    /// The <c>f:</c> prefix keeps it distinct from the name/recency browse cursors.
    /// </summary>
    private static string EncodeFavoriteCursor(long favoritedAtUtcTicks, long favoriteId)
        => $"f:{favoritedAtUtcTicks.ToString(CultureInfo.InvariantCulture)}:{favoriteId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Decodes an <c>f:{utcTicks}:{favoriteId}</c> favorites cursor. Returns false for any
    /// malformed value so a bad cursor degrades to "first page" rather than throwing.
    /// </summary>
    private static bool TryDecodeFavoriteCursor(string cursor, out DateTimeOffset favoritedAt, out long favoriteId)
    {
        favoritedAt = default;
        favoriteId = 0;
        if (!cursor.StartsWith("f:", StringComparison.Ordinal))
            return false;
        var parts = cursor.Split(':');
        if (parts.Length != 3
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out favoriteId))
            return false;
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            return false;
        favoritedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}
