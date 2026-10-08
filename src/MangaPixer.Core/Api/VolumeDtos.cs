namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

// Virtual volumes and volume covers (1.29.0 contract). Types for the wave-2 lanes: S (stacks, view settings),
// C (cover layer, picker), P (companions, volume lists, cover pass). No DTO carries a path, a provider image URL or an
// internal id; unit numbers are canonical invariant strings ("3", "45.5").

/// <summary>Where a card's cover comes from.</summary>
public enum CardCoverSource
{
    /// <summary>The file's own page 1 (or, for a folder, its cover archive's).</summary>
    File = 0,

    /// <summary>The front half of a jacket spread on page 1.</summary>
    Crop = 1,

    /// <summary>A web volume cover.</summary>
    WebVolume = 2,

    /// <summary>The web main cover of the series.</summary>
    WebMain = 3,

    /// <summary>The stored poster of the linked record.</summary>
    Poster = 4,

    /// <summary>An admin's choice.</summary>
    Chosen = 5,
}

/// <summary>How sure a volume stack's membership is.</summary>
public enum VolumeStackConfidence
{
    /// <summary>From the file names / ComicInfo, the provider's volume list, or bounded by known neighbours.</summary>
    Exact = 0,

    /// <summary>Some members were placed by an average (shown "~ Volume N").</summary>
    Estimated = 1,
}

/// <summary>Where a volume stack's mapping came from.</summary>
public enum VolumeListSource
{
    FileNames = 0,
    MangaDex = 1,
    AniList = 2,
    Mixed = 3,

    /// <summary>1.32.0: the volume list was completed from (or built only from) the series' English Wikipedia list page.</summary>
    Wikipedia = 4,
}

/// <summary>A virtual volume stack as a browse entry (<see cref="CatalogNodeDto.VolumeStack"/>).</summary>
public sealed record VolumeStackSummaryDto
{
    /// <summary>The volume key ("3"); with the folder id it names the stack view.</summary>
    public required string Key { get; init; }

    /// <summary>"Volume 3" or "~ Volume 12".</summary>
    public required string Label { get; init; }

    /// <summary>Members present (the volume archive counts as one).</summary>
    public required int PresentCount { get; init; }

    /// <summary>Whole chapters of the volume when known.</summary>
    public int? ChapterCount { get; init; }

    public required int MissingCount { get; init; }
    public required int ExtraCount { get; init; }

    /// <summary>A real volume archive of this volume is a member.</summary>
    public required bool HasVolumeArchive { get; init; }

    public required VolumeStackConfidence Confidence { get; init; }
    public string? FirstChapter { get; init; }
    public string? LastChapter { get; init; }

    /// <summary>
    /// 1.29.0 RC: the volume's chapters that are complete here (a split chapter counts once, when all its listed parts are here),
    /// of <see cref="ChapterCount"/>; null when no list says.
    /// </summary>
    public int? ChaptersPresent { get; init; }

    /// <summary>
    /// 1.29.0 RC: a MISSING volume - neither a volume file nor any chapter of it is here (a gap below the highest volume here, or
    /// released in the preferred language after it). A placeholder card, never opened; PresentCount is 0.
    /// </summary>
    public bool Missing { get; init; }

    /// <summary>
    /// 1.30.0: the language code ("en") when this volume, held here without a volume file, is released officially in the preferred
    /// language - "Volume 15 available in English"; null otherwise.
    /// </summary>
    public string? OfficialRelease { get; init; }

    /// <summary>
    /// 1.31.0: chapters of this volume that more than one file states ("Chapter 1: 2 files"); empty when none. <see cref="PresentCount"/> and
    /// <see cref="ExtraCount"/> count each such chapter once.
    /// </summary>
    public IReadOnlyList<DuplicateUnitDto> Duplicates { get; init; } = [];
}

/// <summary>The stack view (<c>GET /nodes/{folderId}/volumes/{key}</c>).</summary>
public sealed record VolumeStackDto
{
    public required string FolderId { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? CoverUrl { get; init; }
    public required VolumeStackConfidence Confidence { get; init; }
    public required VolumeListSource Source { get; init; }

    /// <summary>1.32.0: the Wikipedia page the series' volume list was completed from, or null (the stack view's source line links it).</summary>
    public ListCreditDto? ListCredit { get; init; }

    public required int PresentCount { get; init; }
    public int? ChapterCount { get; init; }

    /// <summary>1.29.0 RC: the complete chapters of <see cref="ChapterCount"/> (see <see cref="VolumeStackSummaryDto.ChaptersPresent"/>).</summary>
    public int? ChaptersPresent { get; init; }

    /// <summary>1.29.0 RC: a real volume file is the first slot (a fractional volume file, the last slot, is an extra).</summary>
    public bool HasVolumeArchive { get; init; }

    public required int MissingCount { get; init; }
    public required int ExtraCount { get; init; }
    public string? PreviousKey { get; init; }
    public string? NextKey { get; init; }
    public required IReadOnlyList<VolumeSlotDto> Slots { get; init; }

    /// <summary>1.30.0: see <see cref="VolumeStackSummaryDto.OfficialRelease"/>.</summary>
    public string? OfficialRelease { get; init; }

    /// <summary>1.31.0: see <see cref="VolumeStackSummaryDto.Duplicates"/>. Each such chapter has one item slot per file.</summary>
    public IReadOnlyList<DuplicateUnitDto> Duplicates { get; init; } = [];
}

/// <summary>One slot of a stack view: a present item, or a missing chapter's placeholder.</summary>
public enum VolumeSlotKind
{
    Item = 0,
    Missing = 1,
}

public sealed record VolumeSlotDto
{
    public required VolumeSlotKind Kind { get; init; }

    /// <summary>The chapter number ("8"), when known.</summary>
    public string? Chapter { get; init; }

    /// <summary>The present item (Kind Item).</summary>
    public CatalogNodeDto? Item { get; init; }
}

/// <summary>A per-folder view override (<c>GET / PUT /nodes/{nodeId}/view-settings</c>).</summary>
public sealed record FolderViewSettingsDto
{
    public required string NodeId { get; init; }

    /// <summary>This folder's Volumes view override; null = inherit the library.</summary>
    public ViewSwitch? VirtualVolumes { get; init; }
}

/// <summary>Replaces a folder's view overrides; a null field inherits again.</summary>
public sealed record UpdateFolderViewSettingsRequest
{
    public ViewSwitch? VirtualVolumes { get; init; }
}

/// <summary>An admin cover choice mode as the API names it.</summary>
public enum CoverMode
{
    Automatic = 0,
    FilePinned = 1,
    Archive = 2,
    VolumeCover = 3,
    Crop = 4,
}

/// <summary>A node's current cover.</summary>
public sealed record CoverStateDto
{
    public required CoverMode Mode { get; init; }

    /// <summary>What the automatic layer uses (Mode Automatic).</summary>
    public CardCoverSource? AutoSource { get; init; }

    /// <summary>Why (an <c>AutoCoverReason</c> name).</summary>
    public string? Reason { get; init; }

    public string? ImageUrl { get; init; }

    /// <summary>When a missing preferred-language cover is checked again.</summary>
    public DateTimeOffset? RecheckAt { get; init; }
}

/// <summary>A local option of the cover picker.</summary>
public enum CoverOptionKind
{
    File = 0,
    CropLeft = 1,
    CropRight = 2,
    Archive = 3,
}

public sealed record CoverOptionDto
{
    public required CoverOptionKind Kind { get; init; }

    /// <summary>Kind Archive: the archive whose cover it is.</summary>
    public string? ArchiveId { get; init; }

    public required string ImageUrl { get; init; }
    public required string Label { get; init; }
}

/// <summary>A known web cover (a <c>volume_covers</c> row).</summary>
public sealed record WebCoverDto
{
    public required string Id { get; init; }
    public required VolumeCoverKind Kind { get; init; }
    public int? Volume { get; init; }
    public int Variant { get; init; }
    public required string Locale { get; init; }

    /// <summary>
    /// Downloaded. False = known from the provider's list but not downloaded yet: the picker shows it but cannot choose it
    /// (choosing reads only the database - a not downloaded cover answers 409 <c>cover_not_stored</c>, nothing is requested).
    /// </summary>
    public required bool Stored { get; init; }

    public string? ImageUrl { get; init; }
}

public sealed record WebCoverGroupDto
{
    public int? Volume { get; init; }
    public required IReadOnlyList<WebCoverDto> Covers { get; init; }
}

/// <summary>
/// 1.36.0: the stored web covers of one series linked (Confirmed / Auto) somewhere below a folder that is not a series itself -
/// for the picker of that folder (e.g. a main series and its spinoffs in one folder).
/// </summary>
public sealed record WebCoverSeriesDto
{
    /// <summary>The node that carries the series link (a folder, or an archive linked on its own).</summary>
    public required string NodeId { get; init; }

    /// <summary>That node's display name (the heading).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The linked record's title (a secondary line), or null.</summary>
    public string? SeriesTitle { get; init; }

    /// <summary>Its stored covers, grouped by volume as <see cref="CoverOptionsDto.Web"/> (the main cover last).</summary>
    public required IReadOnlyList<WebCoverGroupDto> Groups { get; init; }
}

/// <summary>The cover picker (<c>GET /nodes/{nodeId}/cover-options</c>, admin).</summary>
public sealed record CoverOptionsDto
{
    public required string NodeId { get; init; }
    public required CoverStateDto Current { get; init; }
    public required IReadOnlyList<CoverOptionDto> Local { get; init; }
    /// <summary>The covers of the node's own (or inherited) linked series, grouped by volume; empty for a folder that is not a series.</summary>
    public required IReadOnlyList<WebCoverGroupDto> Web { get; init; }
    public required bool WebAvailable { get; init; }

    /// <summary>
    /// Why there is nothing to choose from the web (<c>not_linked</c>, <c>dont_match</c>, <c>collection</c>, <c>volume_covers_off</c>,
    /// <c>web_covers_hidden</c>, <c>no_companion</c>, <c>no_series_covers</c>), or null.
    /// </summary>
    public string? WebUnavailableReason { get; init; }

    /// <summary>
    /// 1.36.0: a folder that is not a series itself (no own / inherited Confirmed or Auto link): the stored web covers of the series
    /// linked below it, in the folder's order, at most <c>CoverPickerService.MaxWebSeries</c>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<WebCoverSeriesDto> WebSeries { get; init; } = [];

    /// <summary>How many more series below have stored web covers than <see cref="WebSeries"/> lists.</summary>
    public int WebSeriesMore { get; init; }
}

/// <summary>Sets a node's cover (<c>PUT /nodes/{nodeId}/cover-choice</c>, admin).</summary>
public sealed record CoverChoiceRequest
{
    public required CoverMode Mode { get; init; }
    public string? ArchiveId { get; init; }
    public string? VolumeCoverId { get; init; }
    public CoverCropSide? CropSide { get; init; }
}

/// <summary>A companion record of the node's linked series (MangaDex, AniList).</summary>
public sealed record CompanionDto
{
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }
    public string? SiteUrl { get; init; }
    public required CompanionState State { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
}

/// <summary>Sets a companion by reference (<c>PUT /nodes/{nodeId}/companions/mangadex</c>, admin): a title URL or UUID.</summary>
public sealed record CompanionReferenceRequest
{
    public required string Reference { get; init; }
}

/// <summary>The volume list of the node's linked series, for the series information "Sources".</summary>
public sealed record VolumeListInfoDto
{
    public required VolumeListSource Source { get; init; }
    public required int ExactVolumes { get; init; }
    public required int EstimatedVolumes { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
}

/// <summary>The background volume-cover pass, for the Automatic matching card.</summary>
public sealed record CoverPassStatusDto
{
    public required int SeriesPending { get; init; }
    public required int CoversListed { get; init; }
    public required int CoversStored { get; init; }

    /// <summary>A waiting code (<c>provider_not_allowed</c>, <c>budget_exhausted</c>, ...) while the pass cannot run.</summary>
    public string? Waiting { get; init; }
}
