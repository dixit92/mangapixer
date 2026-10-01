namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;

// Move conflicts (1.31.0), admin-only: an item moved to another library while both copies had their own state (the
// destination library was scanned before the source library). No DTO carries a filesystem path; display names and record
// titles are fine (admin surface). Enums arrive as their C# names. Values are append-only: a stored int keeps its meaning.

/// <summary>What differs between the old and the new copy.</summary>
public enum MoveConflictKind
{
    /// <summary>A user's reading position.</summary>
    Progress = 1,

    /// <summary>A user's reader settings for the item.</summary>
    ReaderSettings = 2,

    /// <summary>The series link of the folder or archive (an admin row; no user).</summary>
    SeriesLink = 3,
}

/// <summary>Where a move conflict stands.</summary>
public enum MoveConflictState
{
    Open = 0,

    /// <summary>The old state was copied onto the new copy.</summary>
    Overwritten = 1,

    /// <summary>The new copy kept its own state.</summary>
    Kept = 2,
}

/// <summary>An admin's choice for a move conflict.</summary>
public enum MoveConflictResolution
{
    /// <summary>Overwrite the new state with the old one.</summary>
    Overwrite = 0,

    /// <summary>Keep the new state.</summary>
    Keep = 1,
}

/// <summary>A reading position as the conflicts page shows it.</summary>
public enum MoveProgressState
{
    Unread = 0,
    InProgress = 1,
    Completed = 2,
}

/// <summary>One side (old or new) of a move conflict. Only the fields of the conflict's kind are set.</summary>
public sealed record MoveConflictSideDto
{
    /// <summary>False when this side holds nothing of the kind (for example a link removed meanwhile).</summary>
    public required bool Present { get; init; }

    public MoveProgressState? Progress { get; init; }

    /// <summary>1-based page of the position.</summary>
    public int? Page { get; init; }

    public int? PageCount { get; init; }

    public ReaderMode? ReaderMode { get; init; }

    /// <summary>Settings besides the reading mode (direction, fit, offsets, background) are set.</summary>
    public bool? OtherReaderSettings { get; init; }

    public SeriesLinkState? LinkState { get; init; }
    public string? RecordTitle { get; init; }
    public string? Provider { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One move conflict.</summary>
public sealed record MoveConflictDto
{
    public required string Id { get; init; }
    public required MoveConflictKind Kind { get; init; }
    public required MoveConflictState State { get; init; }

    /// <summary>The user whose state differs; null for a series link.</summary>
    public string? UserId { get; init; }
    public string? UserName { get; init; }

    /// <summary>The new copy (live).</summary>
    public required string NodeId { get; init; }
    public required string Title { get; init; }
    public required bool IsFolder { get; init; }
    public string? ParentTitle { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }

    /// <summary>Where the old copy was.</summary>
    public required string FromTitle { get; init; }
    public required string FromLibraryName { get; init; }

    /// <summary>The state before the move (kept on the removed copy).</summary>
    public required MoveConflictSideDto Old { get; init; }

    /// <summary>The state of the new copy now.</summary>
    public required MoveConflictSideDto New { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

public sealed record MoveConflictPageDto
{
    public required IReadOnlyList<MoveConflictDto> Items { get; init; }

    /// <summary>All open conflicts (not only this page).</summary>
    public required int OpenCount { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record MoveConflictCountDto
{
    public required int Open { get; init; }
}

/// <summary>Resolves the listed conflicts, or every open one (<see cref="All"/>, optionally of one <see cref="Kind"/>).</summary>
public sealed record MoveConflictResolveRequest
{
    public IReadOnlyList<string>? Ids { get; init; }
    public bool All { get; init; }
    public MoveConflictKind? Kind { get; init; }
    public required MoveConflictResolution Resolution { get; init; }
}

public sealed record MoveConflictResolveResultDto
{
    public required int Resolved { get; init; }

    /// <summary>Unknown ids, or conflicts already resolved.</summary>
    public required int Skipped { get; init; }
}
