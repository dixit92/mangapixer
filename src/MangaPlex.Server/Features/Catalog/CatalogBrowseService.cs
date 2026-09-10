namespace com.lifepixer.mangaplex.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
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

    public CatalogBrowseService(MangaPlexDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Browses children of a parent node (or library root if parentId is null).
    /// Folders first, then archives, ordered by the chosen sort key.
    /// Authorization is filtered before pagination.
    ///
    /// Sort modes (1.2.0 follow-up):
    /// - name (default): order by SortKey; cursor = SortKey.
    /// - recentlyAdded: order by Kind ASC, CreatedAt DESC, Id DESC; cursor encodes (Kind, CreatedAt, Id).
    /// - recentlyRead: order by Kind ASC, progress UpdatedAt DESC (NULLs last), SortKey ASC;
    ///   cursor encodes (Kind, ProgressUpdatedAt, SortKey).
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> BrowseAsync(
        long userId,
        long libraryId,
        long? parentId,
        string? cursor,
        int pageSize = 50,
        SortDirection direction = SortDirection.Ascending,
        string sort = "name",
        CancellationToken ct = default)
    {
        // Validate sort — unknown values fall back to "name" (tolerant, like the DTO).
        sort = sort switch
        {
            "recentlyAdded" or "recentlyRead" => sort,
            _ => "name",
        };

        // Authorization filter — applied before pagination
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (!accessibleLibs.Contains(libraryId))
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

        // Total count from the base query (before cursor — fixes the decreasing-count bug
        // where the old code counted after the cursor filter).
        var totalCount = await baseQuery.CountAsync(ct);

        // Sort-specific query: ordering, keyset cursor filter, projection, and paging.
        List<BrowseRow> rows = sort switch
        {
            "recentlyAdded" => await QueryRecentlyAddedAsync(baseQuery, cursor, pageSize, ct),
            "recentlyRead" => await QueryRecentlyReadAsync(baseQuery, userId, cursor, pageSize, ct),
            _ => await QueryNameAsync(baseQuery, cursor, pageSize, direction, ct),
        };

        // Check hasMore and trim to pageSize (the +1 was only to detect hasMore).
        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows = rows.Take(pageSize).ToList();

        // Convert to DTOs for enrichment.
        var nodes = rows.Select(ToDto).ToList();

        // For folders, resolve CoverUrl from the first archive child by SortKey (D17)
        var folderIds = nodes.Where(n => n.Kind == CatalogNodeKind.Folder).Select(n => n.Id).ToList();
        if (folderIds.Count > 0)
        {
            var folderCovers = await _db.CatalogNodes
                .Where(n => folderIds.Contains(n.Parent != null ? n.Parent.PublicId : ""))
                .Where(n => n.Kind == 1)
                .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned)
                .OrderBy(n => n.SortKey)
                .Select(n => new { ParentPublicId = n.Parent != null ? n.Parent.PublicId : "", ChildPublicId = n.PublicId })
                .GroupBy(x => x.ParentPublicId)
                .Select(g => new { ParentId = g.Key, FirstChildId = g.First().ChildPublicId })
                .ToDictionaryAsync(x => x.ParentId, x => x.FirstChildId, ct);

            // Rebuild folder nodes with CoverUrl (init-only property)
            nodes = nodes.Select(n =>
            {
                if (n.Kind != CatalogNodeKind.Folder)
                    return n;
                if (folderCovers.TryGetValue(n.Id, out var firstChildId))
                    return n with { CoverUrl = $"/api/v1/items/{firstChildId}/cover" };
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

        // Compute next cursor from the last row on the current page.
        string? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            nextCursor = EncodeCursor(sort, rows[^1]);
        }

        return new PageResponse<CatalogNodeDto>
        {
            Items = nodes,
            TotalCount = totalCount,
            NextCursor = nextCursor,
            HasMore = hasMore,
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
    /// Recently-added sort: folders first, then archives, each by CreatedAt DESC, Id DESC.
    /// Cursor encodes (Kind, CreatedAtBinary, InternalId).
    /// </summary>
    private async Task<List<BrowseRow>> QueryRecentlyAddedAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        string? cursor,
        int pageSize,
        CancellationToken ct)
    {
        IQueryable<CatalogNodeEntity> query = baseQuery
            .OrderBy(n => n.Kind)
            .ThenByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id);

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
                    query = query.Where(n =>
                        n.Kind > ck ||
                        (n.Kind == ck && n.CreatedAt < cc) ||
                        (n.Kind == ck && n.CreatedAt == cc && n.Id < ci));
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
    /// Recently-read sort: folders first (by name), then archives with progress
    /// (by UpdatedAt DESC), then archives without progress (by name).
    /// Uses a LEFT JOIN to the user's ReadingProgress. NULLs sort last in DESC
    /// order in SQLite, so ThenByDescending(ProgressUpdatedAt) handles both the
    /// has-progress/no-progress segmentation and the UpdatedAt ordering in one
    /// expression. Cursor encodes (Kind, ProgressUpdatedAtBinary, SortKey).
    /// </summary>
    private async Task<List<BrowseRow>> QueryRecentlyReadAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        long userId,
        string? cursor,
        int pageSize,
        CancellationToken ct)
    {
        // LEFT JOIN to the user's reading progress for sort ordering.
        IQueryable<BrowseRow> query = from n in baseQuery
            join p in _db.ReadingProgress.Where(rp => rp.UserId == userId)
                on n.Id equals p.ItemId into pg
            from p in pg.DefaultIfEmpty()
            select new BrowseRow
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
                ProgressUpdatedAt = (DateTimeOffset?)p.UpdatedAt,
            };

        // Ordering: Kind ASC (folders first), ProgressUpdatedAt DESC (has-progress
        // archives by recency, NULLs last = no-progress archives), SortKey ASC (tiebreaker).
        query = query
            .OrderBy(r => r.Kind)
            .ThenByDescending(r => r.ProgressUpdatedAt)
            .ThenBy(r => r.SortKey);

        // Keyset cursor filter — must handle NULL ProgressUpdatedAt explicitly.
        if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith("r:", StringComparison.Ordinal))
        {
            var parts = cursor[2..].Split(':', 3);
            if (parts.Length == 3
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var ck))
            {
                var cs = parts[2];
                if (string.IsNullOrEmpty(parts[1]))
                {
                    // Cursor is at a no-progress row: only later no-progress rows (by SortKey) follow.
                    query = query.Where(r =>
                        r.Kind > ck ||
                        (r.Kind == ck && r.ProgressUpdatedAt == null
                            && string.Compare(r.SortKey, cs) > 0));
                }
                else if (long.TryParse(parts[1], CultureInfo.InvariantCulture, out var cb))
                {
                    try
                    {
                        var cpu = new DateTimeOffset(DateTime.FromBinary(cb), TimeSpan.Zero);
                        // Cursor is at a has-progress row: remaining has-progress rows
                        // (earlier UpdatedAt, or same UpdatedAt + later SortKey) AND
                        // all no-progress rows (they come after every has-progress row).
                        query = query.Where(r =>
                            r.Kind > ck ||
                            (r.Kind == ck && r.ProgressUpdatedAt != null && r.ProgressUpdatedAt < cpu) ||
                            (r.Kind == ck && r.ProgressUpdatedAt != null
                                && r.ProgressUpdatedAt == cpu && string.Compare(r.SortKey, cs) > 0) ||
                            (r.Kind == ck && r.ProgressUpdatedAt == null));
                    }
                    catch { /* invalid binary DateTimeOffset — ignore cursor */ }
                }
            }
        }

        return await query.Take(pageSize + 1).ToListAsync(ct);
    }

    // --- Cursor helpers ---

    /// <summary>
    /// Encodes a cursor for the given sort from a browse row. The cursor is opaque
    /// to the client; the prefix ("a:" / "r:") identifies the sort so mismatched
    /// cursors are safely ignored on the next request.
    /// </summary>
    private static string EncodeCursor(string sort, BrowseRow row) => sort switch
    {
        "recentlyAdded" => $"a:{row.Kind}:{row.CreatedAt.UtcDateTime.ToBinary()}:{row.InternalId}",
        "recentlyRead" => row.ProgressUpdatedAt is { } pu
            ? $"r:{row.Kind}:{pu.UtcDateTime.ToBinary()}:{row.SortKey}"
            : $"r:{row.Kind}::{row.SortKey}",
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
        public DateTimeOffset? ProgressUpdatedAt { get; init; }
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

        // Authorization filter — get accessible libraries first
        var accessibleLibs = await GetAccessibleLibraryIdsAsync(userId, ct);
        if (accessibleLibs.Count == 0)
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
            if (!accessibleLibs.Contains(libraryId.Value))
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
            accessibleLibs = [libraryId.Value];
        }

        // Build the FTS5 query — treat user text as literal, escape FTS syntax
        var ftsQuery = BuildFtsQuery(query);

        // Build the library IDs parameter list
        var libIds = string.Join(",", accessibleLibs);

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
            results.Add(new CatalogNodeDto
            {
                Id = reader.GetString(reader.GetOrdinal("PublicId")),
                ParentId = reader.IsDBNull(reader.GetOrdinal("ParentPublicId"))
                    ? ""
                    : reader.GetString(reader.GetOrdinal("ParentPublicId")),
                LibraryId = reader.GetString(reader.GetOrdinal("LibraryPublicId")),
                Kind = (CatalogNodeKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
                Availability = (CatalogNodeAvailability)reader.GetInt32(reader.GetOrdinal("Availability")),
            });
        }

        var hasMore = results.Count > pageSize;
        if (hasMore)
            results = results.Take(pageSize).ToList();

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
