/**
 * API DTO types matching the .NET contracts in MangaPixer.Core/Api/ApiDtos.cs.
 * These are schema-derived HttpClient types, not auto-generated.
 * No source paths or private fields are present in these types.
 */

export type SortDirection = 'Ascending' | 'Descending';

export interface PageCursor {
  cursor: string | null;
  pageSize: number;
  direction: SortDirection;
}

export interface PageResponse<T> {
  items: T[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
  /**
   * Backward (upward) keyset cursor (1.11.0): pass as the browse `before` param to
   * fetch the page immediately BEFORE this window's first item. Null when this window
   * starts at the listing's true first item, or for sorts without backward paging (only
   * the name sort supports it). Optional on the client so older fixtures keep compiling.
   */
  prevCursor?: string | null;
  /**
   * Whether a page exists BEFORE this window's first item (1.11.0). Pairs with
   * `prevCursor`; true means the client may scroll up and prepend the previous page.
   * Optional/defaulted false so older fixtures keep compiling.
   */
  hasPrevious?: boolean;
  /**
   * The folder's next-to-read descendant archive (1.7.0), surfaced as a pinned
   * "Continue" row above the sorted list. Browse only; null when the browsed
   * folder has no unread descendant archive. Optional on the client so existing
   * PageResponse fixtures keep compiling (the server always sends it); rendered
   * by `ContinueRowComponent`, which hides itself when absent/null.
   */
  nextUnread?: CatalogNodeDto | null;
}

export type CatalogNodeKind = 'Folder' | 'Archive';

export type CatalogNodeAvailability =
  | 'Available'
  | 'Preparing'
  | 'Unsupported'
  | 'Corrupt'
  | 'Unavailable'
  | 'Tombstoned';

export interface CatalogNodeDto {
  id: string;
  parentId: string;
  libraryId: string;
  kind: CatalogNodeKind;
  displayName: string;
  availability: CatalogNodeAvailability;
  coverUrl: string | null;
  childFolderCount: number | null;
  childArchiveCount: number | null;
  pageCount: number | null;
  readingState: ReadingState | null;
  lastReadPage: number | null;
  /** This folder's own global reader-mode override (1.2.0), or null if none. */
  readerDefault: ReaderMode | null;
  /** Whether the current user marked this item read (1.2.0 sticky flag). Archives only. */
  isRead: boolean;
  /**
   * Whether the current user has starred this node as a favorite (1.21.0). Applies to
   * folders and archives alike. Populated by browse, search, node lookup, and the
   * favorites list; optional so responses predating the field read as not-favorited.
   */
  isFavorite?: boolean;
  /**
   * Derived read rollup over a folder's readable descendant archives (1.6.0).
   * Browse only, folders only; null for archives, empty folders, and search results.
   */
  readRollup: FolderReadRollup | null;
}

export interface BreadcrumbEntry {
  id: string;
  displayName: string;
}

export interface BreadcrumbsDto {
  nodeId: string;
  trail: BreadcrumbEntry[];
}

/** One bucket of the per-library jump index. */
export interface JumpIndexBucketDto {
  label: string;
  count: number;
  firstCursor: string | null;
}

/** Per-library A–Z/script rail. */
export interface JumpIndexDto {
  libraryId: string;
  buckets: JumpIndexBucketDto[];
}

export interface SearchResultsDto {
  query: string;
  items: CatalogNodeDto[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export type ReadingState = 'Unread' | 'InProgress' | 'Completed';

/**
 * Derived, display-only read state of a folder rolled up over its descendant
 * archives (1.6.0). Rendered by `FolderRollupBadgeComponent`.
 */
export type FolderReadRollup = 'Unread' | 'Reading' | 'Read';

export interface ReadingProgressDto {
  itemId: string;
  pageIndex: number;
  contentVersion: number;
  updatedAt: string;
  state: ReadingState;
  /** Server revision for optimistic concurrency; sent back as If-Match on PUT. */
  revision: number;
  isStale: boolean;
  /**
   * The page the reader should OPEN at (1.9.0), computed server-side and
   * non-destructively from the read-mark, saved position vs page count, and the
   * user's `alwaysOpenReadFromStart` preference. Unread/Reading items resume
   * (equals `pageIndex`); a read item finished on the last page, or opted into
   * start-from-first, opens at 0. Optional for older servers/fixtures — fall back
   * to `pageIndex` when absent.
   */
  openPageIndex?: number;
}

export interface UpdateProgressRequest {
  pageIndex: number;
  expectedContentVersion: number;
  /** Client-generated unique id for idempotent updates (D32). */
  mutationId: string;
  /** Manifest entry key of the current page. */
  entryKey?: string;
  /** Normalized 0–1 scroll anchor (webtoon mode). */
  normalizedAnchor?: number;
}

export interface ProgressUpdateResult {
  revision: number;
  alreadyApplied: boolean;
}

export type ReaderMode = 'PagedLtr' | 'PagedRtl' | 'DoubleSpread' | 'VerticalWebtoon';

export interface UserPreferencesDto {
  defaultReaderMode: ReaderMode;
  preferDoubleSpread: boolean;
  reducedMotion: boolean;
  preferredBackground: string | null;
  /**
   * When true, archives the user has marked read reopen from the first page (1.9.0);
   * when false (default) read titles resume where they left off, except ones finished
   * on the last page which always start from page 1. Optional for older servers.
   */
  alwaysOpenReadFromStart?: boolean;
}

export interface ContinueReadingEntry {
  itemId: string;
  /** Opaque public ID of the item's library. Enables sidebar grouping. */
  libraryId: string;
  /** Display name of the item's library. */
  libraryName: string;
  displayName: string;
  pageIndex: number;
  contentVersion: number;
  updatedAt: string;
}

/**
 * The current user's Private library designations (1.4.0). Libraries in this
 * list are hidden from listing/discovery surfaces (continue-reading, search,
 * browse-root, library list) while Incognito mode is active. Direct reader
 * URLs remain accessible regardless. Library IDs are opaque public IDs.
 */
export interface PrivateLibrariesDto {
  libraryIds: string[];
}

/** Request to replace the current user's Private library set (replacement semantics). */
export interface SetPrivateLibrariesRequest {
  libraryIds: string[];
}

export interface LibraryDto {
  id: string;
  name: string;
  isScanning: boolean;
  itemCount: number | null;
  lastScanCompleted: string | null;
  /** Global default reader mode for the library (1.2.0), or null to inherit. */
  defaultReaderMode: ReaderMode | null;
  /** Admin-picked icon name (1.22.0), or null for the client-derived default. */
  icon: string | null;
}

/** Request to set a library's icon (1.22.0). Null clears back to the default. */
export interface SetLibraryIconRequest {
  icon: string | null;
}

/** Resolved effective default reader mode for an item (1.2.0). */
export interface EffectiveReaderModeDto {
  readerMode: ReaderMode;
}

/** Current user's sticky read-mark state for a single item (1.2.0). */
export interface ReadMarkDto {
  itemId: string;
  isRead: boolean;
}

/** Response when durable thumbnail regeneration is enqueued for a library (1.2.0). */
export interface ThumbnailRegenerateResponse {
  queuedCount: number;
}

/**
 * Known library view modes. As of 1.6.0 the former 'grid' and 'poster' modes are
 * merged into a single 'card' view whose size is a continuous slider (see cardSize);
 * 'list' stays a separate mode. Tolerant: legacy/unknown persisted values ('grid',
 * 'poster') are read as 'card'.
 */
export type LibraryViewMode = 'card' | 'list';
/**
 * Legacy grid density (1.2.0), subsumed by the Card size slider (1.6.0). Retained
 * only so the stored preference round-trips unchanged for other/older frontends.
 */
export type LibraryGridDensity = 'comfortable' | 'compact';
export type LibrarySortOrder = 'name' | 'recentlyAdded' | 'recentlyRead' | 'recentlyUpdated';

/**
 * Browse sort direction (1.5.0). Optional/tolerant like the other library-view
 * preference fields: absent or unrecognized means "use the sort-specific
 * default" (name -> asc; recentlyAdded/recentlyRead -> desc), resolved by the
 * server (`CatalogController.ParseDirection`) and mirrored client-side in
 * LibraryBrowseComponent so the UI shows the right toggle state before the
 * first preferences round-trip completes.
 */
export type LibrarySortDirection = 'asc' | 'desc';

/**
 * Browse read-state filter (1.10.0). Restricts the listed archives to the current
 * user's per-item read state; 'all' disables the filter. Semantics match the archive
 * cards / folder rollup: 'read' = a sticky read-mark; 'reading' = an in-progress
 * archive with no mark; 'unread' = neither. Sent as the `readState` browse query
 * param. Not a persisted preference — a transient view control on the toolbar.
 */
export type LibraryReadStateFilter = 'all' | 'reading' | 'read' | 'unread';

/** Per-user library browse presentation preferences (1.2.0). Strings for tolerance. */
export interface LibraryViewPreferencesDto {
  viewMode: string;
  density: string;
  sort: string;
  /**
   * Optional (1.8.0): the per-user initial/per-page item count for the browse
   * view's infinite scroll. 0/omitted/unrecognized → the frontend default (50).
   */
  libraryPageSize?: number;
  /** Optional (1.5.0): omitted/unrecognized falls back to the sort-specific default. */
  direction?: string;
  /**
   * Optional (1.6.0): stringified min card column width in px for the merged Card
   * view (e.g. "150"). Omitted/unrecognized → the frontend derives an initial size
   * from the legacy viewMode + density, so pre-1.6.0 stored prefs keep their size.
   */
  cardSize?: string;
  /**
   * Optional (1.12.0 refinement): the per-user "recently added" window, in days,
   * for the home "New chapters" row. 0/omitted → the server default (30). The
   * server clamps stored values to 1-365.
   */
  homeRecentWindowDays?: number;
  /**
   * Optional (1.18.0): the per-user list-view column count (1-3) on wide
   * viewports. 0/omitted/unrecognized → the frontend default (2). Stored
   * verbatim, never interpreted server-side, like cardSize.
   */
  listColumns?: number;
  /**
   * Optional (1.21.0): per-user opt-in for the Home "Favorites" row. false/omitted →
   * the row is hidden (default). The star affordances everywhere else are always on.
   */
  showFavoritesHomeRow?: boolean;
  /**
   * Optional (1.21.0): per-user opt-in for favorites prominence in search. false/omitted
   * → favorited results render normally. true → they get a star badge and are boosted to
   * the top of the result list.
   */
  favoritesSearchProminence?: boolean;
}

// --- YACReader progress import (1.2.0, admin-only) ---

/** Whether a YACReader library was detected inside a MangaPixer library's root. */
export interface YacReaderDetectDto {
  detected: boolean;
  dbVersion: string | null;
}

/** Request to preview or apply a YACReader progress import. Path is server-detected. */
export interface YacReaderImportRequest {
  libraryId: string;
  targetUserId: string;
  overwrite?: boolean;
}

export interface YacReaderImportItemDto {
  itemId: string | null;
  displayName: string | null;
  read: boolean;
  hasBeenOpened: boolean;
  currentPage: number;
  state: string;
  conflict: boolean;
}

export interface YacReaderImportPreviewDto {
  libraryId: string;
  targetUserId: string;
  dbVersion: string | null;
  totalComics: number;
  mapped: number;
  unmapped: number;
  conflicts: number;
  toImport: number;
  items: YacReaderImportItemDto[];
}

export interface YacReaderImportResultDto {
  libraryId: string;
  targetUserId: string;
  dbVersion: string | null;
  totalComics: number;
  mapped: number;
  unmapped: number;
  imported: number;
  skipped: number;
  readMarks: number;
}

/** Result of a bulk folder read-mark operation over descendant archives (1.2.0). */
export interface BulkReadMarkResultDto {
  affected: number;
  total: number;
}

export interface ApiError {
  error: string;
  message: string;
  detail: string | null;
  correlationId: string | null;
}

export interface AuthUserDto {
  id: string;
  username: string;
  role: string;
  isAdmin: boolean;
  /** When true, the account must change its password before using the app. */
  forcePasswordChange?: boolean;
}

export interface CsrfTokenDto {
  token: string;
}

export interface SetupStatusDto {
  setupRequired: boolean;
}

export interface SetupRequest {
  username: string;
  password: string;
}

export interface LoginRequest {
  username: string;
  password: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface DirectoryEntryDto {
  name: string;
  path: string;
  hasChildren: boolean;
}

export interface DirectoryListingDto {
  available: boolean;
  root: string | null;
  current: string | null;
  parent: string | null;
  entries: DirectoryEntryDto[];
}

// --- Admin DTOs ---

export interface RegisterLibraryRequest {
  displayName: string;
  rootPath: string;
}

export interface UpdateLibraryRequest {
  displayName: string;
}

export interface ScanTriggeredDto {
  scanRunId: string;
}

/**
 * Response when a scan is triggered for every registered library at once (1.8.0).
 * Libraries already scanning are skipped rather than failing the batch, so
 * startedCount + skippedCount equals the total number of registered libraries.
 * scanRunIds holds the opaque ids of the scans that were actually started.
 */
export interface ScanAllResultDto {
  startedCount: number;
  skippedCount: number;
  scanRunIds: string[];
}

export interface ScanRunDto {
  id: string;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  nodesObserved: number | null;
  nodesAdded: number | null;
  nodesTombstoned: number | null;
  error: string | null;
}

export interface AdminUserDto {
  id: string;
  username: string;
  isAdmin: boolean;
  isActive: boolean;
  isPendingActivation?: boolean;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface CreateUserRequest {
  username: string;
  password?: string;
  isAdmin: boolean;
}

export interface CreateUserResponse {
  user: AdminUserDto;
  activationUrl: string | null;
}

export interface ActivateAccountRequest {
  token: string;
  password: string;
}

export interface UpdateUserRequest {
  isActive?: boolean | null;
  isAdmin?: boolean | null;
}

export interface ResetPasswordResponse {
  temporaryPassword: string;
}

export interface ReissueActivationResponse {
  user: AdminUserDto;
  activationUrl: string;
}

export interface UserGrantsDto {
  userId: string;
  /** Admins access every library regardless of explicit grants. */
  isAdmin: boolean;
  /** Public ids of libraries this user is explicitly granted. */
  libraryIds: string[];
}

// --- Manifest DTOs ---

export interface ManifestPageEntry {
  entryKey: string;
  pageIndex: number;
  mediaType: string;
  width: number;
  height: number;
  animationState: string;
  byteSize: number;
}

export interface ItemManifest {
  itemId: string;
  contentVersion: number;
  manifestVersion: number;
  archiveFormat: string;
  pageCount: number;
  pages: ManifestPageEntry[];
  isSolid: boolean;
  hasAnimatedPages: boolean;
}

export type ItemReadinessState =
  | 'Ready' | 'Pending' | 'Failed' | 'Unsupported' | 'Encrypted' | 'Missing';

export interface ItemReadiness {
  itemId: string;
  state: ItemReadinessState;
  contentVersion: number;
  error: string | null;
  lastAttempt: string | null;
  isAnalyzing: boolean;
}

// --- Operations DTOs ---

export type LogLevel = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal';

export interface LogCategoryLevelDto {
  name: string;
  level: string;
  inherited: boolean;
}

export interface LogCategoryOverride {
  name: string;
  level: string | null;
}

export interface LogLevelDto {
  level: string;
  categories: LogCategoryLevelDto[];
}

export interface UpdateLogLevelRequest {
  level?: string;
  categories?: LogCategoryOverride[];
}

export interface RotatingBackupStatusDto {
  enabled: boolean;
  intervalHours: number;
  retentionCount: number;
  lastAttemptUtc: string | null;
  lastSuccessUtc: string | null;
  lastFailureUtc: string | null;
  lastBackupFileName: string | null;
  retainedCount: number;
}

/** One on-disk rotating snapshot, exposed for the restore picker (no paths). */
export interface RotatingBackupFileDto {
  fileName: string;
  byteSize: number;
  timestampUtc: string;
}

/** Listing of the on-disk rotating snapshots (newest first). */
export interface RotatingBackupListDto {
  files: RotatingBackupFileDto[];
}

/** Request to restore from a chosen on-disk rotating snapshot. */
export interface RestoreFromBackupRequest {
  fileName: string;
}

/** Response for a staged restore (202 Accepted) — upload or from a snapshot. */
export interface RestoreStageResponseDto {
  preRestoreBackupFileName: string | null;
  message: string | null;
}

// --- Admin audit trail (1.18.0) ---

/**
 * One administrative audit event. Carries action/result verbs, resolved actor
 * user name, numeric ids and a timestamp only — never paths or secrets.
 */
export interface AuditEventDto {
  id: number;
  action: string;
  result: string;
  actorUserId: number | null;
  actorUserName: string | null;
  targetUserId: number | null;
  timestamp: string;
  correlationId: string | null;
}

/** One page of the audit trail (newest first), with total count. */
export interface AuditTrailPageDto {
  items: AuditEventDto[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// --- System info ---

/**
 * The server's OS platform, so the admin UI can speak its path idiom. Absent
 * or null on an older server (or an unrecognized OS) — callers must fall back
 * to the container-oriented wording in that case.
 */
export type SystemPlatform = 'windows' | 'linux';

/** Read-only product version info from GET /api/v1/system/info. */
export interface SystemInfoDto {
  version: string;
  platform?: SystemPlatform | null;
}

// --- Home "New chapters" (1.12.0) ---

/**
 * Home "New chapters" response: recently-added archives STACKED by their top-level
 * unit for each library the caller can see, grouped by library, newest activity
 * first, capped per library. Respects Incognito/Private visibility like the other
 * discovery surfaces, and drops libraries the caller hid from home
 * (see HomeLibraryVisibility).
 */
export interface RecentChaptersDto {
  libraries: RecentChaptersLibraryGroup[];
}

/**
 * One library's "New chapters" group: its recently-updated stacks, ordered by
 * latestAddedAt descending, capped to the requested per-library limit. Empty
 * stacks when the library has no recently-added archives.
 */
export interface RecentChaptersLibraryGroup {
  libraryId: string;
  libraryName: string;
  stacks: RecentChapterStack[];
}

/**
 * A single "New chapters" stack: a top-level unit (folder) with recently-added
 * descendant archives, or a loose top-level archive as its own standalone stack.
 * Convention-agnostic - not assumed to be a "series".
 */
export interface RecentChapterStack {
  /** Top-level folder public id, or the archive public id for a loose archive. */
  id: string;
  /** Top-level folder name, or the archive name for a loose archive. */
  displayName: string;
  /** True = folder card (tap -> folder browse sorted recentlyUpdated); false = standalone archive (tap -> reader). */
  isFolder: boolean;
  /** Folder cover (first descendant archive) or the archive's own cover; null when none. */
  coverUrl: string | null;
  /** Newest descendant archive public id (equals id when isFolder is false). */
  latestItemId: string;
  /** Newest descendant archive display name. */
  latestItemName: string;
  /** Stack ordering key: the newest descendant archive's CreatedAt. */
  latestAddedAt: string;
  /** Count of recently-added descendant archives in the stack (>= 1). */
  newCount: number;
  /**
   * Derived read state of the stack's top-level node (1.20.0, additive): the same rollup
   * the `readState` filter uses, so the tag and the filter never disagree. A folder stack
   * rolls up over its whole subtree; a loose archive stack rolls up over just itself.
   */
  readState: 'read' | 'reading' | 'unread';
}

/**
 * The current user's Home library-visibility preference (1.12.0). Libraries in
 * this list are hidden from the home "New chapters" surface. Independent of the
 * Private designation and of Incognito mode. Library IDs are opaque public IDs.
 */
export interface HomeLibraryVisibility {
  excludedLibraryIds: string[];
}

/**
 * A saved in-reader bookmark (1.17.0). `ordinal` is the zero-based page index it
 * marks (matches `ManifestPageEntry.pageIndex` / `currentPage`), mirroring
 * `BookmarkEntry` on the server (`ReadingStateService.GetBookmarksAsync`).
 */
export interface BookmarkDto {
  id: string;
  itemId: string;
  ordinal: number;
  normalizedAnchor: number;
  label: string | null;
  createdAt: string;
}

export interface AddBookmarkRequest {
  ordinal: number;
  normalizedAnchor?: number;
  label?: string | null;
}

export interface AddBookmarkResult {
  id: string;
}

/**
 * Update Checker status (1.21.0). Mirrors `UpdateCheckStatusDto` on the server.
 * The checker is OFF by default and is the single sanctioned outbound call:
 * only version strings and a timestamp cross the wire — no instance id or
 * telemetry. `latestVersion` is null when the check is off or has never run.
 */
export interface UpdateCheckStatusDto {
  enabled: boolean;
  currentVersion: string;
  latestVersion: string | null;
  updateAvailable: boolean;
  lastChecked: string | null;
}

/** Request to change the Update Checker opt-in (`UpdateCheckSettingsRequest`). */
export interface UpdateCheckSettingsRequest {
  enabled: boolean;
}
