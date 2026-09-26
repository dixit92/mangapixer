namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// Metadata stage 2 (auto-match) - the contract between the pure matcher core (Core, no IO)
// and the server plumbing that feeds it catalog rows and provider candidates and persists
// the outcome. Committed by the integrator before the two lanes branched so both build
// against the same types: the core implements IWorkDetector / IMatchQueryPlanner /
// IMatchScorer, the server consumes them through DI and tests against fakes.
//
// Rules (owner decisions, 2026-09-26): only folders the detector classes as series-like are
// matched at folder level; archive-level matching only inside collection folders, numbered
// mini-series grouped, author conflicts veto auto; ambiguous folders go to review, never
// auto. Thresholds are admin settings with bounds (MatchThresholds). Stored review
// candidates are chosen adaptively (MatchOutcome.ToPersist).
//
// Additive only: extend records with optional members; never repurpose an enum value.

/// <summary>What a folder is, decided from its shape alone (names and counts; no IO).</summary>
public enum WorkClass
{
    /// <summary>Not classified: library root, tombstoned, or excluded by the caller.</summary>
    Excluded = 0,

    /// <summary>A leaf whose archives are units (chapters/volumes) of one work.</summary>
    Series = 1,

    /// <summary>Only unit subfolders (Volumes/, Chapters/, Season N/), optionally with loose archives.</summary>
    SeriesWithUnits = 2,

    /// <summary>Exactly one archive and no subfolder.</summary>
    OneShot = 3,

    /// <summary>A leaf of different works (anthology, one-shots): archive-level matching.</summary>
    CollectionLeaf = 4,

    /// <summary>A collection leaf named after the artist/circle its archives carry: archive-level matching.</summary>
    ArtistCollection = 5,

    /// <summary>At least two related non-unit subfolders (sequels, parts): its children are the candidates.</summary>
    FranchiseContainer = 6,

    /// <summary>At least two unrelated non-unit subfolders (category, author, magazine): children are candidates.</summary>
    CollectionContainer = 7,

    /// <summary>Exactly one non-unit subfolder and no archives: the child is the candidate.</summary>
    Wrapper = 8,

    /// <summary>One non-unit subfolder plus loose archives: review only, never auto.</summary>
    Mixed = 9,

    /// <summary>A unit subfolder (Volumes/, Chapters/, Part N) below a series: inherits, never a candidate.</summary>
    UnitSub = 10,

    /// <summary>Neither a clear series nor a clear collection: review only, never auto.</summary>
    Ambiguous = 11,
}

/// <summary>At which level a classified folder is matched.</summary>
public enum MatchLevel
{
    /// <summary>Not matched (containers, wrappers, unit subfolders, exclusions).</summary>
    None = 0,

    /// <summary>The folder itself is the work (Series, SeriesWithUnits, OneShot).</summary>
    Folder = 1,

    /// <summary>Each archive (or numbered archive group) is its own work.</summary>
    Archive = 2,

    /// <summary>A candidate that may be matched but can never be auto-linked (Mixed, Ambiguous).</summary>
    ReviewOnly = 3,
}

/// <summary>A suggestion for the folder-level Content setting (never applied automatically).</summary>
public enum ContentSuggestion
{
    None = 0,

    /// <summary>Archive names carry doujin anatomy ((event) [circle (artist)] title (parody)).</summary>
    DoujinshiAndAdultOneShots = 1,
}

/// <summary>A direct subfolder as the detector sees it.</summary>
public sealed record ChildFolderShape(string DisplayName, int DescendantArchiveCount);

/// <summary>
/// One folder, as the detector sees it: display names only (never paths), counts, and the
/// category hint (the nearest ancestor named like a category, e.g. "manga", "manhwa").
/// </summary>
public sealed record FolderShape(
    string DisplayName,
    int Depth,
    IReadOnlyList<string> ArchiveNames,
    IReadOnlyList<ChildFolderShape> Subfolders,
    string? ParentDisplayName = null,
    string? CategoryHint = null);

/// <summary>Archives of a collection folder that form one work (a numbered mini-series, or one archive).</summary>
public sealed record ArchiveGroup(string QueryTitle, IReadOnlyList<int> ArchiveIndexes);

/// <summary>The detector's verdict for one folder, with human-readable reasons for the review dashboard.</summary>
public sealed record WorkClassification(
    WorkClass Class,
    MatchLevel Level,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<ArchiveGroup> ArchiveGroups,
    ContentSuggestion ContentSuggestion = ContentSuggestion.None);

/// <summary>Classifies a folder from its shape. Pure and deterministic.</summary>
public interface IWorkDetector
{
    WorkClassification Classify(FolderShape folder);
}

/// <summary>Where a query variant came from (order = query priority).</summary>
public enum QueryVariantKind
{
    ComicInfoSeries = 0,
    Primary = 1,
    EnglishTitle = 2,
    SubtitleSplit = 3,
    SequelNumberSplit = 4,
    ArchiveDerivedTitle = 5,
    DoujinParodyForm = 6,
}

public sealed record QueryVariant(string Text, QueryVariantKind Kind);

/// <summary>Local signals used to corroborate candidates (never sent anywhere).</summary>
public sealed record MatchContext(
    WorkClass Class,
    int ArchiveCount,
    int VolumeLikeCount,
    int ChapterLikeCount,
    int? EarliestYear,
    string? CategoryHint,
    bool TallStrips,
    IReadOnlyList<string> AuthorTags,
    string? ComicInfoSeries = null);

/// <summary>
/// What to look up for one work: ordered, de-duplicated variants (the caller sends at most the
/// first few, stopping at a confident result) plus the corroboration context.
/// </summary>
public sealed record MatchQuery(IReadOnlyList<QueryVariant> Variants, MatchContext Context);

/// <summary>Builds the query for a folder-level work or an archive group. Pure.</summary>
public interface IMatchQueryPlanner
{
    MatchQuery PlanFolder(FolderShape folder, WorkClassification classification, string? comicInfoSeries = null);

    MatchQuery PlanArchiveGroup(FolderShape folder, WorkClassification classification, ArchiveGroup group);
}

public sealed record CandidateRelation(string ExternalId, string Relation);

/// <summary>A provider record as the scorer sees it (public provider data only).</summary>
public sealed record MatchCandidate(
    string Provider,
    string ExternalId,
    string Title,
    IReadOnlyList<string> AltTitles,
    MetadataFormat? Format,
    string? Origin,
    int? StartYear,
    int? Volumes,
    int? LatestChapter,
    IReadOnlyList<string> Authors,
    IReadOnlyList<CandidateRelation> Relations);

/// <summary>
/// Admin-adjustable thresholds (owner decision 13), validated against the bounds below.
/// The one-shot rule (raw title >= 0.95 and a one-shot / 1-volume record) is a code constant.
/// </summary>
public sealed record MatchThresholds(double AutoTitle, double Margin, double ReviewFloor)
{
    public static MatchThresholds Default { get; } = new(0.92, 0.10, 0.60);

    public const double AutoTitleMin = 0.85, AutoTitleMax = 0.99;
    public const double MarginMin = 0.05, MarginMax = 0.30;
    public const double ReviewFloorMin = 0.40, ReviewFloorMax = 0.90;

    public bool IsValid =>
        AutoTitle is >= AutoTitleMin and <= AutoTitleMax
        && Margin is >= MarginMin and <= MarginMax
        && ReviewFloor is >= ReviewFloorMin and <= ReviewFloorMax
        && ReviewFloor < AutoTitle;
}

public enum MatchBand
{
    Unmatched = 0,
    NeedsReview = 1,
    Auto = 2,
}

/// <summary>Why a candidate was demoted or flagged; shown as reason chips on the review dashboard.</summary>
[Flags]
public enum MatchReason
{
    None = 0,
    CloseSecond = 1 << 0,
    CountConflict = 1 << 1,
    YearConflict = 1 << 2,
    TypeConflict = 1 << 3,
    RelatedPair = 1 << 4,
    OneShotMismatch = 1 << 5,
    AuthorConflict = 1 << 6,
    NumberMismatch = 1 << 7,
    ReviewOnlyClass = 1 << 8,
}

public sealed record ScoredCandidate(MatchCandidate Candidate, double TitleScore, double AdjustedScore, MatchReason Reasons);

/// <summary>
/// The scorer's decision. <see cref="ToPersist"/> is the adaptive review set (owner decision 8):
/// candidates within 0.15 of the top, at most 5; only the top when it leads the next by >= 0.30.
/// </summary>
public sealed record MatchOutcome(MatchBand Band, IReadOnlyList<ScoredCandidate> Ranked, IReadOnlyList<ScoredCandidate> ToPersist);

/// <summary>Scores provider candidates for one query and bands the result. Pure and deterministic.</summary>
public interface IMatchScorer
{
    MatchOutcome Score(MatchQuery query, IReadOnlyList<MatchCandidate> candidates, MatchThresholds thresholds);
}
