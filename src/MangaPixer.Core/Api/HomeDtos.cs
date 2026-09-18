namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Home "New chapters" response (1.12.0). Recently-added archives STACKED by their
/// top-level unit (the direct library child they descend from) for each library the
/// caller can see, grouped by library. Respects Incognito/Private visibility exactly
/// like the other discovery surfaces, and additionally drops libraries the caller has
/// hidden from home (see <see cref="HomeLibraryVisibilityDto"/>). No source paths.
/// </summary>
public sealed record RecentChaptersDto
{
    /// <summary>
    /// One group per visible, non-hidden library, ordered by library display name. A
    /// library with no recent stacks still appears here with an empty
    /// <see cref="RecentChaptersLibraryGroup.Stacks"/> list so the frontend can render a
    /// consistent per-library shape; callers that want only non-empty groups filter
    /// client-side.
    /// </summary>
    public required IReadOnlyList<RecentChaptersLibraryGroup> Libraries { get; init; }
}

/// <summary>
/// One library's "New chapters" group: its recently-updated stacks, newest activity
/// first, capped to the requested per-library stack limit.
/// </summary>
public sealed record RecentChaptersLibraryGroup
{
    /// <summary>Opaque public ID of the library.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Display name of the library.</summary>
    public required string LibraryName { get; init; }

    /// <summary>
    /// The library's recently-updated stacks, ordered by <see cref="RecentChapterStack.LatestAddedAt"/>
    /// descending, capped to the requested per-library limit. Empty when the library has
    /// no recently-added archives.
    /// </summary>
    public required IReadOnlyList<RecentChapterStack> Stacks { get; init; }
}

/// <summary>
/// A single "New chapters" stack: a top-level unit (whatever the user's layout puts at
/// the library root) that has recently-added descendant archives. A loose archive at the
/// library root is its own standalone stack. Convention-agnostic: not assumed to be a
/// "series". No source paths.
/// </summary>
public sealed record RecentChapterStack
{
    /// <summary>
    /// Top-level folder public id, OR the archive public id for a loose top-level archive.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Top-level folder name, OR the archive name for a loose archive.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// True = a stacked folder card (tap → folder browse sorted recentlyUpdated); false =
    /// a standalone archive (tap → reader).
    /// </summary>
    public required bool IsFolder { get; init; }

    /// <summary>
    /// Folder cover (its first descendant archive) or the archive's own cover; null when
    /// none is resolvable.
    /// </summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    /// Newest descendant archive public id (equals <see cref="Id"/> when
    /// <see cref="IsFolder"/> is false).
    /// </summary>
    public required string LatestItemId { get; init; }

    /// <summary>Newest descendant archive display name.</summary>
    public required string LatestItemName { get; init; }

    /// <summary>
    /// Stack ordering key: the newest descendant archive's CreatedAt (scan-observed
    /// creation time).
    /// </summary>
    public required DateTimeOffset LatestAddedAt { get; init; }

    /// <summary>
    /// Count of recently-added descendant archives attributed to this stack (always ≥ 1).
    /// </summary>
    public required int NewCount { get; init; }

    /// <summary>
    /// Derived read state of the stack's TOP-LEVEL node (1.20.0), one of
    /// <c>"read"</c> / <c>"reading"</c> / <c>"unread"</c> — the same rollup the read-state
    /// filter already uses (<c>FolderReadRollupRules</c>), so the tag on a card and the
    /// filter that would keep or drop it never disagree. A folder stack rolls up over its
    /// whole subtree; a standalone (loose) archive stack rolls up over just itself. A stack
    /// with no readable descendant archive (should not occur — every stack has at least one
    /// in-window candidate) reports <c>"unread"</c>, matching the filter's treatment of a
    /// missing rollup.
    /// </summary>
    public required string ReadState { get; init; }
}

/// <summary>
/// The current user's Home library-visibility preference (1.12.0). Libraries in this list
/// are hidden from the home "New chapters" surface. Independent of the Private designation
/// and of Incognito mode; never affects browse, search, or direct access. Library IDs are
/// opaque public IDs.
/// </summary>
public sealed record HomeLibraryVisibilityDto
{
    public required IReadOnlyList<string> ExcludedLibraryIds { get; init; }
}
