namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Catalog;

// 1.53.0 lane S (ratings follow-up 5c): the rating: term in the tag view - the viewer's OWN rating, never the server average.

public sealed partial class FacetSearchService
{
    /// <summary>
    /// The <c>rating:</c> term of a tag view (1.53.0, 5c) as a condition: <c>rating:N</c> keeps the works with a location the viewer may see
    /// that the VIEWER rated at least N stars; <c>rating:none</c> keeps the works none of whose visible locations the viewer rated. Resolved
    /// in a pre-pass into an id list (as the read filter is), so the Refine cache's statement names the kept works and a rating change can
    /// never be answered from a stale count. Ids and a score only reach the SQL.
    /// </summary>
    private async Task<string> RatingConditionAsync(SearchRatingTerm term, FacetViewerScope scope, long userId, CancellationToken ct)
    {
        var minScore = term.MinScore ?? 1;
        var rated = await QuerySql.ReadAsync(_db, $"""
            SELECT DISTINCT e.WorkId FROM facet_entries e
            JOIN catalog_nodes n ON n.Id = e.LocationNodeId AND n.Availability != {QuerySql.Tombstoned}
            JOIN user_ratings ur ON ur.CatalogNodeId = e.LocationNodeId AND ur.UserId = @ratingUser AND ur.Score >= @ratingMin
            WHERE e.LibraryId IN ({scope.SqlList})
            """, new Dictionary<string, object?> { ["@ratingUser"] = userId, ["@ratingMin"] = minScore }, r => r.GetInt64(0), ct);
        if (term.MinScore is null)
            return rated.Count == 0 ? "1" : $"w.Id NOT IN ({QuerySql.List(rated)})";
        return rated.Count == 0 ? "0" : $"w.Id IN ({QuerySql.List(rated.Order())})";
    }
}
