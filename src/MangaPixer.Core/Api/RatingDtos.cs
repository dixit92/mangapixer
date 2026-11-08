namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Body of <c>PUT /api/v1/nodes/{nodeId}/rating</c> (1.52.0): the current user's score of the node, an integer 1-10 shown as 5 stars with
/// halves (1 = half a star, 7 = three and a half, 10 = five). Any other value is refused with 400.
/// </summary>
public sealed record SetRatingRequest
{
    /// <summary>The score, 1-10.</summary>
    public int Score { get; init; }
}
