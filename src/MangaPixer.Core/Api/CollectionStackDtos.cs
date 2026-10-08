namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Catalog;

// Stories collected in one volume (1.37.0, "tankoubon stacks"): in a folder that is neither a series nor a collection, two or more
// archives linked to the same collected-volume record show as one stacked card. No DTO carries a path, a provider image URL or an
// internal id; the key is the record's opaque public id.

/// <summary>
/// A stack of stories collected in one volume as a browse entry (<see cref="CatalogNodeDto.CollectionStack"/>, on an entry of kind
/// <see cref="CatalogNodeKind.VolumeStack"/> whose <see cref="CatalogNodeDto.VolumeStack"/> is null). It has no volume
/// number, no completion and no missing count: the record's volume facts describe the collection, not this folder.
/// </summary>
public sealed record CollectionStackSummaryDto
{
    /// <summary>The stack's key (opaque); with the folder id it names the stack view <c>GET /nodes/{folderId}/collection-stacks/{key}</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The collected volume's title (the linked record's), else the first story's name.</summary>
    public required string Title { get; init; }

    /// <summary>The archives here linked to that record (at least 2).</summary>
    public required int StoryCount { get; init; }
}

/// <summary>The stack view of stories collected in one volume (<c>GET /nodes/{folderId}/collection-stacks/{key}</c>, 1.37.0).</summary>
public sealed record CollectionStackDto
{
    public required string FolderId { get; init; }
    public required string Key { get; init; }

    /// <summary>The collected volume's title (the linked record's), else the first story's name.</summary>
    public required string Title { get; init; }

    /// <summary>The record's stored poster when web covers are shown here, else the first story's cover; null when neither exists.</summary>
    public string? CoverUrl { get; init; }

    public required int StoryCount { get; init; }

    /// <summary>The stories, in folder order, as browse cards (read state, star, cover).</summary>
    public required IReadOnlyList<CatalogNodeDto> Items { get; init; }

    /// <summary>The previous / next stack of stories in this folder, by position; null at either end.</summary>
    public string? PreviousKey { get; init; }

    public string? NextKey { get; init; }
}
