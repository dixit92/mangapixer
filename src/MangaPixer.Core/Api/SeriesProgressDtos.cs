namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

// A linked series' progress (1.30.0, "reach"): what the stored record says is out - in the country of origin, officially in
// the preferred language, as released chapters - next to what the folder holds, with volume files and chapter files merged
// through the stored volume -> chapter list. Built from stored data only; no DTO carries a path. Enums arrive as their C# names.

/// <summary>A run of whole unit numbers (<c>1-14</c>); <see cref="From"/> equals <see cref="To"/> for one number.</summary>
public sealed record UnitSpanDto
{
    public required int From { get; init; }
    public required int To { get; init; }
}

/// <summary>How the chapters inside the folder's volume files were known.</summary>
public enum ReachResolution
{
    /// <summary>Only file names: chapter files that state their volume (<c>v10 c085</c>), or nothing.</summary>
    FileNames = 0,

    /// <summary>The stored volume list (exact, or bounded by known neighbours).</summary>
    VolumeList = 1,

    /// <summary>At least one volume file's chapters are an estimate: its overlap with chapter files is not claimed.</summary>
    Estimated = 2,
}

/// <summary>What the folder holds, volumes and chapters merged (a chapter inside a volume file here counts once).</summary>
public sealed record SeriesReachDto
{
    /// <summary>Whole volumes held as volume files.</summary>
    public required IReadOnlyList<UnitSpanDto> VolumeFiles { get; init; }

    /// <summary>Chapters held as chapter files that no volume file here already holds (at most 20 spans).</summary>
    public required IReadOnlyList<UnitSpanDto> Chapters { get; init; }

    /// <summary>The highest whole chapter held (chapter files, and the chapters of volume files through the list).</summary>
    public int? ReachChapter { get; init; }

    /// <summary>The highest volume held whole: a volume file, or every listed chapter of it as chapter files.</summary>
    public int? ReachVolume { get; init; }

    /// <summary>Chapters held both as chapter files and inside a volume file here ("Also in Volume N"); counted once.</summary>
    public required int OverlapChapters { get; init; }

    public required ReachResolution Resolution { get; init; }
}

/// <summary>
/// The per-kind trackers of the stored record: the country of origin, the official release in the preferred language
/// (<see cref="Language"/>; publisher totals are known only for English today) and the chapters released in that language.
/// </summary>
public sealed record SeriesTrackersDto
{
    /// <summary>The preferred language ("en", "fr", ...) the official and released fields are about.</summary>
    public required string Language { get; init; }

    public MetadataOrigin? Origin { get; init; }

    /// <summary>The publication status in the country of origin (the status word of the line).</summary>
    public MetadataOriginStatus? OriginStatus { get; init; }
    public int? OriginVolumes { get; init; }
    public int? OriginChapters { get; init; }

    /// <summary>The official publisher in the preferred language (the one with the most regular volumes).</summary>
    public string? OfficialPublisher { get; init; }

    /// <summary>Official volumes in the preferred language (regular edition; omnibus editions never count).</summary>
    public int? OfficialVolumes { get; init; }

    /// <summary>Official chapters in the preferred language (a digital, chapter-by-chapter publisher).</summary>
    public int? OfficialChapters { get; init; }

    /// <summary>The official publisher's own status (Cancelled = dropped / defunct), or null when unknown.</summary>
    public MetadataOriginStatus? OfficialStatus { get; init; }

    /// <summary>English only: licensed in English (MangaUpdates), or null.</summary>
    public bool? Licensed { get; init; }

    /// <summary>English only: the latest released chapter (scanlation; MangaUpdates), or null.</summary>
    public int? LatestChapter { get; init; }

    /// <summary>English only: the scanlation is complete (MangaUpdates), or null.</summary>
    public bool? ScanlationComplete { get; init; }

    /// <summary>The highest chapter the stored volume list names as released in the preferred language, or null.</summary>
    public int? ReleasedChapter { get; init; }
}

/// <summary>
/// Whether the folder holds a finished series whole. 1.32.0: "finished" always needs the original run to have ended (complete or
/// cancelled where it comes from) - a finished English edition of a running series is not the end of the story.
/// </summary>
public enum SeriesCompletion
{
    /// <summary>Not finished (or not known to be), or no numbers to compare.</summary>
    None = 0,

    /// <summary>Finished in the preferred language (official edition or every chapter), but the folder does not hold all of it.</summary>
    FinishedNotHeld = 1,

    /// <summary>Finished, and the folder holds all of it ("Finished - you have it all").</summary>
    CompleteCollection = 2,
}

/// <summary>What "all of it" meant for <see cref="SeriesCompletion"/>.</summary>
public enum CompletionBasis
{
    /// <summary>Every official volume in the preferred language (the official edition is finished).</summary>
    OfficialVolumes = 0,

    /// <summary>Every chapter of a finished scanlation (English).</summary>
    AllChapters = 1,

    /// <summary>The whole run in the country of origin (complete or cancelled there).</summary>
    OriginRun = 2,

    /// <summary>
    /// 1.32.0: every chapter, released officially in the preferred language by a publisher that releases chapter by chapter (MANGA
    /// Plus-style) - the same rule as <see cref="AllChapters"/>, named for its known source.
    /// </summary>
    OfficialChapters = 3,

    /// <summary>
    /// 1.39.0: every volume of the edition an admin declared for the folder ("Volumes in this edition: N", e.g. an omnibus edition),
    /// held as volume files. Replaces the regular edition's volume bases for that folder.
    /// </summary>
    Edition = 4,
}

/// <summary>
/// The one answer a linked series gets on the Completion tab (1.32.0, owner-approved wording): has it ended, and does the folder hold
/// all of it. Upgrades are a separate flag (<see cref="SeriesProgressDto.UpgradeCount"/>).
/// </summary>
public enum SeriesAnswer
{
    /// <summary>"Can't tell": the names carry no numbers, numbering restarts, or nothing is known to compare with.</summary>
    CantTell = 0,

    /// <summary>"Finished - you have it all": the original run ended and the folder holds the whole edition it collects.</summary>
    HaveItAll = 1,

    /// <summary>"Finished - missing some": the original run ended and something released, or the finished edition, is not all here.</summary>
    FinishedMissing = 2,

    /// <summary>"Everything released so far": nothing released in the preferred language is missing, and the series is not over there.</summary>
    UpToDate = 3,

    /// <summary>"Missing some": the series still runs and something released in the preferred language is not here.</summary>
    MissingSome = 4,
}

/// <summary>Why a <see cref="SeriesAnswer"/> was given (it picks the sentence).</summary>
public enum SeriesAnswerReason
{
    None = 0,

    /// <summary>Still running in the country of origin.</summary>
    Running = 1,

    /// <summary>On hiatus in the country of origin.</summary>
    OnHiatus = 2,

    /// <summary>The record does not say whether the series has ended.</summary>
    StatusUnknown = 3,

    /// <summary>Ended in the country of origin, but not all of it is out in the preferred language yet.</summary>
    WaitingForLanguage = 4,

    /// <summary>Ended in the country of origin; the edition in the preferred language was dropped by its publisher.</summary>
    LanguageEditionDropped = 5,

    /// <summary>No archive name carries a volume or chapter number.</summary>
    NoNumbers = 6,

    /// <summary>Volume or chapter numbers start again in subfolders.</summary>
    NumberingRestarts = 7,

    /// <summary>Nothing is known about what is released in the preferred language.</summary>
    NothingKnownReleased = 8,

    /// <summary>A folder of volumes of a running series, and no volume total is known in the preferred language.</summary>
    NoVolumeTotal = 9,

    /// <summary>A one-shot (one volume, ended) whose file carries no number.</summary>
    OneShot = 10,

    /// <summary>1.39.0: an admin turned "Track completion" off for this folder - no answer is given.</summary>
    NotTracked = 11,
}

/// <summary>A linked series' progress: the trackers, the folder's reach, what is missing, the upgrades and the completion.</summary>
public sealed record SeriesProgressDto
{
    public required SeriesTrackersDto Trackers { get; init; }

    /// <summary>What the folder holds; null when no archive states a number or the numbering restarts across subfolders.</summary>
    public SeriesReachDto? Reach { get; init; }

    /// <summary>Whole volumes released in the preferred language with neither a file nor a chapter here.</summary>
    public required int MissingVolumes { get; init; }

    /// <summary>Chapters released in the preferred language (or below the highest held) that no file holds.</summary>
    public required int MissingChapters { get; init; }

    /// <summary>What is released in the preferred language is known ("up to date" can be said).</summary>
    public required bool ReleaseKnown { get; init; }

    /// <summary>
    /// Volumes released officially in the preferred language that the folder holds only as chapters (whole or in part): an
    /// upgrade, never missing (the first 50).
    /// </summary>
    public required IReadOnlyList<int> UpgradeVolumes { get; init; }
    public required int UpgradeCount { get; init; }

    public required SeriesCompletion Completion { get; init; }
    public CompletionBasis? CompletionBasis { get; init; }

    /// <summary>
    /// 1.32.0: set when the series' volume list was completed from a Wikipedia page ("Volume list: MangaDex, completed from Wikipedia"):
    /// the credit and link every surface that shows such a list carries. Null when no Wikipedia data was used.
    /// </summary>
    public ListCreditDto? ListCredit { get; init; }

    /// <summary>The number of volumes (bases OfficialVolumes / OriginRun by volumes) or chapters the completion is about.</summary>
    public int? CompletionTarget { get; init; }

    /// <summary>How many of <see cref="CompletionTarget"/> the folder holds.</summary>
    public int? CompletionHeld { get; init; }

    /// <summary>True when <see cref="CompletionTarget"/> counts chapters (basis AllChapters, or an origin run known only in chapters).</summary>
    public bool CompletionInChapters { get; init; }

    /// <summary>1.32.0: the one answer of the Completion tab (also behind the completion mark).</summary>
    public SeriesAnswer Answer { get; init; }

    /// <summary>1.32.0: why <see cref="Answer"/> was given.</summary>
    public SeriesAnswerReason AnswerReason { get; init; }

    /// <summary>
    /// 1.39.0: the volumes an admin declared for the edition this folder holds ("Volumes in this edition"), or null. When set, the volume
    /// answers count volumes 1..N of that edition instead of the regular edition's list.
    /// </summary>
    public int? VolumeTotalOverride { get; init; }

    /// <summary>1.39.0: the edition label an admin declared for the folder, or null.</summary>
    public DeclaredEdition? Edition { get; init; }

    /// <summary>
    /// 1.39.0: an admin turned "Track completion" off for the folder: nothing is missing, no upgrade, no completion
    /// (<see cref="Answer"/> is CantTell with the reason NotTracked); the trackers and the reach still show.
    /// </summary>
    public bool TrackingOff { get; init; }
}
