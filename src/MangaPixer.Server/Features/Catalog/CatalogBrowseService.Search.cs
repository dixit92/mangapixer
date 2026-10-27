namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Catalog search (moved out of the browse file in 1.45.0 phase K4, when search gained HTTP paging, the viewer's read state and the
/// read-state filter). 1.49.0 (lane S, owner decisions 4-6): words in any order over NAMES (FTS5 trigram, accents folded), typo-tolerant
/// close matches when exact hits are few, ranked exact > starts with > a word starts with > contains, favourites lifted on the server; an
/// archive found only through its folder is counted on that folder's card ("+N inside"). The viewer's visible libraries only.
/// </summary>
public sealed partial class CatalogBrowseService
{
    /// <summary>Most results one search page returns (1.45.0); a request for fewer than one gets one.</summary>
    public const int MaxSearchPageSize = 100;

    /// <summary>Close (typo-tolerant) matches are looked for only while the exact hits are fewer than this (1.49.0, decision 5 b).</summary>
    public const int CloseMatchesBelow = 10;

    /// <summary>At most this many close matches are listed, after every exact hit.</summary>
    public const int MaxCloseMatches = 50;

    /// <summary>The close-match candidates read from the index: the best ones by FTS rank (shared trigrams).</summary>
    private const int MaxCloseCandidates = 2000;

    private const int MaxSeriesMatches = 20;

    /// <summary>The 1.49.0 cursor prefix: <c>s2:&lt;group&gt;:&lt;rank&gt;:&lt;id&gt;:&lt;SortKey&gt;</c>.</summary>
    private const string SearchCursorPrefix = "s2:";

    /// <summary>A name the index (or the short-search scan) returned: internal id, kind, display name, sort key.</summary>
    private readonly record struct SearchCandidate(long Id, int Kind, string Name, string SortKey);

    /// <summary>
    /// A listed hit and its place in the order: <see cref="Group"/> 0 = exact favourite, 1 = exact, 2 = close favourite, 3 = close (exact
    /// hits always before close ones; favourites lifted only when the viewer turned "Favorites in search" on); <see cref="Rank"/> the tier
    /// (exact) or the typo distance (close).
    /// </summary>
    private readonly record struct SearchHit(long Id, int Kind, string SortKey, int Group, int Rank);

    /// <summary>
    /// Searches catalog node NAMES (1.49.0). Authorization (the visible libraries, Incognito applied) comes first; every hit list is built
    /// per request (no cache). The text is parsed into words (<see cref="SearchText.Parse"/>): every word of 3+ characters must be in the
    /// name (any order, accents and punctuation ignored), a 1-2 character word must start a word of it, a quoted phrase must appear as
    /// typed; a search with no word of 3+ characters matches names with a word starting with each word. When fewer than
    /// <see cref="CloseMatchesBelow"/> exact hits remain, up to <see cref="MaxCloseMatches"/> close matches follow them.
    ///
    /// <paramref name="readState"/> keeps only Read / Reading / Unread results by browse's Show filter rule (archives by mark and progress,
    /// folders by rollup, an empty folder counts as Unread) before paging, so the page, the cursor and the counts agree. The order is
    /// (exact before close, favourites, rank, SortKey - folders first - , id) and the cursor <c>s2:&lt;group&gt;:&lt;rank&gt;:&lt;id&gt;:&lt;SortKey&gt;</c>
    /// is opaque; a cursor of an older form restarts at the first page.
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
        var parsed = SearchText.Parse(query);
        if (parsed.IsEmpty)
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

        var libIds = string.Join(",", visibleLibs);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            // 1. Exact hits: the index's candidates (or, for a short search, every visible live name), checked and ranked here.
            var exactFts = SearchText.ExactFtsQuery(parsed);
            var candidates = exactFts is null
                ? await ReadSearchCandidatesAsync(connection, $"""
                    SELECT cn.Id, cn.Kind, cn.DisplayName, cn.SortKey
                    FROM catalog_nodes cn
                    WHERE cn.Availability != 5 AND cn.LibraryId IN ({libIds})
                    """, null, ct)
                : await ReadSearchCandidatesAsync(connection, $"""
                    SELECT cn.Id, cn.Kind, cn.DisplayName, cn.SortKey
                    FROM catalog_search cs
                    JOIN catalog_nodes cn ON cn.Id = cs.node_id
                    WHERE catalog_search MATCH @query
                    AND cn.Availability != 5 AND cn.LibraryId IN ({libIds})
                    """, exactFts, ct);

            var exact = new List<SearchHit>();
            foreach (var c in candidates)
            {
                if (SearchText.Rank(parsed, SearchText.FoldName(c.Name, c.Kind == (int)CatalogNodeKind.Archive)) is { } rank)
                    exact.Add(new SearchHit(c.Id, c.Kind, c.SortKey, 1, rank));
            }
            // Every name hit, before the read filter: "+N inside" never counts an archive that is a hit of its own.
            var nameHitIds = exact.Select(h => h.Id).ToHashSet();
            if (readState != BrowseReadStateFilter.All)
                exact = await KeepByReadStateAsync(connection, exact, userId, readState, ct);

            // 2. Close matches (decision 5 b): only while the exact hits are few, always after them.
            var close = new List<SearchHit>();
            if (exact.Count < CloseMatchesBelow && SearchText.FuzzyFtsQuery(parsed) is { } fuzzyFts)
            {
                var fuzzyCandidates = await ReadSearchCandidatesAsync(connection, $"""
                    SELECT cn.Id, cn.Kind, cn.DisplayName, cn.SortKey
                    FROM catalog_search cs
                    JOIN catalog_nodes cn ON cn.Id = cs.node_id
                    WHERE catalog_search MATCH @query
                    AND cn.Availability != 5 AND cn.LibraryId IN ({libIds})
                    ORDER BY cs.rank
                    LIMIT {MaxCloseCandidates}
                    """, fuzzyFts, ct);
                foreach (var c in fuzzyCandidates)
                {
                    if (nameHitIds.Contains(c.Id))
                        continue;
                    if (SearchText.FuzzyDistance(parsed, SearchText.FoldName(c.Name, c.Kind == (int)CatalogNodeKind.Archive)) is { } distance)
                        close.Add(new SearchHit(c.Id, c.Kind, c.SortKey, 3, distance));
                }
                if (readState != BrowseReadStateFilter.All)
                    close = await KeepByReadStateAsync(connection, close, userId, readState, ct);
                close = close.OrderBy(h => h.Rank).ThenBy(h => h.SortKey, StringComparer.Ordinal).ThenBy(h => h.Id).Take(MaxCloseMatches).ToList();
                nameHitIds.UnionWith(close.Select(h => h.Id));
            }

            // 3. Favourites lifted on the server (decision 6; before 1.49.0 per page on the client), when the viewer turned it on.
            var favorites = await SearchFavoritesAsync(userId, ct);
            var hits = exact.Concat(close)
                .Select(h => favorites.Contains(h.Id) ? h with { Group = h.Group - 1 } : h)
                .OrderBy(h => h.Group).ThenBy(h => h.Rank).ThenBy(h => h.SortKey, StringComparer.Ordinal).ThenBy(h => h.Id)
                .ToList();

            // 4. The page after the cursor.
            var position = ParseSearchCursor(cursor);
            var from = position is { } at ? hits.FindIndex(h => CompareToCursor(h, at) > 0) : 0;
            if (from < 0)
                from = hits.Count;
            var pageHits = hits.Skip(from).Take(pageSize + 1).ToList();
            var hasMore = pageHits.Count > pageSize;
            if (hasMore)
                pageHits = pageHits.Take(pageSize).ToList();
            var nextCursor = hasMore ? EncodeSearchCursor(pageHits[^1]) : null;

            var (results, internalIds) = await ReadSearchNodesAsync(connection, pageHits.Select(h => h.Id).ToList(), ct);
            var insideCounts = await InsideCountsAsync(connection,
                pageHits.Where(h => h.Kind == (int)CatalogNodeKind.Folder).Select(h => h.Id).ToList(), nameHitIds, ct);

            // Alt-title matches (1.26.0): first page only, one extra query; 1.49.0 the same words and rank.
            var anchors = position is null && exactFts is not null
                ? await SeriesMatchAnchorsAsync(connection, parsed, exactFts, libIds, readState, ct)
                : null;

            // 1.46.0 (lane B, measured cost #10): ONE enrichment pass for the results and the series-match anchors together (it ran once
            // for each); a node's enrichment depends on that node only, so a node in both lists gets the same card either way.
            var cards = results.ToList();
            var cardIds = new Dictionary<string, long>(internalIds, StringComparer.Ordinal);
            foreach (var anchor in anchors?.Nodes ?? [])
            {
                if (cardIds.TryAdd(anchor.Id, anchors!.InternalIds[anchor.Id]))
                    cards.Add(anchor);
            }
            var enriched = (await EnrichSearchNodesAsync(cards, cardIds, userId, ct)).ToDictionary(n => n.Id, StringComparer.Ordinal);
            results = results.Select(n => enriched[n.Id]).ToList();

            // Series information (1.27.0): the same (i) flag as browse, for the card (i) and
            // the hover summary. Batched, fixed cost per page.
            results = await ApplyHasSeriesInfoByPublicIdAsync(results, ct);

            var seriesMatches = anchors is null ? (position is null ? [] : null) : SeriesMatchesOf(anchors, enriched, readState);

            var folderCount = hits.Count(h => h.Kind == (int)CatalogNodeKind.Folder);
            _logger?.LogDebug(LogEvents.Search.Answered,
                "Search answered: {Words} words, {Hits} hits ({Close} close), page {PageCount}, {ElapsedMs} ms",
                parsed.AllWords.Count, hits.Count, close.Count, results.Count, started.ElapsedMilliseconds);

            return new SearchResultsDto
            {
                Query = query,
                Items = results,
                TotalCount = hits.Count,
                NextCursor = nextCursor,
                HasMore = hasMore,
                SeriesMatches = seriesMatches,
                FolderCount = folderCount,
                ArchiveCount = hits.Count - folderCount,
                CloseMatchCount = close.Count,
                InsideCounts = insideCounts.Count == 0
                    ? null
                    : internalIds.Where(p => insideCounts.ContainsKey(p.Value))
                        .ToDictionary(p => p.Key, p => insideCounts[p.Value], StringComparer.Ordinal),
            };
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    /// <summary>Reads search candidates (id, kind, name, sort key) for one SQL text; <paramref name="ftsQuery"/> binds <c>@query</c>.</summary>
    private static async Task<List<SearchCandidate>> ReadSearchCandidatesAsync(
        DbConnection connection, string sql, string? ftsQuery, CancellationToken ct)
    {
        var list = new List<SearchCandidate>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (ftsQuery is not null)
            command.Parameters.Add(new SqliteParameter("@query", ftsQuery));
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new SearchCandidate(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        return list;
    }

    /// <summary>The page's nodes (public ids, parent and library public ids) in the hits' order.</summary>
    private static async Task<(List<CatalogNodeDto> Nodes, Dictionary<string, long> InternalIds)> ReadSearchNodesAsync(
        DbConnection connection, List<long> ids, CancellationToken ct)
    {
        var byId = new Dictionary<long, CatalogNodeDto>();
        if (ids.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT cn.Id, cn.PublicId, cn.Kind, cn.DisplayName, cn.Availability,
                       parent.PublicId AS ParentPublicId,
                       lib.PublicId AS LibraryPublicId
                FROM catalog_nodes cn
                LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
                JOIN libraries lib ON cn.LibraryId = lib.Id
                WHERE cn.Id IN ({string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                """;
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                byId[reader.GetInt64(reader.GetOrdinal("Id"))] = ReadSearchNode(reader);
        }
        var nodes = new List<CatalogNodeDto>();
        var internalIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var node))
                continue;
            nodes.Add(node);
            internalIds[node.Id] = id;
        }
        return (nodes, internalIds);
    }

    /// <summary>
    /// "+N inside" (1.49.0, decision 4): per folder on the page, the live archives below it that are not hits of their own - archives
    /// found only through the folder's name. A sub-folder that is itself a hit has its own card: its archives are not counted again.
    /// Independent of the read filter. One recursive walk per page; the hit ids travel as one JSON parameter.
    /// </summary>
    private static async Task<Dictionary<long, int>> InsideCountsAsync(
        DbConnection connection, List<long> folderIds, HashSet<long> hitIds, CancellationToken ct)
    {
        var counts = new Dictionary<long, int>();
        if (folderIds.Count == 0)
            return counts;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE hits(id) AS MATERIALIZED (SELECT value FROM json_each(@hits)),
            d(root, id, kind) AS (
                SELECT c.ParentId, c.Id, c.Kind FROM catalog_nodes c
                WHERE c.ParentId IN ({string.Join(",", folderIds.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                AND c.Availability != 5
                UNION ALL
                SELECT d.root, c.Id, c.Kind FROM d
                CROSS JOIN catalog_nodes c ON c.ParentId = d.id
                WHERE d.kind = {(int)CatalogNodeKind.Folder} AND c.Availability != 5 AND d.id NOT IN (SELECT id FROM hits)
            )
            SELECT root, COUNT(*) FROM d
            WHERE kind = {(int)CatalogNodeKind.Archive} AND id NOT IN (SELECT id FROM hits)
            GROUP BY root
            """;
        command.Parameters.Add(new SqliteParameter("@hits",
            "[" + string.Join(",", hitIds.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]"));
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var n = reader.GetInt32(1);
            if (n > 0)
                counts[reader.GetInt64(0)] = n;
        }
        return counts;
    }

    /// <summary>
    /// The read-state filter over hits (1.45.0 rule, 1.49.0 in memory): an ARCHIVE is Read with the viewer's sticky read mark, Reading
    /// without one and with in-progress progress, Unread with neither; a FOLDER by its descendant read rollup (Unread also keeps a folder
    /// with nothing readable).
    /// </summary>
    private async Task<List<SearchHit>> KeepByReadStateAsync(
        DbConnection connection, List<SearchHit> hits, long userId, BrowseReadStateFilter readState, CancellationToken ct)
    {
        if (hits.Count == 0)
            return hits;
        var archiveIds = hits.Where(h => h.Kind == (int)CatalogNodeKind.Archive).Select(h => h.Id).ToHashSet();
        var marked = new HashSet<long>();
        var inProgress = new HashSet<long>();
        if (archiveIds.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT ItemId, 0 FROM read_marks WHERE UserId = @user
                UNION ALL
                SELECT ItemId, 1 FROM reading_progress WHERE UserId = @user AND State = {(int)ReadingState.InProgress}
                """;
            command.Parameters.Add(new SqliteParameter("@user", userId));
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                (reader.GetInt32(1) == 0 ? marked : inProgress).Add(reader.GetInt64(0));
        }
        var folderIds = hits.Where(h => h.Kind == (int)CatalogNodeKind.Folder).Select(h => h.Id).ToList();
        var rollups = await ResolveFolderReadRollupsAsync(folderIds, userId, ct);
        return hits.Where(h =>
        {
            if (h.Kind == (int)CatalogNodeKind.Folder)
                return MatchesFolderReadState(rollups, h.Id, readState);
            var read = marked.Contains(h.Id);
            var reading = !read && inProgress.Contains(h.Id);
            return readState switch
            {
                BrowseReadStateFilter.Read => read,
                BrowseReadStateFilter.Reading => reading,
                _ => !read && !reading,
            };
        }).ToList();
    }

    /// <summary>The viewer's favourites (internal node ids) when their "Favorites in search" preference is on; empty otherwise.</summary>
    private async Task<HashSet<long>> SearchFavoritesAsync(long userId, CancellationToken ct)
    {
        var lifted = await _db.ReaderPreferences.Where(p => p.UserId == userId).Select(p => p.FavoritesSearchProminence).FirstOrDefaultAsync(ct);
        if (!lifted)
            return [];
        return (await _db.Favorites.Where(f => f.UserId == userId).Select(f => f.CatalogNodeId).ToListAsync(ct)).ToHashSet();
    }

    private static int CompareToCursor(SearchHit hit, (int Group, int Rank, long Id, string SortKey) at)
    {
        var c = hit.Group.CompareTo(at.Group);
        if (c != 0) return c;
        c = hit.Rank.CompareTo(at.Rank);
        if (c != 0) return c;
        c = string.CompareOrdinal(hit.SortKey, at.SortKey);
        return c != 0 ? c : hit.Id.CompareTo(at.Id);
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

    /// <summary>The search cursor (1.49.0): <c>s2:&lt;group&gt;:&lt;rank&gt;:&lt;internal id&gt;:&lt;SortKey&gt;</c> of the page's last hit.</summary>
    private static string EncodeSearchCursor(SearchHit hit) =>
        SearchCursorPrefix + hit.Group.ToString(CultureInfo.InvariantCulture) + ":" + hit.Rank.ToString(CultureInfo.InvariantCulture) + ":"
        + hit.Id.ToString(CultureInfo.InvariantCulture) + ":" + hit.SortKey;

    /// <summary>
    /// The position after which a search page starts (1.49.0 form only). Null for no cursor, a malformed one, or a cursor of the forms
    /// before 1.49.0 (<c>k:&lt;id&gt;:&lt;SortKey&gt;</c>, a bare SortKey) - the order changed, so those restart at the first page.
    /// </summary>
    internal static (int Group, int Rank, long Id, string SortKey)? ParseSearchCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !cursor.StartsWith(SearchCursorPrefix, StringComparison.Ordinal))
            return null;
        var parts = cursor[SearchCursorPrefix.Length..].Split(':', 4);
        return parts.Length == 4
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var group)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var rank)
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (group, rank, id, parts[3])
            : null;
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

    /// <summary>The series-match anchors before enrichment (<see cref="SeriesMatchAnchorsAsync"/>).</summary>
    private sealed record SeriesMatchAnchors(
        List<CatalogNodeDto> Nodes, Dictionary<string, string> Titles, Dictionary<string, long> InternalIds);

    /// <summary>
    /// Nodes linked (Confirmed or Auto; never NeedsReview or DontMatch) to a metadata
    /// record with a title matching the query, and (1.34.0) folders that are a "Collection about" such a record. Same visibility as catalog search
    /// (<paramref name="libIds"/> is the viewer's visible set, narrowed to the requested
    /// library); libraries and instances with "Show series information" off are excluded.
    /// 1.49.0: the same words as the node search (<paramref name="ftsQuery"/>, accents folded) and the same rank tiers, by each anchor's best
    /// matching title (then the shortest title), then SortKey; at most 20 distinct anchors. 1.45.0: they carry the viewer's read state, and a
    /// read-state filter keeps only the anchors it would keep among the results (<see cref="SeriesMatchesOf"/>, after the one enrichment
    /// pass of the search - 1.46.0). An empty set when series information is hidden or nothing matches.
    /// </summary>
    private async Task<SeriesMatchAnchors> SeriesMatchAnchorsAsync(
        DbConnection connection, SearchQuery parsed, string ftsQuery, string libIds, BrowseReadStateFilter readState, CancellationToken ct)
    {
        var nodes = new List<CatalogNodeDto>();
        var titles = new Dictionary<string, string>();
        var internalIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var globallyHidden = await _db.AppSettings
            .AnyAsync(s => s.Id == AppSettingsEntity.SingletonId && s.MetadataSeriesInfoHidden, ct);
        if (globallyHidden)
            return new SeriesMatchAnchors(nodes, titles, internalIds);

        var sql = $"""
            SELECT cn.Id, cn.PublicId, cn.Kind, cn.DisplayName, cn.Availability, cn.SortKey,
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
            LIMIT 5000
            """;

        // Per anchor: its best title by (rank, length, ordinal).
        var best = new Dictionary<long, (int Rank, string Title, string SortKey, CatalogNodeDto Node)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.Add(new SqliteParameter("@query", ftsQuery));
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var title = reader.GetString(reader.GetOrdinal("MatchedTitle"));
                if (SearchText.Rank(parsed, SearchText.Fold(title)) is not { } rank)
                    continue;
                var id = reader.GetInt64(reader.GetOrdinal("Id"));
                if (best.TryGetValue(id, out var seen)
                    && (seen.Rank, seen.Title.Length).CompareTo((rank, title.Length)) is var cmp
                    && (cmp < 0 || (cmp == 0 && string.CompareOrdinal(seen.Title, title) <= 0)))
                    continue;
                best[id] = (rank, title, reader.GetString(reader.GetOrdinal("SortKey")), seen.Node ?? ReadSearchNode(reader));
            }
        }

        // Without a filter the first 20 anchors are the answer; with one, every anchor is a candidate until its read state is known.
        var limit = readState == BrowseReadStateFilter.All ? MaxSeriesMatches : int.MaxValue;
        foreach (var (id, anchor) in best.OrderBy(kv => kv.Value.Rank).ThenBy(kv => kv.Value.SortKey, StringComparer.Ordinal).ThenBy(kv => kv.Key).Take(limit))
        {
            titles[anchor.Node.Id] = anchor.Title;
            nodes.Add(anchor.Node);
            internalIds[anchor.Node.Id] = id;
        }
        return new SeriesMatchAnchors(nodes, titles, internalIds);
    }

    /// <summary>
    /// The series matches from their enriched anchors (by public id): a read-state filter keeps the anchors it would keep among the results,
    /// at most 20. Every anchor has its OWN confirmed / auto web link and shown series information (the anchor query), which is exactly the
    /// browse (i) rule - no extra query.
    /// </summary>
    private static List<SeriesMatchDto> SeriesMatchesOf(SeriesMatchAnchors anchors, IReadOnlyDictionary<string, CatalogNodeDto> enriched,
        BrowseReadStateFilter readState)
    {
        if (anchors.Nodes.Count == 0)
            return [];
        var nodes = anchors.Nodes.Select(n => enriched[n.Id]);
        if (readState != BrowseReadStateFilter.All)
            nodes = nodes.Where(n => MatchesSearchReadState(n, readState)).Take(MaxSeriesMatches);
        return nodes.Select(n => new SeriesMatchDto { Node = n with { HasSeriesInfo = true }, MatchedTitle = anchors.Titles[n.Id] }).ToList();
    }
}
