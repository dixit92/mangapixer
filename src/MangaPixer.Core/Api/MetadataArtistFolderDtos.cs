namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// 1.37.0: marks a folder an artist's folder - the works of one creator, each a work of its own. The artist becomes the folder's
/// declared creator. Both fields are optional: the name defaults to the folder's name, the role to <c>author</c> ("Story &amp; art").
/// Nothing is sent to a provider; the name is only compared on the server with what providers return.
/// </summary>
public sealed record SetArtistFolderRequest
{
    /// <summary>The artist's name (default: the folder's name). Cleaned like a declared creator (at most 200 characters).</summary>
    public string? Name { get; init; }

    /// <summary>A declared creator role: <c>author</c> (default, "Story &amp; art"), <c>writer</c> or <c>artist</c>.</summary>
    public string? Role { get; init; }
}

/// <summary>1.37.0: what marking a folder an artist's folder did.</summary>
public sealed record ArtistFolderResultDto
{
    public required NodeSeriesLinkChangeDto Change { get; init; }

    /// <summary>The artist as declared on the folder.</summary>
    public required DeclaredCreatorDto Artist { get; init; }

    /// <summary>The artist was added to the folder's own declared creators by this call (false: it was already declared there).</summary>
    public bool CreatorAdded { get; init; }

    /// <summary>
    /// Works at and below the folder queued for automatic matching now (0 while automatic matching or the library's "Fetch from the
    /// web" is off - "Match this library now" finds them then).
    /// </summary>
    public int Queued { get; init; }
}
