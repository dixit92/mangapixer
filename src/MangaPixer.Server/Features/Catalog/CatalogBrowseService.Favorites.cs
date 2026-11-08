namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
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

    /// <summary>The Favorites view without a backward position (1.50.0's signature).</summary>
    public Task<PageResponse<CatalogNodeDto>> ListFavoritesAsync(
        long userId,
        FavoritesQuery query,
        string? cursor,
        int pageSize,
        bool incognito,
        CancellationToken ct)
        => ListFavoritesAsync(userId, query, cursor, before: null, pageSize, incognito, ct);

    /// <summary>
    /// 1.50.0 lane F - the Favorites view: <paramref name="query"/>'s sort, read state, kind and library filters (owner decisions 6-9).
    /// The default query is the listing above, unchanged (the home Favorites row relies on it). The library filter narrows the visible
    /// libraries (an unknown or not-visible library lists nothing); the kind and read-state filters judge each ENTRY after grouping, so a
    /// stack counts as archives and is judged by its whole folder's rollup (as its badge, decision 8) - and all of them run before the
    /// page is cut, so <c>TotalCount</c>, <c>HasMore</c> and every cursor stay right. One cursor prefix per sort (see
    /// <c>EncodeFavoriteCursor</c>); a cursor of another sort reads as "first page".
    /// 1.51.0 lane F: <paramref name="before"/> (a <c>prevCursor</c>) answers the page just ABOVE that position, and every window that
    /// does not start the list carries <c>PrevCursor</c> / <c>HasPrevious</c> (every sort); the Recently read sort; and
    /// <see cref="FavoritesQuery.GroupByLibrary"/> (library first, in the picker's order; <c>FavoriteGroups</c> = each listed library's
    /// count).
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> ListFavoritesAsync(
        long userId,
        FavoritesQuery query,
        string? cursor,
        string? before,
        int pageSize,
        bool incognito,
        CancellationToken ct)
    {
        if (pageSize < 1)
            pageSize = 50;

        var list = await LoadFavoriteEntriesAsync(userId, query, incognito, ct);
        if (list.VisibleLibs.Count == 0)
        {
            return new PageResponse<CatalogNodeDto>
            {
                Items = [],
                TotalCount = 0,
                NextCursor = null,
                HasMore = false,
                FavoriteGroups = list.Groups,
            };
        }
        var entries = list.Entries;
        var order = query.Order;
        var rank = list.LibraryRank;
        var starredNodeIds = list.StarredNodeIds;
        var visibleLibs = list.VisibleLibs;

        // The page is a window [start, end) of the whole sorted list (it is in memory). Forward: the entries strictly after the cursor's
        // position (keyset on the sort's position; recently favorited: the representative favorite's (FavoritedAt desc, favorite Id desc),
        // a stack's newest member). 1.51.0 lane F, backward (`before`, browse's param): the page just above the position - so a jump that
        // lands mid-list pages up. A cursor of another sort or shape is ignored (forward: the first page; backward: no backward page).
        int start;
        int end;
        if (!string.IsNullOrEmpty(before) && TryDecodeFavoriteCursor(before, order, rank, out var above))
        {
            end = CountBefore(entries, order, above, rank);
            start = Math.Max(0, end - pageSize);
        }
        else
        {
            start = !string.IsNullOrEmpty(cursor) && TryDecodeFavoriteCursor(cursor, order, rank, out var position)
                ? CountBefore(entries, order, position, rank, inclusive: true)
                : 0;
            end = Math.Min(entries.Count, start + pageSize);
        }
        var pageEntries = entries.GetRange(start, end - start);
        var hasMore = end < entries.Count && end > start;

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
                // 1.52.0 lane R: the viewer's own rating (a stack's card is its folder's node, so it carries the folder's rating).
                MyRating = _db.UserRatings.Where(r => r.UserId == userId && r.CatalogNodeId == n.Id).Select(r => (int?)r.Score).FirstOrDefault(),
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
            MyRating = x.Row.MyRating,
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

        // 1.50.0 (lane W's helper): an archive card's context line - the folder it belongs to (its nearest non-kind ancestor), one batched
        // upward walk for the page. Archive entries only: a stack is its folder's card, and a folder needs no context.
        var archiveRows = rows.Where(x => x.Entry.StackCount is null && x.Row.Kind == (int)CatalogNodeKind.Archive)
            .Select(x => x.Row.InternalId).ToList();
        if (archiveRows.Count > 0)
        {
            var contexts = await ArchiveContextNames.ResolveAsync(_db, archiveRows, visibleLibs, ct);
            if (contexts.Count > 0)
            {
                var contextByPublicId = rowsById.Values.Where(r => contexts.ContainsKey(r.InternalId))
                    .ToDictionary(r => r.Id, r => contexts[r.InternalId], StringComparer.Ordinal);
                nodes = nodes.Select(n => n.Kind == CatalogNodeKind.Archive && n.FavoriteStackCount is null
                    && contextByPublicId.TryGetValue(n.Id, out var context)
                        ? n with { ContextName = context }
                        : n).ToList();
            }
        }

        // Series information (1.27.0): the browse (i) flag, batched for the page. 1.48.0: ANCHORED to the series, as the home
        // Continue row - an archive favorite inside a linked series gets that series' (i); "Show series information" off still wins.
        nodes = await ApplyHasSeriesInfoByPublicIdAsync(nodes, anchored: true, ct);

        string? nextCursor = null;
        if (hasMore && pageEntries.Count > 0)
            nextCursor = EncodeFavoriteCursor(pageEntries[^1], order, rank);
        // 1.51.0 lane F: a window that does not start the list says so (browse's pair) - the client pages up with `before = prevCursor`.
        var prevCursor = start > 0 && pageEntries.Count > 0 ? EncodeFavoriteCursor(pageEntries[0], order, rank) : null;

        return new PageResponse<CatalogNodeDto>
        {
            Items = nodes,
            TotalCount = entries.Count,
            NextCursor = nextCursor,
            HasMore = hasMore,
            PrevCursor = prevCursor,
            HasPrevious = prevCursor is not null,
            FavoriteGroups = list.Groups,
        };
    }

    /// <summary>
    /// 1.51.0 lane F (owner decisions 2 and 8): the jump index of the Favorites list under the name sort in <paramref name="query"/>'s
    /// direction, over the SAME filtered list the listing pages (read state, kind, library; visibility through the same chokepoint) - see
    /// <see cref="FavoriteJumpIndex"/>. Each bucket's cursor is a normal <c>fn:</c> favorites cursor (the entry just before the bucket's first
    /// one; null for the first bucket) and its <c>FirstNodeId</c> that first entry's node (a stack's: its folder). Empty while grouped by
    /// library (the letters would repeat per library). Two queries: the key query and the bucket-first entries' public ids.
    /// </summary>
    public async Task<FavoritesJumpIndexDto> GetFavoritesJumpIndexAsync(long userId, FavoritesQuery query, bool incognito, CancellationToken ct)
    {
        var nameQuery = query with { Sort = "name" };
        var direction = nameQuery.Order == FavoriteStacks.Order.NameDescending ? "desc" : "asc";
        if (query.GroupByLibrary)
            return new FavoritesJumpIndexDto { Buckets = [], Direction = direction };

        var list = await LoadFavoriteEntriesAsync(userId, nameQuery, incognito, ct);
        var entries = list.Entries;
        if (entries.Count == 0)
            return new FavoritesJumpIndexDto { Buckets = [], Direction = direction };

        var spans = FavoriteJumpIndex.Build(entries
            .Select(e => new FavoriteJumpIndex.Row(e.SortKey, e.Kind == (int)CatalogNodeKind.Folder)).ToList());
        var firstIds = spans.Select(sp => entries[sp.Start].NodeId).Distinct().ToList();
        var publicIds = await _db.CatalogNodes.Where(n => firstIds.Contains(n.Id))
            .Select(n => new { n.Id, n.PublicId })
            .ToDictionaryAsync(n => n.Id, n => n.PublicId, ct);
        var order = nameQuery.Order;
        var buckets = spans.Select(sp => new JumpIndexBucketDto
        {
            Label = sp.Label,
            Count = sp.Count,
            FirstCursor = sp.Start > 0 ? EncodeFavoriteCursor(entries[sp.Start - 1], order) : null,
            FirstNodeId = publicIds.GetValueOrDefault(entries[sp.Start].NodeId),
            Group = sp.Group,
        }).ToList();
        return new FavoritesJumpIndexDto { Buckets = buckets, Direction = direction };
    }

    /// <summary>How many of the sorted <paramref name="entries"/> come before <paramref name="position"/> (with
    /// <paramref name="inclusive"/>: at or before it - an entry equal to the position is the one the cursor was taken from).</summary>
    private static int CountBefore(List<FavoriteStacks.Entry> entries, FavoriteStacks.Order order, FavoriteStacks.Entry position,
        IReadOnlyDictionary<long, int>? rank, bool inclusive = false)
    {
        // Binary search on the total order: the first index whose entry is after (or, not inclusive, at or after) the position.
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            var c = FavoriteStacks.Compare(entries[mid], position, order, rank);
            if (c < 0 || (inclusive && c == 0))
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// The viewer's visible favorites as sorted entries (1.51.0 lane F: shared by the listing and its jump index). Visibility through the
    /// SAME chokepoint as browse (grants, Private under Incognito) and the library filter inside it; the kind and read-state filters judge
    /// each ENTRY after grouping. Grouped by library (<see cref="FavoritesQuery.GroupByLibrary"/>): the order is (library in the picker's
    /// order - display name case-insensitive, then id), then the sort, and <c>Groups</c> carries each listed library's entry count.
    /// </summary>
    private async Task<FavoriteList> LoadFavoriteEntriesAsync(long userId, FavoritesQuery query, bool incognito, CancellationToken ct)
    {
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
            return query.GroupByLibrary
                ? new FavoriteList([], [], [], new Dictionary<long, int>(), [])
                : new FavoriteList([], [], [], null, null);
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

        // Recently read (1.51.0 lane F, owner decision 2): browse's recency - the viewer's newest progress or read mark on the node and,
        // for a starred folder, its whole subtree - one recursive query over the starred nodes. Only for this sort.
        var recency = order == FavoriteStacks.Order.RecentlyRead && keys.Count > 0
            ? await ComputeRecentlyReadRecencyAsync(keys.Select(k => k.NodeId).Distinct().ToList(), userId, ct)
            : null;
        var entries = FavoriteStacks.Group(
            keys.Select(k => new FavoriteStacks.Key(k.FavoriteId, k.CreatedAt, k.NodeId, k.ParentId, k.Kind,
                k.SortKey, k.ParentSortKey, k.AddedAt, k.LatestDescendantAddedAt, k.LibraryId,
                recency is not null && recency.TryGetValue(k.NodeId, out var stamp) ? stamp : null)),
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

        // 1.52.0 lane R: the viewer's own rating of each entry's node (a stack's: its folder's) - for the My rating sort and the Rated /
        // Not rated filter only; one query over the entries' nodes.
        if ((order == FavoriteStacks.Order.MyRating || query.Rating != FavoriteRatingFilter.All) && entries.Count > 0)
        {
            var entryNodeIds = entries.Select(e => e.NodeId).Distinct().ToList();
            var scores = await _db.UserRatings.Where(r => r.UserId == userId && entryNodeIds.Contains(r.CatalogNodeId))
                .ToDictionaryAsync(r => r.CatalogNodeId, r => r.Score, ct);
            entries = entries.Select(e => scores.TryGetValue(e.NodeId, out var score) ? e with { Rating = score } : e).ToList();
            if (query.Rating != FavoriteRatingFilter.All)
                entries = entries.Where(e => (e.Rating is not null) == (query.Rating == FavoriteRatingFilter.Rated)).ToList();
            if (order == FavoriteStacks.Order.MyRating)
                FavoriteStacks.Sort(entries, order);
        }

        if (!query.GroupByLibrary)
            return new FavoriteList(entries, visibleLibs, starredNodeIds, null, null);

        // Group by library (1.51.0 lane F, owner addition): the picker's order over the visible libraries (the access chokepoint above
        // is unchanged - a library the viewer cannot see now is never a group, and a cursor naming one is foreign). The counts come from
        // the filtered list, no further query per group; a library that lists nothing has no group.
        var libraries = await _db.Libraries.Where(l => visibleLibs.Contains(l.Id))
            .Select(l => new { l.Id, l.PublicId, l.DisplayName })
            .ToListAsync(ct);
        var ordered = libraries.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id).ToList();
        var rank = ordered.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
        FavoriteStacks.Sort(entries, order, rank);
        var counts = entries.GroupBy(e => e.LibraryId).ToDictionary(g => g.Key, g => g.Count());
        var groups = ordered.Where(l => counts.ContainsKey(l.Id)).Select(l => new FavoriteLibraryGroupDto
        {
            LibraryId = l.PublicId,
            Name = l.DisplayName,
            Count = counts[l.Id],
        }).ToList();
        return new FavoriteList(entries, visibleLibs, starredNodeIds, rank, groups);
    }

    /// <summary>The favorites list of one query (1.51.0 lane F): sorted entries, the visible libraries, the starred node ids, and - grouped
    /// by library - each visible library's rank and the listed libraries' groups (null ungrouped).</summary>
    private sealed record FavoriteList(
        List<FavoriteStacks.Entry> Entries,
        IReadOnlyList<long> VisibleLibs,
        HashSet<long> StarredNodeIds,
        IReadOnlyDictionary<long, int>? LibraryRank,
        IReadOnlyList<FavoriteLibraryGroupDto>? Groups);

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

        /// <summary>1.52.0 lane R: the viewer's own rating of the node.</summary>
        public int? MyRating { get; init; }
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
    /// updated <c>fu:</c> in the same shape; 1.51.0 recently read <c>fr:{stamp|-}:...</c> in the same shape. The sort key comes last
    /// because it may itself contain ':'. 1.51.0, grouped by library (<paramref name="libraryRank"/> set): <c>fl:{libraryId}:</c> in
    /// front of the sort's own cursor, so a grouped and an ungrouped cursor are foreign to each other.
    /// </summary>
    private static string EncodeFavoriteCursor(FavoriteStacks.Entry last, FavoriteStacks.Order order, IReadOnlyDictionary<long, int>? libraryRank = null)
    {
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Date(long? ticks) => ticks is long t ? N(t) : "-";
        var inner = order switch
        {
            FavoriteStacks.Order.Favorited => $"f:{N(last.RepresentativeTicks)}:{N(last.RepresentativeId)}",
            FavoriteStacks.Order.NameAscending or FavoriteStacks.Order.NameDescending =>
                $"fn:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            FavoriteStacks.Order.RecentlyAdded => $"fa:{Date(last.AddedTicks)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            FavoriteStacks.Order.RecentlyRead => $"fr:{Date(last.ReadStamp)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            FavoriteStacks.Order.MyRating => $"fm:{Date(last.Rating)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
            _ => $"fu:{Date(last.UpdatedTicks)}:{N(last.Discriminator)}:{N(last.NodeId)}:{last.SortKey}",
        };
        return libraryRank is null ? inner : $"{GroupedCursorPrefix}{N(last.LibraryId)}:{inner}";
    }

    /// <summary>The prefix of a cursor of the list grouped by library (1.51.0 lane F).</summary>
    private const string GroupedCursorPrefix = "fl:";

    /// <summary>
    /// 1.51.0 lane F: decodes a favorites cursor of <paramref name="order"/>, grouped by library when <paramref name="libraryRank"/> is set
    /// (<c>fl:{libraryId}:{the sort's cursor}</c>, the library one of the viewer's visible ones - any other is foreign). False for a
    /// malformed value or a cursor of another sort or grouping, which the caller reads as "first page".
    /// </summary>
    private static bool TryDecodeFavoriteCursor(string cursor, FavoriteStacks.Order order, IReadOnlyDictionary<long, int>? libraryRank,
        out FavoriteStacks.Entry position)
    {
        position = default;
        if (libraryRank is null)
            return TryDecodeFavoriteCursor(cursor, order, out position);
        if (!cursor.StartsWith(GroupedCursorPrefix, StringComparison.Ordinal))
            return false;
        var rest = cursor[GroupedCursorPrefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0
            || !long.TryParse(rest.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var libraryId)
            || !libraryRank.ContainsKey(libraryId)
            || !TryDecodeFavoriteCursor(rest[(colon + 1)..], order, out var inner))
            return false;
        position = inner with { LibraryId = libraryId };
        return true;
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
            FavoriteStacks.Order.RecentlyRead => "fr:",
            FavoriteStacks.Order.MyRating => "fm:",
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
        if (prefix == "fm:" && ticks is < UserRatingEntity.MinScore or > UserRatingEntity.MaxScore)
            return false;
        position = new FavoriteStacks.Entry(nodeId, discriminator == 1 ? FavoriteStacks.MinimumStackSize : null, 0, 0, parts[i + 2],
            AddedTicks: ticks, UpdatedTicks: ticks, ReadStamp: ticks, Rating: prefix == "fm:" ? (int?)ticks : null);
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
/// <c>recentlyUpdated</c>; 1.51.0: <c>recentlyRead</c>), its direction (name only; the date sorts are newest first, as browse's), and the
/// transient filters - read state, kind and one library (its public id). 1.51.0: <see cref="GroupByLibrary"/> (<c>group=library</c>).
/// <see cref="Default"/> is the listing as it was before 1.50.0.
/// </summary>
public sealed record FavoritesQuery(
    string Sort = "favorited",
    SortDirection Direction = SortDirection.Ascending,
    BrowseReadStateFilter ReadState = BrowseReadStateFilter.All,
    FavoriteKindFilter Kind = FavoriteKindFilter.All,
    string? LibraryId = null,
    bool GroupByLibrary = false,
    FavoriteRatingFilter Rating = FavoriteRatingFilter.All)
{
    public const string FavoritedSort = "favorited";

    /// <summary>1.51.0 lane F: the wire value of the Recently read sort.</summary>
    public const string RecentlyReadSort = "recentlyRead";

    /// <summary>1.52.0 lane R: the wire value of the My rating sort.</summary>
    public const string MyRatingSort = CatalogBrowseService.MyRatingSort;

    /// <summary>1.51.0 lane F: the wire value of <c>group=library</c>.</summary>
    public const string LibraryGroup = "library";

    /// <summary>Recently favorited, no filter.</summary>
    public static FavoritesQuery Default { get; } = new();

    /// <summary>The in-memory order <see cref="Sort"/> and <see cref="Direction"/> name; an unknown sort is recently favorited.</summary>
    internal FavoriteStacks.Order Order => Sort switch
    {
        "name" => Direction == SortDirection.Descending ? FavoriteStacks.Order.NameDescending : FavoriteStacks.Order.NameAscending,
        "recentlyAdded" => FavoriteStacks.Order.RecentlyAdded,
        "recentlyUpdated" => FavoriteStacks.Order.RecentlyUpdated,
        RecentlyReadSort => FavoriteStacks.Order.RecentlyRead,
        MyRatingSort => FavoriteStacks.Order.MyRating,
        _ => FavoriteStacks.Order.Favorited,
    };

    /// <summary>Parses the wire kind (<c>folders</c> / <c>archives</c>, case-insensitive); anything else is no filter.</summary>
    public static FavoriteKindFilter ParseKind(string? value) =>
        string.Equals(value, "folders", StringComparison.OrdinalIgnoreCase) ? FavoriteKindFilter.Folders
        : string.Equals(value, "archives", StringComparison.OrdinalIgnoreCase) ? FavoriteKindFilter.Archives
        : FavoriteKindFilter.All;

    /// <summary>1.52.0 lane R: parses the wire rating filter (<c>rated</c> / <c>unrated</c>, case-insensitive); anything else is no filter.</summary>
    public static FavoriteRatingFilter ParseRating(string? value) =>
        string.Equals(value, "rated", StringComparison.OrdinalIgnoreCase) ? FavoriteRatingFilter.Rated
        : string.Equals(value, "unrated", StringComparison.OrdinalIgnoreCase) ? FavoriteRatingFilter.NotRated
        : FavoriteRatingFilter.All;
}

/// <summary>
/// The Favorites "Rated / Not rated" filter (1.52.0 lane R): by the viewer's own rating of the entry's node (a stack: its folder's).
/// </summary>
public enum FavoriteRatingFilter
{
    All,
    Rated,
    NotRated,
}
