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

    /// <summary>
    /// 1.29.0 RC: the total in the country of origin, as context only - it never makes a series "behind" (an untranslated
    /// volume is not missing); null when the record states none.
    /// </summary>
    public int? OriginTotal { get; init; }
}

/// <summary>One linked series folder in the report.</summary>
public sealed record MissingSeriesDto
{
    /// <summary>1.29.0 RC: the preferred language the totals follow ("en", "fr", ...): "behind" only against what is released in it.</summary>
    public string? Language { get; init; }

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

    /// <summary>
    /// 1.28.0: the stored chapters-per-volume source for this series (an AniList entry an admin looked up), or null.
    /// </summary>
    public MissingConversionDto? Conversion { get; init; }

    /// <summary>The stored status text in the country of origin (for example "14 Volumes (Complete)").</summary>
    public string? StatusText { get; init; }
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>
    /// 1.30.0 (reach): the series' progress - trackers, what the folder holds (volumes and chapters merged through the stored volume
    /// list), upgrades and completion; the same engine as the Volumes view.
    /// </summary>
    public SeriesProgressDto? Progress { get; init; }

    /// <summary>
    /// 1.31.0: chapter / volume numbers that more than one file of the same folder states ("Chapter 1: 2 files"), volumes first, at
    /// most <see cref="MissingUnits.MaxListed"/> listed; <see cref="DuplicateCount"/> is the full count. Split chapters (2.1 + 2.2)
    /// and ranges are not duplicates. Empty when there are none.
    /// </summary>
    public IReadOnlyList<DuplicateUnitDto> Duplicates { get; init; } = [];

    /// <summary>1.31.0: how many numbers are duplicated in all (the size of <see cref="Duplicates"/> before its cap).</summary>
    public int DuplicateCount { get; init; }
}

/// <summary>1.31.0: a chapter or volume number that <see cref="Files"/> (two or more) files of one folder state.</summary>
public sealed record DuplicateUnitDto
{
    public required MissingUnitKind Kind { get; init; }

    /// <summary>The number as the names state it ("1", "45.5").</summary>
    public required string Number { get; init; }

    public required int Files { get; init; }
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

    /// <summary>1.30.0: series with official volumes in the preferred language held only as chapters (the Official releases tab).</summary>
    public int Upgrades { get; init; }

    /// <summary>1.39.0: linked series left out of the report because an admin turned "Track completion" off for their folder.</summary>
    public int NotTracked { get; init; }
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

/// <summary>A stored chapters-per-volume source (an AniList entry matched to the linked series).</summary>
public sealed record MissingConversionDto
{
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public string? SiteUrl { get; init; }
    public int? Volumes { get; init; }
    public int? Chapters { get; init; }

    /// <summary>Chapters / volumes of a FINISHED entry; null while it is running (its totals are not final).</summary>
    public double? ChaptersPerVolume { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
}

/// <summary>What one chapters-per-volume lookup found.</summary>
public enum MissingConversionOutcome
{
    /// <summary>A matching finished entry with volume and chapter totals: the ratio is stored.</summary>
    Found = 0,

    /// <summary>A matching entry, but without both totals (still running, or not counted): stored, no ratio.</summary>
    NoCounts = 1,

    /// <summary>No entry matched the linked series confidently; nothing stored.</summary>
    NoMatch = 2,
}

/// <summary>POST /admin/metadata/missing/{nodeId}/conversion.</summary>
public sealed record MissingConversionResultDto
{
    public required MissingConversionOutcome Outcome { get; init; }

    /// <summary>The report row after the lookup.</summary>
    public required MissingSeriesDto Row { get; init; }
}

/// <summary>POST /admin/metadata/missing/conversions: up to 20 series without a stored source, one request each.</summary>
public sealed record MissingConversionBatchRequest
{
    public string? Library { get; init; }
}

public sealed record MissingConversionBatchResultDto
{
    /// <summary>Lookups sent.</summary>
    public required int Looked { get; init; }
    public required int Found { get; init; }
    public required int NoCounts { get; init; }
    public required int NoMatch { get; init; }

    /// <summary>Series still without a source (not looked up in this batch, or skipped after a recent miss).</summary>
    public required int Remaining { get; init; }

    /// <summary>Why the batch stopped early (a gateway code such as <c>budget_exhausted</c>), or null.</summary>
    public string? StoppedCode { get; init; }
    public string? StoppedMessage { get; init; }
}
