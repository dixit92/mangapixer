namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

// Declared facts DTOs (1.28.0). An admin declares the type / format and the
// creators of the works below a folder or a library; everything below inherits
// them, the nearest declaration winning per key. No DTO carries a path; folder
// and library display names are already visible to anyone who can see the node.

/// <summary>A declared creator: name plus an optional role (<c>author</c>, <c>writer</c>, <c>artist</c>).</summary>
public sealed record DeclaredCreatorDto
{
    public required string Name { get; init; }
    public string? Role { get; init; }
}

/// <summary>
/// Replaces the declared facts of ONE scope (a folder or a library). A null type and an empty creator list
/// clear that key here, so the value from above applies again.
/// </summary>
public sealed record SetDeclaredFactsRequest
{
    public DeclaredType? Type { get; init; }
    public IReadOnlyList<DeclaredCreatorDto>? Creators { get; init; }
}

/// <summary>
/// 1.39.0: the edition facts of ONE folder itself (owner, 2026-10-09) - never inherited by its subfolders, never set on a library,
/// never used by matching. <see cref="VolumeTotal"/> ("Volumes in this edition") makes the volume answers count volumes 1..N of
/// this edition instead of the regular edition's list; <see cref="Edition"/> is a label; <see cref="Tracking"/> false ("Track
/// completion: off") keeps the link, metadata, covers and refresh but gives no Completion / missing / upgrade answer.
/// </summary>
public sealed record DeclaredEditionDto
{
    public int? VolumeTotal { get; init; }
    public DeclaredEdition? Edition { get; init; }
    public bool Tracking { get; init; } = true;
}

/// <summary>
/// 1.39.0: replaces the edition facts of one FOLDER (<c>PUT .../folders/{nodeId}/declared/edition</c>). A null volume total or edition
/// clears it; <see cref="Tracking"/> defaults to true (tracked). The type and creators of the folder are left alone.
/// </summary>
public sealed record SetDeclaredEditionRequest
{
    /// <summary>"Volumes in this edition": a whole number 1..999, or null.</summary>
    public int? VolumeTotal { get; init; }
    public DeclaredEdition? Edition { get; init; }
    public bool Tracking { get; init; } = true;
}

/// <summary>The facts declared on one scope itself.</summary>
public sealed record DeclaredFactValuesDto
{
    public DeclaredType? Type { get; init; }
    public IReadOnlyList<DeclaredCreatorDto> Creators { get; init; } = [];

    /// <summary>1.39.0: the folder's own edition facts, or null when none are set (always null for a library).</summary>
    public DeclaredEditionDto? Edition { get; init; }
}

/// <summary>
/// The effective declared facts at a node (nearest wins per key). For each key: the value, where it comes from
/// (<see cref="DeclaredFactSource"/>) and the display name of the folder or library that declares it.
/// </summary>
public sealed record EffectiveDeclaredFactsDto
{
    public DeclaredType? Type { get; init; }
    public DeclaredFactSource? TypeSource { get; init; }
    public string? TypeFrom { get; init; }

    public IReadOnlyList<DeclaredCreatorDto> Creators { get; init; } = [];
    public DeclaredFactSource? CreatorsSource { get; init; }
    public string? CreatorsFrom { get; init; }
}

/// <summary>
/// Admin view of one scope: what is declared HERE (<see cref="Own"/>) and what applies from above
/// (<see cref="Inherited"/>: the parent folders and the library for a folder; nothing for a library).
/// </summary>
public sealed record DeclaredFactsScopeDto
{
    /// <summary>The folder, or null for the library scope.</summary>
    public string? NodeId { get; init; }
    public required string LibraryId { get; init; }

    /// <summary>The folder's or library's display name.</summary>
    public required string DisplayName { get; init; }

    public required DeclaredFactValuesDto Own { get; init; }
    public required EffectiveDeclaredFactsDto Inherited { get; init; }
}

/// <summary>
/// What a declaration contradicts in the linked web record (owner, 2026-09-27: the Info panel shows BOTH with
/// a clear conflict indication). The record is never changed by a declaration.
/// </summary>
public sealed record DeclaredFactsConflictDto
{
    /// <summary>The provider display name of the linked record (e.g. MangaUpdates).</summary>
    public required string ProviderName { get; init; }

    /// <summary>The declared type contradicts the record's origin / format.</summary>
    public bool Type { get; init; }

    /// <summary>The record's type as the provider states it (e.g. <c>Manhwa</c>), when <see cref="Type"/>.</summary>
    public string? RecordType { get; init; }

    /// <summary>Declared creators and the record's creators share no name.</summary>
    public bool Creators { get; init; }

    /// <summary>The record's creator names (at most 10), when <see cref="Creators"/>.</summary>
    public IReadOnlyList<string> RecordCreators { get; init; } = [];
}

/// <summary>
/// Declared facts for the Info panel / series page of a node (a folder, or an archive inheriting from its
/// folders). Empty when nothing is declared above the node or "Show series information" is off for its library.
/// </summary>
public sealed record NodeDeclaredFactsDto
{
    public required string NodeId { get; init; }
    public required EffectiveDeclaredFactsDto Effective { get; init; }

    /// <summary>Set when a linked web record applies to the node and contradicts the declaration.</summary>
    public DeclaredFactsConflictDto? Conflict { get; init; }

    /// <summary>
    /// 1.39.0: the edition facts declared on this node itself (a folder; never inherited), or null when none are set or "Show series
    /// information" is off for its library.
    /// </summary>
    public DeclaredEditionDto? Edition { get; init; }
}
