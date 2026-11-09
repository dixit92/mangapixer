namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

// 1.52.0 lane R - the reads of the viewer's own ratings in browse and search: the "My rating" browse sort and the rating: search term.
// The MyRating flag on cards rides in ApplyFavoritesAsync / GetNodeAsync (the favourite lookup); the Favorites view's sort and filter
// live in CatalogBrowseService.Favorites.cs / FavoriteStacks.cs.

public sealed partial class CatalogBrowseService
{
    /// <summary>The wire value of the "My rating" sort (browse and Favorites).</summary>
    public const string MyRatingSort = "myRating";

    /// <summary>The browse cursor prefix of the "My rating" sort: an offset, <c>m:{offset}</c> - the recentlyRead pattern (<c>r:</c>).</summary>
    internal const string MyRatingCursorPrefix = "m:";

    /// <summary>Parses the "My rating" offset cursor <c>m:{offset}</c>; 0 when absent, of another sort, or invalid.</summary>
    private static int MyRatingOffset(string? cursor)
    {
        if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith(MyRatingCursorPrefix, StringComparison.Ordinal)
            && int.TryParse(cursor.AsSpan(MyRatingCursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;
        return 0;
    }

    /// <summary>
    /// "My rating" sort (1.52.0 lane R): the level's nodes by the viewer's own score, highest first, then by name (the catalog key - so
    /// folders before archives at one score, as the name sort); nodes the viewer has not rated last, by name. Folders and archives are
    /// rated alike (no rollup). One-directional like the recency sorts (the label bakes it in). Ordered and paged in SQL; the cursor is an
    /// offset into that order (<c>m:{offset}</c>), the recentlyRead pattern.
    /// </summary>
    private async Task<List<BrowseRow>> QueryMyRatingAsync(
        IQueryable<CatalogNodeEntity> baseQuery, long userId, string? cursor, int pageSize, CancellationToken ct)
    {
        var mine = _db.UserRatings.Where(r => r.UserId == userId);
        return await baseQuery
            .Select(n => new { Node = n, Score = mine.Where(r => r.CatalogNodeId == n.Id).Select(r => (int?)r.Score).FirstOrDefault() })
            .OrderBy(x => x.Score == null)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Node.SortKey)
            .ThenBy(x => x.Node.Id)
            .Skip(MyRatingOffset(cursor))
            .Take(pageSize + 1)
            .Select(x => new BrowseRow
            {
                Id = x.Node.PublicId,
                ParentId = x.Node.Parent != null ? x.Node.Parent.PublicId : "",
                LibraryId = x.Node.Library != null ? x.Node.Library.PublicId : "",
                Kind = x.Node.Kind,
                DisplayName = x.Node.DisplayName,
                Availability = x.Node.Availability,
                PageCount = x.Node.ArchiveItem != null ? x.Node.ArchiveItem.PageCount : null,
                InternalId = x.Node.Id,
                CreatedAt = x.Node.CreatedAt,
                SortKey = x.Node.SortKey,
            })
            .ToListAsync(ct);
    }

    /// <summary>
    /// The candidates of a search of only a <c>rating:N</c> minimum (1.52.0 lane R): the viewer's own rated nodes with at least
    /// <paramref name="minScore"/>, live and in the visible libraries (<paramref name="libIds"/>, the search's chokepoint). Ids and a
    /// score only - nothing typed reaches the SQL.
    /// </summary>
    private static string RatedSearchCandidatesSql(long userId, int minScore, string libIds) => $"""
        SELECT cn.Id, cn.Kind, cn.DisplayName, cn.SortKey, cn.ParentId
        FROM user_ratings ur
        JOIN catalog_nodes cn ON cn.Id = ur.CatalogNodeId
        WHERE ur.UserId = {userId.ToString(CultureInfo.InvariantCulture)} AND ur.Score >= {minScore.ToString(CultureInfo.InvariantCulture)}
        AND cn.Availability != 5 AND cn.LibraryId IN ({libIds})
        """;

    /// <summary>The viewer's own ratings (node id -> score), one query - for the <c>rating:</c> term (1.52.0 lane R).</summary>
    private static async Task<Dictionary<long, int>> ViewerRatingsAsync(DbConnection connection, long userId, CancellationToken ct)
    {
        var scores = new Dictionary<long, int>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CatalogNodeId, Score FROM user_ratings WHERE UserId = $user";
        var p = command.CreateParameter();
        p.ParameterName = "$user";
        p.Value = userId;
        command.Parameters.Add(p);
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            scores[reader.GetInt64(0)] = reader.GetInt32(1);
        return scores;
    }

    /// <summary>The hits whose node the viewer's own rating lets through <paramref name="term"/> (1.52.0 lane R).</summary>
    private static List<SearchHit> KeepByRating(List<SearchHit> hits, SearchRatingTerm term, Dictionary<long, int> scores) =>
        hits.Where(h => term.Accepts(scores.TryGetValue(h.Id, out var score) ? score : null)).ToList();
}
