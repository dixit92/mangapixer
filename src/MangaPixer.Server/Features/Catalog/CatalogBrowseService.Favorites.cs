namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
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
    public Task<PageResponse<CatalogNodeDto>> ListFavoritesAsync(
        long userId,
        string? cursor = null,
        int pageSize = 50,
        bool incognito = false,
        CancellationToken ct = default)
        => ListFavoritesAsync(userId, FavoritesQuery.Default, cursor, pageSize, incognito, ct);

    /// <summary>
    /// 1.50.0 lane F - the Favorites view: <paramref name="query"/>'s sort, read state, kind and library filters (owner decisions 6-9).
    /// The default query is the listing above, unchanged (the home Favorites row relies on it). The library filter narrows the visible
    /// libraries (an unknown or not-visible library lists nothing); the kind and read-state filters judge each ENTRY after grouping, so a
    /// stack counts as archives and is judged by its whole folder's rollup (as its badge, decision 8) - and all of them run before the
    /// page is cut, so <c>TotalCount</c>, <c>HasMore</c> and every cursor stay right. One cursor prefix per sort (see
    /// <c>EncodeFavoriteCursor</c>); a cursor of another sort reads as "first page".
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> ListFavoritesAsync(
        long userId,
        FavoritesQuery query,
        string? cursor,
        int pageSize,
        bool incognito,
        CancellationToken ct)
    {
        if (pageSize < 1)
            pageSize = 50;

        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (!string.IsNullOrEmpty(query.LibraryId) && visibleLibs.Count > 0)
        {
            // The library filter (1.50.0): only within what the viewer may see right now (grants, Private under Incognito) - a library
            // outside that set, or no library at all, answers like an empty one and names nothing.
            var wanted = query.LibraryId;
            var libraryId = await _db.Libraries.Where(l => l.PublicId == wanted).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            visibleLibs = libraryId is long id && visibleLibs.Contains(id) ? [id] : [];
        }
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
        // visible, non-tombstoned nodes), grouped into stacks in memory. 1.50.0: with what the sorts read (the node's and its parent's
        // sort keys and dates) - still the one key query.
        var keys = await _db.Favorites
            .Where(f => f.UserId == userId)
            .Join(_db.CatalogNodes, f => f.CatalogNodeId, n => n.Id, (f, n) => new { f, n })
            .Where(x => visibleLibs.Contains(x.n.LibraryId))
            .Where(x => x.n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(x => new
            {
                FavoriteId = x.f.Id,
                x.f.CreatedAt,
                NodeId = x.n.Id,
                x.n.ParentId,
                x.n.Kind,
                x.n.SortKey,
                ParentSortKey = x.n.Parent != null ? x.n.Parent.SortKey : "",
                AddedAt = x.n.CreatedAt,
                x.n.LatestDescendantAddedAt,
                x.n.LibraryId,
            })
            .ToListAsync(ct);
        var order = query.Order;
        var entries = FavoriteStacks.Group(
            keys.Select(k => new FavoriteStacks.Key(k.FavoriteId, k.CreatedAt, k.NodeId, k.ParentId, k.Kind,
                k.SortKey, k.ParentSortKey, k.AddedAt, k.LatestDescendantAddedAt, k.LibraryId)),
            order);
        var starredNodeIds = keys.Select(k => k.NodeId).ToHashSet();

        // Kind (1.50.0): folders = starred folders; archives = starred archives AND stacks (a stack stands for starred archives).
        if (query.Kind != FavoriteKindFilter.All)
        {
            var folders = query.Kind == FavoriteKindFilter.Folders;
            entries = entries.Where(e => folders == (!e.IsStack && e.Kind == (int)CatalogNodeKind.Folder)).ToList();
        }

        // Read state (1.50.0): judged per entry before the page is cut, as browse judges a level.
        if (query.ReadState != BrowseReadStateFilter.All && entries.Count > 0)
            entries = await FilterFavoritesByReadStateAsync(entries, userId, query.ReadState, ct);

        // Keyset on the sort's position (recently favorited: the representative favorite's (FavoritedAt desc, favorite Id desc), a
        // stack's newest member). The cursor carries the last item's position so paging is stable through ties.
        IEnumerable<FavoriteStacks.Entry> remaining = entries;
        if (!string.IsNullOrEmpty(cursor) && TryDecodeFavoriteCursor(cursor, order, out var position))
            remaining = FavoriteStacks.After(entries, order, position);

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

        // Covers - the same resolver as browse/search. Folders (single starred folders AND stacks) and, since 1.50.0, archives too
        // (a favorite archive's card and list row read coverUrl, as browse's; the web no longer builds the item cover URL).
        nodes = await ApplyCoversAsync(nodes,
            rowsById.Values.Select(r => new CoverTarget(r.InternalId, r.Id, r.Kind == (int)CatalogNodeKind.Folder)), ct);

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
            nextCursor = EncodeFavoriteCursor(pageEntries[^1], order);

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
    /// 1.50.0 lane F: the read-state filter over the favorites' ENTRIES, as browse judges a level - an archive by the viewer's sticky read
    /// mark and progress (Read = marked; Reading = not marked, in progress; Unread = neither), a starred folder and a stack by the WHOLE
    /// folder's rollup (<see cref="ResolveFolderReadRollupsAsync"/>, the badge's; Unread keeps an empty folder). Three queries for the
    /// whole list: the marks and the in-progress rows of the viewer's starred nodes, and one rollup walk over the starred folders and the
    /// stacks' folders.
    /// </summary>
    private async Task<List<FavoriteStacks.Entry>> FilterFavoritesByReadStateAsync(
        List<FavoriteStacks.Entry> entries, long userId, BrowseReadStateFilter readState, CancellationToken ct)
    {
        var hasArchives = entries.Any(e => !e.IsStack && e.Kind == (int)CatalogNodeKind.Archive);
        HashSet<long> read = [];
        HashSet<long> inProgress = [];
        if (hasArchives)
        {
            read = (await _db.ReadMarks
                .Where(m => m.UserId == userId && _db.Favorites.Any(f => f.UserId == userId && f.CatalogNodeId == m.ItemId))
                .Select(m => m.ItemId)
                .ToListAsync(ct)).ToHashSet();
            inProgress = (await _db.ReadingProgress
                .Where(p => p.UserId == userId && p.State == (int)ReadingState.InProgress
                    && _db.Favorites.Any(f => f.UserId == userId && f.CatalogNodeId == p.ItemId))
                .Select(p => p.ItemId)
                .ToListAsync(ct)).ToHashSet();
        }

        var folderIds = entries.Where(e => e.Kind == (int)CatalogNodeKind.Folder).Select(e => e.NodeId).Distinct().ToList();
        var rollups = folderIds.Count > 0
            ? await ResolveFolderReadRollupsAsync(folderIds, userId, ct)
            : new Dictionary<long, FolderReadRollup>();

        return entries.Where(e =>
        {
            if (e.Kind == (int)CatalogNodeKind.Folder)
                return MatchesFolderReadState(rollups, e.NodeId, readState);
            var isRead = read.Contains(e.NodeId);
            var reading = !isRead && inProgress.Contains(e.NodeId);
            return readState switch
            {
                BrowseReadStateFilter.Read => isRead,
                BrowseReadStateFilter.Reading => reading,
                BrowseReadStateFilter.Unread => !isRead && !reading,
                _ => true,
            };
        }).ToList();
    }

    /// <summary>
    /// Encodes the keyset cursor of <paramref name="last"/> under <paramref name="order"/>. One prefix per sort, so a cursor of another
    /// sort is never misread: recently favorited <c>f:{utcTicks}:{favoriteId}</c> (unchanged since 1.21.0); name (both directions)
    /// <c>fn:{discriminator}:{nodeId}:{sortKey}</c>; recently added <c>fa:{ticks|-}:{discriminator}:{nodeId}:{sortKey}</c>; recently
    /// updated <c>fu:</c> in the same shape. The sort key comes last because it may itself contain ':'.
    /// </summary>
    private static string EncodeFavoriteCursor(FavoriteStacks.Entry last, FavoriteStacks.Order order)
    {
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Date(long? ticks) => ticks is long t ? N(t) : "-";
        return order switch
        {
            FavoriteStacks.Order.Favorited => $"f:{N(last.RepresentativeTicks)}:{N(last.RepresentativeId)}",
            FavoriteStacks.Order.NameAscending or FavoriteStacks.Order.NameDescending =>
                $"fn:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            FavoriteStacks.Order.RecentlyAdded => $"fa:{Date(last.AddedTicks)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            _ => $"fu:{Date(last.UpdatedTicks)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
        };
    }

    /// <summary>
    /// Decodes a favorites cursor of <paramref name="order"/> into the probe position <c>FavoriteStacks.After</c>
    /// compares with. Returns false for a malformed value or another sort's cursor, so a bad cursor degrades to "first page" rather
    /// than throwing.
    /// </summary>
    private static bool TryDecodeFavoriteCursor(string cursor, FavoriteStacks.Order order, out FavoriteStacks.Entry position)
    {
        position = default;
        if (order == FavoriteStacks.Order.Favorited)
        {
            if (!TryDecodeFavoriteCursor(cursor, out var favoritedAt, out var favoriteId))
                return false;
            position = new FavoriteStacks.Entry(0, null, favoritedAt.UtcTicks, favoriteId);
            return true;
        }

        var prefix = order switch
        {
            FavoriteStacks.Order.NameAscending or FavoriteStacks.Order.NameDescending => "fn:",
            FavoriteStacks.Order.RecentlyAdded => "fa:",
            _ => "fu:",
        };
        if (!cursor.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var dated = prefix != "fn:";
        var parts = cursor[prefix.Length..].Split(':', dated ? 4 : 3);
        if (parts.Length != (dated ? 4 : 3))
            return false;
        var i = 0;
        long? ticks = null;
        if (dated)
        {
            if (parts[0] != "-")
            {
                if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                    return false;
                ticks = t;
            }
            i = 1;
        }
        if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var discriminator)
            || discriminator is not (0 or 1)
            || !long.TryParse(parts[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodeId))
            return false;
        position = new FavoriteStacks.Entry(nodeId, discriminator == 1 ? FavoriteStacks.MinimumStackSize : null, 0, 0, parts[i + 2],
            AddedTicks: ticks, UpdatedTicks: ticks);
        return true;
    }

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

/// <summary>The Favorites kind filter (1.50.0 lane F, owner decision 7): a stack counts as archives.</summary>
public enum FavoriteKindFilter
{
    All,
    Folders,
    Archives,
}

/// <summary>
/// What the Favorites view asks for (1.50.0 lane F): the sort (<c>favorited</c> - the default -, <c>name</c>, <c>recentlyAdded</c>,
/// <c>recentlyUpdated</c>), its direction (name only; the date sorts are newest first, as browse's), and the transient filters - read
/// state, kind and one library (its public id). <see cref="Default"/> is the listing as it was before 1.50.0.
/// </summary>
public sealed record FavoritesQuery(
    string Sort = "favorited",
    SortDirection Direction = SortDirection.Ascending,
    BrowseReadStateFilter ReadState = BrowseReadStateFilter.All,
    FavoriteKindFilter Kind = FavoriteKindFilter.All,
    string? LibraryId = null)
{
    public const string FavoritedSort = "favorited";

    /// <summary>Recently favorited, no filter.</summary>
    public static FavoritesQuery Default { get; } = new();

    /// <summary>The in-memory order <see cref="Sort"/> and <see cref="Direction"/> name; an unknown sort is recently favorited.</summary>
    internal FavoriteStacks.Order Order => Sort switch
    {
        "name" => Direction == SortDirection.Descending ? FavoriteStacks.Order.NameDescending : FavoriteStacks.Order.NameAscending,
        "recentlyAdded" => FavoriteStacks.Order.RecentlyAdded,
        "recentlyUpdated" => FavoriteStacks.Order.RecentlyUpdated,
        _ => FavoriteStacks.Order.Favorited,
    };

    /// <summary>Parses the wire kind (<c>folders</c> / <c>archives</c>, case-insensitive); anything else is no filter.</summary>
    public static FavoriteKindFilter ParseKind(string? value) =>
        string.Equals(value, "folders", StringComparison.OrdinalIgnoreCase) ? FavoriteKindFilter.Folders
        : string.Equals(value, "archives", StringComparison.OrdinalIgnoreCase) ? FavoriteKindFilter.Archives
        : FavoriteKindFilter.All;
}
