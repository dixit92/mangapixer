namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Catalog search (moved out of the browse file in 1.45.0 phase K4, when search gained HTTP paging, the viewer's read state and the
/// read-state filter). FTS5 trigram over names and paths, the viewer's visible libraries only, keyset-paged by <c>(SortKey, Id)</c>.
/// </summary>
public sealed partial class CatalogBrowseService
{
    /// <summary>Most results one search page returns (1.45.0); a request for fewer than one gets one.</summary>
    public const int MaxSearchPageSize = 100;

    private const int MaxSeriesMatches = 20;

    /// <summary>
    /// Searches catalog nodes using FTS5 trigram. Authorization is filtered
    /// before pagination/counting. Treats user text as a literal search,
    /// escaping FTS syntax through a tested query builder.
    ///
    /// 1.45.0 (phase K4): the results carry the viewer's read state as browse cards do (archives: the sticky read mark, the progress state
    /// and page; folders: the descendant read rollup) - a fixed handful of queries per page. <paramref name="readState"/> keeps only Read /
    /// Reading / Unread results by the same rule as browse's Show filter (archives by mark and progress, folders by rollup, an empty folder
    /// counts as Unread); it is part of the WHERE clause, so the page, the keyset cursor and <c>TotalCount</c> all agree. The cursor is
    /// <c>k:&lt;id&gt;:&lt;SortKey&gt;</c> - names repeat across folders, so the SortKey alone would skip the rest of a tie at a page
    /// boundary; a bare SortKey cursor (the earlier form) still continues after that key.
    /// </summary>
    public async Task<SearchResultsDto> SearchAsync(
        long userId,
        string query,
        long? libraryId = null,
        string? cursor = null,
        int pageSize = 50,
        bool incognito = false,
        BrowseReadStateFilter readState = BrowseReadStateFilter.All,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return EmptySearch(query ?? "");

        pageSize = Math.Clamp(pageSize, 1, MaxSearchPageSize);

        // Authorization filter — get visible libraries first (accessible minus
        // Private when incognito is active, 1.4.0).
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
            return EmptySearch(query);

        // If a specific library is requested, verify access and narrow the filter
        if (libraryId.HasValue)
        {
            if (!visibleLibs.Contains(libraryId.Value))
                return EmptySearch(query);
            visibleLibs = [libraryId.Value];
        }

        // Build the FTS5 query — treat user text as literal, escape FTS syntax
        var ftsQuery = BuildFtsQuery(query);

        // Build the library IDs parameter list
        var libIds = string.Join(",", visibleLibs);

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            // The match, visibility and (1.45.0) read-state filter: shared by the page and the count, so both see the same set.
            var matchWhere = $"""
                catalog_search MATCH @query
                AND cn.Availability != 5
                AND cn.LibraryId IN ({libIds})
                """;
            var readWhere = readState == BrowseReadStateFilter.All
                ? ""
                : "AND " + await SearchReadStateClauseAsync(connection, matchWhere, ftsQuery, userId, readState, ct);

            var position = ParseSearchCursor(cursor);
            var cursorWhere = position switch
            {
                null => "",
                { Id: null } => "AND cn.SortKey > @cursorKey",
                _ => "AND (cn.SortKey > @cursorKey OR (cn.SortKey = @cursorKey AND cn.Id > @cursorId))",
            };

            // Query the FTS5 index joined with catalog nodes and their parents/libraries
            // to project public IDs (audit defect D29). Keyset pagination on (SortKey, Id).
            var pageSql = $"""
                SELECT cn.Id, cn.PublicId, cn.Kind,
                       cn.DisplayName, cn.Availability, cn.SortKey,
                       parent.PublicId AS ParentPublicId,
                       lib.PublicId AS LibraryPublicId
                FROM catalog_search cs
                JOIN catalog_nodes cn ON cs.node_id = cn.Id
                LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
                JOIN libraries lib ON cn.LibraryId = lib.Id
                WHERE {matchWhere}
                {readWhere}
                {cursorWhere}
                ORDER BY cn.SortKey, cn.Id
                LIMIT @limit
                """;

            var results = new List<CatalogNodeDto>();
            var internalIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var sortKeys = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = pageSql;
                command.Parameters.Add(new SqliteParameter("@query", ftsQuery));
                command.Parameters.Add(new SqliteParameter("@user", userId));
                command.Parameters.Add(new SqliteParameter("@limit", pageSize + 1));
                if (position is { } at)
                {
                    command.Parameters.Add(new SqliteParameter("@cursorKey", at.SortKey));
                    if (at.Id is { } id)
                        command.Parameters.Add(new SqliteParameter("@cursorId", id));
                }

                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var node = ReadSearchNode(reader);
                    results.Add(node);
                    internalIds[node.Id] = reader.GetInt64(reader.GetOrdinal("Id"));
                    sortKeys.Add(reader.GetString(reader.GetOrdinal("SortKey")));
                }
            }

            var hasMore = results.Count > pageSize;
            if (hasMore)
                results = results.Take(pageSize).ToList();

            // Next cursor: the last result's (Id, SortKey).
            var nextCursor = hasMore && results.Count > 0
                ? EncodeSearchCursor(internalIds[results[^1].Id], sortKeys[results.Count - 1])
                : null;

            // Compute total count with a separate query (audit defect D6/D28) - the same WHERE as the page, without the cursor.
            int totalCount;
            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = $"""
                    SELECT COUNT(*)
                    FROM catalog_search cs
                    JOIN catalog_nodes cn ON cs.node_id = cn.Id
                    WHERE {matchWhere}
                    {readWhere}
                    """;
                countCommand.Parameters.Add(new SqliteParameter("@query", ftsQuery));
                countCommand.Parameters.Add(new SqliteParameter("@user", userId));
                totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            }

            results = await EnrichSearchNodesAsync(results, internalIds, userId, ct);

            // Series information (1.27.0): the same (i) flag as browse, for the card (i) and
            // the hover summary. Batched, fixed cost per page.
            results = await ApplyHasSeriesInfoByPublicIdAsync(results, ct);

            // Alt-title matches (1.26.0): first page only, one extra query.
            var seriesMatches = position is null
                ? await SearchSeriesMatchesAsync(connection, userId, ftsQuery, libIds, readState, ct)
                : null;

            return new SearchResultsDto
            {
                Query = query,
                Items = results,
                TotalCount = totalCount,
                NextCursor = nextCursor,
                HasMore = hasMore,
                SeriesMatches = seriesMatches,
            };
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// 1.45.0 (tag view, lane Q): search cards for nodes by internal id - the first visible location of each work card - with the same
    /// enrichment as search results (folder covers, favourite star, the viewer's read state, the (i) flag). The caller has already
    /// limited <paramref name="nodeIds"/> to locations the viewer may see; tombstoned nodes are left out here too.
    /// </summary>
    public async Task<Dictionary<long, CatalogNodeDto>> SearchCardsAsync(long userId, IReadOnlyCollection<long> nodeIds, CancellationToken ct = default)
    {
        var byInternal = new Dictionary<long, CatalogNodeDto>();
        if (nodeIds.Count == 0)
            return byInternal;

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        var nodes = new List<CatalogNodeDto>();
        var internalIds = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var chunk in nodeIds.Distinct().Chunk(500))
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT cn.Id, cn.PublicId, cn.Kind, cn.DisplayName, cn.Availability,
                           parent.PublicId AS ParentPublicId,
                           lib.PublicId AS LibraryPublicId
                    FROM catalog_nodes cn
                    LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
                    JOIN libraries lib ON cn.LibraryId = lib.Id
                    WHERE cn.Id IN ({string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                    AND cn.Availability != 5
                    """;
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var node = ReadSearchNode(reader);
                    nodes.Add(node);
                    internalIds[node.Id] = reader.GetInt64(reader.GetOrdinal("Id"));
                }
            }
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }

        nodes = await EnrichSearchNodesAsync(nodes, internalIds, userId, ct);
        nodes = await ApplyHasSeriesInfoByPublicIdAsync(nodes, ct);
        foreach (var node in nodes)
            byInternal[internalIds[node.Id]] = node;
        return byInternal;
    }

    private static SearchResultsDto EmptySearch(string query) => new()
    {
        Query = query,
        Items = [],
        TotalCount = 0,
        NextCursor = null,
        HasMore = false,
    };

    /// <summary>The search keyset cursor (1.45.0): <c>k:&lt;internal id&gt;:&lt;SortKey&gt;</c>.</summary>
    private static string EncodeSearchCursor(long id, string sortKey) =>
        "k:" + id.ToString(CultureInfo.InvariantCulture) + ":" + sortKey;

    /// <summary>
    /// The position after which a search page starts: <c>k:&lt;id&gt;:&lt;SortKey&gt;</c>, or (the form before 1.45.0) a bare SortKey -
    /// stored sort keys start with the kind digit, never with <c>k:</c>. A malformed <c>k:</c> cursor starts from the first page; null for
    /// no cursor.
    /// </summary>
    internal static (string SortKey, long? Id)? ParseSearchCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        if (!cursor.StartsWith("k:", StringComparison.Ordinal))
            return (cursor, null);
        var rest = cursor[2..];
        var sep = rest.IndexOf(':', StringComparison.Ordinal);
        return sep > 0 && long.TryParse(rest.AsSpan(0, sep), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (rest[(sep + 1)..], id)
            : null;
    }

    /// <summary>
    /// The SQL condition (over <c>cn</c>, parameter <c>@user</c>) for a read-state filter, the browse Show filter's rule: an ARCHIVE is Read
    /// with the viewer's sticky read mark, Reading without one and with in-progress progress, Unread with neither; a FOLDER by its descendant
    /// read rollup (Read / Reading; Unread also keeps a folder with nothing readable). The folders' rollups are resolved in a pre-pass over
    /// every matching folder (one recursive CTE, as browse does for a level) and folded in as an id list.
    /// </summary>
    private async Task<string> SearchReadStateClauseAsync(
        DbConnection connection, string matchWhere, string ftsQuery, long userId, BrowseReadStateFilter readState, CancellationToken ct)
    {
        var folderIds = new List<long>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT cn.Id
                FROM catalog_search cs
                JOIN catalog_nodes cn ON cs.node_id = cn.Id
                WHERE {matchWhere}
                AND cn.Kind = {(int)CatalogNodeKind.Folder}
                """;
            command.Parameters.Add(new SqliteParameter("@query", ftsQuery));
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                folderIds.Add(reader.GetInt64(0));
        }

        var rollups = await ResolveFolderReadRollupsAsync(folderIds, userId, ct);
        var kept = folderIds.Where(id => MatchesFolderReadState(rollups, id, readState)).ToList();
        var folderClause = kept.Count == 0
            ? "0"
            : $"cn.Id IN ({string.Join(",", kept.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";

        const string marked = "EXISTS (SELECT 1 FROM read_marks rm WHERE rm.UserId = @user AND rm.ItemId = cn.Id)";
        var inProgress = "EXISTS (SELECT 1 FROM reading_progress rp WHERE rp.UserId = @user AND rp.ItemId = cn.Id AND rp.State = "
            + (int)ReadingState.InProgress + ")";
        var archiveClause = readState switch
        {
            BrowseReadStateFilter.Read => marked,
            BrowseReadStateFilter.Reading => $"NOT {marked} AND {inProgress}",
            _ => $"NOT {marked} AND NOT {inProgress}",
        };
        return $"((cn.Kind = {(int)CatalogNodeKind.Archive} AND {archiveClause}) OR (cn.Kind = {(int)CatalogNodeKind.Folder} AND {folderClause}))";
    }

    /// <summary>
    /// Whether a search result (already carrying its read state) passes a read-state filter - the in-memory twin of
    /// <see cref="SearchReadStateClauseAsync"/>, for the series matches.
    /// </summary>
    internal static bool MatchesSearchReadState(CatalogNodeDto node, BrowseReadStateFilter readState)
    {
        if (readState == BrowseReadStateFilter.All)
            return true;
        if (node.Kind == CatalogNodeKind.Folder)
        {
            return readState switch
            {
                BrowseReadStateFilter.Read => node.ReadRollup == FolderReadRollup.Read,
                BrowseReadStateFilter.Reading => node.ReadRollup == FolderReadRollup.Reading,
                _ => node.ReadRollup is null or FolderReadRollup.Unread,
            };
        }
        var inProgress = !node.IsRead && node.ReadingState == ReadingState.InProgress;
        return readState switch
        {
            BrowseReadStateFilter.Read => node.IsRead,
            BrowseReadStateFilter.Reading => inProgress,
            _ => !node.IsRead && !inProgress,
        };
    }

    private static CatalogNodeDto ReadSearchNode(DbDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("PublicId")),
        ParentId = reader.IsDBNull(reader.GetOrdinal("ParentPublicId"))
            ? ""
            : reader.GetString(reader.GetOrdinal("ParentPublicId")),
        LibraryId = reader.GetString(reader.GetOrdinal("LibraryPublicId")),
        Kind = (CatalogNodeKind)reader.GetInt32(reader.GetOrdinal("Kind")),
        DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
        Availability = (CatalogNodeAvailability)reader.GetInt32(reader.GetOrdinal("Availability")),
    };

    /// <summary>
    /// Card enrichment of search results: folder covers (parity with browse - a folder otherwise renders with no thumbnail; folders with no
    /// readable descendant stay coverless), the favourite star, and (1.45.0) the viewer's read state - for archives the read mark, progress
    /// state and page in ONE query, for folders the read rollup in one recursive CTE. Fixed cost per page.
    /// </summary>
    private async Task<List<CatalogNodeDto>> EnrichSearchNodesAsync(
        List<CatalogNodeDto> nodes, IReadOnlyDictionary<string, long> internalIds, long userId, CancellationToken ct)
    {
        if (nodes.Count == 0)
            return nodes;

        var folders = nodes.Where(n => n.Kind == CatalogNodeKind.Folder).Select(n => (Public: n.Id, Internal: internalIds[n.Id])).ToList();
        if (folders.Count > 0)
            nodes = await ApplyCoversAsync(nodes, folders.Select(f => new CoverTarget(f.Internal, f.Public, true)), ct);

        // Per-user favorite star (1.21.0): one batched join over the page's public ids. Search prominence (badge + boost) is a client-side
        // presentation choice driven by the FavoritesSearchProminence preference; the server only supplies the truthful IsFavorite flag.
        nodes = await ApplyFavoritesAsync(nodes, userId, ct);

        var archiveIds = nodes.Where(n => n.Kind == CatalogNodeKind.Archive).Select(n => internalIds[n.Id]).ToList();
        if (archiveIds.Count > 0)
        {
            var states = new Dictionary<long, (bool IsRead, int? State, int? Ordinal)>();
            var connection = _db.Database.GetDbConnection();
            var wasOpen = connection.State == System.Data.ConnectionState.Open;
            if (!wasOpen) await connection.OpenAsync(ct);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT cn.Id, rm.ItemId IS NOT NULL AS IsRead, rp.State, rp.Ordinal
                    FROM catalog_nodes cn
                    LEFT JOIN read_marks rm ON rm.ItemId = cn.Id AND rm.UserId = @user
                    LEFT JOIN reading_progress rp ON rp.ItemId = cn.Id AND rp.UserId = @user
                    WHERE cn.Id IN ({string.Join(",", archiveIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                    AND (rm.ItemId IS NOT NULL OR rp.ItemId IS NOT NULL)
                    """;
                command.Parameters.Add(new SqliteParameter("@user", userId));
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    states[reader.GetInt64(0)] = (
                        reader.GetInt64(1) != 0,
                        reader.IsDBNull(2) ? null : reader.GetInt32(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3));
                }
            }
            finally
            {
                if (!wasOpen) await connection.CloseAsync();
            }

            // The same fields as browse's EnrichAsync: IsRead from the mark; ReadingState + LastReadPage whenever a progress row exists.
            if (states.Count > 0)
            {
                nodes = nodes.Select(n =>
                {
                    if (n.Kind != CatalogNodeKind.Archive || !states.TryGetValue(internalIds[n.Id], out var s))
                        return n;
                    return s.State is { } state
                        ? n with { IsRead = s.IsRead, ReadingState = (ReadingState)state, LastReadPage = s.Ordinal }
                        : n with { IsRead = s.IsRead };
                }).ToList();
            }
        }

        if (folders.Count > 0)
        {
            var rollups = await ResolveFolderReadRollupsAsync(folders.Select(f => f.Internal).ToList(), userId, ct);
            if (rollups.Count > 0)
            {
                nodes = nodes.Select(n => n.Kind == CatalogNodeKind.Folder && rollups.TryGetValue(internalIds[n.Id], out var rollup)
                    ? n with { ReadRollup = rollup }
                    : n).ToList();
            }
        }

        return nodes;
    }

    /// <summary>
    /// Nodes linked (Confirmed or Auto; never NeedsReview or DontMatch) to a metadata
    /// record with a title matching the query, and (1.34.0) folders that are a "Collection about" such a record. Same visibility as catalog search
    /// (<paramref name="libIds"/> is the viewer's visible set, narrowed to the requested
    /// library); libraries and instances with "Show series information" off are excluded.
    /// Distinct anchors, first 20 by SortKey, each with its shortest matching title. 1.45.0: they carry the viewer's read state, and a
    /// read-state filter keeps only the anchors it would keep among the results.
    /// </summary>
    private async Task<List<SeriesMatchDto>> SearchSeriesMatchesAsync(
        DbConnection connection, long userId, string ftsQuery, string libIds, BrowseReadStateFilter readState, CancellationToken ct)
    {
        var globallyHidden = await _db.AppSettings
            .AnyAsync(s => s.Id == AppSettingsEntity.SingletonId && s.MetadataSeriesInfoHidden, ct);
        if (globallyHidden)
            return [];

        var sql = $"""
            SELECT cn.Id, cn.PublicId, cn.Kind, cn.DisplayName, cn.Availability,
                   parent.PublicId AS ParentPublicId,
                   lib.PublicId AS LibraryPublicId,
                   h.title AS MatchedTitle
            FROM (SELECT record_id, title FROM series_search WHERE series_search MATCH @query) h
            JOIN node_series_links l ON l.RecordId = h.record_id AND l.State IN ({(int)SeriesLinkState.Confirmed}, {(int)SeriesLinkState.Auto}, {(int)SeriesLinkState.CollectionAbout})
            JOIN catalog_nodes cn ON cn.Id = l.NodeId
            LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
            JOIN libraries lib ON cn.LibraryId = lib.Id
            WHERE cn.Availability != 5
            AND cn.LibraryId IN ({libIds})
            AND lib.MetadataSeriesInfoHidden = 0
            ORDER BY cn.SortKey, length(h.title), h.title
            LIMIT 2000
            """;

        // Without a filter the first 20 distinct anchors are the answer; with one, every distinct anchor is a candidate until its read
        // state is known.
        var limit = readState == BrowseReadStateFilter.All ? MaxSeriesMatches : int.MaxValue;
        var nodes = new List<CatalogNodeDto>();
        var titles = new Dictionary<string, string>();
        var internalIds = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.Add(new SqliteParameter("@query", ftsQuery));
            using var reader = await command.ExecuteReaderAsync(ct);
            while (nodes.Count < limit && await reader.ReadAsync(ct))
            {
                var publicId = reader.GetString(reader.GetOrdinal("PublicId"));
                if (titles.ContainsKey(publicId))
                    continue;
                titles[publicId] = reader.GetString(reader.GetOrdinal("MatchedTitle"));
                nodes.Add(ReadSearchNode(reader));
                internalIds[publicId] = reader.GetInt64(reader.GetOrdinal("Id"));
            }
        }

        if (nodes.Count == 0)
            return [];

        if (readState != BrowseReadStateFilter.All)
        {
            nodes = (await EnrichSearchNodesAsync(nodes, internalIds, userId, ct))
                .Where(n => MatchesSearchReadState(n, readState))
                .Take(MaxSeriesMatches)
                .ToList();
        }
        else
        {
            nodes = await EnrichSearchNodesAsync(nodes, internalIds, userId, ct);
        }

        // Every anchor has its OWN confirmed / auto web link and shown series information
        // (the query above), which is exactly the browse (i) rule - no extra query.
        return nodes.Select(n => new SeriesMatchDto { Node = n with { HasSeriesInfo = true }, MatchedTitle = titles[n.Id] }).ToList();
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
}
