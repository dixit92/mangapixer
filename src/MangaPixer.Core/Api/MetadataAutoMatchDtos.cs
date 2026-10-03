namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

// Series metadata stage 2 (auto-match) DTOs: the admin review dashboard, match
// runs, "Match this library now", user flags, folder Content and missing-folder
// re-attach. Admin-only except the two flag endpoints under /nodes. No DTO carries
// a filesystem path; display names and provider titles are fine (node names are
// already exposed to anyone who can see the node). Flag notes are user content:
// admin-only, never logged. Enums arrive as their C# names.
// Values are append-only: a stored int must keep its meaning.

/// <summary>A tab of the admin review dashboard.</summary>
public enum MetadataReviewTab
{
    /// <summary>Works the matcher could not decide (a <c>NeedsReview</c> link with stored candidates).</summary>
    NeedsReview = 0,

    /// <summary>Automatic links (<c>Auto</c> state), newest first; Confirm turns them into <c>Confirmed</c>.</summary>
    AutoLinked = 1,

    /// <summary>Candidates with no confident match (next retry date), and review-only works with no candidate.</summary>
    Unmatched = 2,

    /// <summary>Anchors with at least one open user flag (auto links first).</summary>
    Flags = 3,

    /// <summary>Nodes an admin marked "Don't match".</summary>
    DontMatch = 4,

    /// <summary>Admin-confirmed links.</summary>
    Confirmed = 5,

    /// <summary>Links / Don't match rows on removed folders that carry-over could not place.</summary>
    MissingFolders = 6,
}

/// <summary>
/// Folder-level Content setting (owner decision 4b): inherited by the subtree, the
/// nearest folder with a row wins. "Doujinshi &amp; adult one-shots" lifts the
/// Doujinshi exclusion of automatic searches below that folder.
/// </summary>
public enum MetadataFolderContent
{
    Auto = 0,
    DoujinshiAndAdultOneShots = 1,
    NotDoujinshi = 2,
}

/// <summary>What started a match run.</summary>
public enum MetadataMatchRunTrigger
{
    /// <summary>New folders found by a library scan (post-scan hook).</summary>
    Scan = 0,

    /// <summary>"Match this library now".</summary>
    Bulk = 1,

    /// <summary>Unmatched works whose retry date came.</summary>
    Retry = 2,

    /// <summary>"Re-run matching" on selected review rows.</summary>
    Rerun = 3,

    /// <summary>Works waiting in Needs review, checked once more after the matcher's rules changed (1.31.0).</summary>
    Recheck = 4,
}

public enum MetadataMatchRunStatus
{
    /// <summary>Queued work remains (possibly waiting for budget, backoff or the switch).</summary>
    Running = 0,
    Completed = 1,
    Cancelled = 2,
}

/// <summary>Bulk actions of the review dashboard.</summary>
public enum MetadataReviewBulkAction
{
    /// <summary>Links each row to its top stored candidate (Confirmed).</summary>
    AcceptTop = 0,

    /// <summary>Marks each row "Don't match".</summary>
    DontMatch = 1,

    /// <summary>Queues each row for matching again (spends budget).</summary>
    RerunMatching = 2,

    /// <summary>Turns automatic links into confirmed ones (no network).</summary>
    Confirm = 3,

    /// <summary>Removes each row's own link (inheritance resumes).</summary>
    Unlink = 4,

    /// <summary>1.33.0: sets each Needs review row aside ("Later": listed after the others until it is decided or checked again).</summary>
    Later = 5,

    /// <summary>1.33.0: brings each row set aside back into the normal order.</summary>
    ClearLater = 6,
}

/// <summary>Why a user flagged a series.</summary>
public enum MetadataFlagReason
{
    WrongSeries = 0,
    WrongDetails = 1,
    NotOneSeries = 2,
    Other = 3,
}

/// <summary>A flag's state; everything but <see cref="Open"/> is a resolution.</summary>
public enum MetadataFlagState
{
    Open = 0,
    Relinked = 1,
    Unlinked = 2,
    DontMatch = 3,
    Dismissed = 4,
}

// --- Settings additions (on MetadataSettingsDto / UpdateMetadataSettingsRequest) ---

/// <summary>The three admin-adjustable match thresholds (owner decision 13).</summary>
public sealed record MetadataMatchThresholdsDto
{
    /// <summary>Auto-link title score, 0.85-0.99 (default 0.92).</summary>
    public required double AutoTitle { get; init; }

    /// <summary>Lead over the runner-up, 0.05-0.30 (default 0.10).</summary>
    public required double Margin { get; init; }

    /// <summary>Review floor, 0.40-0.90 (default 0.60); must be below <see cref="AutoTitle"/>.</summary>
    public required double ReviewFloor { get; init; }
}

/// <summary>The inclusive bounds the server validates thresholds against.</summary>
public sealed record MetadataMatchThresholdBoundsDto
{
    public required double AutoTitleMin { get; init; }
    public required double AutoTitleMax { get; init; }
    public required double MarginMin { get; init; }
    public required double MarginMax { get; init; }
    public required double ReviewFloorMin { get; init; }
    public required double ReviewFloorMax { get; init; }
}

// --- Review dashboard ---

/// <summary>Counts per review tab (the admin summary tile and nav badge). Optional library filter.</summary>
public sealed record MetadataReviewSummaryDto
{
    public required int NeedsReview { get; init; }

    /// <summary>1.33.0: the Needs review rows set aside ("Later"); included in <see cref="NeedsReview"/>.</summary>
    public int Later { get; init; }
    public required int AutoLinked { get; init; }
    public required int Unmatched { get; init; }
    public required int OpenFlags { get; init; }
    public required int DontMatch { get; init; }
    public required int Confirmed { get; init; }
    public required int MissingFolders { get; init; }

    /// <summary>Queue rows still waiting to be matched.</summary>
    public required int Pending { get; init; }

    /// <summary>
    /// 1.31.0: works in review that are being checked again under the matcher's current rules (queued or being scored). Included in
    /// <see cref="Pending"/>; the dashboard says so while it is above zero.
    /// </summary>
    public int RecheckPending { get; init; }
}

/// <summary>The link a review row currently has (own row only).</summary>
public sealed record MetadataReviewLinkDto
{
    public required SeriesLinkState State { get; init; }
    public string? Provider { get; init; }
    public string? ExternalId { get; init; }
    public string? RecordId { get; init; }

    /// <summary>The linked record's title (public provider data).</summary>
    public string? Title { get; init; }
    public MetadataMatchMethod? MatchMethod { get; init; }
    public double? MatchScore { get; init; }

    /// <summary>Stored poster of the linked record, served by MangaPixer (never a provider URL).</summary>
    public string? ImageUrl { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One stored candidate of a review row (public provider data; no search is made to show it).</summary>
public sealed record MetadataReviewCandidateDto
{
    /// <summary>1-based rank (the value <c>accept</c> takes).</summary>
    public required int Rank { get; init; }
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public string? ProviderType { get; init; }
    public MetadataFormat? Format { get; init; }
    public MetadataOrigin? Origin { get; init; }
    public int? Year { get; init; }
    public int? Volumes { get; init; }

    /// <summary>Raw title similarity, 0-1.</summary>
    public required double TitleScore { get; init; }

    /// <summary>After corroboration (type, count, year, author ...), 0-1.</summary>
    public required double AdjustedScore { get; init; }

    /// <summary>
    /// Reason chips: <c>close_second</c>, <c>count</c>, <c>year</c>, <c>type</c>, <c>related_pair</c>, <c>one_shot</c>, <c>author</c>,
    /// <c>number</c>, <c>review_only</c>; the declared-type evidence (1.30.0) <c>declared_type</c> (fits) and <c>not_declared_type</c>;
    /// <c>reach</c>; the series family (1.30.0) <c>subtitle_family</c> (only the folder's subtitle separates the top from a record of
    /// its family) and <c>series_family</c> (another candidate is the same series family); <c>cover_differs</c> (1.31.0: after an
    /// automatic link, the folder's volume covers are clearly different pictures from the record's stored volume covers).
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>
    /// Short-lived token for <c>GET /admin/metadata/candidates/{token}/image</c>; the
    /// poster is fetched (one request, gated) only when the client loads it.
    /// </summary>
    public string? ImageToken { get; init; }

    /// <summary>
    /// 1.30.0: candidates of this row that are one series family (a main story with its spin-offs, side stories, prequels or
    /// sequels - MangaUpdates' related series, else the same title head and author) share this value: the <see cref="Rank"/> of
    /// the family's first candidate. Null when no other candidate of the row is its family.
    /// </summary>
    public int? FamilyGroup { get; init; }

    /// <summary>
    /// 1.30.0: the candidate's role in its family: <c>main_story</c>, <c>spin_off</c>, <c>side_story</c>, <c>prequel</c>, <c>sequel</c>,
    /// <c>alternate</c> (version), <c>alternate_story</c>, <c>adaptation</c>, <c>source</c> or <c>related</c> (same family, relation unknown). Null without a family.
    /// </summary>
    public string? FamilyRole { get; init; }
}

/// <summary>One row of a review tab: a work (a folder, an archive, or a numbered archive group).</summary>
public sealed record MetadataReviewItemDto
{
    /// <summary>The work's anchor node (the folder, or the first archive of a group).</summary>
    public required string NodeId { get; init; }
    public required CatalogNodeKind NodeKind { get; init; }
    public required string DisplayName { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }

    /// <summary>Display names of up to 3 ancestors below the library root, outermost first.</summary>
    public IReadOnlyList<string> Trail { get; init; } = [];

    /// <summary>True when the node was removed by a scan (Missing folders tab).</summary>
    public bool Missing { get; init; }

    /// <summary>
    /// The folder that contains the node, for the row's "open folder" link (an archive opens in context); null at the
    /// library's top level.
    /// </summary>
    public string? ParentNodeId { get; init; }

    /// <summary>
    /// The local cover (1.26.x): an archive's own, or a folder's first archive - the thumbnail browse
    /// shows, so it can be compared with the candidates' covers. Null when there is none.
    /// </summary>
    public string? CoverUrl { get; init; }

    /// <summary>The detector's class, when the work went through matching.</summary>
    public WorkClass? WorkClass { get; init; }
    public MatchLevel? MatchLevel { get; init; }

    /// <summary>Archives in the work.</summary>
    public required int ItemCount { get; init; }

    /// <summary>For an archive group: the other archives of the group (the anchor excluded).</summary>
    public IReadOnlyList<string> MemberNodeIds { get; init; } = [];

    /// <summary>The node's own link row, if any.</summary>
    public MetadataReviewLinkDto? Link { get; init; }

    /// <summary>Stored candidates, best first (Needs review; Unmatched rows may carry weak ones).</summary>
    public IReadOnlyList<MetadataReviewCandidateDto> Candidates { get; init; } = [];

    /// <summary>Reason chips of the outcome (same codes as on candidates).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>When the matcher last decided this work.</summary>
    public DateTimeOffset? MatchedAt { get; init; }

    /// <summary>Unmatched: when it is retried automatically (null = never again).</summary>
    public DateTimeOffset? NextRetryAt { get; init; }

    /// <summary>The run that decided this work.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// 1.31.0: the work is queued to be scored again under the matcher's current rules (a background re-check after an update
    /// changed how matches are scored). The reasons and candidates shown are the earlier result until it is done.
    /// </summary>
    public bool CheckingAgain { get; init; }

    /// <summary>
    /// 1.33.0 (Needs review): when an admin set the row aside ("Later"); such rows are listed after the others, the same for every
    /// admin, until the work is decided or checked again. Null when it is not set aside.
    /// </summary>
    public DateTimeOffset? LaterAt { get; init; }

    /// <summary>
    /// 1.33.0 (Needs review): other waiting works by the same circle or artist, read from the works' own names (the leading
    /// <c>[Circle (Artist)]</c> tag, balanced or not), else an artist folder or ComicInfo writer / penciller. Null when none.
    /// </summary>
    public MetadataReviewGroupHintDto? SameAuthor { get; init; }

    /// <summary>1.33.0 (Needs review): other waiting works in the same folder (<c>Key</c> = the folder's node id). Null when none.</summary>
    public MetadataReviewGroupHintDto? SameFolder { get; init; }

    /// <summary>
    /// 1.31.0 (folder works): how many chapter numbers more than one file of the same folder below it states ("2 duplicate chapters").
    /// Split chapters and ranges are not duplicates.
    /// </summary>
    public int DuplicateChapters { get; init; }

    /// <summary>1.31.0 (folder works): the same for volume numbers.</summary>
    public int DuplicateVolumes { get; init; }

    public required int OpenFlagCount { get; init; }

    /// <summary>Flags tab only: the open flags on this anchor.</summary>
    public IReadOnlyList<MetadataFlagDto> Flags { get; init; } = [];
}

/// <summary>
/// 1.33.0: a group of works waiting in Needs review that a row belongs to - by author or by folder. <c>Key</c> is the value of the
/// list's <c>author</c> / <c>folder</c> filter; <c>Label</c> is the author's or the folder's name (display data, never a path).
/// </summary>
public sealed record MetadataReviewGroupHintDto
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>The other waiting works of the group (at least 1).</summary>
    public required int Others { get; init; }
}

/// <summary>1.33.0: an author with at least two works waiting in Needs review (the Authors list, largest first).</summary>
public sealed record MetadataReviewAuthorDto
{
    /// <summary>The list's <c>author</c> filter value.</summary>
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required int Count { get; init; }

    /// <summary>Of <see cref="Count"/>, the works set aside ("Later").</summary>
    public int Later { get; init; }
}

public sealed record MetadataReviewAuthorsDto
{
    public required IReadOnlyList<MetadataReviewAuthorDto> Items { get; init; }
}

public sealed record MetadataReviewPageDto
{
    public required MetadataReviewTab Tab { get; init; }
    public required IReadOnlyList<MetadataReviewItemDto> Items { get; init; }

    /// <summary>Total rows of this tab (with the library filter).</summary>
    public required int Total { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>Accepts one stored candidate of a review row (by rank) as a confirmed link.</summary>
public sealed record MetadataReviewAcceptRequest
{
    /// <summary>1-based rank of the stored candidate.</summary>
    public required int Rank { get; init; }
}

public sealed record MetadataReviewBulkRequest
{
    public required MetadataReviewBulkAction Action { get; init; }

    /// <summary>Anchor node ids (max 200).</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }
}

public sealed record MetadataReviewBulkItemResultDto
{
    public required string NodeId { get; init; }

    /// <summary><c>ok</c>, or an error code (<c>not_found</c>, <c>no_candidate</c>, <c>budget_exhausted</c>, ...).</summary>
    public required string Code { get; init; }
}

public sealed record MetadataReviewBulkResultDto
{
    public required MetadataReviewBulkAction Action { get; init; }
    public required int Succeeded { get; init; }
    public required int Failed { get; init; }
    public required IReadOnlyList<MetadataReviewBulkItemResultDto> Results { get; init; }
}

// --- Runs and "Match this library now" ---

/// <summary>Why automatic matching is not sending requests right now, if it is not.</summary>
public sealed record MetadataAutoMatchStatusDto
{
    /// <summary>The global Automatic matching switch with the current automatic consent.</summary>
    public required bool Enabled { get; init; }

    /// <summary>True when the worker is sending requests (queued work, nothing blocking).</summary>
    public required bool Active { get; init; }

    /// <summary>
    /// <c>automatic_off</c>, <c>metadata_disabled</c>, <c>metadata_network_disabled</c>,
    /// <c>budget_exhausted</c>, <c>provider_backoff</c>, or null.
    /// </summary>
    public string? WaitingCode { get; init; }

    /// <summary>When the wait ends by itself (next UTC day, backoff end), if known.</summary>
    public DateTimeOffset? WaitingUntil { get; init; }

    /// <summary>Queue rows waiting to be matched (all libraries).</summary>
    public required int Pending { get; init; }
}

public sealed record MetadataMatchRunDto
{
    public required string RunId { get; init; }
    public required string LibraryId { get; init; }
    public required string LibraryName { get; init; }
    public required MetadataMatchRunTrigger Trigger { get; init; }
    public required MetadataMatchRunStatus Status { get; init; }

    /// <summary>"Review everything once": automatic links of this run went to Needs review instead.</summary>
    public required bool ReviewFirst { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public required int Candidates { get; init; }
    public required int Queued { get; init; }
    public required int Processed { get; init; }
    public required int AutoLinked { get; init; }
    public required int NeedsReview { get; init; }
    public required int Unmatched { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required int RequestsUsed { get; init; }

    // Local-only outcome counters (never sent anywhere): how admins judged this run's results.

    /// <summary>Automatic links of this run later unlinked, re-identified or set to Don't match by an admin.</summary>
    public required int AutoChangedByAdmin { get; init; }
    public required int ReviewAcceptedTop { get; init; }
    public required int ReviewAcceptedOther { get; init; }
    public required int ReviewDontMatch { get; init; }
}

public sealed record MetadataMatchRunsDto
{
    public required MetadataAutoMatchStatusDto Status { get; init; }
    public required IReadOnlyList<MetadataMatchRunDto> Items { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>What "Match this library now" would do (no network).</summary>
public sealed record MetadataMatchEstimateDto
{
    public required string LibraryId { get; init; }

    /// <summary>Works that would be queued (never-matched candidates; with retry, unmatched ones too).</summary>
    public required int Candidates { get; init; }

    /// <summary>Estimated provider requests (about 2-3 per work).</summary>
    public required int EstimatedRequests { get; init; }

    /// <summary>Days at the current daily budget (the one global budget; manual Identify shares it).</summary>
    public required double EstimatedDays { get; init; }

    /// <summary>Works that already have a link or Don't match (not queued).</summary>
    public required int AlreadyLinked { get; init; }

    /// <summary>Unmatched works that "retry unmatched" would add.</summary>
    public required int Unmatched { get; init; }
    public required int DailyBudget { get; init; }
    public required int BudgetUsedToday { get; init; }

    /// <summary>True when this library never had a bulk run (offer "review everything once").</summary>
    public required bool FirstRun { get; init; }

    /// <summary>True when the automatic gate is open for this library (global switch + consent + library Fetch).</summary>
    public required bool AutomaticAvailable { get; init; }
    public string? UnavailableCode { get; init; }
}

public sealed record MetadataMatchLibraryRequest
{
    /// <summary>Send this run's would-be automatic links to Needs review instead (decision 1).</summary>
    public bool ReviewFirst { get; init; }

    /// <summary>Also queue unmatched works again, before their retry date.</summary>
    public bool RetryUnmatched { get; init; }
}

// --- Flags ---

/// <summary>A user's "Wrong series?" report. The note is plain text, max 500 characters.</summary>
public sealed record CreateMetadataFlagRequest
{
    public required MetadataFlagReason Reason { get; init; }
    public string? Note { get; init; }

    public override string ToString() => $"CreateMetadataFlagRequest {{ Reason = {Reason}, Note = [redacted] }}";
}

/// <summary>The reporter's own view of a flag (never the note of anyone else, never the resolver).</summary>
public sealed record MetadataMyFlagDto
{
    public required string FlagId { get; init; }

    /// <summary>The series anchor the flag is on (the node holding the link).</summary>
    public required string AnchorNodeId { get; init; }
    public required MetadataFlagReason Reason { get; init; }
    public required MetadataFlagState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
}

/// <summary><c>GET /nodes/{id}/series-info/flags/mine</c>: the caller's latest flag on the node's anchor.</summary>
public sealed record MetadataMyFlagStateDto
{
    /// <summary>True when the node shows web data the caller may flag and they have no open flag on it.</summary>
    public required bool CanFlag { get; init; }
    public MetadataMyFlagDto? Flag { get; init; }
}

/// <summary>Admin view of a flag.</summary>
public sealed record MetadataFlagDto
{
    public required string FlagId { get; init; }
    public required string NodeId { get; init; }
    public required CatalogNodeKind NodeKind { get; init; }
    public required string NodeDisplayName { get; init; }
    public required string LibraryId { get; init; }
    public required MetadataFlagReason Reason { get; init; }

    /// <summary>The reporter's note (user content; admin-only, never logged).</summary>
    public string? Note { get; init; }
    public required MetadataFlagState State { get; init; }
    public required string ReporterDisplayName { get; init; }
    public string? ResolvedByDisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>The record the reporter saw (snapshot at flag time).</summary>
    public string? Provider { get; init; }
    public string? ExternalId { get; init; }

    /// <summary>The anchor's current own link.</summary>
    public MetadataReviewLinkDto? CurrentLink { get; init; }
}

public sealed record MetadataFlagPageDto
{
    public required IReadOnlyList<MetadataFlagDto> Items { get; init; }
    public required int Total { get; init; }
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// Resolves a flag. <c>Unlinked</c> and <c>DontMatch</c> change the anchor's link;
/// <c>Relinked</c> records that the admin re-identified it (Identify does the
/// linking); <c>Dismissed</c> keeps the link.
/// </summary>
public sealed record ResolveMetadataFlagRequest
{
    public required MetadataFlagState Outcome { get; init; }
}

// --- Missing folders ---

/// <summary>Moves a removed folder's metadata rows (link / Don't match, precedence, reader default, Content) onto a live folder.</summary>
public sealed record MetadataReattachRequest
{
    public required string TargetNodeId { get; init; }
}

public sealed record MetadataReattachResultDto
{
    public required string NodeId { get; init; }
    public required string TargetNodeId { get; init; }
    public required bool Link { get; init; }
    public required bool Precedence { get; init; }
    public required bool ReaderDefault { get; init; }
    public required bool Content { get; init; }

    /// <summary>The folder's declared facts moved too (1.28.0; only when the target declared nothing of its own).</summary>
    public bool Declared { get; init; }
}

// --- Folder Content ---

public sealed record SetFolderMetadataContentRequest
{
    public required MetadataFolderContent Content { get; init; }
}

/// <summary>A folder's Content setting: its own row, the effective (inherited) value and a detector suggestion.</summary>
public sealed record FolderMetadataContentDto
{
    public required string NodeId { get; init; }

    /// <summary>The folder's own override, or null when it inherits.</summary>
    public MetadataFolderContent? Content { get; init; }
    public required MetadataFolderContent Effective { get; init; }

    /// <summary>The folder the effective value comes from (null = none set anywhere: Auto).</summary>
    public string? SourceNodeId { get; init; }

    /// <summary>A suggestion from archive-name signals (never applied automatically).</summary>
    public MetadataFolderContent? Suggested { get; init; }

    /// <summary>Set on PUT / DELETE when the change allows or excludes doujinshi below the folder: what it re-queued.</summary>
    public MetadataContentRematchDto? Rematch { get; init; }
}

/// <summary>
/// Matching again after a Content change (owner, 2026-09-26): the Needs-review and Unmatched works
/// below the folder (the folder itself included, subfolders with their own Content excluded) were
/// decided with the other doujinshi rule. Linked and Don't-match works are never touched.
/// </summary>
public sealed record MetadataContentRematchDto
{
    /// <summary>Works below the folder decided with the other rule (Needs review, Unmatched).</summary>
    public required int Affected { get; init; }

    /// <summary>Works queued to match again.</summary>
    public required int Queued { get; init; }

    /// <summary>More than the limit: nothing queued; <c>POST folders/{id}/content/rematch</c> queues them.</summary>
    public bool NeedsConfirmation { get; init; }

    /// <summary>Automatic matching is off: nothing queued (Review › Re-run matching works once it is on).</summary>
    public bool AutomaticOff { get; init; }
}
