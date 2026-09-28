namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata.Missing;

// The missing volumes / chapters report (1.28.0), admin-only. Built from stored data
// only: the unit numbers in archive names and the totals of each linked series'
// stored record; reading it never contacts a provider. No DTO carries a filesystem
// path; display names and record titles are fine (admin surface). Enums arrive as
// their C# names.

/// <summary>One unit kind (volumes or chapters) of one linked series.</summary>
public sealed record MissingUnitGapDto
{
    public required MissingUnitKind Kind { get; init; }

    /// <summary>Archives whose name states a number of this kind.</summary>
    public required int ArchiveCount { get; init; }

    /// <summary>Distinct unit numbers on disk (a range archive counts each number it covers).</summary>
    public required int UnitCount { get; init; }

    /// <summary>The lowest number on disk.</summary>
    public required int Lowest { get; init; }

    /// <summary>The highest number on disk ("have").</summary>
    public required int Have { get; init; }

    /// <summary>The published total the comparison used, or null when the record states none.</summary>
    public int? Available { get; init; }
    public MissingTotalSource? Source { get; init; }
    public MissingConfidence? Confidence { get; init; }

    /// <summary><see cref="Available"/> minus <see cref="Have"/>, never negative.</summary>
    public required int BehindBy { get; init; }

    /// <summary>Numbers below <see cref="Have"/> that no archive states (the first 50).</summary>
    public required IReadOnlyList<int> Missing { get; init; }

    /// <summary>All missing numbers below <see cref="Have"/> (may exceed the list).</summary>
    public required int MissingCount { get; init; }
}

/// <summary>One linked series folder in the report.</summary>
public sealed record MissingSeriesDto
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public string? CoverUrl { get; init; }

    public required string Provider { get; init; }
    public required string RecordTitle { get; init; }

    /// <summary><c>Confirmed</c> or <c>Auto</c>.</summary>
    public required Metadata.SeriesLinkState LinkState { get; init; }

    public required MissingVerdict Verdict { get; init; }
    public MissingUnitGapDto? Volumes { get; init; }
    public MissingUnitGapDto? Chapters { get; init; }

    /// <summary>Folders of this series whose archives mix volumes and chapters (they give no numbers).</summary>
    public required int MixedFolders { get; init; }

    /// <summary>
    /// The record lists an English publisher but no English total: its notes state none, or the record was fetched
    /// before 1.28.0 stored them (the next refresh reads them). The comparison used the origin total instead.
    /// </summary>
    public required bool EnglishTotalUnknown { get; init; }

    /// <summary>The stored status text in the country of origin (for example "14 Volumes (Complete)").</summary>
    public string? StatusText { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>Counts over the whole filtered set (before paging).</summary>
public sealed record MissingReportSummaryDto
{
    public required int Series { get; init; }
    public required int Behind { get; init; }
    public required int Holes { get; init; }
    public required int UpToDate { get; init; }
    public required int NoTotal { get; init; }
    public required int NoVerdict { get; init; }
}

/// <summary>A page of the report: worst first (behind, holes, up to date, no total, no verdict), then by name.</summary>
public sealed record MissingReportPageDto
{
    public required IReadOnlyList<MissingSeriesDto> Items { get; init; }
    public required MissingReportSummaryDto Summary { get; init; }

    /// <summary>Rows matching the filter.</summary>
    public required int Total { get; init; }
    public string? NextCursor { get; init; }
}
