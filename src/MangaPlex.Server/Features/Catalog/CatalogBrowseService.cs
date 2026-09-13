namespace com.lifepixer.mangaplex.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Catalog browse service. Provides paginated actual-tree browse, breadcrumbs,
/// neighbors, and search with authorization filtering.
///
/// Rules:
/// - Filter authorization in the SQL query BEFORE pagination/counting.
/// - Folders and archives appear together, folders first by default.
/// - Keyset pagination using persisted sort keys.
/// - Search uses FTS5 trigram with a literal query builder (no raw MATCH syntax).
/// - Neighbors are previous/next readable archives in the same parent folder.
/// - No source paths in any DTO.
/// </summary>
public sealed class CatalogBrowseService
{
    private readonly MangaPlexDbContext _db;
    private readonly LibraryAuthorizationService _auth;

    public CatalogBrowseService(MangaPlexDbContext db, LibraryAuthorizationService auth)
    {
        _db = db;
        _auth = auth;
    }

    /// <summary>
    /// Browses children of a parent node (or library root if parentId is null).
    /// Folders first, then archives, ordered by the chosen sort key.
    /// Authorization is filtered before pagination.
    ///
    /// Sort modes (1.2.0 follow-up; direction added 1.5.0 — every mode honors
    /// <paramref name="direction"/>, default Descending for recentlyAdded/recentlyRead and
    /// Ascending for name, matching pre-1.5.0 behavior):
    /// - name (default): order by SortKey; cursor = SortKey.
    /// - recentlyAdded: order by Kind, CreatedAt, Id; cursor encodes (Kind, CreatedAt, Id).
    /// - recentlyRead: pure recency — each node by its subtree's most recent read activity
    ///   (progress or read-mark), folders and archives interleaved; cursor is an offset.
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> BrowseAsync(
        long userId,
        long libraryId,
        long? parentId,
        string? cursor,
        int pageSize = 50,
        SortDirection? direction = null,
        string sort = "name",
        bool incognito = false,
        BrowseReadStateFilter readState = BrowseReadStateFilter.All,
        CancellationToken ct = default)
    {
        // Validate sort — unknown values fall back to "name" (tolerant, like the DTO).
        sort = sort switch
        {
            "recentlyAdded" or "recentlyRead" => sort,
            _ => "name",
        };

        // Default direction is sort-specific (1.5.0), so a caller that doesn't specify
        // one gets the pre-1.5.0 behavior unchanged: Name ascending, recentlyAdded/
        // recentlyRead descending (newest/most-recent first).
        var effectiveDirection = direction ?? (sort == "name" ? SortDirection.Ascending : SortDirection.Descending);

        // Authorization filter — applied before pagination.
        // Uses visible-ids (accessible minus Private) when incognito is active,
        // so a Private library's browse-root returns empty while direct item
        // URLs remain accessible (1.4.0).
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (!visibleLibs.Contains(libraryId))
        {
            return new PageResponse<CatalogNodeDto>
            {
                Items = [],
                TotalCount = 0,
                NextCursor = null,
                HasMore = false,
            };
        }

        // Base query — common filter, no cursor (authorization + library + parent + availability).
        var baseQuery = _db.CatalogNodes
            .Where(n => n.LibraryId == libraryId)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned);

        if (parentId is null)
            baseQuery = baseQuery.Where(n => n.ParentId == null);
        else
            baseQuery = baseQuery.Where(n => n.ParentId == parentId);

        // Read-state filter (1.10.0, additive) — restrict ARCHIVES to the chosen
        // per-user read state, applied to the base query BEFORE counting/pagination
        // (alongside the authorization filter) so it composes with the keyset paging +
        // infinite scroll and yields an accurate TotalCount. Semantics match the archive
        // cards and the folder rollup exactly: Read = a sticky read-mark exists; Reading
        // = no mark AND an in-progress ReadingProgress row; Unread = neither. FOLDERS
        // carry no per-item read signal, so they are always kept regardless of the
        // filter — hiding them would make the filter unable to reach archives nested in
        // subfolders, yet the finding requires it to work "at any folder level". Folders
        // therefore stay navigable while the archives listed honor the filter (a filter
        // over an all-folders level is simply a no-op, which is the intended behavior).
        switch (readState)
        {
            case BrowseReadStateFilter.Read:
                baseQuery = baseQuery.Where(n =>
                    n.Kind != (int)CatalogNodeKind.Archive
                    || _db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id));
                break;
            case BrowseReadStateFilter.Reading:
                baseQuery = baseQuery.Where(n =>
                    n.Kind != (int)CatalogNodeKind.Archive
                    || (!_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id)
                        && _db.ReadingProgress.Any(p =>
                            p.UserId == userId && p.ItemId == n.Id && p.State == (int)ReadingState.InProgress)));
                break;
            case BrowseReadStateFilter.Unread:
                baseQuery = baseQuery.Where(n =>
                    n.Kind != (int)CatalogNodeKind.Archive
                    || (!_db.ReadMarks.Any(m => m.UserId == userId && m.ItemId == n.Id)
                        && !_db.ReadingProgress.Any(p =>
                            p.UserId == userId && p.ItemId == n.Id && p.State == (int)ReadingState.InProgress)));
                break;
            case BrowseReadStateFilter.All:
            default:
                break;
        }

        // Total count from the base query (before cursor — fixes the decreasing-count bug
        // where the old code counted after the cursor filter).
        var totalCount = await baseQuery.CountAsync(ct);

        // Sort-specific query: ordering, keyset cursor filter, projection, and paging.
        List<BrowseRow> rows = sort switch
        {
            "recentlyAdded" => await QueryRecentlyAddedAsync(baseQuery, cursor, pageSize, effectiveDirection, ct),
            "recentlyRead" => await QueryRecentlyReadAsync(baseQuery, userId, cursor, pageSize, effectiveDirection, ct),
            _ => await QueryNameAsync(baseQuery, cursor, pageSize, effectiveDirection, ct),
        };

        // Check hasMore and trim to pageSize (the +1 was only to detect hasMore).
        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows = rows.Take(pageSize).ToList();

        // Convert to DTOs for enrichment.
        var nodes = rows.Select(ToDto).ToList();

        // For folders, resolve CoverUrl from the first descendant archive by SortKey
        // (D17). Recurses into subfolders so a folder containing only subfolders
        // still gets a cover (1.3.1 fix). Set-based via a recursive CTE modeled on
        // ReadingStateService.GetDescendantArchiveIdsAsync — avoids N+1 across the
        // folder page. SortKey ordering is ordinal (SQLite BINARY collation), matching
        // the EF LINQ OrderBy(n => n.SortKey) used elsewhere.
        var folderIds = nodes.Where(n => n.Kind == CatalogNodeKind.Folder).Select(n => n.Id).ToList();
        if (folderIds.Count > 0)
        {
            var folderRows = rows.Where(r => r.Kind == (int)CatalogNodeKind.Folder).ToList();
            var folderInternalIds = folderRows.Select(r => r.InternalId).ToList();
            var coversByInternalId = await ResolveFolderCoversAsync(folderInternalIds, ct);
            var coversByPublicId = folderRows
                .Where(r => coversByInternalId.ContainsKey(r.InternalId))
                .ToDictionary(r => r.Id, r => coversByInternalId[r.InternalId]);

            // Rebuild folder nodes with CoverUrl (init-only property)
            nodes = nodes.Select(n =>
            {
                if (n.Kind != CatalogNodeKind.Folder)
                    return n;
                if (coversByPublicId.TryGetValue(n.Id, out var coverPublicId))
                    return n with { CoverUrl = $"/api/v1/items/{coverPublicId}/cover" };
                return n;
            }).ToList();
        }

        // Populate ReaderDefault for folders that carry a global override (1.2.0).
        if (folderIds.Count > 0)
        {
            var folderReaderDefaults = await _db.FolderReaderDefaults
                .Where(f => f.Node != null && folderIds.Contains(f.Node.PublicId))
                .Select(f => new { PublicId = f.Node!.PublicId, f.ReaderMode })
                .ToDictionaryAsync(x => x.PublicId, x => x.ReaderMode, ct);

            if (folderReaderDefaults.Count > 0)
            {
                nodes = nodes.Select(n =>
                {
                    if (n.Kind == CatalogNodeKind.Folder && folderReaderDefaults.TryGetValue(n.Id, out var rm))
                        return n with { ReaderDefault = (ReaderMode)rm };
                    return n;
                }).ToList();
            }
        }

        // Surface per-user read state for archives (1.2.0): the sticky read-mark
        // (IsRead) plus the progress-derived ReadingState. Both are keyed by internal
        // node id, so we join back to catalog_nodes to map to the public ids the DTOs
        // carry. (Prior to this, ReadingState was never populated in browse at all.)
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

        // Derived folder read rollup (1.6.0): for every folder on the page, classify
        // its readable descendant archives as Read / Reading / Unread from the same
        // two signals the archive cards show (sticky read-mark, in-progress progress).
        // Set-based: ONE recursive CTE over all page folders (no per-folder walk) -
        // same shape as the cover and recency aggregates above. Folders with no
        // readable descendant archive get null (no badge), mirroring the cover rule.
        if (folderIds.Count > 0)
        {
            var folderRows = rows.Where(r => r.Kind == (int)CatalogNodeKind.Folder).ToList();
            var rollupsByInternalId = await ResolveFolderReadRollupsAsync(
                folderRows.Select(r => r.InternalId).ToList(), userId, ct);

            if (rollupsByInternalId.Count > 0)
            {
                var rollupsByPublicId = folderRows
                    .Where(r => rollupsByInternalId.ContainsKey(r.InternalId))
                    .ToDictionary(r => r.Id, r => rollupsByInternalId[r.InternalId]);

                nodes = nodes.Select(n =>
                {
                    if (n.Kind == CatalogNodeKind.Folder && rollupsByPublicId.TryGetValue(n.Id, out var rollup))
                        return n with { ReadRollup = rollup };
                    return n;
                }).ToList();
            }
        }

        // Compute next cursor from the last row on the current page. recentlyRead
        // paginates by offset (in-memory pure-recency order); the others use a keyset.
        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            nextCursor = sort == "recentlyRead"
                ? $"r:{RecentlyReadOffset(cursor) + rows.Count}"
                : EncodeCursor(sort, rows[^1]);
        }

        // Pinned "Continue" row (1.7.0): the folder's next-to-read descendant archive,
        // resolved set-based over the same recursive descendant walk the cover/recency
        // aggregates use. Surfaced above the sorted list; the list order is unchanged.
        var nextUnread = await ResolveNextUnreadAsync(libraryId, parentId, userId, ct);

        return new PageResponse<CatalogNodeDto>
        {
            Items = nodes,
            TotalCount = totalCount,
            NextCursor = nextCursor,
            HasMore = hasMore,
            NextUnread = nextUnread,
        };
    }

    // --- Sort-specific query helpers ---

    /// <summary>
    /// Name sort: order by SortKey (folders first via the SortKey prefix encoding).
    /// Cursor is the raw SortKey (backward compatible with pre-sort browse).
    /// </summary>
    private async Task<List<BrowseRow>> QueryNameAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        string? cursor,
        int pageSize,
        SortDirection direction,
        CancellationToken ct)
    {
        var query = baseQuery;

        // Cursor filter — only apply if the cursor is a raw SortKey (no sort prefix).
        // Cursors from other sorts ("a:..." or "r:...") are ignored (sort changed).
        if (!string.IsNullOrEmpty(cursor)
            && !cursor.StartsWith("a:", StringComparison.Ordinal)
            && !cursor.StartsWith("r:", StringComparison.Ordinal))
        {
            if (direction == SortDirection.Ascending)
                query = query.Where(n => string.Compare(n.SortKey, cursor) > 0);
            else
                query = query.Where(n => string.Compare(n.SortKey, cursor) < 0);
        }

        if (direction == SortDirection.Ascending)
            query = query.OrderBy(n => n.SortKey);
        else
            query = query.OrderByDescending(n => n.SortKey);

        return await query
            .Take(pageSize + 1)
            .Select(n => new BrowseRow
            {
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                Kind = n.Kind,
                DisplayName = n.DisplayName,
                Availability = n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                CoverUrl = n.Kind == 1 ? "/api/v1/items/" + n.PublicId + "/cover" : null,
                InternalId = n.Id,
                CreatedAt = n.CreatedAt,
                SortKey = n.SortKey,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Recently-added sort: folders first, then archives, each by CreatedAt/Id in the
    /// requested direction (default Descending = current pre-1.5.0 behavior: newest
    /// first). Ascending reverses the whole tuple, including the Kind grouping — the
    /// same full-reversal semantics as Name's OrderByDescending, so archives-oldest-first
    /// can precede folders, mirroring how Name-descending already lets archives sort
    /// before folders. Cursor encodes (Kind, CreatedAtBinary, InternalId); the comparison
    /// direction flips with <paramref name="direction"/> so cursor paging stays correct
    /// across the boundary regardless of which way the page is sorted.
    /// </summary>
    private async Task<List<BrowseRow>> QueryRecentlyAddedAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        string? cursor,
        int pageSize,
        SortDirection direction,
        CancellationToken ct)
    {
        IQueryable<CatalogNodeEntity> query = direction == SortDirection.Descending
            ? baseQuery.OrderBy(n => n.Kind).ThenByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            : baseQuery.OrderByDescending(n => n.Kind).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id);

        // Keyset cursor filter
        if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith("a:", StringComparison.Ordinal))
        {
            var parts = cursor[2..].Split(':', 3);
            if (parts.Length == 3
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var ck)
                && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var cb)
                && long.TryParse(parts[2], CultureInfo.InvariantCulture, out var ci))
            {
                try
                {
                    var cc = new DateTimeOffset(DateTime.FromBinary(cb), TimeSpan.Zero);
                    query = direction == SortDirection.Descending
                        ? query.Where(n =>
                            n.Kind > ck ||
                            (n.Kind == ck && n.CreatedAt < cc) ||
                            (n.Kind == ck && n.CreatedAt == cc && n.Id < ci))
                        : query.Where(n =>
                            n.Kind < ck ||
                            (n.Kind == ck && n.CreatedAt > cc) ||
                            (n.Kind == ck && n.CreatedAt == cc && n.Id > ci));
                }
                catch { /* invalid binary DateTimeOffset — ignore cursor, start from beginning */ }
            }
        }

        return await query
            .Take(pageSize + 1)
            .Select(n => new BrowseRow
            {
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                Kind = n.Kind,
                DisplayName = n.DisplayName,
                Availability = n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                CoverUrl = n.Kind == 1 ? "/api/v1/items/" + n.PublicId + "/cover" : null,
                InternalId = n.Id,
                CreatedAt = n.CreatedAt,
                SortKey = n.SortKey,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// Recently-read sort (1.4.0 rewrite): PURE RECENCY. Every node ranks by the most
    /// recent reading activity in its subtree — most-recent first, nodes with no activity
    /// last (by name). "Activity" = the later of ReadingProgress.UpdatedAt and
    /// ReadMark.MarkedAt over the node itself and (for folders) all descendant archives,
    /// so a series folder floats up by its most-recently-read chapter (recursive — series
    /// nest into volumes/seasons). Folders and archives interleave by recency, unlike
    /// name/recentlyAdded (folders-first), because "recently read" is inherently a recency
    /// question (owner decision, 2026-09-10). The recursive descendant aggregate cannot be
    /// expressed in LINQ, so recency is computed with a recursive CTE and the ordering +
    /// paging happen in memory over the level's direct children (bounded). Cursor is an
    /// offset: "r:{offset}", which stays valid across a direction change in the same
    /// request lifecycle since it indexes into the freshly-recomputed in-memory list
    /// rather than a persisted key.
    ///
    /// Default direction (Descending) reproduces the pre-1.5.0 behavior: most-recent
    /// activity first, no-activity nodes last (by name). Ascending fully reverses that
    /// tuple — oldest activity first, no-activity nodes first (by reverse name) — the
    /// same full-reversal semantics used for Name and RecentlyAdded.
    /// </summary>
    private async Task<List<BrowseRow>> QueryRecentlyReadAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        long userId,
        string? cursor,
        int pageSize,
        SortDirection direction,
        CancellationToken ct)
    {
        // This level's direct children (bounded — a folder's immediate children).
        var rows = await baseQuery
            .Select(n => new BrowseRow
            {
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                Kind = n.Kind,
                DisplayName = n.DisplayName,
                Availability = n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                CoverUrl = n.Kind == 1 ? "/api/v1/items/" + n.PublicId + "/cover" : null,
                InternalId = n.Id,
                CreatedAt = n.CreatedAt,
                SortKey = n.SortKey,
            })
            .ToListAsync(ct);

        // recency[nodeId] = max reading-activity timestamp (order-preserving binary) over
        // the node + its descendants. Absent = no activity anywhere (sorts last, by name).
        var recency = await ComputeRecentlyReadRecencyAsync(
            rows.Select(r => r.InternalId).ToList(), userId, ct);

        var ordered = direction == SortDirection.Descending
            ? rows
                .OrderByDescending(r => recency.ContainsKey(r.InternalId))
                .ThenByDescending(r => recency.TryGetValue(r.InternalId, out var v) ? v : long.MinValue)
                .ThenBy(r => r.SortKey, StringComparer.Ordinal)
                .ToList()
            : rows
                .OrderBy(r => recency.ContainsKey(r.InternalId))
                .ThenBy(r => recency.TryGetValue(r.InternalId, out var v) ? v : long.MinValue)
                .ThenByDescending(r => r.SortKey, StringComparer.Ordinal)
                .ToList();

        return ordered.Skip(RecentlyReadOffset(cursor)).Take(pageSize + 1).ToList();
    }

    /// <summary>Parses the recently-read offset cursor "r:{offset}"; 0 when absent/invalid.</summary>
    private static int RecentlyReadOffset(string? cursor)
    {
        if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith("r:", StringComparison.Ordinal)
            && int.TryParse(cursor.AsSpan(2), CultureInfo.InvariantCulture, out var n) && n >= 0)
            return n;
        return 0;
    }

    /// <summary>
    /// For each of the given node ids, the max reading-activity timestamp
    /// (ReadingProgress.UpdatedAt or ReadMark.MarkedAt, whichever is later) over that node
    /// and all of its descendants, for the given user. Returns only nodes with activity.
    /// Both timestamps use the same order-preserving binary storage, so MAX over the union
    /// is comparable and the raw long orders chronologically.
    /// </summary>
    private async Task<Dictionary<long, long>> ComputeRecentlyReadRecencyAsync(
        List<long> nodeIds,
        long userId,
        CancellationToken ct)
    {
        var result = new Dictionary<long, long>();
        if (nodeIds.Count == 0)
            return result;

        var ids = string.Join(",", nodeIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE subtree(RootId, NodeId) AS (
                    SELECT r.Id, r.Id FROM catalog_nodes r WHERE r.Id IN ({ids})
                    UNION ALL
                    SELECT s.RootId, cn.Id FROM subtree s
                    JOIN catalog_nodes cn ON cn.ParentId = s.NodeId
                ),
                activity(NodeId, ts) AS (
                    SELECT ItemId, UpdatedAt FROM reading_progress WHERE UserId = $user
                    UNION ALL
                    SELECT ItemId, MarkedAt FROM read_marks WHERE UserId = $user
                )
                SELECT s.RootId, MAX(a.ts)
                FROM subtree s
                JOIN activity a ON a.NodeId = s.NodeId
                GROUP BY s.RootId;
                """;
            var p = command.CreateParameter();
            p.ParameterName = "$user";
            p.Value = userId;
            command.Parameters.Add(p);

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                    result[reader.GetInt64(0)] = reader.GetInt64(1);
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    // --- Cursor helpers ---

    /// <summary>
    /// Encodes a keyset cursor for the given sort from a browse row. The cursor is
    /// opaque to the client; the prefix ("a:") identifies the sort so mismatched
    /// cursors are safely ignored on the next request. recentlyRead does not go
    /// through here - it paginates by offset ("r:{n}"), see BrowseAsync.
    /// </summary>
    private static string EncodeCursor(string sort, BrowseRow row) => sort switch
    {
        "recentlyAdded" => $"a:{row.Kind}:{row.CreatedAt.UtcDateTime.ToBinary()}:{row.InternalId}",
        _ => row.SortKey,
    };

    /// <summary>
    /// Converts a browse row to a CatalogNodeDto (strips cursor-only fields).
    /// </summary>
    private static CatalogNodeDto ToDto(BrowseRow row) => new()
    {
        Id = row.Id,
        ParentId = row.ParentId,
        LibraryId = row.LibraryId,
        Kind = (CatalogNodeKind)row.Kind,
        DisplayName = row.DisplayName,
        Availability = (CatalogNodeAvailability)row.Availability,
        PageCount = row.PageCount,
        CoverUrl = row.CoverUrl,
    };

    /// <summary>
    /// Intermediate projection type carrying both DTO fields and cursor-relevant
    /// fields so the cursor can be computed without a re-fetch.
    /// </summary>
    private sealed record BrowseRow
    {
        public required string Id { get; init; }
        public required string ParentId { get; init; }
        public required string LibraryId { get; init; }
        public required int Kind { get; init; }
        public required string DisplayName { get; init; }
        public required int Availability { get; init; }
        public int? PageCount { get; init; }
        public string? CoverUrl { get; init; }
        public long InternalId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string SortKey { get; init; } = "";
    }

    /// <summary>
    /// Gets breadcrumbs from library root to a specific node.
    /// Returns the trail of parent nodes (not including the library root itself).
    /// </summary>
    public async Task<BreadcrumbsDto?> GetBreadcrumbsAsync(
        long userId,
        long nodeId,
        CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null)
            return null;

        // Authorization check
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(node.LibraryId))
            return null;

        var trail = new List<BreadcrumbEntry>();
        var current = node;

        while (current.ParentId.HasValue)
        {
            var parent = await _db.CatalogNodes
                .FirstOrDefaultAsync(n => n.Id == current.ParentId.Value, ct);
            if (parent is null)
                break;

            trail.Add(new BreadcrumbEntry
            {
                Id = parent.PublicId,
                DisplayName = parent.DisplayName,
            });

            current = parent;
        }

        trail.Reverse();

        return new BreadcrumbsDto
        {
            NodeId = node.PublicId,
            Trail = trail,
        };
    }

    /// <summary>
    /// Gets the previous and next readable archives in the same parent folder.
    /// Uses the same ordering algorithm as browse. No cross-folder traversal.
    /// </summary>
    public async Task<NeighborsResult?> GetNeighborsAsync(
        long userId,
        long nodeId,
        CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null)
            return null;

        // Authorization check
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(node.LibraryId))
            return null;

        // Find siblings in the same parent folder, ordered by sort key
        var siblings = await _db.CatalogNodes
            .Where(n => n.LibraryId == node.LibraryId)
            .Where(n => n.ParentId == node.ParentId)
            .Where(n => n.Kind == (int)CatalogNodeKind.Archive)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .OrderBy(n => n.SortKey)
            .Select(n => new { n.Id, n.PublicId, n.DisplayName })
            .ToListAsync(ct);

        var currentIndex = siblings.FindIndex(s => s.Id == nodeId);
        if (currentIndex < 0)
            return new NeighborsResult { Previous = null, Next = null };

        NeighborEntry? previous = null;
        NeighborEntry? next = null;

        if (currentIndex > 0)
        {
            var prev = siblings[currentIndex - 1];
            previous = new NeighborEntry { Id = prev.PublicId, DisplayName = prev.DisplayName };
        }

        if (currentIndex < siblings.Count - 1)
        {
            var nxt = siblings[currentIndex + 1];
            next = new NeighborEntry { Id = nxt.PublicId, DisplayName = nxt.DisplayName };
        }

        return new NeighborsResult { Previous = previous, Next = next };
    }

    /// <summary>
    /// Searches catalog nodes using FTS5 trigram. Authorization is filtered
    /// before pagination/counting. Treats user text as a literal search,
    /// escaping FTS syntax through a tested query builder.
    /// </summary>
    public async Task<SearchResultsDto> SearchAsync(
        long userId,
        string query,
        long? libraryId = null,
        string? cursor = null,
        int pageSize = 50,
        bool incognito = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResultsDto
            {
                Query = query ?? "",
                Items = [],
                TotalCount = 0,
                NextCursor = null,
                HasMore = false,
            };
        }

        // Authorization filter — get visible libraries first (accessible minus
        // Private when incognito is active, 1.4.0).
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
        {
            return new SearchResultsDto
            {
                Query = query,
                Items = [],
                TotalCount = 0,
                NextCursor = null,
                HasMore = false,
            };
        }

        // If a specific library is requested, verify access and narrow the filter
        if (libraryId.HasValue)
        {
            if (!visibleLibs.Contains(libraryId.Value))
            {
                return new SearchResultsDto
                {
                    Query = query,
                    Items = [],
                    TotalCount = 0,
                    NextCursor = null,
                    HasMore = false,
                };
            }
            visibleLibs = [libraryId.Value];
        }

        // Build the FTS5 query — treat user text as literal, escape FTS syntax
        var ftsQuery = BuildFtsQuery(query);

        // Build the library IDs parameter list
        var libIds = string.Join(",", visibleLibs);

        // Query the FTS5 index joined with catalog nodes and their parents/libraries
        // to project public IDs (audit defect D29). Keyset pagination on SortKey
        // (audit defect D6/D28 — search now returns real results with proper paging).
        var ftsSql = $"""
            SELECT cn.Id, cn.PublicId, cn.Kind,
                   cn.DisplayName, cn.Availability, cn.SortKey,
                   parent.PublicId AS ParentPublicId,
                   lib.PublicId AS LibraryPublicId
            FROM catalog_search cs
            JOIN catalog_nodes cn ON cs.node_id = cn.Id
            LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
            JOIN libraries lib ON cn.LibraryId = lib.Id
            WHERE catalog_search MATCH @query
            AND cn.Availability != 5
            AND cn.LibraryId IN ({libIds})
            {(cursor is not null ? "AND cn.SortKey > @cursor" : "")}
            ORDER BY cn.SortKey
            LIMIT @limit
            """;

        var results = new List<CatalogNodeDto>();
        // Folder internal id -> public id, so we can resolve folder covers for search
        // results the same way BrowseAsync does (folders otherwise show no thumbnail).
        var folderInternalToPublic = new Dictionary<long, string>();
        using var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = ftsSql;
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@query", ftsQuery));
        command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@limit", pageSize + 1));
        if (cursor is not null)
            command.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@cursor", cursor));

        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var publicId = reader.GetString(reader.GetOrdinal("PublicId"));
            var kind = (CatalogNodeKind)reader.GetInt32(reader.GetOrdinal("Kind"));
            results.Add(new CatalogNodeDto
            {
                Id = publicId,
                ParentId = reader.IsDBNull(reader.GetOrdinal("ParentPublicId"))
                    ? ""
                    : reader.GetString(reader.GetOrdinal("ParentPublicId")),
                LibraryId = reader.GetString(reader.GetOrdinal("LibraryPublicId")),
                Kind = kind,
                DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
                Availability = (CatalogNodeAvailability)reader.GetInt32(reader.GetOrdinal("Availability")),
            });
            if (kind == CatalogNodeKind.Folder)
                folderInternalToPublic[reader.GetInt64(reader.GetOrdinal("Id"))] = publicId;
        }

        var hasMore = results.Count > pageSize;
        if (hasMore)
            results = results.Take(pageSize).ToList();

        // Resolve folder covers for search results (parity with BrowseAsync) - a folder
        // in search otherwise renders with no thumbnail. Same recursive-CTE cover
        // resolution (first descendant archive by SortKey) as browse; folders with no
        // readable descendant stay coverless.
        if (folderInternalToPublic.Count > 0)
        {
            var coversByInternalId = await ResolveFolderCoversAsync(folderInternalToPublic.Keys.ToList(), ct);
            var coversByPublicId = folderInternalToPublic
                .Where(kv => coversByInternalId.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Value, kv => coversByInternalId[kv.Key]);
            results = results.Select(n =>
            {
                if (n.Kind != CatalogNodeKind.Folder)
                    return n;
                if (coversByPublicId.TryGetValue(n.Id, out var coverPublicId))
                    return n with { CoverUrl = $"/api/v1/items/{coverPublicId}/cover" };
                return n;
            }).ToList();
        }

        // Compute total count with a separate query (audit defect D6/D28)
        var countSql = $"""
            SELECT COUNT(*)
            FROM catalog_search cs
            JOIN catalog_nodes cn ON cs.node_id = cn.Id
            WHERE catalog_search MATCH @query
            AND cn.Availability != 5
            AND cn.LibraryId IN ({libIds})
            """;
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = countSql;
        countCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@query", ftsQuery));
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));

        // Next cursor is the SortKey of the last result
        string? nextCursor = null;
        if (hasMore && results.Count > 0)
        {
            var lastId = results[^1].Id;
            using var cursorCommand = connection.CreateCommand();
            cursorCommand.CommandText = "SELECT SortKey FROM catalog_nodes WHERE PublicId = @pubId";
            cursorCommand.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@pubId", lastId));
            nextCursor = (string?)await cursorCommand.ExecuteScalarAsync(ct);
        }

        return new SearchResultsDto
        {
            Query = query,
            Items = results,
            TotalCount = totalCount,
            NextCursor = nextCursor,
            HasMore = hasMore,
        };
    }

    /// <summary>
    /// Gets a single catalog node by its public ID.
    /// </summary>
    public async Task<CatalogNodeDto?> GetNodeAsync(
        long userId,
        string publicId,
        CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes
            .Include(n => n.Parent)
            .Include(n => n.Library)
            .Include(n => n.ArchiveItem)
            .FirstOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (node is null)
            return null;

        // Authorization check
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(node.LibraryId))
            return null;

        return new CatalogNodeDto
        {
            Id = node.PublicId,
            ParentId = node.Parent != null ? node.Parent.PublicId : "",
            LibraryId = node.Library != null ? node.Library.PublicId : "",
            Kind = (CatalogNodeKind)node.Kind,
            DisplayName = node.DisplayName,
            Availability = (CatalogNodeAvailability)node.Availability,
            PageCount = node.ArchiveItem?.PageCount,
        };
    }

    /// <summary>
    /// Builds a safe FTS5 query string from user input.
    /// Treats the input as a literal substring search using the trigram tokenizer.
    /// Escapes FTS5 special characters by wrapping in double quotes.
    /// </summary>
    private static string BuildFtsQuery(string input)
    {
        // FTS5 trigram tokenizer: wrap in double quotes for literal phrase search
        // Escape any double quotes in the input by doubling them
        var escaped = input.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// Resolves a cover (first non-tombstoned descendant archive by SortKey) for
    /// each folder in <paramref name="folderInternalIds"/> via a single recursive
    /// CTE, avoiding an N+1 query across the folder page. Returns a dictionary
    /// mapping folder internal ID → cover archive public ID. Folders with no
    /// non-tombstoned descendant archive are omitted.
    /// </summary>
    private async Task<Dictionary<long, string>> ResolveFolderCoversAsync(
        List<long> folderInternalIds,
        CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (folderInternalIds.Count == 0)
            return result;

        var ids = string.Join(",", folderInternalIds);
        var connection = _db.Database.GetDbConnection();
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

    /// <summary>
    /// Resolves the derived read rollup (1.6.0) for each folder in
    /// <paramref name="folderInternalIds"/> in a single set-based query: one recursive
    /// CTE enumerates every descendant of every page folder tagged with its RootId,
    /// then the readable archives are LEFT JOINed to the user's read_marks and
    /// reading_progress rows and aggregated per RootId. Both joined tables have a
    /// unique (UserId, ItemId) index, so the joins cannot multiply rows and the counts
    /// are exact. Folders with no readable descendant archive are omitted (null rollup).
    ///
    /// Cost is proportional to the total descendant count of the folders on the page
    /// (walked once via the (ParentId, Kind, SortKey) index) - the same order as the
    /// existing cover and recently-read aggregates, so browse gains no new per-folder
    /// round trips. Classification of the three counts lives in
    /// <see cref="FolderReadRollupRules.Classify"/> (Core, unit-tested).
    /// </summary>
    private async Task<Dictionary<long, FolderReadRollup>> ResolveFolderReadRollupsAsync(
        List<long> folderInternalIds,
        long userId,
        CancellationToken ct)
    {
        var result = new Dictionary<long, FolderReadRollup>();
        if (folderInternalIds.Count == 0)
            return result;

        var ids = string.Join(",", folderInternalIds);
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            // Kind = 1 is Archive; Availability = 5 is Tombstoned; State = 1 is InProgress
            // (same literals as the sibling CTEs in this file). The root row itself is a
            // folder (Kind 0) so it is filtered out by the archive join.
            command.CommandText = $"""
                WITH RECURSIVE subtree(RootId, NodeId) AS (
                    SELECT r.Id, r.Id FROM catalog_nodes r WHERE r.Id IN ({ids})
                    UNION ALL
                    SELECT s.RootId, cn.Id FROM subtree s
                    JOIN catalog_nodes cn ON cn.ParentId = s.NodeId
                )
                SELECT s.RootId,
                       COUNT(*) AS Total,
                       SUM(CASE WHEN rm.ItemId IS NOT NULL THEN 1 ELSE 0 END) AS ReadCount,
                       SUM(CASE WHEN rm.ItemId IS NULL AND rp.State = 1 THEN 1 ELSE 0 END) AS InProgressCount
                FROM subtree s
                JOIN catalog_nodes a ON a.Id = s.NodeId AND a.Kind = 1 AND a.Availability != 5
                LEFT JOIN read_marks rm ON rm.ItemId = a.Id AND rm.UserId = $user
                LEFT JOIN reading_progress rp ON rp.ItemId = a.Id AND rp.UserId = $user
                GROUP BY s.RootId;
                """;
            var p = command.CreateParameter();
            p.ParameterName = "$user";
            p.Value = userId;
            command.Parameters.Add(p);

            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var rollup = FolderReadRollupRules.Classify(
                    total: reader.GetInt32(1),
                    read: reader.GetInt32(2),
                    inProgress: reader.GetInt32(3));
                if (rollup is not null)
                    result[reader.GetInt64(0)] = rollup.Value;
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
        return result;
    }

    /// <summary>
    /// Resolves the browsed folder's next-to-read descendant archive (1.7.0): the
    /// in-progress archive if one exists (resume the most recently updated), else
    /// the first UNREAD archive in SortKey order (ordinal, matching
    /// <c>NaturalOrderComparer</c> / <c>StringComparer.Ordinal</c>); null when every
    /// descendant is read. Set-based via a single recursive CTE - the same descendant
    /// walk the cover and recency aggregates use, so browse gains no new per-folder
    /// round trip. "Read" = a sticky read-mark exists (the same signal the archive
    /// cards and the folder rollup use); "in-progress" = no read-mark and a
    /// ReadingProgress row with State = InProgress. At the library root
    /// (<paramref name="parentId"/> null) the descendants are every archive in the
    /// library; at a folder they are that folder's recursive descendants (the folder
    /// itself is never a candidate). Tombstoned archives are excluded. The returned
    /// DTO carries the same read-state fields the archive card shows (IsRead,
    /// ReadingState, LastReadPage) so the Continue row's affordance matches the list.
    /// </summary>
    private async Task<CatalogNodeDto?> ResolveNextUnreadAsync(
        long libraryId,
        long? parentId,
        long userId,
        CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            using var command = connection.CreateCommand();
            // Kind = 1 is Archive; Availability = 5 is Tombstoned; State = 1 is
            // InProgress (same literals as the sibling CTEs in this file). The seed is
            // the browsed folder's direct children (or every root-level node when
            // browsing the library root); recursion reaches every descendant. Only
            // not-read archives (no read-mark) are candidates. An in-progress one wins
            // (resume the most recently updated); otherwise the first by SortKey.
            command.CommandText = """
                WITH RECURSIVE descendants(NodeId) AS (
                    SELECT cn.Id FROM catalog_nodes cn
                    WHERE cn.LibraryId = $lib
                      AND cn.Availability != 5
                      AND (($parent IS NULL AND cn.ParentId IS NULL) OR cn.ParentId = $parent)
                    UNION ALL
                    SELECT cn.Id FROM descendants d
                    JOIN catalog_nodes cn ON cn.ParentId = d.NodeId
                    WHERE cn.Availability != 5
                )
                SELECT cn.PublicId,
                       parent.PublicId AS ParentPublicId,
                       lib.PublicId AS LibraryPublicId,
                       cn.DisplayName,
                       cn.Availability,
                       ai.PageCount,
                       rm.ItemId IS NOT NULL AS IsRead,
                       rp.State,
                       rp.Ordinal
                FROM descendants d
                JOIN catalog_nodes cn ON cn.Id = d.NodeId AND cn.Kind = 1
                LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
                JOIN libraries lib ON lib.Id = cn.LibraryId
                LEFT JOIN archive_items ai ON ai.NodeId = cn.Id
                LEFT JOIN read_marks rm ON rm.ItemId = cn.Id AND rm.UserId = $user
                LEFT JOIN reading_progress rp ON rp.ItemId = cn.Id AND rp.UserId = $user
                WHERE rm.ItemId IS NULL
                ORDER BY
                    CASE WHEN rp.State = 1 THEN 0 ELSE 1 END,
                    CASE WHEN rp.State = 1 THEN rp.UpdatedAt END DESC,
                    cn.SortKey,
                    cn.Id
                LIMIT 1;
                """;

            var libParam = command.CreateParameter();
            libParam.ParameterName = "$lib";
            libParam.Value = libraryId;
            command.Parameters.Add(libParam);

            var parentParam = command.CreateParameter();
            parentParam.ParameterName = "$parent";
            parentParam.Value = (object?)parentId ?? DBNull.Value;
            command.Parameters.Add(parentParam);

            var userParam = command.CreateParameter();
            userParam.ParameterName = "$user";
            userParam.Value = userId;
            command.Parameters.Add(userParam);

            using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var publicId = reader.GetString(reader.GetOrdinal("PublicId"));
            var parentOrdinal = reader.GetOrdinal("ParentPublicId");
            var libraryPublicId = reader.GetString(reader.GetOrdinal("LibraryPublicId"));
            var displayName = reader.GetString(reader.GetOrdinal("DisplayName"));
            var availability = reader.GetInt32(reader.GetOrdinal("Availability"));
            var pageCountOrdinal = reader.GetOrdinal("PageCount");
            var stateOrdinal = reader.GetOrdinal("State");
            var ordinalOrdinal = reader.GetOrdinal("Ordinal");

            return new CatalogNodeDto
            {
                Id = publicId,
                ParentId = reader.IsDBNull(parentOrdinal) ? "" : reader.GetString(parentOrdinal),
                LibraryId = libraryPublicId,
                Kind = CatalogNodeKind.Archive,
                DisplayName = displayName,
                Availability = (CatalogNodeAvailability)availability,
                PageCount = reader.IsDBNull(pageCountOrdinal) ? null : reader.GetInt32(pageCountOrdinal),
                CoverUrl = $"/api/v1/items/{publicId}/cover",
                IsRead = reader.GetBoolean(reader.GetOrdinal("IsRead")),
                ReadingState = reader.IsDBNull(stateOrdinal) ? null : (ReadingState)reader.GetInt32(stateOrdinal),
                LastReadPage = reader.IsDBNull(ordinalOrdinal) ? null : reader.GetInt32(ordinalOrdinal),
            };
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    private async Task<List<long>> GetAccessibleLibraryIdsAsync(long userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return [];

        if (user.IsAdmin)
            return await _db.Libraries.Select(l => l.Id).ToListAsync(ct);

        return await _db.LibraryGrants
            .Where(g => g.UserId == userId)
            .Select(g => g.LibraryId)
            .ToListAsync(ct);
    }
}

/// <summary>
/// Browse read-state filter (1.10.0). Restricts the browsed archives to the chosen
/// per-user read state; <see cref="All"/> disables the filter. Semantics match the
/// archive cards and folder rollup: Read = a sticky read-mark; Reading = no mark and
/// an in-progress ReadingProgress row; Unread = neither. Folders are always kept
/// (they have no per-item read signal and must stay navigable at any folder level).
/// </summary>
public enum BrowseReadStateFilter
{
    All,
    Reading,
    Read,
    Unread,
}

/// <summary>
/// Result of a neighbors query.
/// </summary>
public sealed record NeighborsResult
{
    public NeighborEntry? Previous { get; init; }
    public NeighborEntry? Next { get; init; }
}

/// <summary>
/// A neighbor entry (previous or next readable archive).
/// </summary>
public sealed record NeighborEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
}
