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

/// <summary>Whether the folder holds a finished series whole.</summary>
public enum SeriesCompletion
{
    /// <summary>Not finished (or not known to be), or no numbers to compare.</summary>
    None = 0,

    /// <summary>Finished in the preferred language (official edition or scanlation), but the folder does not hold all of it.</summary>
    FinishedNotHeld = 1,

    /// <summary>Finished, and the folder holds all of it ("Complete collection").</summary>
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

    /// <summary>The number of volumes (bases OfficialVolumes / OriginRun by volumes) or chapters the completion is about.</summary>
    public int? CompletionTarget { get; init; }

    /// <summary>How many of <see cref="CompletionTarget"/> the folder holds.</summary>
    public int? CompletionHeld { get; init; }

    /// <summary>True when <see cref="CompletionTarget"/> counts chapters (basis AllChapters, or an origin run known only in chapters).</summary>
    public bool CompletionInChapters { get; init; }
}
