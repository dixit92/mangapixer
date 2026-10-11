namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;

// Reading-mode suggestions (1.41.0), admin-only: folders whose LOCAL evidence (page shapes, ComicInfo, the linked record as a hint) points
// to a reader mode other than the one they resolve to today. Display names only, never a path. Enums arrive as their C# names.

/// <summary>Where a folder's current reader mode comes from (users' own overrides left out).</summary>
public enum ReaderModeSource
{
    /// <summary>Nothing set by an admin: each reader's own default applies.</summary>
    None = 0,

    /// <summary>The library's default reader mode.</summary>
    Library = 1,

    /// <summary>An admin's folder default on this folder.</summary>
    ThisFolder = 2,

    /// <summary>An admin's folder default on a folder above it.</summary>
    FolderAbove = 3,
}

/// <summary>One folder (or one linked series with its subfolders) and the reader mode its local evidence points to.</summary>
public sealed record ReadingModeSuggestionDto
{
    public required string NodeId { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public required string FolderName { get; init; }

    /// <summary>The parent folder's name, for context; null at the library root.</summary>
    public string? ParentName { get; init; }

    public required ReaderMode SuggestedMode { get; init; }

    /// <summary>The mode the folder resolves to today without users' own overrides; null when nothing is set.</summary>
    public ReaderMode? CurrentMode { get; init; }

    public required ReaderModeSource CurrentSource { get; init; }
    public required ReadingModeConfidence Confidence { get; init; }

    /// <summary>The signal the suggestion rests on.</summary>
    public required ReadingModeSignal Signal { get; init; }

    /// <summary>Measured pages in the tall-strip sample (summed over the subfolders of a linked series).</summary>
    public required int MeasuredPages { get; init; }

    /// <summary>Of those, tall strips.</summary>
    public required int TallPages { get; init; }

    /// <summary>Archives whose ComicInfo says <c>Manga = YesAndRightToLeft</c>.</summary>
    public required int ComicInfoRightToLeft { get; init; }

    /// <summary>Archives whose ComicInfo states <c>Manga</c> at all.</summary>
    public required int ComicInfoStated { get; init; }

    /// <summary>The linked series record's origin (Confirmed / Auto links only).</summary>
    public MetadataOrigin? LinkedOrigin { get; init; }

    /// <summary>The linked series record's webtoon flag.</summary>
    public bool? LinkedWebtoon { get; init; }

    /// <summary>Subfolders whose archives were counted with this folder's (a linked series' volume or chapter folders); 0 = itself only.</summary>
    public required int Subfolders { get; init; }
}

/// <summary>The suggestions list.</summary>
public sealed record ReadingModeSuggestionsDto
{
    /// <summary>Folders an Apply would change, most confident first (at most <see cref="Limit"/>).</summary>
    public required IReadOnlyList<ReadingModeSuggestionDto> Items { get; init; }

    /// <summary>Folders whose evidence disagrees with a folder default an admin set (on it or above it): shown, never suggested.</summary>
    public required IReadOnlyList<ReadingModeSuggestionDto> SetByAdmin { get; init; }

    /// <summary>Folders (and linked series) that were looked at.</summary>
    public required int FoldersChecked { get; init; }

    /// <summary>All suggestions before the limit.</summary>
    public required int Total { get; init; }

    /// <summary>The most rows a list returns.</summary>
    public required int Limit { get; init; }
}

/// <summary>One folder default to write.</summary>
public sealed record ReadingModeApplyItem
{
    public required string NodeId { get; init; }
    public required ReaderMode ReaderMode { get; init; }
}

/// <summary>Apply suggestions: writes the folders' reader defaults (the same as the folder reader-default PUT).</summary>
public sealed record ReadingModeApplyRequest
{
    public required IReadOnlyList<ReadingModeApplyItem> Items { get; init; }
}

/// <summary>An item that was not applied: <c>not_found</c> (unknown or removed) or <c>not_a_folder</c>.</summary>
public sealed record ReadingModeApplySkipDto
{
    public required string NodeId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>What an apply did.</summary>
public sealed record ReadingModeApplyResultDto
{
    public required int Applied { get; init; }
    public required IReadOnlyList<ReadingModeApplySkipDto> Skipped { get; init; }
}
