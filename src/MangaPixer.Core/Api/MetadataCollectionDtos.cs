namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// 1.34.0: marks a folder "Collection about" a series record (a folder of works about it - fan works). The record is fetched and stored
/// first when it is not stored yet (one gated GET, as for a link).
/// </summary>
public sealed record SetCollectionAboutRequest
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }

    /// <summary>How the admin found the record (search / reference); stored on the row.</summary>
    public MetadataMatchMethod? MatchMethod { get; init; }

    /// <summary>
    /// Also set the folder's Content to "Doujinshi &amp; adult one-shots" when its effective Content is another one (default true):
    /// automatic searches only include doujinshi records below such a folder, so the works inside can be found.
    /// </summary>
    public bool SetDoujinContent { get; init; } = true;
}

/// <summary>1.34.0: accepts a Needs-review folder as a collection about one of its stored candidates.</summary>
public sealed record MetadataReviewAcceptCollectionRequest
{
    /// <summary>The candidate's rank (1-based).</summary>
    public required int Rank { get; init; }
}

/// <summary>1.34.0: what marking a folder "Collection about" did.</summary>
public sealed record CollectionAboutResultDto
{
    public required NodeSeriesLinkChangeDto Change { get; init; }

    /// <summary>The folder's Content was set to "Doujinshi &amp; adult one-shots" by this call.</summary>
    public bool ContentSet { get; init; }

    /// <summary>
    /// Works at and below the folder queued for automatic matching now (0 while automatic matching or the library's "Fetch from the
    /// web" is off - "Match this library now" finds them then).
    /// </summary>
    public int Queued { get; init; }
}
