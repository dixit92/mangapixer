namespace com.lifepixer.mangapixer.Core.Api;

// 1.45.0 lane F: the admin creator API (canonical creators of the tag index; D1 2.2). Admin only, cookie + CSRF. Ids are the public
// creator ids (cr_...) and provider references (mangaupdates:<author id>); names are the spellings the sources wrote (shown to admins,
// never logged). No path anywhere.

/// <summary>One spelling of a creator and where it came from.</summary>
public sealed record CreatorNameDto
{
    public required string Name { get; init; }

    /// <summary><c>record</c> (a web record's credit), <c>providerAuthor</c> (the stored author record), <c>comicInfo</c>, <c>declared</c>, <c>admin</c>.</summary>
    public required string Source { get; init; }

    /// <summary><c>latin</c>, <c>cjk</c>, <c>hangul</c> or <c>other</c>.</summary>
    public required string Script { get; init; }
}

/// <summary>
/// A canonical creator for the admin views (the creator page header, Merge into..., Split...). Asked by a MERGED creator's id, the answer
/// is the creator it was merged into, with <see cref="RedirectedFrom"/> = the id asked for (the web replaces its URL).
/// </summary>
public sealed record CreatorDto
{
    /// <summary>The public creator id (<c>cr_...</c>).</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Why this display name: <c>provider</c> (the author record's main name), <c>mostUsed</c> (the most used spelling) or <c>admin</c>.</summary>
    public required string DisplayNameSource { get; init; }

    /// <summary>The spellings it is known by, sorted.</summary>
    public IReadOnlyList<CreatorNameDto> Names { get; init; } = [];

    /// <summary>The provider references it holds (<c>mangaupdates:&lt;author id&gt;</c>), sorted; empty for a name-only creator.</summary>
    public IReadOnlyList<string> ProviderIds { get; init; } = [];

    /// <summary>The ids of the creators merged into this one.</summary>
    public IReadOnlyList<string> MergedFrom { get; init; } = [];

    /// <summary>The creator's tag value (<c>fv_...</c>, for <c>/search?f=creators:&lt;id&gt;</c>), or null while it has no indexed work.</summary>
    public string? ValueId { get; init; }

    /// <summary>How many indexed works credit it (all libraries; admin view only).</summary>
    public int Works { get; init; }

    /// <summary>Set when the id asked for was merged into this creator: that id.</summary>
    public string? RedirectedFrom { get; init; }
}

/// <summary>A page of creators matching an admin's search (live creators only; merged ones are left out).</summary>
public sealed record CreatorSearchResultDto
{
    public IReadOnlyList<CreatorDto> Items { get; init; } = [];
}

/// <summary>Merge the creator of the route into <see cref="IntoId"/> (the route's id then redirects to it).</summary>
public sealed record MergeCreatorRequest
{
    public string? IntoId { get; init; }
}

/// <summary>
/// Split the chosen provider references and spellings off the creator of the route into a NEW creator; admin pins keep the creator rules
/// from undoing it. At least one item, and not everything the creator holds.
/// </summary>
public sealed record SplitCreatorRequest
{
    public IReadOnlyList<string>? ProviderIds { get; init; }
    public IReadOnlyList<string>? Names { get; init; }
}

/// <summary>The admin's display name for a creator; null or blank clears it (the automatic rule applies again).</summary>
public sealed record SetCreatorDisplayNameRequest
{
    public string? DisplayName { get; init; }
}
