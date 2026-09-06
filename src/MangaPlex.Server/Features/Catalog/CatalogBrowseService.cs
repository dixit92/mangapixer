namespace com.lifepixer.mangaplex.Server.Features.Catalog;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
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
    /// Folders first, then archives, ordered by persisted sort key.
    /// Authorization is filtered before pagination.
    /// </summary>
    public async Task<PageResponse<CatalogNodeDto>> BrowseAsync(
        long userId,
        long libraryId,
        long? parentId,
        string? cursor,
        int pageSize = 50,
        SortDirection direction = SortDirection.Ascending,
        CancellationToken ct = default)
    {
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

        // Build query with authorization filter
        var query = _db.CatalogNodes
            .Where(n => n.LibraryId == libraryId)
            .Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned);

        if (parentId is null)
        {
            // Root children: parent is null or parent is the synthetic root
            query = query.Where(n => n.ParentId == null);
        }
        else
        {
            query = query.Where(n => n.ParentId == parentId);
        }

        // Apply cursor for keyset pagination
        if (!string.IsNullOrEmpty(cursor))
        {
            if (direction == SortDirection.Ascending)
                query = query.Where(n => string.Compare(n.SortKey, cursor) > 0);
            else
                query = query.Where(n => string.Compare(n.SortKey, cursor) < 0);
        }

        // Order by sort key (folders first, then archives, then natural order)
        if (direction == SortDirection.Ascending)
            query = query.OrderBy(n => n.SortKey);
        else
            query = query.OrderByDescending(n => n.SortKey);

        // Get total count (with authorization filter already applied)
        var totalCount = await query.CountAsync(ct);

        // Page the results — project ParentId as the parent's PublicId and
        // LibraryId as the Library's PublicId (audit defect D29). Clients
        // must be able to round-trip parentId back into browse?parentId=.
        var nodes = await query
            .Take(pageSize + 1) // +1 to check hasMore
            .Select(n => new CatalogNodeDto
            {
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                Kind = (CatalogNodeKind)n.Kind,
                DisplayName = n.DisplayName,
                Availability = (CatalogNodeAvailability)n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
            })
            .ToListAsync(ct);

        var hasMore = nodes.Count > pageSize;
        if (hasMore)
            nodes = nodes.Take(pageSize).ToList();

        string? nextCursor = null;
        if (hasMore && nodes.Count > 0)
        {
            // The cursor is the sort key of the last item
            var lastId = nodes[nodes.Count - 1].Id;
            var lastNode = await _db.CatalogNodes
                .Where(n => n.PublicId == lastId)
                .Select(n => n.SortKey)
                .FirstOrDefaultAsync(ct);
            nextCursor = lastNode;
        }

        return new PageResponse<CatalogNodeDto>
        {
            Items = nodes,
            TotalCount = totalCount,
            NextCursor = nextCursor,
            HasMore = hasMore,
        };
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
