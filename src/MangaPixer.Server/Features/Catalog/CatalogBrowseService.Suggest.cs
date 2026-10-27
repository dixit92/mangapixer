namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Quick results (1.49.0, lane O): the "top N per kind" entry point of the plain search. It reuses lane S's candidates, word rules and
/// rank tiers (<see cref="SearchText"/>, <c>ReadSearchCandidatesAsync</c>, <c>SeriesMatchAnchorsAsync</c>) - no second copy of the SQL -
/// and stops after the best few folders, archives and linked series.
/// </summary>
public sealed partial class CatalogBrowseService
{
    /// <summary>One quick-result node: public ids, the kind, the name, the folder it sits in, and (a series) the title that matched.</summary>
    internal sealed record SuggestNode(string Id, string LibraryId, CatalogNodeKind Kind, string Name, string? Place, string? Aka);

    /// <summary>The best nodes of one text, per kind, in rank order.</summary>
    internal sealed record SuggestNodes(IReadOnlyList<SuggestNode> Series, IReadOnlyList<SuggestNode> Folders, IReadOnlyList<SuggestNode> Archives)
    {
        public static readonly SuggestNodes Empty = new([], [], []);
    }

    /// <summary>
    /// The best <paramref name="perKind"/> folders, archives and linked series for <paramref name="parsed"/>, ranked exactly as the search
    /// ranks them (exact, starts with, a word starts, contains; favourites first when the viewer turned them on; folders by sort key).
    /// The viewer's visible libraries come first (Incognito applied); series-information-hidden libraries give no series. A text with no
    /// word of 3+ characters answers nothing here: the short-input scan reads every visible name, too much for a box that asks on each
    /// keystroke. No typo pass (the scan costs 50-90 ms on 40 k nodes). A linked series' node is listed as a series only, as in the search.
    /// </summary>
    internal async Task<SuggestNodes> SuggestNodesAsync(long userId, SearchQuery parsed, bool incognito, int perKind, CancellationToken ct)
    {
        if (parsed.IndexWords.Count == 0 || SearchText.ExactFtsQuery(parsed) is not { } exactFts)
            return SuggestNodes.Empty;

        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
            return SuggestNodes.Empty;
        var libIds = string.Join(",", visibleLibs);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            var anchors = await SeriesMatchAnchorsAsync(connection, parsed, exactFts, libIds, BrowseReadStateFilter.All, ct);
            var anchorIds = anchors.InternalIds.Values.ToHashSet();

            var candidates = await ReadSearchCandidatesAsync(connection, $"""
                SELECT cn.Id, cn.Kind, cn.DisplayName, cn.SortKey
                FROM catalog_search cs
                JOIN catalog_nodes cn ON cn.Id = cs.node_id
                WHERE catalog_search MATCH @query
                AND cn.Availability != 5 AND cn.LibraryId IN ({libIds})
                """, exactFts, ct);
            var favorites = await SearchFavoritesAsync(userId, ct);

            var hits = new List<SearchHit>();
            foreach (var c in candidates)
            {
                if (anchorIds.Contains(c.Id))
                    continue;
                if (SearchText.Rank(parsed, SearchText.FoldName(c.Name, c.Kind == (int)CatalogNodeKind.Archive)) is { } rank)
                    hits.Add(new SearchHit(c.Id, c.Kind, c.SortKey, favorites.Contains(c.Id) ? 0 : 1, rank));
            }
            var ordered = hits.OrderBy(h => h.Group).ThenBy(h => h.Rank).ThenBy(h => h.SortKey, StringComparer.Ordinal).ThenBy(h => h.Id).ToList();
            var folders = ordered.Where(h => h.Kind == (int)CatalogNodeKind.Folder).Take(perKind).ToList();
            var archives = ordered.Where(h => h.Kind == (int)CatalogNodeKind.Archive).Take(perKind).ToList();
            var series = anchors.Nodes.Take(perKind).ToList();

            var details = await ReadSuggestNodesAsync(connection,
                folders.Concat(archives).Select(h => h.Id).Concat(series.Select(s => anchors.InternalIds[s.Id])).ToList(), ct);
            SuggestNode? Node(long id) => details.GetValueOrDefault(id);

            var result = new SuggestNodes(
                series.Select(s =>
                    {
                        var node = Node(anchors.InternalIds[s.Id]);
                        var title = anchors.Titles.GetValueOrDefault(s.Id);
                        return node is null ? null : node with
                        {
                            Aka = title is not null && SearchText.Fold(title) != SearchText.Fold(node.Name) ? title : null,
                        };
                    })
                    .OfType<SuggestNode>().ToList(),
                folders.Select(h => Node(h.Id)).OfType<SuggestNode>().ToList(),
                archives.Select(h => Node(h.Id)).OfType<SuggestNode>().ToList());
            _logger?.LogDebug(LogEvents.QuickResults.NodesAnswered,
                "Quick results nodes: {Words} words, {Candidates} candidates, {Hits} hits, {Series}/{Folders}/{Archives} listed, {ElapsedMs} ms",
                parsed.AllWords.Count, candidates.Count, hits.Count, result.Series.Count, result.Folders.Count, result.Archives.Count,
                started.ElapsedMilliseconds);
            return result;
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// The titles of linked series (a title or alternative title the typed words match) for <c>series:</c> suggestions: distinct, ranked as
    /// the series matches are, at most <paramref name="limit"/>. Libraries that show no series information give none.
    /// </summary>
    internal async Task<IReadOnlyList<string>> SuggestSeriesTitlesAsync(long userId, SearchQuery parsed, bool incognito, int limit, CancellationToken ct)
    {
        if (parsed.IndexWords.Count == 0 || SearchText.ExactFtsQuery(parsed) is not { } exactFts)
            return [];
        var visibleLibs = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
        if (visibleLibs.Count == 0)
            return [];

        var connection = _db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(ct);
        try
        {
            var anchors = await SeriesMatchAnchorsAsync(connection, parsed, exactFts, string.Join(",", visibleLibs), BrowseReadStateFilter.All, ct);
            return anchors.Nodes
                .Select(n => anchors.Titles.GetValueOrDefault(n.Id))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }
        finally
        {
            if (!wasOpen) await connection.CloseAsync();
        }
    }

    /// <summary>The listed nodes' public ids, library and the display name of the folder they sit in.</summary>
    private static async Task<Dictionary<long, SuggestNode>> ReadSuggestNodesAsync(DbConnection connection, List<long> ids, CancellationToken ct)
    {
        var byId = new Dictionary<long, SuggestNode>();
        if (ids.Count == 0)
            return byId;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT cn.Id, cn.PublicId, cn.Kind, cn.DisplayName, parent.DisplayName, lib.PublicId
            FROM catalog_nodes cn
            LEFT JOIN catalog_nodes parent ON cn.ParentId = parent.Id
            JOIN libraries lib ON cn.LibraryId = lib.Id
            WHERE cn.Id IN ({string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)))})
            """;
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            byId[reader.GetInt64(0)] = new SuggestNode(
                reader.GetString(1), reader.GetString(5), (CatalogNodeKind)reader.GetInt32(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), null);
        }
        return byId;
    }
}
