namespace com.lifepixer.mangapixer.Core.Api;

// The Official releases tab of Metadata Manager (1.30.0), admin-only: linked series with volumes released officially in the
// preferred language that the folder holds only as chapters (an upgrade, never "missing"), finished series the folder does not
// hold whole, and complete collections. Built from stored data only; no DTO carries a path.

/// <summary>Which series the tab lists.</summary>
public enum OfficialReleasesFilter
{
    /// <summary>Upgrades and finished series not held whole (the default).</summary>
    ToAct = 0,
    Upgrades = 1,
    Finished = 2,
    Complete = 3,
    All = 4,
}

/// <summary>One linked series folder in the tab.</summary>
public sealed record OfficialReleaseRowDto
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public string? CoverUrl { get; init; }
    public required string RecordTitle { get; init; }

    /// <summary><c>Confirmed</c> or <c>Auto</c>.</summary>
    public required Metadata.SeriesLinkState LinkState { get; init; }

    public required SeriesProgressDto Progress { get; init; }
}

/// <summary>Counts over every linked series of the library filter (before the tab filter and paging).</summary>
public sealed record OfficialReleasesSummaryDto
{
    public required int Series { get; init; }

    /// <summary>Series with at least one official volume held only as chapters.</summary>
    public required int Upgrades { get; init; }

    /// <summary>Series finished in the preferred language that the folder does not hold whole.</summary>
    public required int FinishedNotHeld { get; init; }
    public required int CompleteCollections { get; init; }
}

/// <summary>
/// A page of the tab: upgrades first (most volumes to get first), then finished series not held whole (most missing first), then
/// complete collections, then the rest; by name, then node id.
/// </summary>
public sealed record OfficialReleasesPageDto
{
    public required IReadOnlyList<OfficialReleaseRowDto> Items { get; init; }
    public required OfficialReleasesSummaryDto Summary { get; init; }

    /// <summary>Rows matching the filter.</summary>
    public required int Total { get; init; }
    public string? NextCursor { get; init; }

    /// <summary>The preferred language ("en", "fr", ...); official volume totals are known only for English today.</summary>
    public required string Language { get; init; }
}
