namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The answer of <c>GET /api/v1/search/suggest</c> (1.49.0, lane O): quick results while typing, grouped by kind, about five each.
/// Every list holds only what the viewer may open (visible libraries, Incognito applied; series information hidden = no series, creators
/// or tags). <see cref="Field"/> is set instead of the groups when the text ends in a typed field (<c>creator:ura</c>).
/// </summary>
public sealed record SearchSuggestDto
{
    /// <summary>The text as sent (trimmed to the longest length the route reads).</summary>
    public required string Query { get; init; }

    /// <summary>Linked series found by title or alternative title: the folder or archive the series is linked to opens.</summary>
    public required IReadOnlyList<SuggestItemDto> Series { get; init; }

    /// <summary>Folders found by name.</summary>
    public required IReadOnlyList<SuggestItemDto> Folders { get; init; }

    /// <summary>Archives found by name.</summary>
    public required IReadOnlyList<SuggestItemDto> Archives { get; init; }

    /// <summary>Creators (a tag view opens through <see cref="SuggestItemDto.Filter"/>).</summary>
    public required IReadOnlyList<SuggestItemDto> Creators { get; init; }

    /// <summary>Tags and genres (a tag view opens through <see cref="SuggestItemDto.Filter"/>).</summary>
    public required IReadOnlyList<SuggestItemDto> Tags { get; init; }

    /// <summary>The values of the typed field the text ends in; null when it ends in no typed field (then the groups above answer).</summary>
    public SuggestFieldDto? Field { get; init; }
}

/// <summary>
/// One quick result. A node (<c>series</c> / <c>folder</c> / <c>archive</c>) carries <see cref="Id"/> and <see cref="LibraryId"/> (public
/// ids: a folder opens in browse, an archive in the reader); a value (<c>creator</c> / <c>tag</c> / <c>genre</c>) carries
/// <see cref="Filter"/>, the ready <c>f</c> value of the tag view (<c>creator:fv_...</c>) and <see cref="Count"/>.
/// </summary>
public sealed record SuggestItemDto
{
    /// <summary><c>series</c>, <c>folder</c>, <c>archive</c>, <c>creator</c>, <c>tag</c> or <c>genre</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The node's public id, or the value's public id.</summary>
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>A node: the folder it sits in (display name); a series: the title that matched when it differs from the name.</summary>
    public string? Detail { get; init; }

    /// <summary>A node: its library's public id.</summary>
    public string? LibraryId { get; init; }

    /// <summary>A node: <c>Folder</c> (opens in browse) or <c>Archive</c> (opens in the reader) - a series is either.</summary>
    public string? NodeKind { get; init; }

    /// <summary>A value: the tag view's <c>f</c> parameter.</summary>
    public string? Filter { get; init; }

    /// <summary>A value: how many works carry it at a place the viewer may see.</summary>
    public int? Count { get; init; }
}

/// <summary>The values suggested for the typed field the text ends in.</summary>
public sealed record SuggestFieldDto
{
    /// <summary>The field's name as typed, lower-case (<c>creator</c>, <c>tag</c>, <c>library</c>, ...).</summary>
    public required string Name { get; init; }

    /// <summary>The value typed so far (without quotes).</summary>
    public required string Prefix { get; init; }

    /// <summary>Where the field's token starts in the text; choosing a value replaces the text from here on.</summary>
    public required int Start { get; init; }

    public required IReadOnlyList<SuggestValueDto> Items { get; init; }
}

/// <summary>One value of a typed field: what to show and the complete token that replaces the one being typed.</summary>
public sealed record SuggestValueDto
{
    public required string Label { get; init; }

    /// <summary>The whole token, e.g. <c>creator:"Naoki Urasawa"</c> (quoted when it holds a space; a leading <c>-</c> kept).</summary>
    public required string Insert { get; init; }

    public int? Count { get; init; }
}
