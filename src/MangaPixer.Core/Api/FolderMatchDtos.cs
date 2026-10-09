namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>1.38.0 "Match folders by name": what the selected folders are matched as.</summary>
public enum FolderMatchKind
{
    /// <summary>Artist folders: the folder's name against every stored artist name (creator spellings, stored other names).</summary>
    Artists = 0,

    /// <summary>Collections about a series: the folder's name against the titles and alt titles of the stored series records.</summary>
    Collections = 1,
}

/// <summary>1.38.0: what the preview found for one selected node.</summary>
public enum FolderMatchStatus
{
    /// <summary>Exactly one match: proposed and ticked.</summary>
    Proposed = 0,

    /// <summary>Several matches: the admin picks one or skips the folder (unticked until one is picked).</summary>
    Ambiguous = 1,

    /// <summary>No stored name equals the folder's name.</summary>
    NoMatch = 2,

    /// <summary>
    /// The folder already has an admin decision or a link (Confirmed, Auto, Don't match, Collection about, Artist folder): shown with
    /// <see cref="FolderMatchRowDto.CurrentState"/>, unticked; its matches are still listed. A Needs-review row does not count.
    /// </summary>
    Decided = 3,

    /// <summary>An archive: only folders can be marked.</summary>
    NotAFolder = 4,

    /// <summary>No such node.</summary>
    NotFound = 5,
}

/// <summary>1.38.0: preview "Match folders by name" for 1-200 selected nodes. Stored data only: nothing is sent.</summary>
public sealed record FolderMatchPreviewRequest
{
    public required FolderMatchKind Kind { get; init; }

    /// <summary>The selected nodes (1-200; archives answer <see cref="FolderMatchStatus.NotAFolder"/>).</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }
}

/// <summary>A known artist a folder's name matched.</summary>
public sealed record FolderMatchArtistDto
{
    /// <summary>The name the folder would declare: the MangaUpdates main name (the stored author record's, else the spelling on the most records).</summary>
    public required string Name { get; init; }

    /// <summary>The declared role: <c>author</c> ("Story &amp; art") or <c>artist</c>.</summary>
    public required string Role { get; init; }

    /// <summary>The artist's name that equals the folder's name (may be another spelling or another name than <see cref="Name"/>).</summary>
    public required string MatchedName { get; init; }

    /// <summary>The provider the artist is credited by (<c>mangaupdates</c>, <c>gcd</c>).</summary>
    public required string Provider { get; init; }

    /// <summary>How many stored records credit this artist.</summary>
    public required int RecordCount { get; init; }
}

/// <summary>A stored series record a folder's name matched.</summary>
public sealed record FolderMatchRecordDto
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }

    /// <summary>The record's title or alt title that equals the folder's name.</summary>
    public required string MatchedTitle { get; init; }

    public int? Year { get; init; }

    /// <summary>The provider's raw type (for example <c>Manga</c>, <c>Doujinshi</c>).</summary>
    public string? ProviderType { get; init; }

    /// <summary>The record is linked as a series (Confirmed or Auto) somewhere on this server; such records are listed first.</summary>
    public bool LinkedAsSeries { get; init; }
}

/// <summary>1.38.0: one selected node in the preview.</summary>
public sealed record FolderMatchRowDto
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required FolderMatchStatus Status { get; init; }

    /// <summary>The node's own link state, if it has a row.</summary>
    public SeriesLinkState? CurrentState { get; init; }

    /// <summary>Artists: the matched artists (one = the proposal), in the order shown.</summary>
    public IReadOnlyList<FolderMatchArtistDto> Artists { get; init; } = [];

    /// <summary>Collections: the matched stored records (series linked here first).</summary>
    public IReadOnlyList<FolderMatchRecordDto> Records { get; init; } = [];

    /// <summary>
    /// Collections, a folder without a local match: the exact text "Search the web for the rest" would send for it (the Identify dialog's
    /// first suggestion for this name); null when the name gives no search text.
    /// </summary>
    public string? SearchText { get; init; }
}

public sealed record FolderMatchPreviewDto
{
    public required FolderMatchKind Kind { get; init; }
    public required IReadOnlyList<FolderMatchRowDto> Rows { get; init; }

    /// <summary>How many artists (Artists) or series records (Collections) the names were compared with.</summary>
    public required int Compared { get; init; }
}

/// <summary>One folder to mark: an artist (name, role) or a stored record (provider, external id).</summary>
public sealed record FolderMatchApplyItem
{
    public required string NodeId { get; init; }

    /// <summary>Artists: the name to declare (empty: the folder's own name).</summary>
    public string? Name { get; init; }

    /// <summary>Artists: the role (<c>author</c> by default, <c>writer</c>, <c>artist</c>).</summary>
    public string? Role { get; init; }

    /// <summary>Collections: the stored record's provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Collections: the stored record's id.</summary>
    public string? ExternalId { get; init; }
}

/// <summary>
/// 1.38.0: mark 1-200 folders at once, each through the single action (Artist folder / Collection about) - stored data only: a collection
/// whose record is not stored answers <c>record_not_stored</c> (nothing is fetched).
/// </summary>
public sealed record FolderMatchApplyRequest
{
    public required FolderMatchKind Kind { get; init; }
    public required IReadOnlyList<FolderMatchApplyItem> Items { get; init; }

    /// <summary>Collections: also set each folder's Content to "Doujinshi &amp; adult one-shots" (as the single action, default true).</summary>
    public bool SetDoujinContent { get; init; } = true;
}

public sealed record FolderMatchApplyItemResultDto
{
    public required string NodeId { get; init; }

    /// <summary>
    /// <c>ok</c>, or an error code: <c>not_found</c>, <c>not_a_folder</c>, <c>duplicate</c>, <c>creator_name_invalid</c>,
    /// <c>creator_role_invalid</c>, <c>creators_too_many</c>, <c>record_missing</c>, <c>record_not_stored</c>, <c>invalid_request</c>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>Works at and below the folder queued for automatic matching (0 while it is off).</summary>
    public int Queued { get; init; }
}

public sealed record FolderMatchApplyResultDto
{
    public required FolderMatchKind Kind { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<FolderMatchApplyItemResultDto> Results { get; init; }
}
