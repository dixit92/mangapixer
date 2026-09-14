namespace com.lifepixer.mangaplex.Core.Api;

/// <summary>
/// Home "New chapters" response (1.11.0 Lane C). The most-recently-added
/// archives for each library the caller can see, grouped by library, newest
/// first, capped per library. An archive is a readable chapter. Respects
/// Incognito/Private visibility exactly like the other discovery surfaces
/// (continue-reading, search, browse-root, library list): a Private library
/// the caller has marked is excluded entirely while Incognito is active, and
/// a library the caller has no grant for never appears. No source paths.
/// </summary>
public sealed record RecentChaptersDto
{
    /// <summary>
    /// One group per visible library, ordered by library display name. A
    /// library with no recent archives still appears here with an empty
    /// <see cref="RecentChaptersLibraryGroup.Items"/> list so the frontend can
    /// render a consistent per-library row shape; callers that want only
    /// non-empty groups filter client-side.
    /// </summary>
    public required IReadOnlyList<RecentChaptersLibraryGroup> Libraries { get; init; }
}

/// <summary>
/// One library's "New chapters" group: its most-recently-added archives,
/// newest first, capped to the requested per-library limit.
/// </summary>
public sealed record RecentChaptersLibraryGroup
{
    /// <summary>Opaque public ID of the library.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Display name of the library.</summary>
    public required string LibraryName { get; init; }

    /// <summary>
    /// The library's most-recently-added archives, newest first, capped to the
    /// requested per-library limit. Empty when the library has no (visible,
    /// non-tombstoned) archives.
    /// </summary>
    public required IReadOnlyList<RecentChapterEntry> Items { get; init; }
}

/// <summary>
/// A single recently-added archive (chapter) in a library's New-chapters row.
/// Carries its immediate parent folder so the frontend can group/label by
/// series where natural. No source paths.
/// </summary>
public sealed record RecentChapterEntry
{
    /// <summary>Opaque public ID of the archive node (the readable chapter).</summary>
    public required string ItemId { get; init; }

    /// <summary>Display name of the archive (filename stem).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Opaque public ID of the item's library.</summary>
    public required string LibraryId { get; init; }

    /// <summary>
    /// Opaque public ID of the archive's immediate parent folder, or an empty
    /// string when the archive sits at the library root.
    /// </summary>
    public required string ParentId { get; init; }

    /// <summary>
    /// Display name of the immediate parent folder (the natural "series" label
    /// for a typical Library/Series/Chapter layout), or null when the archive
    /// sits at the library root. This is the immediate parent only; deeper
    /// nesting (Library/Series/Volume/Chapter) shows the volume name, which is
    /// the natural per-chapter grouping label for the home row.
    /// </summary>
    public string? SeriesName { get; init; }

    /// <summary>
    /// When the archive was added to the catalog (scan-observed creation
    /// time). Newest first means descending by this (then by internal id as a
    /// stable tiebreaker).
    /// </summary>
    public required DateTimeOffset AddedAt { get; init; }

    /// <summary>Page count when known (archive has a manifest), else null.</summary>
    public int? PageCount { get; init; }
}
