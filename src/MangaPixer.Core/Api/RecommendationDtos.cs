namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The answer of <c>GET /api/v1/recommendations</c> (Home "Recommended for you") and <c>GET /api/v1/nodes/{nodeId}/similar</c> (the series
/// page's "More like this"), 1.53.0. Content-based and local only: every pick is a series ON this server the viewer can see, scored by what
/// it shares with the viewer's own ratings, favorites and finished series (Home) or with the page's series (similar). Empty when there is
/// no seed or nothing scores above the floor - the row is then not shown.
/// </summary>
public sealed record RecommendationsDto
{
    /// <summary>The picks, best first (at most the asked limit).</summary>
    public required IReadOnlyList<RecommendationDto> Items { get; init; }
}

/// <summary>One pick: the card of its best visible location, and the one reason it was picked.</summary>
public sealed record RecommendationDto
{
    /// <summary>
    /// The card, enriched like a browse card (cover, read state or folder rollup, the favourite star, the viewer's own rating, the (i)
    /// flag). A work in several locations is one pick, at its first visible location (library name, then the folder's sort order).
    /// </summary>
    public required CatalogNodeDto Node { get; init; }

    public required RecommendationReasonDto Reason { get; init; }
}

/// <summary>What a reason is about.</summary>
public enum RecommendationReasonKind
{
    /// <summary>"Same author: &lt;creator&gt;" - a shared writer or artist contributed most.</summary>
    Creator,

    /// <summary>"Like &lt;series&gt;: horror, school life" (Home) or "Horror, School Life" (similar) - the top two shared values.</summary>
    SharedValues,

    /// <summary>"Sequel of &lt;series&gt;", "Side story of ...", "Related to ..." - the linked records name each other.</summary>
    Relation,
}

/// <summary>
/// Why a pick appears: the largest contributor to its score, as one line. It names only things on this server the viewer can see (a
/// creator or tag of a visible series, the title of one of the viewer's own seeds).
/// </summary>
public sealed record RecommendationReasonDto
{
    public required RecommendationReasonKind Kind { get; init; }

    /// <summary>The line to show (one line, plain text).</summary>
    public required string Display { get; init; }

    /// <summary>The seed series the reason names (its card's node), when it names one; null for <see cref="RecommendationReasonKind.Creator"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RelatedNodeId { get; init; }
}
