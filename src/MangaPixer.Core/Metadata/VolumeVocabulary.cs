namespace com.lifepixer.mangapixer.Core.Metadata;

// Virtual volumes and volume covers (1.29.0). Every enum below is persisted as its int
// value: append new members at the end, never renumber.

/// <summary>State of a companion record (MangaDex / AniList) of a linked series record.</summary>
public enum CompanionState
{
    /// <summary>Found by the cross-link rule (the companion's own MangaUpdates link names the series record).</summary>
    Auto = 0,

    /// <summary>Chosen by an admin (a pasted reference).</summary>
    Confirmed = 1,

    /// <summary>Looked up, nothing acceptable found; retried on the record's refresh cadence.</summary>
    NotFound = 2,

    /// <summary>An admin said the series is not on this provider: never retried.</summary>
    None = 3,

    /// <summary>The last attempt failed (network, provider error); retried later.</summary>
    Failed = 4,
}

/// <summary>How a companion was found.</summary>
public enum CompanionMethod
{
    /// <summary>MangaDex search by the series record's own title, accepted on <c>links.mu</c>.</summary>
    CrossLink = 0,

    /// <summary>An admin pasted a reference.</summary>
    Reference = 1,

    /// <summary>Taken from another companion's link (the AniList id in MangaDex's <c>links.al</c>).</summary>
    FromCompanion = 2,

    /// <summary>An AniList ratio record written before 1.29.0 (keyed by <c>CrossIdsJson</c>), adopted.</summary>
    Legacy = 3,
}

/// <summary>Where a stored volume -> chapters map came from.</summary>
public enum VolumeMapSource
{
    /// <summary>MangaDex <c>GET /manga/{id}/aggregate?includeUnavailable=1</c>: the exact list.</summary>
    MangaDexAggregate = 1,

    /// <summary>AniList totals: a chapters-per-volume ratio only, no list.</summary>
    AniListRatio = 2,

    /// <summary>
    /// The English Wikipedia "List of ... chapters" page of the series (1.32.0): a validated volume -> chapters list that FILLS what
    /// MangaDex's list lacks (<c>VolumeListMerge</c>). Stored as numbers only.
    /// </summary>
    WikipediaList = 3,
}

/// <summary>State of a stored volume map.</summary>
public enum VolumeMapState
{
    Ok = 0,

    /// <summary>The provider answered but has nothing (no volumes).</summary>
    Empty = 1,

    Failed = 2,
}

/// <summary>What a known web cover is.</summary>
public enum VolumeCoverKind
{
    /// <summary>The cover of one volume (MangaDex key <c>"N"</c>, or <c>"N.k"</c> for an edition alternate).</summary>
    Volume = 0,

    /// <summary>The record's main cover (MangaDex <c>cover_art</c>) - webtoons and series without volume covers.</summary>
    Main = 1,
}

/// <summary>Download state of a known web cover.</summary>
public enum VolumeCoverState
{
    /// <summary>Listed by the provider, not downloaded.</summary>
    Listed = 0,

    /// <summary>Downloaded, re-encoded and hashed; the file is in the data root.</summary>
    Stored = 1,

    Failed = 2,

    /// <summary>No longer listed by the provider; the file is kept while a choice uses it.</summary>
    Gone = 3,
}

/// <summary>An admin's per-node cover choice (no row = automatic).</summary>
public enum CoverChoiceMode
{
    /// <summary>"Use the file's cover": the file default, and no automatic layer.</summary>
    FilePinned = 1,

    /// <summary>Another archive's file cover.</summary>
    Archive = 2,

    /// <summary>A known web cover.</summary>
    VolumeCover = 3,

    /// <summary>A half of the node's own page 1 (a jacket spread).</summary>
    Crop = 4,
}

/// <summary>The source the automatic cover layer decided for a node.</summary>
public enum AutoCoverSource
{
    /// <summary>Decided: keep the file cover.</summary>
    File = 0,

    /// <summary>The front half of a spread page 1.</summary>
    Crop = 1,

    /// <summary>A web volume cover.</summary>
    WebVolume = 2,

    /// <summary>The web main cover of the record.</summary>
    WebMain = 3,

    /// <summary>The stored MangaUpdates image of the linked record.</summary>
    Poster = 4,

    /// <summary>
    /// A linked series folder shows its local volume 1 archive's RESOLVED cover (that archive's own choice, crop, web cover or
    /// file - 1.30.0, owner soak test); the archive is <c>node_auto_covers.ArchiveNodeId</c>.
    /// </summary>
    LocalVolume1 = 5,
}

/// <summary>Why the automatic layer decided what it did (shown in the picker).</summary>
public enum AutoCoverReason
{
    None = 0,

    /// <summary>The local cover and the web cover are the same (hash distance within the Same bound).</summary>
    FileMatchesWeb = 1,

    /// <summary>Page 1 is a jacket spread; its front half is used.</summary>
    Spread = 2,

    /// <summary>Page 1 is a spread and the other half matched the web cover.</summary>
    SpreadOtherSide = 3,

    /// <summary>The local cover is clearly not the volume's cover; the web cover is used.</summary>
    LocalNotCover = 4,

    /// <summary>Local and web covers are close but not the same (another edition): the local one is kept.</summary>
    UncertainKept = 5,

    /// <summary>No web cover exists for this node; the file is kept.</summary>
    NoWebCover = 6,

    /// <summary>A series folder shows its volume 1 cover.</summary>
    SeriesVolume1 = 7,

    /// <summary>A chapter folder shows the web cover (its page 1 is a chapter page or a credit card).</summary>
    ChapterFolderDefault = 8,

    /// <summary>A webtoon folder shows the web cover (its page 1 is a strip fragment).</summary>
    WebtoonDefault = 9,

    /// <summary>A one-shot shows the web cover by default.</summary>
    OneShotDefault = 10,

    /// <summary>A Season / Part subfolder shows the cover of the volume its first chapter belongs to.</summary>
    SubfolderFirstVolume = 11,

    /// <summary>
    /// The only web cover is in the series' original language (the preferred one has none): a cover in another language is no
    /// evidence that page 1 is not the cover, so the local cover is kept (1.30.0, owner soak test).
    /// </summary>
    OtherLanguageKept = 12,

    /// <summary>A linked series folder shows the cover of its own volume 1 archive, as that archive decided it (1.30.0).</summary>
    SeriesLocalVolume1 = 13,

    /// <summary>1.34.0: a "Collection about" folder shows the poster of the series it is about (its items are other works).</summary>
    CollectionPoster = 14,
}

/// <summary>Which half of a spread page.</summary>
public enum CoverCropSide
{
    Left = 1,
    Right = 2,
}

/// <summary>A per-library / per-folder view override (null = inherit).</summary>
public enum ViewSwitch
{
    Off = 0,
    On = 1,
}

/// <summary>The per-user series view (the Volumes | Folders switch in the series header).</summary>
public enum SeriesViewMode
{
    /// <summary>The real folder structure, no stacks.</summary>
    Folders = 0,

    /// <summary>Volume-ordered, with virtual volume stacks and merged unit subfolders where the data exists.</summary>
    Volumes = 1,
}

/// <summary>State of the Wikipedia companion of a linked series (<c>wikipedia_lists.State</c>, 1.32.0). Persisted as its int value.</summary>
public enum WikipediaListState
{
    /// <summary>A list was found, validated and stored.</summary>
    Found = 0,

    /// <summary>No English article / list page was found for the series; retried on the record's refresh cadence.</summary>
    NotFound = 1,

    /// <summary>A page was found but its list failed validation (see the reject code); checked again when the page changes.</summary>
    Rejected = 2,

    /// <summary>An admin said the series has no Wikipedia list: never asked again.</summary>
    None = 3,

    /// <summary>The last attempt failed (network, provider error); retried after a day.</summary>
    Failed = 4,
}

/// <summary>How the Wikipedia page of a series was found (<c>wikipedia_lists.Method</c>, 1.32.0).</summary>
public enum WikipediaListMethod
{
    /// <summary>Wikidata's item with the series' MangaUpdates id -> its English article.</summary>
    Wikidata = 0,

    /// <summary>The linked MangaUpdates record's title (Wikidata had no such item).</summary>
    Title = 1,

    /// <summary>An admin chose the page title.</summary>
    Admin = 2,
}
