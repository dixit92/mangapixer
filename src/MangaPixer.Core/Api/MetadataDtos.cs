namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;

// Series metadata DTOs (1.24.0 stage 1). One SeriesInfoDto feeds both the
// overlay and the series page, so the two surfaces never disagree. No DTO here
// carries a filesystem path; titles are fine (node display names are already
// exposed to anyone who can see the node).

/// <summary>
/// Resolved series information for a node: the nearest web link (walking from
/// the node itself up to 64 ancestors) merged per field with ComicInfo.xml data,
/// according to the effective source precedence.
/// </summary>
public sealed record SeriesInfoDto
{
    /// <summary>The node that was asked about.</summary>
    public required string NodeId { get; init; }
    public required CatalogNodeKind NodeKind { get; init; }

    /// <summary>
    /// The canonical home of this series: the node holding the link, or the
    /// ComicInfo folder (for an archive whose parent folder agrees on the series).
    /// The series page redirects to it.
    /// </summary>
    public required string AnchorNodeId { get; init; }
    public required CatalogNodeKind AnchorKind { get; init; }
    public required string AnchorDisplayName { get; init; }

    /// <summary>The anchor's library (for "Browse folder" links).</summary>
    public required string LibraryId { get; init; }

    public required SeriesInfoState State { get; init; }

    public string? Title { get; init; }
    public IReadOnlyList<string> AltTitles { get; init; } = [];
    public string? Description { get; init; }
    public IReadOnlyList<SeriesCreatorDto> Creators { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public MetadataOrigin? Origin { get; init; }
    public MetadataFormat? Format { get; init; }

    /// <summary>Tri-state: true / false / null (unknown).</summary>
    public bool? Webtoon { get; init; }
    public int? StartYear { get; init; }
    public MetadataOriginStatus? OriginStatus { get; init; }
    public int? OriginVolumes { get; init; }
    public double? LatestChapter { get; init; }
    public string? StatusText { get; init; }
    public bool? LicensedEn { get; init; }
    public bool? TranslationComplete { get; init; }
    public IReadOnlyList<SeriesPublisherDto> Publishers { get; init; } = [];

    /// <summary>
    /// Per-field attribution, keyed by the camelCase field name (title, description,
    /// creators, genres, startYear, publishers, altTitles, ...). Only fields that
    /// carry a value appear.
    /// </summary>
    public IReadOnlyDictionary<string, MetadataFieldSource> FieldSources { get; init; } =
        new Dictionary<string, MetadataFieldSource>();

    /// <summary>Per-item ComicInfo fields; archives only (always from ComicInfo).</summary>
    public SeriesInfoItemDto? Item { get; init; }

    /// <summary>For a mixed folder: the distinct ComicInfo series names with counts, most frequent first.</summary>
    public IReadOnlyList<SeriesMixedEntryDto> MixedSeries { get; init; } = [];

    /// <summary>The linked web record's attribution block, when a web link applies.</summary>
    public SeriesInfoWebDto? Web { get; init; }

    /// <summary>ComicInfo coverage, when any item carries ComicInfo.</summary>
    public SeriesInfoComicInfoDto? ComicInfo { get; init; }

    /// <summary>The nearest link row that applied (own or inherited), if any.</summary>
    public SeriesLinkInfoDto? Link { get; init; }

    public required MetadataPrecedence Precedence { get; init; }
    public required MetadataPrecedenceSource PrecedenceSource { get; init; }

    /// <summary>
    /// For a folder, one row per ComicInfo-carrying item (max 500) when the caller
    /// asked for <c>includeItems=true</c> (the series page); empty otherwise.
    /// </summary>
    public IReadOnlyList<SeriesInfoItemRowDto> Items { get; init; } = [];
}

public sealed record SeriesCreatorDto
{
    public required string Name { get; init; }

    /// <summary>writer, artist, author, penciller, inker, colorist, letterer, coverArtist, editor, translator, other.</summary>
    public required string Role { get; init; }
}

public sealed record SeriesPublisherDto
{
    public required string Name { get; init; }

    /// <summary>original, english, other.</summary>
    public required string Kind { get; init; }
}

public sealed record SeriesInfoItemDto
{
    public string? Number { get; init; }
    public int? Volume { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }
}

public sealed record SeriesInfoItemRowDto
{
    public required string NodeId { get; init; }
    public required string DisplayName { get; init; }
    public string? Number { get; init; }
    public int? Volume { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
}

public sealed record SeriesMixedEntryDto
{
    public required string Name { get; init; }
    public required int Count { get; init; }
}

public sealed record SeriesInfoWebDto
{
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }

    /// <summary>The provider's canonical page (https, provider host only), for attribution.</summary>
    public string? SiteUrl { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }

    /// <summary>Stage 1 lane B2 fills the poster; false/null until then.</summary>
    public bool HasImage { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record SeriesInfoComicInfoDto
{
    public required int ItemsWithComicInfo { get; init; }
    public required int ItemsTotal { get; init; }

    /// <summary>ComicInfo <c>Count</c> (total issues/volumes the tagger declared), max over items.</summary>
    public int? Count { get; init; }

    /// <summary>ComicInfo <c>Web</c> URLs on allowlisted hosts only.</summary>
    public IReadOnlyList<string> WebLinks { get; init; } = [];
}

public sealed record SeriesLinkInfoDto
{
    public required SeriesLinkState State { get; init; }

    /// <summary>The node holding the link row.</summary>
    public required string NodeId { get; init; }

    /// <summary>True when the row sits on an ancestor, not on the node itself.</summary>
    public required bool Inherited { get; init; }
    public DateTimeOffset? LinkedAt { get; init; }
}

// --- Admin ---

/// <summary>Admin view of the metadata settings (both toggles + the B2 network status fields).</summary>
public sealed record MetadataSettingsDto
{
    /// <summary>Global "Show series information" (hides all metadata, web and ComicInfo, when off).</summary>
    public required bool ShowSeriesInfo { get; init; }

    /// <summary>Global "Fetch from the web" switch (network; used by lane B2).</summary>
    public required bool FetchEnabled { get; init; }

    /// <summary>True when the operator set <c>Metadata:NetworkDisabled=true</c> (overrides the UI).</summary>
    public required bool NetworkDisabledByConfig { get; init; }

    public int? AcceptedConsentVersion { get; init; }
    public required int CurrentConsentVersion { get; init; }
    public DateTimeOffset? ConsentAt { get; init; }

    /// <summary>Effective daily request budget (default 5000 when not set).</summary>
    public required int DailyBudget { get; init; }
    public required int DefaultDailyBudget { get; init; }
    public required int BudgetUsedToday { get; init; }
    public DateTimeOffset? BackoffUntil { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
    public string? LastErrorCode { get; init; }

    public required MetadataComicInfoStatsDto ComicInfo { get; init; }
    public required int WebRecordCount { get; init; }
    public required IReadOnlyList<MetadataLibrarySettingsDto> Libraries { get; init; }
}

public sealed record MetadataComicInfoStatsDto
{
    /// <summary>Ready archives whose ComicInfo was read for the current content version.</summary>
    public required int ArchivesRead { get; init; }

    /// <summary>Ready archives in total.</summary>
    public required int ArchivesTotal { get; init; }

    /// <summary>Of the read ones, how many carried a parseable ComicInfo.xml.</summary>
    public required int ArchivesWithComicInfo { get; init; }
}

public sealed record MetadataLibrarySettingsDto
{
    public required string LibraryId { get; init; }
    public required string Name { get; init; }
    public required bool FetchEnabled { get; init; }
    public required bool ShowSeriesInfo { get; init; }

    /// <summary>Library precedence override; null = default (web first).</summary>
    public MetadataPrecedence? Precedence { get; init; }
    public required int LinkCount { get; init; }
}

/// <summary>
/// Partial update of the global settings; null fields are left unchanged.
/// Turning <see cref="FetchEnabled"/> on requires <see cref="AcceptedConsentVersion"/>
/// to equal the current consent version.
/// </summary>
public sealed record UpdateMetadataSettingsRequest
{
    public bool? ShowSeriesInfo { get; init; }
    public bool? FetchEnabled { get; init; }
    public int? AcceptedConsentVersion { get; init; }

    /// <summary>A positive integer; see <see cref="ResetDailyBudget"/> to return to the default.</summary>
    public int? DailyBudget { get; init; }
    public bool ResetDailyBudget { get; init; }
}

/// <summary>Partial update of one library's toggles; null fields are left unchanged.</summary>
public sealed record UpdateMetadataLibraryRequest
{
    public bool? FetchEnabled { get; init; }
    public bool? ShowSeriesInfo { get; init; }
}

/// <summary>Sets a precedence override. For a library, null clears it (back to the default).</summary>
public sealed record SetMetadataPrecedenceRequest
{
    public MetadataPrecedence? Precedence { get; init; }
}

/// <summary>Links a node to an existing web record by provider + external id (no network in B1).</summary>
public sealed record LinkSeriesRequest
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public MetadataMatchMethod? MatchMethod { get; init; }
    public double? MatchScore { get; init; }
}

/// <summary>A node's own link row, as the admin endpoints report it.</summary>
public sealed record NodeSeriesLinkDto
{
    public required string NodeId { get; init; }
    public required SeriesLinkState State { get; init; }
    public string? Provider { get; init; }
    public string? ExternalId { get; init; }
    public string? RecordId { get; init; }
    public MetadataMatchMethod? MatchMethod { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Result of a link / dont-match change: the new row and the one it replaced (for Undo).</summary>
public sealed record NodeSeriesLinkChangeDto
{
    public required string NodeId { get; init; }
    public NodeSeriesLinkDto? Link { get; init; }
    public NodeSeriesLinkDto? Previous { get; init; }
}

/// <summary>Purge scope: null library = everything.</summary>
public sealed record MetadataPurgeRequest
{
    public string? LibraryId { get; init; }
}

public sealed record MetadataPurgeResultDto
{
    public required int LinksRemoved { get; init; }
    public required int RecordsRemoved { get; init; }
}

/// <summary>A folder's precedence override as stored.</summary>
public sealed record FolderMetadataPrecedenceDto
{
    public required string NodeId { get; init; }
    public required MetadataPrecedence Precedence { get; init; }
}
