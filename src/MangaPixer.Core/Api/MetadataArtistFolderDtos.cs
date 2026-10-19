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

    /// <summary>
    /// 1.45.0: the provider of an author the admin picked from an author search - only <c>mangaupdates</c>; given together with
    /// <see cref="ExternalId"/> or not at all. Stored on the declared artist as its provider reference; nothing is sent.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>1.45.0: the picked author's MangaUpdates author id (a positive decimal number).</summary>
    public string? ExternalId { get; init; }
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

    /// <summary>1.45.0: the declared artist's provider reference (<c>mangaupdates:&lt;author id&gt;</c>) when the admin picked an author.</summary>
    public string? AuthorRef { get; init; }
}

/// <summary>
/// 1.45.0: search MangaUpdates for an author by name (admin only, on demand, never automatic). The text is the one the admin confirms - the
/// folder's name as listed, or the Artist field - and is the only thing sent, with a fixed page and page size.
/// </summary>
public sealed record AuthorSearchRequest
{
    /// <summary>The search text, 1-200 characters after whitespace is collapsed.</summary>
    public string? Text { get; init; }
}

/// <summary>1.45.0: one author an author search found. Only offered: nothing is stored or marked until the admin picks it.</summary>
public sealed record AuthorSearchCandidateDto
{
    /// <summary>The provider (<c>mangaupdates</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>The MangaUpdates author id.</summary>
    public required string ExternalId { get; init; }

    /// <summary>The author's main name on MangaUpdates (what the folder declares when picked).</summary>
    public required string Name { get; init; }

    /// <summary>The other name the search matched, when it is not the main name.</summary>
    public string? HitName { get; init; }

    /// <summary>How many series MangaUpdates lists for the author.</summary>
    public int? TotalSeries { get; init; }

    /// <summary>The author's genres on MangaUpdates (most first, at most 10).</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>The author's page on MangaUpdates (opened by the admin's own browser).</summary>
    public string? SiteUrl { get; init; }
}

/// <summary>1.45.0: the answer of an author search - the first page (at most 10) and the provider's total hit count.</summary>
public sealed record AuthorSearchResultDto
{
    public required IReadOnlyList<AuthorSearchCandidateDto> Candidates { get; init; }
    public required int Total { get; init; }
}
