namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Ratings;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

// 1.53.0 lane S - shared ratings in browse: the single node's server average (GetNodeAsync) and the "Server rating" browse sort. The
// aggregate's rules (who counts, the Private-library rule, the cache) live in Features/Ratings/SharedRatingsService.cs.

public sealed partial class CatalogBrowseService
{
    /// <summary>The wire value of the "Server rating" browse sort (1.53.0).</summary>
    public const string ServerRatingSort = "serverRating";

    /// <summary>The browse cursor prefix of the "Server rating" sort: an offset, <c>sr:{offset}</c> - the "My rating" pattern.</summary>
    internal const string ServerRatingCursorPrefix = "sr:";

    /// <summary>Parses the "Server rating" offset cursor <c>sr:{offset}</c>; 0 when absent, of another sort, or invalid.</summary>
    private static int ServerRatingOffset(string? cursor)
    {
        if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith(ServerRatingCursorPrefix, StringComparison.Ordinal)
            && int.TryParse(cursor.AsSpan(ServerRatingCursorPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return n;
        return 0;
    }

    /// <summary>Whether the "Server rating" sort may run: only while the admin's switch is on (otherwise browse sorts by name).</summary>
    private Task<bool> ServerRatingSortAllowedAsync(CancellationToken ct) => SharedRatingsService.IsServerEnabledAsync(_db, ct);

    /// <summary>The node's server average for the single-node lookup (null without the shared-ratings service, e.g. bare tests).</summary>
    private Task<ServerRatingDto?> ServerRatingOfAsync(long nodeId, long libraryId, CancellationToken ct) =>
        _sharedRatings is null ? Task.FromResult<ServerRatingDto?>(null) : _sharedRatings.GetAverageAsync(nodeId, libraryId, ct);

    /// <summary>
    /// "Server rating" sort (1.53.0 lane S): the level's nodes by their server average, highest first, then the higher count, then by name
    /// (the catalog key - folders before archives); nodes with no shared rating last, by name. The same ratings count as in the single-node
    /// average (<see cref="SharedRatingsService.CountingRatings"/>; the caller checked the switch). One-directional like "My rating"; ordered
    /// and paged in SQL; the cursor is an offset (<c>sr:{offset}</c>). The page shows no average - only its order.
    /// </summary>
    private async Task<List<BrowseRow>> QueryServerRatingAsync(
        IQueryable<CatalogNodeEntity> baseQuery, long libraryId, string? cursor, int pageSize, CancellationToken ct)
    {
        var counting = SharedRatingsService.CountingRatings(_db, libraryId);
        return await baseQuery
            .Select(n => new
            {
                Node = n,
                Average = counting.Where(r => r.CatalogNodeId == n.Id).Average(r => (double?)r.Score),
                Count = counting.Count(r => r.CatalogNodeId == n.Id),
            })
            .OrderBy(x => x.Average == null)
            .ThenByDescending(x => x.Average)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Node.SortKey)
            .ThenBy(x => x.Node.Id)
            .Skip(ServerRatingOffset(cursor))
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
}
