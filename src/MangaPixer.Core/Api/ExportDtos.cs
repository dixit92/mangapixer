namespace com.lifepixer.mangapixer.Core.Api;

using System.Text.Json.Serialization;

// The read-only metadata export (1.33.0, for MangaList): every library and, per library, every live node with its own series
// link, as a stored per-item snapshot with incremental sync (updatedSince + removals). Vocabulary values are plain strings
// mapped explicitly on the server (the wire format is the contract). Names only - no DTO carries a path. Times are UTC ISO 8601
// with milliseconds ("2026-10-03T12:00:00.000Z").

/// <summary>An export error: <c>{ "error": "fullSyncRequired" }</c>.</summary>
public sealed record ExportErrorDto
{
    public required string Error { get; init; }
}

/// <summary><c>GET /api/v1/export/libraries</c>.</summary>
public sealed record ExportLibrariesDto
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset ServerTime { get; init; }
    public required IReadOnlyList<ExportLibraryDto> Libraries { get; init; }
}

/// <summary>One library.</summary>
public sealed record ExportLibraryDto
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The library's declared type (<c>manga</c>, <c>manhwa</c>, <c>comic</c>, ...), or null when none is declared.</summary>
    public string? Kind { get; init; }

    /// <summary>Live folders in the library.</summary>
    public int? FolderCount { get; init; }

    /// <summary>Items the export has for the library (live nodes with their own link row).</summary>
    public int? ItemCount { get; init; }

    public DateTimeOffset? LastScanAt { get; init; }
}

/// <summary>The library an export page is about (no counts).</summary>
public sealed record ExportLibraryRefDto
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? Kind { get; init; }
}

/// <summary><c>GET /api/v1/export/metadata</c>: one page of one library.</summary>
public sealed record ExportMetadataPageDto
{
    public required int SchemaVersion { get; init; }

    /// <summary>The time of the rebuild this page was served from; the FIRST page's value is the next <c>updatedSince</c>.</summary>
    public required DateTimeOffset ServerTime { get; init; }

    public required ExportLibraryRefDto Library { get; init; }
    public required IReadOnlyList<ExportItemDto> Items { get; init; }

    /// <summary>Nodes that left the export since <c>updatedSince</c> (first page of an incremental call only; empty otherwise).</summary>
    public required IReadOnlyList<ExportRemovalDto> Removed { get; init; }

    public string? NextCursor { get; init; }
}

/// <summary>A node that left the export.</summary>
public sealed record ExportRemovalDto
{
    public required string NodeId { get; init; }

    /// <summary><c>nodeGone</c>, <c>linkCleared</c> or <c>movedToOtherLibrary</c>.</summary>
    public required string Reason { get; init; }

    public required DateTimeOffset At { get; init; }
}

/// <summary>One exported node: a folder or an archive with its own link row.</summary>
public sealed record ExportItemDto
{
    public required string NodeId { get; init; }

    /// <summary><c>folder</c> or <c>archive</c>.</summary>
    public required string NodeKind { get; init; }

    /// <summary>The node id this link was carried from (a renamed or moved folder), or null.</summary>
    public string? CarriedFrom { get; init; }

    /// <summary>On-disk names from below the library root down to the node itself (an archive's ends with its file name).</summary>
    public required IReadOnlyList<string> Trail { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
    public required ExportLinkDto Link { get; init; }

    /// <summary>The linked record, or null (Needs review and Don't match have none).</summary>
    public ExportRecordDto? Record { get; init; }

    public required ExportCompanionsDto Companions { get; init; }
    public required IReadOnlyList<ExportOfficialLinkDto> OfficialLinks { get; init; }

    /// <summary>The per-volume list, or null when nothing is stored (omitted when not requested).</summary>
    public ExportVolumesDto? Volumes { get; init; }

    /// <summary>The Completion answer (linked folders only), or null (omitted when not requested).</summary>
    public ExportCompletionDto? Completion { get; init; }

    /// <summary>When the record is looked at again, or null without a record (omitted when not requested).</summary>
    public ExportRefreshDto? Refresh { get; init; }

    /// <summary>
    /// 1.38.0: chapter / volume numbers that more than one file of a linked series folder states, each with its files (the first
    /// 50). Left out of the item when there are none or the node is not a linked series folder - and when not requested.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExportDuplicateDto>? Duplicates { get; init; }
}

/// <summary>One number that several files in the same folder state (chapter 12 uploaded twice), with each of those files.</summary>
public sealed record ExportDuplicateDto
{
    /// <summary><c>Volume</c> or <c>Chapter</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The number as the names state it (<c>"1"</c>, <c>"45.5"</c>).</summary>
    public required string Number { get; init; }

    /// <summary>The files that state it, in the folder's name order (the first 20).</summary>
    public required IReadOnlyList<ExportDuplicateFileDto> Files { get; init; }
}

/// <summary>One file of a duplicate: open it in the reader at <c>/reader/{nodeId}</c>.</summary>
public sealed record ExportDuplicateFileDto
{
    /// <summary>The archive's node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>The file name on disk.</summary>
    public required string Name { get; init; }

    /// <summary>The unit subfolder the file is in (<c>Volumes</c>, <c>Season 2</c>), or null when it is in the series folder itself - a
    /// number is a duplicate only within one folder.</summary>
    public string? Folder { get; init; }
}

/// <summary>The node's own link row.</summary>
public sealed record ExportLinkDto
{
    /// <summary>
    /// <c>Confirmed</c>, <c>Auto</c>, <c>NeedsReview</c>, <c>DontMatch</c> or (1.34.0) <c>CollectionAbout</c> - a folder of works about the
    /// <c>record</c> (a label: never matched as the series, no numbers) or (1.37.0) <c>ArtistFolder</c> - a folder of one artist's works
    /// (no <c>record</c>, no companions or completion; its works are items of their own). Treat an unknown state as "not a series".
    /// </summary>
    public required string State { get; init; }

    /// <summary><c>search</c>, <c>reference</c>, <c>comicInfo</c> or <c>auto</c>, or null.</summary>
    public string? Method { get; init; }

    public double? Score { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The linked provider record.</summary>
public sealed record ExportRecordDto
{
    /// <summary><c>mangaupdates</c> or <c>gcd</c>.</summary>
    public required string Provider { get; init; }

    public required string ExternalId { get; init; }
    public string? SiteUrl { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<string> AltTitles { get; init; }

    /// <summary>The provider's own type word (<c>Manga</c>, <c>Manhwa</c>, ...).</summary>
    public string? Type { get; init; }

    /// <summary><c>Ongoing</c>, <c>Complete</c>, <c>Hiatus</c>, <c>Cancelled</c>, or null.</summary>
    public string? OriginStatus { get; init; }

    public int? OriginVolumes { get; init; }

    /// <summary>The latest released chapter as an exact string (<c>380</c>, <c>12.5</c>), or null.</summary>
    public string? LatestChapter { get; init; }

    /// <summary>Chapters in the country of origin, from the status text, or null.</summary>
    public int? TotalChapters { get; init; }

    public string? StatusText { get; init; }
    public bool? LicensedEn { get; init; }
    public bool? TranslationComplete { get; init; }
    public bool? CompletedInOrigin { get; init; }
    public required IReadOnlyList<ExportPublisherDto> EnglishPublishers { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
}

/// <summary>An English publisher of the record.</summary>
public sealed record ExportPublisherDto
{
    public required string Name { get; init; }
    public int? Volumes { get; init; }
    public int? Chapters { get; init; }

    /// <summary><c>Ongoing</c>, <c>Complete</c>, <c>Hiatus</c>, <c>Cancelled</c>, or null.</summary>
    public string? Status { get; init; }

    public required bool Omnibus { get; init; }
}

/// <summary>The companion records of the linked series.</summary>
public sealed record ExportCompanionsDto
{
    /// <summary>The MangaDex record id (a UUID), or null.</summary>
    public string? Mangadex { get; init; }

    public ExportAniListDto? Anilist { get; init; }
}

/// <summary>The AniList entry of the linked series and its totals.</summary>
public sealed record ExportAniListDto
{
    public required long Id { get; init; }
    public int? Chapters { get; init; }
    public int? Volumes { get; init; }
}

/// <summary>An official source of the series.</summary>
public sealed record ExportOfficialLinkDto
{
    /// <summary><c>publisher</c> or <c>store</c>.</summary>
    public required string Kind { get; init; }

    public required string Label { get; init; }
    public required string Url { get; init; }

    /// <summary><c>mangadex</c>.</summary>
    public required string Source { get; init; }
}

/// <summary>The per-volume list of the series.</summary>
public sealed record ExportVolumesDto
{
    /// <summary><c>mangadex</c>, <c>wikipedia</c> or <c>merged</c>.</summary>
    public required string Source { get; init; }

    public DateTimeOffset? FetchedAt { get; init; }
    public required IReadOnlyList<ExportVolumeDto> Items { get; init; }
}

/// <summary>One volume.</summary>
public sealed record ExportVolumeDto
{
    /// <summary>The volume number as an exact string.</summary>
    public required string Volume { get; init; }

    public string? Title { get; init; }

    /// <summary>The first and last chapter the volume collects, or null when unknown.</summary>
    public ExportChapterRangeDto? Chapters { get; init; }

    /// <summary>The English release date (<c>2024-05-14</c>, or partial: <c>2024-05</c>, <c>2024</c>), or null.</summary>
    public string? EnglishDate { get; init; }

    /// <summary><c>released</c> (not after today) or <c>announced</c>, or null without a date.</summary>
    public string? EnglishDateKind { get; init; }

    public string? Isbn { get; init; }

    /// <summary>Where this volume's data came from: <c>mangadex</c>, <c>wikipedia</c>.</summary>
    public required IReadOnlyList<string> Sources { get; init; }
}

/// <summary>A chapter range; numbers are exact strings.</summary>
public sealed record ExportChapterRangeDto
{
    public required string From { get; init; }
    public required string To { get; init; }
}

/// <summary>The series' Completion answer as MangaPixer computed it from its own scan.</summary>
public sealed record ExportCompletionDto
{
    /// <summary><c>CantTell</c>, <c>HaveItAll</c>, <c>FinishedMissing</c>, <c>UpToDate</c> or <c>MissingSome</c>.</summary>
    public required string Answer { get; init; }

    /// <summary>The reason code (<c>None</c>, <c>Running</c>, <c>NoNumbers</c>, ...).</summary>
    public required string Reason { get; init; }

    public required bool UpgradeAvailable { get; init; }

    /// <summary>Volumes released officially that the folder holds only as chapters (the first 50).</summary>
    public required IReadOnlyList<int> UpgradeVolumes { get; init; }

    /// <summary>When this answer was computed (the rebuild that last changed the item).</summary>
    public required DateTimeOffset ComputedAt { get; init; }

    /// <summary>The library scan the answer was computed from, or null when the library was never scanned.</summary>
    public DateTimeOffset? BasedOnScanAt { get; init; }
}

/// <summary>When the linked record is looked at again.</summary>
public sealed record ExportRefreshDto
{
    public required DateTimeOffset LastFetchedAt { get; init; }
    public required DateTimeOffset NextDueAt { get; init; }
    public required int IntervalDays { get; init; }
}
