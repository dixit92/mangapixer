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

/// <summary>
/// The server average of one node (1.53.0 shared ratings, <c>CatalogNodeDto.ServerRating</c>): the mean <see cref="Average"/> of the shared
/// ratings on the 1-10 scale (shown as stars: <c>Average / 2</c>) and how many ratings it is made of. An aggregate only - never who rated.
/// </summary>
public sealed record ServerRatingDto
{
    /// <summary>The mean score, 1-10, rounded to two decimals (7.5 = 3.75 stars).</summary>
    public double Average { get; init; }

    /// <summary>How many shared ratings the average is made of (at least 1).</summary>
    public int Count { get; init; }
}

/// <summary>
/// <c>GET /api/v1/ratings/sharing</c> (1.53.0): the current user's "Share my ratings" choice and whether the administrator turned shared
/// ratings on for the server (the Settings checkbox is disabled while it is off).
/// </summary>
public sealed record RatingSharingDto
{
    /// <summary>Whether the user's ratings count in the server averages (on by default).</summary>
    public bool Share { get; init; }

    /// <summary>Whether the server-wide "Shared ratings" switch is on (Administration).</summary>
    public bool ServerEnabled { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/ratings/sharing</c> (1.53.0): the current user's "Share my ratings" choice.</summary>
public sealed record SetRatingSharingRequest
{
    public bool Share { get; init; }
}

/// <summary>
/// <c>GET</c> / <c>PUT /api/v1/admin/ratings/sharing</c> (1.53.0): the server-wide "Shared ratings" switch - off until an admin turns it on.
/// While it is off no average is shown anywhere and the "Server rating" sort is not offered; no rating is deleted.
/// </summary>
public sealed record AdminRatingSharingDto
{
    public bool Enabled { get; init; }
}
