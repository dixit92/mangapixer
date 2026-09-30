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

    /// <summary>
    /// One non-unit subfolder plus loose archives: loose archives that are works of their own are matched one by
    /// one (archive level, auto possible); loose units of one work keep the folder review-only.
    /// </summary>
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

    /// <summary>A candidate that may be matched but can never be auto-linked (Ambiguous; Mixed whose loose archives are units of one work).</summary>
    ReviewOnly = 3,
}

/// <summary>A suggestion for the folder-level Content setting (never applied automatically).</summary>
public enum ContentSuggestion
{
    None = 0,

    /// <summary>Archive names carry doujin anatomy ((event) [circle (artist)] title (parody)).</summary>
    DoujinshiAndAdultOneShots = 1,
}

/// <summary>
/// A direct subfolder as the detector sees it. <c>ArchiveNames</c> (optional, 1.29.0): the display names of the archives
/// below a UNIT subfolder (bounded), so the count rule reads their unit numbers (<see cref="CountEvidence.LocalOf"/>);
/// null for other subfolders.
/// </summary>
public sealed record ChildFolderShape(string DisplayName, int DescendantArchiveCount, IReadOnlyList<string>? ArchiveNames = null);

/// <summary>
/// One folder, as the detector sees it: display names only (never paths), counts, and the
/// category hint (the nearest ancestor named like a category, e.g. "manga", "manhwa").
/// <c>KnownAuthorNames</c> (optional): provider author names the caller already holds locally (the
/// server passes the creators of records linked in the library, 1.28.0); a leaf of two or more archives
/// named like one of them, whose shape is not one series, is an artist collection.
/// <c>Depth</c>: 0 = the library root, its direct children 1.
/// </summary>
public sealed record FolderShape(
    string DisplayName,
    int Depth,
    IReadOnlyList<string> ArchiveNames,
    IReadOnlyList<ChildFolderShape> Subfolders,
    string? ParentDisplayName = null,
    string? CategoryHint = null,
    IReadOnlyList<string>? KnownAuthorNames = null);

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

    /// <summary>
    /// The title part of a name that also carries a plain-separator author (<c>Title by Author</c>, <c>Title - Chapter |
    /// Author</c>, <c>Author - Title</c>; 1.27.0). Retrieval only: it scores at most the review-only cap unless a
    /// creator hint names one of the record's authors.
    /// </summary>
    CreatorSplit = 7,
}

public sealed record QueryVariant(string Text, QueryVariantKind Kind);

/// <summary>
/// Local signals used to corroborate candidates (never sent anywhere). <c>CreatorHints</c> (optional,
/// 1.26.1): names from trailing <c>[...]</c> / <c>(...)</c> groups of the folder or archive name that may be
/// an author; positive evidence only - unlike <c>AuthorTags</c> they never veto.
/// <c>LocalVolumes</c> / <c>LocalChapters</c> (optional, 1.27.0): what the count rule compares - the highest unit
/// number the archive names state (decimals and extras do not inflate it), or the archive count of unit
/// subfolders whose archive names are not read; null falls back to <c>VolumeLikeCount</c> / <c>ChapterLikeCount</c>.
/// <c>Units</c> (optional, 1.29.0): the count rule's local side (<see cref="CountEvidence.LocalOf"/>) - the unit numbers of the
/// folder and its unit subfolders; when null the rule reads the fields above (<see cref="CountEvidence.FromContext"/>).
/// <c>CoverMatches</c> (optional, 1.28.0): external ids of candidates whose cover image is the same as the work's
/// local cover (<see cref="CoverEvidence"/>); positive evidence only, set by the caller after comparing covers.
/// <c>DeclaredType</c> (optional, 1.30.0): the type an admin declared for the work's folder (<see cref="DeclaredHints"/>) - a
/// strong hint (<see cref="MatchScorer.DeclaredTypeAgree"/> / <see cref="MatchScorer.DeclaredTypeMismatch"/>), never a veto;
/// while set, the folder's <c>CategoryHint</c> is not read (the declaration wins).
/// </summary>
public sealed record MatchContext(
    WorkClass Class,
    int ArchiveCount,
    int VolumeLikeCount,
    int ChapterLikeCount,
    int? EarliestYear,
    string? CategoryHint,
    bool TallStrips,
    IReadOnlyList<string> AuthorTags,
    string? ComicInfoSeries = null,
    IReadOnlyList<string>? CreatorHints = null,
    int? LocalVolumes = null,
    int? LocalChapters = null,
    IReadOnlySet<string>? CoverMatches = null,
    LocalUnitCounts? Units = null,
    DeclaredType? DeclaredType = null);

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

/// <summary>
/// A provider record as the scorer sees it (public provider data only). <c>Origin</c> accepts a
/// provider type ("Manga", "Manhwa", "Manhua", "OEL") or a <see cref="MetadataOrigin"/> name.
/// <c>Webtoon</c> (optional, added by the matcher-core lane) is the record's webtoon flag.
/// <c>TotalChapters</c> (optional, added by the matcher-core lane): the provider's total chapter count when
/// it states one (MangaUpdates status "652 Chapters (Ongoing)"); <c>LatestChapter</c> restarts per season for
/// season-renumbered webtoons, so the count rule compares chapters with the larger of the two.
/// <c>EnglishVolumes</c> / <c>EnglishChapters</c> (optional, 1.27.0): the English publisher's totals (MangaUpdates
/// <c>publishers[].notes</c> such as "10 Volumes / 60 Chapters; Ongoing"); the count rule reads the largest
/// published number of any source.
/// </summary>
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
    IReadOnlyList<CandidateRelation> Relations,
    bool? Webtoon = null,
    int? TotalChapters = null,
    int? EnglishVolumes = null,
    int? EnglishChapters = null);

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
    /// <summary>Retired 2026-09-26 (owner: one archive can hold a whole series); never raised, kept so values stay stable.</summary>
    OneShotMismatch = 1 << 5,
    AuthorConflict = 1 << 6,
    NumberMismatch = 1 << 7,
    ReviewOnlyClass = 1 << 8,

    /// <summary>
    /// Positive evidence, not a flag against the candidate (1.28.0): its cover is the same as the work's local
    /// cover. No reason chip (it never demotes); kept so the stored reasons show why a tie was broken.
    /// </summary>
    CoverMatch = 1 << 9,

    /// <summary>
    /// Positive evidence (1.30.0): the record's type and origin are the ones the folder's declared type implies
    /// (<see cref="DeclaredFactsComparer.TypeSignal"/>). Shown as a chip; never demotes.
    /// </summary>
    DeclaredTypeAgree = 1 << 10,

    /// <summary>
    /// The record contradicts the folder's declared type (1.30.0). Lowers the adjusted score; NEVER a veto (owner: a declared
    /// type is a strong hint - users are often unsure between manga, manhwa and manhua).
    /// </summary>
    DeclaredTypeMismatch = 1 << 11,

    /// <summary>
    /// 1.30.0 (lane R, reach): after linking, the folder's reach contradicts the record - a volume or chapter file far past every
    /// total known for it (the Auto link dropped to Needs review), or chapter files whose stated volumes disagree with the record's
    /// volume list (flagged on the Auto-linked list only). Never raised by the scorer.
    /// </summary>
    ReachConflict = 1 << 12,

    /// <summary>
    /// 1.30.0 (owner: a spin-off decision is not automatic): only the folder name's subtitle decides between the top record and
    /// one of its SERIES FAMILY (<see cref="SeriesFamilies"/>) - the other matched the name's head and, without the subtitle cap
    /// (<see cref="MatchScorer.SubtitleHeadCap"/>), would have been a close second. A veto: the work goes to Needs review.
    /// </summary>
    SubtitleFamily = 1 << 13,

    /// <summary>
    /// 1.30.0: another candidate at the review floor is the same series family as the top (a main story, spin-off, side story,
    /// prequel or sequel - <see cref="SeriesFamilies"/>). Informational, never a veto: shown as a chip on review and Auto-linked rows.
    /// </summary>
    SeriesFamily = 1 << 14,
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
