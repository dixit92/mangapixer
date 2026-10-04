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
  /**
   * The sort the server actually used when it differs from the requested one (1.31.0): 'name' when a browse with
   * `preferVolumes` (the home "New chapters" tap) opened a linked series' Volumes view. Null otherwise. Optional (additive).
   */
  effectiveSort?: string | null;
}

/** 'VolumeStack' (1.29.0): a virtual volume stack of the Volumes view - a browse entry only, never a stored node. */
export type CatalogNodeKind = 'Folder' | 'Archive' | 'VolumeStack';

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
  /**
   * Whether the node ITSELF has series information (1.24.0): its own web link, its own
   * ComicInfo.xml, or ComicInfo on the archives inside a folder. Inherited links and
   * "Don't match" do not count; always false while "Show series information" is off.
   * Drives the card (i). Optional so older fixtures keep compiling.
   */
  hasSeriesInfo?: boolean;
  /**
   * Favorites stacking (1.27.0): set only on a `GET /favorites` item that stands for a
   * STACK - this folder holds this many (>= 2) of the user's starred archives as direct
   * children. The item is the folder; `isFavorite` is the folder's own star. Null or
   * absent everywhere else.
   */
  favoriteStackCount?: number | null;
  /** 1.29.0: set only on a browse entry of kind 'VolumeStack' (the Volumes view of a series). */
  volumeStack?: VolumeStackSummaryDto | null;
  /** 1.29.0: where coverUrl comes from (the cover layer); null/absent = the file cover. */
  coverSource?: CardCoverSource | null;
  /** 1.30.0 (reach): on a chapter archive, the volume key of a volume FILE of the same series that already holds it ("Also in Volume 10"). */
  alsoInVolume?: string | null;
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
  /** Series found through a linked series' alternative titles; first page only (1.26.0). */
  seriesMatches?: SeriesMatchDto[] | null;
}

export interface SeriesMatchDto {
  node: CatalogNodeDto;
  matchedTitle: string;
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
  /** Theme preference: "dark" (server default), "light" or "system". Optional so older fixtures keep compiling. */
  theme?: string;
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
  /** The caller has starred this archive - the Home card's star (1.28.0). Optional for older servers. */
  isFavorite?: boolean;
  /**
   * The card shows the (i) and the hover summary (1.28.0), by the series-info anchor rule:
   * the nearest web link on the archive or an ancestor folder, or its own ComicInfo.
   * Optional for older servers.
   */
  hasSeriesInfo?: boolean;
  /**
   * The card's cover URL from the cover layer (1.29.0): versioned, layered (an admin choice or an automatic crop / web
   * cover when one applies), else the file cover. Optional for older servers (the client then builds the file cover URL).
   */
  coverUrl?: string | null;
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
  /**
   * Effective automatic scan schedule (1.23.0): one of `LIBRARY_SCAN_SCHEDULES`.
   * Admin library responses only; absent/null from the catalog listing.
   */
  scanSchedule?: LibraryScanSchedule | null;
  /**
   * Approximate next automatic scan (1.23.0); a past time means due at the next
   * scheduler pass. Null when off / scheduler disabled; admin responses only.
   */
  nextScheduledScanAt?: string | null;
  /** Server-local hour (0-23) of a Daily / Weekly scan (1.32.0); null = any time. Admin responses only. */
  scanHour?: number | null;
  /** Weekday of a Weekly scan with an hour (0 = Sunday ... 6); null = Sunday. Admin responses only. */
  scanWeekday?: number | null;
}

/** Request to set a library's icon (1.22.0). Null clears back to the default. */
export interface SetLibraryIconRequest {
  icon: string | null;
}

/** Automatic scan schedule presets (1.23.0), mirroring `LibraryScanSchedules` on the server. */
export const LIBRARY_SCAN_SCHEDULES = ['off', '1h', '6h', '1d', '7d'] as const;
export type LibraryScanSchedule = (typeof LIBRARY_SCAN_SCHEDULES)[number];

/** Request to set a library's automatic scan schedule (1.23.0). Null clears back to the daily default. */
export interface SetLibraryScanScheduleRequest {
  scanSchedule: LibraryScanSchedule | null;
  /** 1.32.0: the whole schedule - hour (Daily / Weekly only; null = any time) and weekday (Weekly with an hour only). */
  scanHour?: number | null;
  scanWeekday?: number | null;
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
  /**
   * Per-user "Series information on hover" (1.27.0). Default true (server-side): on a
   * device with a hovering fine pointer, resting on the cover, title or (i) of an item
   * that shows the (i) opens a read-only series summary popover.
   */
  seriesInfoOnHover?: boolean;
  /**
   * Per-user series view (1.29.0): the Volumes | Folders switch in the series header, remembered for the user.
   * Null/absent = follow the folder / library / global default.
   */
  seriesViewMode?: SeriesViewMode | null;
  /** 1.30.0: the viewer's view of a volume's page ('card' | 'list'); null = follow `viewMode`. */
  stackViewMode?: string | null;
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
  /** Snapshot progress before applying (server default true). */
  snapshot?: boolean;
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
  /**
   * Shared per-archive double-page pairing (1.23.0): sorted forced spread-start page
   * indices. Null/absent when none is saved for this content version (the reader then
   * uses its device cover setting); `[]` is an explicit "no shifts".
   */
  spreadStarts?: number[] | null;
}

/** PUT /items/{itemId}/spread-layout body (1.23.0). */
export interface SetSpreadLayoutRequest {
  /** The manifest content version the pairing was made against (409 stale_content on mismatch). */
  expectedContentVersion: number;
  spreadStarts: number[];
}

/** The saved shared pairing returned by the spread-layout write (1.23.0). */
export interface SpreadLayoutDto {
  itemId: string;
  contentVersion: number;
  spreadStarts: number[];
  updatedAt: string;
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
  /** The caller has starred the stack's node (the folder, or the loose archive) (1.28.0). */
  isFavorite?: boolean;
  /**
   * The card shows the (i) and the hover summary (1.28.0): a folder by the browse rule, a
   * loose archive by the series-info anchor rule.
   */
  hasSeriesInfo?: boolean;
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

// --- Admin analytics (1.22.0 lane E) ---

/**
 * Instance-wide analytics overview (`AnalyticsOverviewDto`). Counts and a
 * generation timestamp only — on-demand aggregation, no rollup table.
 */
export interface AnalyticsOverviewDto {
  generatedAt: string;

  libraryCount: number;
  totalNodeCount: number;
  archiveNodeCount: number;
  folderNodeCount: number;
  tombstonedNodeCount: number;
  analyzedItemCount: number;
  pendingItemCount: number;
  failedItemCount: number;

  userCount: number;
  activeUserCount: number;
  adminCount: number;
  pendingActivationCount: number;

  readingProgressCount: number;
  completedItemCount: number;
  inProgressItemCount: number;
  bookmarkCount: number;
  favoriteCount: number;
  activeSessionCount: number;
}

/**
 * One row of the per-user analytics table (`AnalyticsUserRowDto`). Counts and
 * timestamps only — never item names or paths. Reading activity in a library
 * the user marked Private is excluded from their own counts (owner decision
 * 2026-09-22, privacy-conservative default).
 */
export interface AnalyticsUserRowDto {
  id: string;
  username: string;
  isAdmin: boolean;
  isActive: boolean;
  isPendingActivation: boolean;
  lastLoginAt: string | null;

  chaptersCompleted: number;
  chaptersInProgress: number;
  bookmarkCount: number;
  favoriteCount: number;
  lastReadingActivityAt: string | null;
}

/**
 * Backup settings (1.22.0). Mirrors `BackupSettingsDto` on the server. Every
 * field carries its source; `configuration`-sourced fields are read-only in the
 * UI. The default location is never sent as a path (`locationKind: 'default'`
 * means "inside the data folder"); `customLocation` is what an admin or
 * operator typed.
 */
export type BackupSettingSource = 'default' | 'settings' | 'configuration';
export type BackupLocationStatus = 'ok' | 'unavailable' | 'invalid' | 'unknown';

export interface BackupSettingsDto {
  enabled: boolean;
  enabledSource: BackupSettingSource;
  intervalHours: number;
  intervalHoursSource: BackupSettingSource;
  retentionCount: number;
  retentionCountSource: BackupSettingSource;
  locationKind: 'default' | 'custom';
  locationSource: BackupSettingSource;
  customLocation: string | null;
  locationChangeAllowed: boolean;
  locationStatus: BackupLocationStatus;
  platform: 'linux' | 'windows' | null;
  /** Rotating snapshots in the current location (1.23.0): what a move would take. */
  rotatingSnapshotCount?: number;
  rotatingSnapshotBytes?: number;
}

/**
 * Partial update (`UpdateBackupSettingsRequest`); omitted fields stay unchanged.
 * `currentPassword` is required whenever `location` is present (also for
 * `validateOnly`, the "Test" action).
 */
export interface UpdateBackupSettingsRequest {
  enabled?: boolean;
  intervalHours?: number;
  retentionCount?: number;
  location?: { mode: 'default' | 'custom'; customLocation?: string };
  currentPassword?: string;
  validateOnly?: boolean;
  adoptExistingMarker?: boolean;
  /** On a location change, move the existing rotating snapshots too (1.23.0). */
  moveExistingSnapshots?: boolean;
}

/** Result of a backup settings PUT (`BackupSettingsUpdateResultDto`). */
export interface BackupSettingsUpdateResultDto {
  settings: BackupSettingsDto;
  validateOnly: boolean;
  willCreate: boolean;
  locationChanged: boolean;
  /** A background move of the existing snapshots started (1.23.0). */
  snapshotMoveStarted?: boolean;
  warnings: string[];
}

/**
 * Background move of the existing rotating snapshots after a location change
 * (1.23.0, `GET /operations/backups/move`). In memory on the server: `idle`
 * after a restart. File names only, never a folder.
 */
export type BackupSnapshotMoveState = 'idle' | 'running' | 'completed' | 'cancelled';

export interface BackupSnapshotMoveIssueDto {
  fileName: string;
  /** `name_conflict` | `verify_failed` | `copy_failed` | `source_delete_failed` | `cancelled`. */
  code: string;
  /** `previous`: still in the previous location; `both`: in both locations. */
  location: 'previous' | 'both';
}

export interface BackupSnapshotMoveStatusDto {
  state: BackupSnapshotMoveState;
  fromKind: 'default' | 'custom' | null;
  toKind: 'default' | 'custom' | null;
  totalFiles: number;
  filesDone: number;
  totalBytes: number;
  bytesDone: number;
  movedCount: number;
  prunedCount: number;
  startedUtc: string | null;
  finishedUtc: string | null;
  issues: BackupSnapshotMoveIssueDto[];
}

/**
 * 1.22.0 additions to the rotating backup status (interface merge, so the
 * original declaration above stays untouched): the location KIND and health,
 * never the location itself.
 */
export interface RotatingBackupStatusDto {
  locationKind?: 'default' | 'custom';
  locationStatus?: BackupLocationStatus;
  lastFailureCode?: string | null;
}

// --- Series metadata (1.24.0, stage 1) ---------------------------------------------
// Mirrors MangaPixer.Core/Api/MetadataDtos.cs + Core/Metadata/MetadataVocabulary.cs.
// Enums arrive as their C# names (JsonStringEnumConverter).

export type SeriesInfoState = 'None' | 'ComicInfo' | 'Web' | 'WebAndComicInfo' | 'Mixed' | 'DontMatch';
export type MetadataPrecedence = 'WebFirst' | 'ComicInfoFirst';
export type MetadataPrecedenceSource = 'Default' | 'Library' | 'Folder';
export type SeriesLinkState = 'Confirmed' | 'Auto' | 'NeedsReview' | 'DontMatch';
export type MetadataMatchMethod = 'Search' | 'Reference' | 'ComicInfoWebHint' | 'Auto';
export type MetadataOrigin =
  | 'Japan' | 'Korea' | 'ChinaTaiwan' | 'EnglishOriginal' | 'Philippines' | 'Indonesia' | 'Thailand'
  | 'Vietnam' | 'Malaysia' | 'Nordic' | 'French' | 'Spanish' | 'German' | 'Other' | 'Italian' | 'Dutch';
export type MetadataFormat = 'Comic' | 'Novel' | 'Artbook' | 'Doujinshi' | 'Audio';
export type MetadataOriginStatus = 'Unknown' | 'Ongoing' | 'Complete' | 'Hiatus' | 'Cancelled';
export type MetadataFieldSource = 'Web' | 'ComicInfo';

export interface SeriesCreatorDto {
  name: string;
  /** writer, artist, author, penciller, inker, colorist, letterer, coverArtist, editor, translator, other. */
  role: string;
}

export interface SeriesPublisherDto {
  name: string;
  /** original, english, other. */
  kind: string;
}

export interface SeriesInfoItemDto {
  number?: string | null;
  volume?: number | null;
  title?: string | null;
  summary?: string | null;
  year?: number | null;
  month?: number | null;
}

export interface SeriesInfoItemRowDto {
  nodeId: string;
  displayName: string;
  number?: string | null;
  volume?: number | null;
  title?: string | null;
  year?: number | null;
}

export interface SeriesMixedEntryDto {
  name: string;
  count: number;
}

export interface SeriesInfoWebDto {
  provider: string;
  providerName: string;
  siteUrl?: string | null;
  fetchedAt: string;
  /** Poster (lane B2); false/null in stage-1 lane B1. */
  hasImage?: boolean;
  imageUrl?: string | null;
  /** Licence credit shown with the source link, e.g. "Data: Grand Comics Database, CC BY-SA 4.0" (1.32.0). */
  credit?: string | null;
}

export interface SeriesInfoComicInfoDto {
  itemsWithComicInfo: number;
  itemsTotal: number;
  count?: number | null;
  /** ComicInfo Web URLs on allowlisted hosts only. */
  webLinks?: string[];
}

export interface SeriesLinkInfoDto {
  state: SeriesLinkState;
  /** The node holding the link row. */
  nodeId: string;
  inherited: boolean;
  linkedAt?: string | null;
}

/** GET /nodes/{nodeId}/series-info - one DTO for the overlay and the series page. */
export interface SeriesInfoDto {
  nodeId: string;
  nodeKind: CatalogNodeKind;
  anchorNodeId: string;
  anchorKind: CatalogNodeKind;
  anchorDisplayName: string;
  libraryId: string;
  state: SeriesInfoState;
  title?: string | null;
  altTitles?: string[];
  description?: string | null;
  creators?: SeriesCreatorDto[];
  genres?: string[];
  origin?: MetadataOrigin | null;
  format?: MetadataFormat | null;
  webtoon?: boolean | null;
  startYear?: number | null;
  originStatus?: MetadataOriginStatus | null;
  originVolumes?: number | null;
  latestChapter?: number | null;
  statusText?: string | null;
  licensedEn?: boolean | null;
  translationComplete?: boolean | null;
  publishers?: SeriesPublisherDto[];
  /** Per-field attribution keyed by camelCase field name. */
  fieldSources?: Partial<Record<string, MetadataFieldSource>>;
  item?: SeriesInfoItemDto | null;
  mixedSeries?: SeriesMixedEntryDto[];
  web?: SeriesInfoWebDto | null;
  comicInfo?: SeriesInfoComicInfoDto | null;
  link?: SeriesLinkInfoDto | null;
  precedence: MetadataPrecedence;
  precedenceSource: MetadataPrecedenceSource;
  items?: SeriesInfoItemRowDto[];
}

export interface MetadataComicInfoStatsDto {
  archivesRead: number;
  archivesTotal: number;
  archivesWithComicInfo: number;
}

export interface MetadataLibrarySettingsDto {
  libraryId: string;
  name: string;
  fetchEnabled: boolean;
  showSeriesInfo: boolean;
  precedence?: MetadataPrecedence | null;
  linkCount: number;
  /** Global switch + automatic consent + this library's Fetch. */
  autoMatchActive?: boolean;
  /** 1.29.0: "Show saved web covers" (on by default) - separate from showSeriesInfo. */
  showWebCovers?: boolean;
  /** 1.29.0: this library's Volumes view override; null = the global default. */
  virtualVolumes?: ViewSwitch | null;
}

/** GET/PUT /admin/metadata/settings (the settings card is lane B2's). */
export interface MetadataSettingsDto {
  showSeriesInfo: boolean;
  fetchEnabled: boolean;
  networkDisabledByConfig: boolean;
  acceptedConsentVersion?: number | null;
  currentConsentVersion: number;
  consentAt?: string | null;
  dailyBudget: number;
  defaultDailyBudget: number;
  budgetUsedToday: number;
  backoffUntil?: string | null;
  lastErrorAt?: string | null;
  lastErrorCode?: string | null;
  comicInfo: MetadataComicInfoStatsDto;
  webRecordCount: number;
  libraries: MetadataLibrarySettingsDto[];
  /** Stage 2: the global Automatic matching switch (one switch; decision 3). */
  autoMatchEnabled?: boolean;
  acceptedAutoConsentVersion?: number | null;
  currentAutoConsentVersion?: number;
  autoConsentAt?: string | null;
  thresholds?: MetadataMatchThresholdsDto | null;
  defaultThresholds?: MetadataMatchThresholdsDto | null;
  thresholdBounds?: MetadataMatchThresholdBoundsDto | null;
  thresholdsAreDefault?: boolean;
  /** 1.28.0: "Compare covers" under Automatic matching (on by default). */
  compareCoversEnabled?: boolean;
  /** True when Metadata:AutoMatch:CompareCovers=false switches it off regardless of the setting. */
  compareCoversDisabledByConfig?: boolean;
  /** 1.29.0: "Preferred language (covers and releases)" (a MangaDex locale code, default "en"). */
  preferredCoverLanguage?: string;
  /** 1.29.0: "Volume covers from the web" (on by default). */
  volumeCoversEnabled?: boolean;
  /** 1.29.0: true when Metadata:AutoMatch:VolumeCovers=false switches it off regardless of the setting. */
  volumeCoversDisabledByConfig?: boolean;
  /** 1.29.0: local front / back spread crop (on by default; no network). */
  spreadCropEnabled?: boolean;
  /** 1.29.0: global default of the Volumes view (on by default). */
  virtualVolumesEnabled?: boolean;
  /** 1.28.0: the approved sites (the provider allowlist), each with whether it is in. */
  providers?: MetadataProviderDto[];
  /** 1.28.0: "Fetch from the web" was on under an older consent - off until an admin accepts again. */
  consentRenewalNeeded?: boolean;
  /** 1.28.0: Automatic matching was on under an older automatic consent. */
  autoConsentRenewalNeeded?: boolean;
}

/** One approved metadata site (1.28.0, the provider allowlist). */
export interface MetadataProviderDto {
  id: string;
  name: string;
  hosts: string[];
  usedFor: string;
  sends: string;
  allowed: boolean;
}

export interface UpdateMetadataSettingsRequest {
  showSeriesInfo?: boolean | null;
  fetchEnabled?: boolean | null;
  acceptedConsentVersion?: number | null;
  dailyBudget?: number | null;
  resetDailyBudget?: boolean;
  /** On requires acceptedAutoConsentVersion = currentAutoConsentVersion and Fetch on. */
  autoMatchEnabled?: boolean | null;
  acceptedAutoConsentVersion?: number | null;
  thresholds?: MetadataMatchThresholdsDto | null;
  resetThresholds?: boolean;
  /** 1.28.0: "Compare covers"; null leaves it unchanged (no consent of its own). */
  compareCoversEnabled?: boolean | null;
  /** 1.28.0: the full set of provider ids to keep off the allowlist ([] = all in); null/absent = unchanged. */
  removedProviders?: string[] | null;
  /** 1.29.0: a MangaDex locale code (en, ja, pt-br, es-la); anything else is invalid_cover_language. */
  preferredCoverLanguage?: string | null;
  /** 1.29.0: "Volume covers from the web"; null leaves it unchanged. */
  volumeCoversEnabled?: boolean | null;
  /** 1.29.0: local spread crop; null leaves it unchanged. */
  spreadCropEnabled?: boolean | null;
  /** 1.29.0: global default of the Volumes view; null leaves it unchanged. */
  virtualVolumesEnabled?: boolean | null;
}

export interface UpdateMetadataLibraryRequest {
  fetchEnabled?: boolean | null;
  showSeriesInfo?: boolean | null;
  /** 1.29.0: "Show saved web covers". */
  showWebCovers?: boolean | null;
  /** 1.29.0: the library's Volumes view override; see resetVirtualVolumes. */
  virtualVolumes?: ViewSwitch | null;
  /** 1.29.0: back to the global default. */
  resetVirtualVolumes?: boolean;
}

export interface SetMetadataPrecedenceRequest {
  precedence?: MetadataPrecedence | null;
}

export interface LinkSeriesRequest {
  provider: string;
  externalId: string;
  matchMethod?: MetadataMatchMethod | null;
  matchScore?: number | null;
}

export interface NodeSeriesLinkDto {
  nodeId: string;
  state: SeriesLinkState;
  provider?: string | null;
  externalId?: string | null;
  recordId?: string | null;
  matchMethod?: MetadataMatchMethod | null;
  updatedAt: string;
}

export interface NodeSeriesLinkChangeDto {
  nodeId: string;
  link?: NodeSeriesLinkDto | null;
  previous?: NodeSeriesLinkDto | null;
}

export interface MetadataPurgeRequest {
  libraryId?: string | null;
}

export interface MetadataPurgeResultDto {
  linksRemoved: number;
  recordsRemoved: number;
}

export interface FolderMetadataPrecedenceDto {
  nodeId: string;
  precedence: MetadataPrecedence;
}

// --- Series metadata identify flow (1.24.0, lane B2; admin-only) ---

export type MatchStrength = 'Weak' | 'Possible' | 'Strong';

export interface IdentifyReferenceDto {
  provider: string;
  externalId: string;
}

export interface IdentifyLocalDto {
  displayName: string;
  itemCount: number;
  comicInfoSeries?: string | null;
  tallStrips?: boolean | null;
  yearHint?: number | null;
  /** Local cover: the archive's own, or the folder's first archive. */
  coverUrl?: string | null;
}

/** GET /admin/metadata/nodes/{id}/identify - no network. */
export interface IdentifyContextDto {
  nodeId: string;
  nodeKind: CatalogNodeKind;
  displayName: string;
  libraryId: string;
  provider: string;
  providerName: string;
  fetchAvailable: boolean;
  unavailableCode?: string | null;
  unavailableMessage?: string | null;
  suggestions?: string[];
  comicInfoHint?: IdentifyReferenceDto | null;
  budgetUsedToday: number;
  dailyBudget: number;
  backoffUntil?: string | null;
  currentLink?: NodeSeriesLinkDto | null;
  local: IdentifyLocalDto;
  /** In (or is) a folder whose Content is "Doujinshi & adult one-shots": the dialog starts with the type filter off. */
  doujinshiContent?: boolean;
  /** The sites Identify can search (1.32.0): MangaUpdates and the Grand Comics Database. */
  sites?: IdentifySiteDto[];
  /** The node's local signs route it to comics: `provider` is then `gcd`. */
  comicsSignalled?: boolean;
}

/** A site Identify can search (1.32.0). */
export interface IdentifySiteDto {
  id: string;
  name: string;
  available: boolean;
  unavailableCode?: string | null;
  /** E.g. the Grand Comics Database's "about 25 requests an hour". */
  note?: string | null;
}

export interface IdentifySearchRequest {
  query: string;
  page?: number;
  /** Leave doujinshi, novels, artbooks and drama CDs out (a fixed provider type filter). */
  hideDoujinshiAndNovels?: boolean;
  /** `mangaupdates` (default) or `gcd` (1.32.0). */
  provider?: string | null;
  /** GCD only: the (YYYY) of the node's own name, to narrow to series that began that year. */
  startYear?: number | null;
}

export interface IdentifyLookupRequest {
  reference: string;
}

export interface IdentifyPreviewRequest {
  provider: string;
  externalId: string;
}

export interface IdentifyCandidateDto {
  externalId: string;
  title: string;
  hitTitle?: string | null;
  providerType?: string | null;
  origin?: MetadataOrigin | null;
  format?: MetadataFormat | null;
  year?: number | null;
  score: number;
  strength: MatchStrength;
  imageToken?: string | null;
  /** The edition's country / language and its issue or book count (1.32.0, GCD). */
  country?: string | null;
  language?: string | null;
  unitCount?: number | null;
  unitKind?: string | null;
}

export interface IdentifySearchResultDto {
  provider: string;
  page: number;
  totalHits: number;
  candidates: IdentifyCandidateDto[];
  budgetUsedToday: number;
  dailyBudget: number;
}

export interface IdentifyWarningDto {
  code: string;
  message: string;
}

export interface IdentifyPreviewDto {
  provider: string;
  providerName: string;
  externalId: string;
  title: string;
  altTitles?: string[];
  description?: string | null;
  providerType?: string | null;
  origin?: MetadataOrigin | null;
  format?: MetadataFormat | null;
  webtoon?: boolean | null;
  startYear?: number | null;
  originStatus?: MetadataOriginStatus | null;
  originVolumes?: number | null;
  latestChapter?: number | null;
  creators?: SeriesCreatorDto[];
  genres?: string[];
  siteUrl?: string | null;
  imageToken?: string | null;
  score: number;
  strength: MatchStrength;
  fetchedAt: string;
  local: IdentifyLocalDto;
  warnings?: IdentifyWarningDto[];
  /** The edition's country / language and publishers (1.32.0, GCD). */
  country?: string | null;
  language?: string | null;
  publishers?: string[];
  /** Licence credit, e.g. "Data: Grand Comics Database, CC BY-SA 4.0" (1.32.0). */
  credit?: string | null;
}

export interface MetadataRefreshResultDto {
  state: string;
  fetchedAt: string;
  imageUpdated: boolean;
}

// --- Series metadata stage 2: auto-match, review, flags (lane B contract) ---------
// Mirrors MangaPixer.Core/Api/MetadataAutoMatchDtos.cs + Core/Metadata/AutoMatch/AutoMatchContracts.cs.

export type WorkClass =
  | 'Excluded' | 'Series' | 'SeriesWithUnits' | 'OneShot' | 'CollectionLeaf' | 'ArtistCollection'
  | 'FranchiseContainer' | 'CollectionContainer' | 'Wrapper' | 'Mixed' | 'UnitSub' | 'Ambiguous';
export type MatchLevel = 'None' | 'Folder' | 'Archive' | 'ReviewOnly';
export type MetadataReviewTab =
  | 'NeedsReview' | 'AutoLinked' | 'Unmatched' | 'Flags' | 'DontMatch' | 'Confirmed' | 'MissingFolders';
export type MetadataFolderContent = 'Auto' | 'DoujinshiAndAdultOneShots' | 'NotDoujinshi';
export type MetadataMatchRunTrigger = 'Scan' | 'Bulk' | 'Retry' | 'Rerun' | 'Recheck';
export type MetadataMatchRunStatus = 'Running' | 'Completed' | 'Cancelled';
export type MetadataReviewBulkAction = 'AcceptTop' | 'DontMatch' | 'RerunMatching' | 'Confirm' | 'Unlink' | 'Later' | 'ClearLater';
export type MetadataFlagReason = 'WrongSeries' | 'WrongDetails' | 'NotOneSeries' | 'Other';
export type MetadataFlagState = 'Open' | 'Relinked' | 'Unlinked' | 'DontMatch' | 'Dismissed';

export interface MetadataMatchThresholdsDto {
  /** 0.85-0.99, default 0.92. */
  autoTitle: number;
  /** 0.05-0.30, default 0.10. */
  margin: number;
  /** 0.40-0.90, default 0.60; below autoTitle. */
  reviewFloor: number;
}

export interface MetadataMatchThresholdBoundsDto {
  autoTitleMin: number;
  autoTitleMax: number;
  marginMin: number;
  marginMax: number;
  reviewFloorMin: number;
  reviewFloorMax: number;
}

/** GET /admin/metadata/review/summary?library= */
/** 1.33.0: a group of waiting works a review row belongs to; `key` is the list's `author` / `folder` filter value. */
export interface MetadataReviewGroupHintDto {
  key: string;
  label: string;
  others: number;
}

/** 1.33.0: an author with at least two works waiting in Needs review (largest first). */
export interface MetadataReviewAuthorDto {
  key: string;
  label: string;
  count: number;
  later?: number;
}

export interface MetadataReviewAuthorsDto {
  items: MetadataReviewAuthorDto[];
}

export interface MetadataReviewSummaryDto {
  needsReview: number;
  /** 1.33.0: Needs review rows set aside ("Later"); part of `needsReview`. */
  later: number;
  autoLinked: number;
  unmatched: number;
  openFlags: number;
  dontMatch: number;
  confirmed: number;
  missingFolders: number;
  pending: number;
  /** 1.31.0: works in review being checked again under the matcher's current rules (part of `pending`). */
  recheckPending: number;
}

export interface MetadataReviewLinkDto {
  state: SeriesLinkState;
  provider?: string | null;
  externalId?: string | null;
  recordId?: string | null;
  title?: string | null;
  matchMethod?: MetadataMatchMethod | null;
  matchScore?: number | null;
  imageUrl?: string | null;
  updatedAt: string;
}

export interface MetadataReviewCandidateDto {
  /** 1-based; the value accept takes. */
  rank: number;
  provider: string;
  externalId: string;
  title: string;
  providerType?: string | null;
  format?: MetadataFormat | null;
  origin?: MetadataOrigin | null;
  year?: number | null;
  volumes?: number | null;
  titleScore: number;
  adjustedScore: number;
  /**
   * close_second, count, year, type, related_pair, one_shot, author, number, review_only; declared_type, not_declared_type, reach,
   * subtitle_family, series_family (1.30.0); cover_differs (1.31.0).
   */
  reasons?: string[];
  /** For GET /admin/metadata/candidates/{token}/image (fetched only when loaded). */
  imageToken?: string | null;
  /**
   * 1.30.0: candidates of one series family (a main story with its spin-offs, side stories, prequels, sequels) share this value -
   * the rank of the family's first candidate. Null when no other candidate of the row is its family.
   */
  familyGroup?: number | null;
  /**
   * 1.30.0: the candidate's role in its family (main_story, spin_off, side_story, prequel, sequel, alternate, alternate_story,
   * adaptation, source, related).
   */
  familyRole?: string | null;
}

export interface MetadataReviewItemDto {
  nodeId: string;
  nodeKind: CatalogNodeKind;
  displayName: string;
  libraryId: string;
  libraryName: string;
  /** Up to 3 ancestor display names below the library root, outermost first. */
  trail?: string[];
  missing?: boolean;
  /** The containing folder, for the row's "open folder" link; null at the library's top level. */
  parentNodeId?: string | null;
  /** Local cover: the archive's own, or a folder's first archive (as browse shows it). */
  coverUrl?: string | null;
  workClass?: WorkClass | null;
  matchLevel?: MatchLevel | null;
  itemCount: number;
  /** Archive group: the other archives (anchor excluded). */
  memberNodeIds?: string[];
  link?: MetadataReviewLinkDto | null;
  candidates?: MetadataReviewCandidateDto[];
  reasons?: string[];
  matchedAt?: string | null;
  nextRetryAt?: string | null;
  runId?: string | null;
  /** 1.31.0: queued to be scored again under the matcher's current rules; the reasons and candidates are the earlier result until then. */
  checkingAgain?: boolean;
  /** 1.33.0 (Needs review): when an admin set the row aside ("Later"); listed after the others until decided or checked again. */
  laterAt?: string | null;
  /** 1.33.0 (Needs review): other waiting works by the same circle / artist (from the works' own names). */
  sameAuthor?: MetadataReviewGroupHintDto | null;
  /** 1.33.0 (Needs review): other waiting works in the same folder (`key` = the folder's node id). */
  sameFolder?: MetadataReviewGroupHintDto | null;
  /** 1.31.0 (folder works): chapter numbers that more than one file of the same folder states. */
  duplicateChapters?: number;
  /** 1.31.0 (folder works): the same for volume numbers. */
  duplicateVolumes?: number;
  openFlagCount: number;
  /** Flags tab only. */
  flags?: MetadataFlagDto[];
}

/** GET /admin/metadata/review?tab=&library=&cursor=&limit= */
export interface MetadataReviewPageDto {
  tab: MetadataReviewTab;
  items: MetadataReviewItemDto[];
  total: number;
  nextCursor?: string | null;
  hasMore?: boolean;
}

export interface MetadataReviewAcceptRequest {
  rank: number;
}

export interface MetadataReviewBulkRequest {
  action: MetadataReviewBulkAction;
  /** Max 200. */
  nodeIds: string[];
}

export interface MetadataReviewBulkItemResultDto {
  nodeId: string;
  /** ok, or an error code. */
  code: string;
}

export interface MetadataReviewBulkResultDto {
  action: MetadataReviewBulkAction;
  succeeded: number;
  failed: number;
  results: MetadataReviewBulkItemResultDto[];
}

export interface MetadataAutoMatchStatusDto {
  enabled: boolean;
  active: boolean;
  /** automatic_off, metadata_disabled, metadata_network_disabled, provider_not_allowed, budget_exhausted, provider_backoff. */
  waitingCode?: string | null;
  waitingUntil?: string | null;
  pending: number;
}

export interface MetadataMatchRunDto {
  runId: string;
  libraryId: string;
  libraryName: string;
  trigger: MetadataMatchRunTrigger;
  status: MetadataMatchRunStatus;
  reviewFirst: boolean;
  startedAt: string;
  completedAt?: string | null;
  candidates: number;
  queued: number;
  processed: number;
  autoLinked: number;
  needsReview: number;
  unmatched: number;
  skipped: number;
  failed: number;
  requestsUsed: number;
  /** Local-only outcome counters. */
  autoChangedByAdmin: number;
  reviewAcceptedTop: number;
  reviewAcceptedOther: number;
  reviewDontMatch: number;
}

/** GET /admin/metadata/runs?library=&cursor=&limit= */
export interface MetadataMatchRunsDto {
  status: MetadataAutoMatchStatusDto;
  items: MetadataMatchRunDto[];
  nextCursor?: string | null;
  hasMore?: boolean;
}

/** GET /admin/metadata/libraries/{id}/match/estimate?retryUnmatched= (no network). */
export interface MetadataMatchEstimateDto {
  libraryId: string;
  candidates: number;
  estimatedRequests: number;
  estimatedDays: number;
  alreadyLinked: number;
  unmatched: number;
  dailyBudget: number;
  budgetUsedToday: number;
  firstRun: boolean;
  automaticAvailable: boolean;
  unavailableCode?: string | null;
}

export interface MetadataMatchLibraryRequest {
  reviewFirst?: boolean;
  retryUnmatched?: boolean;
}

/** POST /nodes/{id}/series-info/flags. Note: plain text, max 500. */
export interface CreateMetadataFlagRequest {
  reason: MetadataFlagReason;
  note?: string | null;
}

export interface MetadataMyFlagDto {
  flagId: string;
  anchorNodeId: string;
  reason: MetadataFlagReason;
  state: MetadataFlagState;
  createdAt: string;
  resolvedAt?: string | null;
}

/** GET /nodes/{id}/series-info/flags/mine */
export interface MetadataMyFlagStateDto {
  canFlag: boolean;
  flag?: MetadataMyFlagDto | null;
}

export interface MetadataFlagDto {
  flagId: string;
  nodeId: string;
  nodeKind: CatalogNodeKind;
  nodeDisplayName: string;
  libraryId: string;
  reason: MetadataFlagReason;
  /** User content: admin-only. */
  note?: string | null;
  state: MetadataFlagState;
  reporterDisplayName: string;
  resolvedByDisplayName?: string | null;
  createdAt: string;
  resolvedAt?: string | null;
  provider?: string | null;
  externalId?: string | null;
  currentLink?: MetadataReviewLinkDto | null;
}

/** GET /admin/metadata/flags?state=open|resolved|all&library=&cursor=&limit= */
export interface MetadataFlagPageDto {
  items: MetadataFlagDto[];
  total: number;
  nextCursor?: string | null;
  hasMore?: boolean;
}

export interface ResolveMetadataFlagRequest {
  /** Anything but Open. */
  outcome: MetadataFlagState;
}

export interface MetadataReattachRequest {
  targetNodeId: string;
}

export interface MetadataReattachResultDto {
  nodeId: string;
  targetNodeId: string;
  link: boolean;
  precedence: boolean;
  readerDefault: boolean;
  content: boolean;
  /** 1.28.0: the declared facts moved too (only when the target declared nothing of its own). */
  declared?: boolean;
}

export interface SetFolderMetadataContentRequest {
  content: MetadataFolderContent;
}

/** GET/PUT/DELETE /admin/metadata/folders/{id}/content */
export interface FolderMetadataContentDto {
  nodeId: string;
  content?: MetadataFolderContent | null;
  effective: MetadataFolderContent;
  sourceNodeId?: string | null;
  suggested?: MetadataFolderContent | null;
  /** PUT/DELETE only, when the change allows or excludes doujinshi below the folder. */
  rematch?: MetadataContentRematchDto | null;
}

/** Matching again after a Content change; POST /admin/metadata/folders/{id}/content/rematch returns it too. */
export interface MetadataContentRematchDto {
  affected: number;
  queued: number;
  needsConfirmation?: boolean;
  automaticOff?: boolean;
}

// --- Declared facts (1.28.0) ---

/** Admin-declared type / format of the works below a folder or library. */
export type DeclaredType = 'Manga' | 'Manhwa' | 'Manhua' | 'Webtoon' | 'Comic' | 'GraphicNovel' | 'Novel';

/** Where an effective declared fact comes from: the node itself, an ancestor folder, or the library. */
export type DeclaredFactSource = 'Own' | 'Inherited' | 'Library';

export interface DeclaredCreatorDto {
  name: string;
  /** author, writer or artist; null = no role. */
  role?: string | null;
}

/** PUT /admin/metadata/{folders|libraries}/{id}/declared: replaces the scope's type and creators. */
export interface SetDeclaredFactsRequest {
  type?: DeclaredType | null;
  creators?: DeclaredCreatorDto[] | null;
}

export interface DeclaredFactValuesDto {
  type?: DeclaredType | null;
  creators?: DeclaredCreatorDto[];
}

export interface EffectiveDeclaredFactsDto {
  type?: DeclaredType | null;
  typeSource?: DeclaredFactSource | null;
  /** Display name of the folder or library that declares the type. */
  typeFrom?: string | null;
  creators?: DeclaredCreatorDto[];
  creatorsSource?: DeclaredFactSource | null;
  creatorsFrom?: string | null;
}

/** GET/PUT/DELETE /admin/metadata/{folders|libraries}/{id}/declared */
export interface DeclaredFactsScopeDto {
  /** The folder, or null for the library scope. */
  nodeId?: string | null;
  libraryId: string;
  displayName: string;
  own: DeclaredFactValuesDto;
  /** What applies from above (parent folders, then the library); empty for a library. */
  inherited: EffectiveDeclaredFactsDto;
}

export interface DeclaredFactsConflictDto {
  providerName: string;
  type?: boolean;
  recordType?: string | null;
  creators?: boolean;
  recordCreators?: string[];
}

/** GET /nodes/{id}/declared-facts (Info panel, series page). */
export interface NodeDeclaredFactsDto {
  nodeId: string;
  effective: EffectiveDeclaredFactsDto;
  conflict?: DeclaredFactsConflictDto | null;
}

// --- Missing volumes / chapters report (1.28.0, admin-only; stored data only) ---

export type MissingUnitKind = 'Volume' | 'Chapter';
export type MissingTotalSource = 'English' | 'Origin' | 'LatestChapter' | 'Converted' | 'Released';
export type MissingConfidence = 'Low' | 'Medium' | 'High';
/** Worst first: Behind, Holes, UpToDate, NoTotal, then no verdict (Mixed, NoUnits). */
export type MissingVerdict = 'Behind' | 'Holes' | 'UpToDate' | 'NoTotal' | 'Mixed' | 'NoUnits' | 'Restarts';

export interface MissingUnitGapDto {
  kind: MissingUnitKind;
  archiveCount: number;
  /** Distinct numbers on disk. */
  unitCount: number;
  lowest: number;
  /** The highest number on disk. */
  have: number;
  available?: number | null;
  source?: MissingTotalSource | null;
  confidence?: MissingConfidence | null;
  behindBy: number;
  /** Holes below `have` (the first 50); `missingCount` has them all. */
  missing: number[];
  missingCount: number;
  /** 1.29.0 RC: the total in the country of origin, context only (never makes a series "behind"). */
  originTotal?: number | null;
}

export interface MissingSeriesDto {
  /** 1.29.0 RC: the preferred language the totals follow ("en", "fr"): "behind" only against what is released in it. */
  language?: string | null;
  nodeId: string;
  displayName: string;
  libraryId: string;
  libraryName: string;
  coverUrl?: string | null;
  provider: string;
  recordTitle: string;
  linkState: SeriesLinkState;
  verdict: MissingVerdict;
  volumes?: MissingUnitGapDto | null;
  chapters?: MissingUnitGapDto | null;
  mixedFolders: number;
  /** An English publisher is listed but no English total is stored (read on the next refresh). */
  englishTotalUnknown: boolean;
  /** 1.28.0: the stored chapters-per-volume source (an AniList entry), or null. */
  conversion?: MissingConversionDto | null;
  statusText?: string | null;
  fetchedAt?: string;
  /** 1.30.0 (reach): trackers, what the folder holds, upgrades and completion (the same engine as the Volumes view). */
  progress?: SeriesProgressDto | null;
  /** 1.31.0: chapter / volume numbers that more than one file of the same folder states, capped; `duplicateCount` is the full count. */
  duplicates?: DuplicateUnitDto[];
  duplicateCount?: number;
}

/** 1.31.0: a chapter or volume number that `files` (two or more) files of one folder state. */
export interface DuplicateUnitDto {
  kind: MissingUnitKind;
  /** As the names state it ("1", "45.5"). */
  number: string;
  files: number;
}

export interface MissingReportSummaryDto {
  series: number;
  behind: number;
  holes: number;
  upToDate: number;
  noTotal: number;
  noVerdict: number;
  /** 1.30.0: series with official volumes held only as chapters (the Official releases tab). */
  upgrades?: number;
}

/** GET /admin/metadata/missing?library=&onlyMissing=&cursor=&limit= */
export interface MissingReportPageDto {
  items: MissingSeriesDto[];
  summary: MissingReportSummaryDto;
  total: number;
  nextCursor?: string | null;
}

/** A stored chapters-per-volume source (1.28.0). */
export interface MissingConversionDto {
  provider: string;
  providerName: string;
  externalId: string;
  title: string;
  siteUrl?: string | null;
  volumes?: number | null;
  chapters?: number | null;
  /** Only for a finished entry with both totals. */
  chaptersPerVolume?: number | null;
  fetchedAt?: string;
}

export type MissingConversionOutcome = 'Found' | 'NoCounts' | 'NoMatch';

/** POST /admin/metadata/missing/{nodeId}/conversion */
export interface MissingConversionResultDto {
  outcome: MissingConversionOutcome;
  row: MissingSeriesDto;
}

/** POST /admin/metadata/missing/conversions */
export interface MissingConversionBatchRequest {
  library?: string | null;
}

export interface MissingConversionBatchResultDto {
  looked: number;
  found: number;
  noCounts: number;
  noMatch: number;
  remaining: number;
  stoppedCode?: string | null;
  stoppedMessage?: string | null;
}

// --- Virtual volumes and volume covers (1.29.0 contract; lanes S, C and P fill the endpoints) ---

/** A per-library / per-folder view override. */
export type ViewSwitch = 'Off' | 'On';

/** The per-user series view: the Volumes | Folders switch in the series header. */
export type SeriesViewMode = 'Folders' | 'Volumes';

/** GET/PUT /admin/folders/{nodeId}/view-settings (admin). */
export interface FolderViewSettingsDto {
  nodeId: string;
  /** This folder's Volumes view override; null = inherit the library. */
  virtualVolumes?: ViewSwitch | null;
}

/** PUT /admin/folders/{nodeId}/view-settings: replaces the overrides; a null field inherits again. */
export interface UpdateFolderViewSettingsRequest {
  virtualVolumes?: ViewSwitch | null;
}

/** Where a card's cover comes from. */
export type CardCoverSource = 'File' | 'Crop' | 'WebVolume' | 'WebMain' | 'Poster' | 'Chosen';

/** Exact: file names / ComicInfo, the provider list, or bounded by neighbours. Estimated: shown "~ Volume N". */
export type VolumeStackConfidence = 'Exact' | 'Estimated';

export type VolumeListSource = 'FileNames' | 'MangaDex' | 'AniList' | 'Mixed' | 'Wikipedia';

/** A virtual volume stack as a browse entry (CatalogNodeDto.volumeStack). Unit numbers are strings ("3", "45.5"). */
export interface VolumeStackSummaryDto {
  key: string;
  label: string;
  presentCount: number;
  chapterCount?: number | null;
  missingCount: number;
  extraCount: number;
  hasVolumeArchive: boolean;
  confidence: VolumeStackConfidence;
  firstChapter?: string | null;
  lastChapter?: string | null;
  /** 1.29.0 RC: complete chapters of chapterCount (a split chapter counts once, when all its listed parts are here). */
  chaptersPresent?: number | null;
  /** 1.29.0 RC: a missing volume - a placeholder card (presentCount 0), never opened. */
  missing?: boolean;
  /** 1.30.0: the language code when this volume (no volume file here) is released officially in the preferred language. */
  officialRelease?: string | null;
  /** 1.31.0: chapters of this volume that more than one file states; `presentCount` / `extraCount` count each once. */
  duplicates?: DuplicateUnitDto[];
}

export type VolumeSlotKind = 'Item' | 'Missing';

export interface VolumeSlotDto {
  kind: VolumeSlotKind;
  chapter?: string | null;
  item?: CatalogNodeDto | null;
}

/** GET /nodes/{folderId}/volumes/{key} (lane S). */
export interface VolumeStackDto {
  folderId: string;
  key: string;
  label: string;
  coverUrl?: string | null;
  confidence: VolumeStackConfidence;
  source: VolumeListSource;
  /** 1.32.0: the Wikipedia page the series' volume list was completed from, or null (the source line links it). */
  listCredit?: ListCreditDto | null;
  presentCount: number;
  chapterCount?: number | null;
  /** 1.29.0 RC: complete chapters of chapterCount. */
  chaptersPresent?: number | null;
  /** 1.29.0 RC: a real volume file is the first slot (a fractional volume file, the last slot, is an extra). */
  hasVolumeArchive?: boolean;
  missingCount: number;
  extraCount: number;
  previousKey?: string | null;
  nextKey?: string | null;
  slots: VolumeSlotDto[];
  /** 1.30.0: see `VolumeStackSummaryDto.officialRelease`. */
  officialRelease?: string | null;
  /** 1.31.0: see `VolumeStackSummaryDto.duplicates`; each such chapter has one item slot per file. */
  duplicates?: DuplicateUnitDto[];
}

/** GET /nodes/{nodeId}/volume-view (lane S): whether a folder has a Volumes view and whether it is on for the viewer. */
export interface VolumeViewDto {
  nodeId: string;
  available: boolean;
  active: boolean;
  /** 1.29.0 RC: `active` without the viewer's own switch (folder / library / global default); choosing it clears the switch. */
  defaultActive?: boolean;
  consolidated: boolean;
  stackCount: number;
  /**
   * 1.34.0: a webtoon / manhwa / manhua without a real volume list - the view lists its chapters in chapter order (the switch says
   * Chapters), never volumes from a list or missing-volume placeholders.
   */
  chaptersOnly?: boolean;
  /** 1.29.0 RC: the folder has its own series link - the status line below is shown. */
  hasSeriesStatus?: boolean;
  seriesStatus?: MetadataOriginStatus | null;
  /** Whole volumes missing (gaps below the highest one here, and volumes released in the preferred language after it). */
  missingVolumes?: number;
  /** Chapters missing (gaps below the highest one here, and chapters released in the preferred language after it). */
  missingChapters?: number;
  /** What is released in the preferred language is known: "up to date" can be said. */
  releaseKnown?: boolean;
  language?: string | null;
  /** 1.29.0 RC: the country / language of origin the status is about ("Complete (Japan)"). */
  origin?: MetadataOrigin | null;
  originVolumes?: number | null;
  /** Volumes published in the preferred language (English publishers today). */
  releasedVolumes?: number | null;
  /** The highest chapter released in the preferred language. */
  releasedChapter?: number | null;
  /** English only (MangaUpdates): licensed in English / the scanlation is complete. */
  licensed?: boolean | null;
  scanlationComplete?: boolean | null;
  /** 1.29.0 RC: covers of this series still being downloaded in the background (0 when none or the pass waits). */
  coversPending?: number;
  /** 1.30.0 (reach): trackers, what the folder holds, upgrades and completion; set with `hasSeriesStatus`. */
  progress?: SeriesProgressDto | null;
}

// --- Series progress (1.30.0, reach) ---

export interface UnitSpanDto {
  from: number;
  to: number;
}

export type ReachResolution = 'FileNames' | 'VolumeList' | 'Estimated';

/** What the folder holds, volume files and chapter files merged through the stored volume list. */
export interface SeriesReachDto {
  volumeFiles: UnitSpanDto[];
  /** Chapter files that no volume file here already holds (at most 20 spans). */
  chapters: UnitSpanDto[];
  reachChapter?: number | null;
  reachVolume?: number | null;
  overlapChapters: number;
  resolution: ReachResolution;
}

/** The per-kind trackers of the stored record (origin, the official release and released chapters in `language`). */
export interface SeriesTrackersDto {
  language: string;
  origin?: MetadataOrigin | null;
  originStatus?: MetadataOriginStatus | null;
  originVolumes?: number | null;
  originChapters?: number | null;
  officialPublisher?: string | null;
  officialVolumes?: number | null;
  officialChapters?: number | null;
  /** The official publisher's own status (Cancelled = dropped). */
  officialStatus?: MetadataOriginStatus | null;
  licensed?: boolean | null;
  /** English only: the latest released chapter (scanlation). */
  latestChapter?: number | null;
  scanlationComplete?: boolean | null;
  releasedChapter?: number | null;
}

export type SeriesCompletion = 'None' | 'FinishedNotHeld' | 'CompleteCollection';
export type CompletionBasis = 'OfficialVolumes' | 'AllChapters' | 'OriginRun' | 'OfficialChapters';
/** 1.32.0: the one answer of the Completion tab - has the series ended, and does the folder hold all of it. */
export type SeriesAnswer = 'CantTell' | 'HaveItAll' | 'FinishedMissing' | 'UpToDate' | 'MissingSome';
export type SeriesAnswerReason =
  | 'None' | 'Running' | 'OnHiatus' | 'StatusUnknown' | 'WaitingForLanguage' | 'LanguageEditionDropped'
  | 'NoNumbers' | 'NumberingRestarts' | 'NothingKnownReleased' | 'NoVolumeTotal' | 'OneShot';

export interface SeriesProgressDto {
  trackers: SeriesTrackersDto;
  reach?: SeriesReachDto | null;
  missingVolumes: number;
  missingChapters: number;
  releaseKnown: boolean;
  /** Official volumes in the preferred language held only as chapters (an upgrade, never missing; the first 50). */
  upgradeVolumes: number[];
  upgradeCount: number;
  completion: SeriesCompletion;
  completionBasis?: CompletionBasis | null;
  completionTarget?: number | null;
  completionHeld?: number | null;
  completionInChapters?: boolean;
  /** 1.32.0: set when the series' volume list was completed from a Wikipedia page ("Volume list: MangaDex, completed from Wikipedia"). */
  listCredit?: ListCreditDto | null;
  /** 1.32.0: the Completion tab's answer (also behind the completion mark). */
  answer?: SeriesAnswer;
  answerReason?: SeriesAnswerReason;
}

// --- Official releases tab (1.30.0; the Completion tab since 1.32.0 - the contract keeps its names) ---

export type OfficialReleasesFilter = 'ToAct' | 'Upgrades' | 'Finished' | 'Complete' | 'All';

export interface OfficialReleaseRowDto {
  nodeId: string;
  displayName: string;
  libraryId: string;
  libraryName: string;
  coverUrl?: string | null;
  recordTitle: string;
  linkState: SeriesLinkState;
  progress: SeriesProgressDto;
}

export interface OfficialReleasesSummaryDto {
  series: number;
  upgrades: number;
  finishedNotHeld: number;
  completeCollections: number;
  /** 1.32.0: series per answer (with the Upgrades only switch applied, before the answer filter). */
  haveItAll?: number;
  finishedMissing?: number;
  upToDate?: number;
  missingSome?: number;
  cantTell?: number;
}

/** GET /admin/metadata/official-releases?library=&filter=&cursor=&limit=&basis=&answer=&upgrades= */
export interface OfficialReleasesPageDto {
  items: OfficialReleaseRowDto[];
  summary: OfficialReleasesSummaryDto;
  total: number;
  nextCursor?: string | null;
  language: string;
}

export type CoverMode = 'Automatic' | 'FilePinned' | 'Archive' | 'VolumeCover' | 'Crop';
export type CoverOptionKind = 'File' | 'CropLeft' | 'CropRight' | 'Archive';
export type VolumeCoverKind = 'Volume' | 'Main';
export type CoverCropSide = 'Left' | 'Right';
export type CompanionState = 'Auto' | 'Confirmed' | 'NotFound' | 'None' | 'Failed';

export interface CoverStateDto {
  mode: CoverMode;
  autoSource?: CardCoverSource | null;
  reason?: string | null;
  imageUrl?: string | null;
  recheckAt?: string | null;
}

export interface CoverOptionDto {
  kind: CoverOptionKind;
  archiveId?: string | null;
  imageUrl: string;
  label: string;
}

export interface WebCoverDto {
  id: string;
  kind: VolumeCoverKind;
  volume?: number | null;
  variant?: number;
  locale: string;
  /** false = choosing it downloads it (one request). */
  stored: boolean;
  imageUrl?: string | null;
}

export interface WebCoverGroupDto {
  volume?: number | null;
  covers: WebCoverDto[];
}

/** GET /nodes/{nodeId}/cover-options (admin, lane C). */
export interface CoverOptionsDto {
  nodeId: string;
  current: CoverStateDto;
  local: CoverOptionDto[];
  web: WebCoverGroupDto[];
  webAvailable: boolean;
  webUnavailableReason?: string | null;
}

/** PUT /nodes/{nodeId}/cover-choice (admin, lane C). */
export interface CoverChoiceRequest {
  mode: CoverMode;
  archiveId?: string | null;
  volumeCoverId?: string | null;
  cropSide?: CoverCropSide | null;
}

/** A companion record of the node's linked series (lane P): GET /admin/metadata/nodes/{nodeId}/companions. */
export interface CompanionDto {
  provider: string;
  providerName: string;
  siteUrl?: string | null;
  state: CompanionState;
  checkedAt?: string | null;
}

/** PUT /admin/metadata/nodes/{nodeId}/companions/mangadex (admin, lane P): a MangaDex title URL or UUID. */
export interface CompanionReferenceRequest {
  reference: string;
}

export interface VolumeListInfoDto {
  source: VolumeListSource;
  exactVolumes: number;
  estimatedVolumes: number;
  fetchedAt?: string | null;
}

/** The background volume-cover pass (lane P): GET /admin/metadata/volume-covers/status, DELETE /admin/metadata/volume-covers. */
export interface CoverPassStatusDto {
  seriesPending: number;
  coversListed: number;
  coversStored: number;
  waiting?: string | null;
}

// --- Move conflicts (1.31.0, admin): an item moved to another library while both copies had their own state ---

/** What differs between the old and the new copy. */
export type MoveConflictKind = 'Progress' | 'ReaderSettings' | 'SeriesLink';

export type MoveConflictState = 'Open' | 'Overwritten' | 'Kept';

/** Overwrite the new state with the old one, or keep the new one. */
export type MoveConflictResolution = 'Overwrite' | 'Keep';

export type MoveProgressState = 'Unread' | 'InProgress' | 'Completed';

/** One side (old or new) of a move conflict; only the fields of the conflict's kind are set. */
export interface MoveConflictSideDto {
  present: boolean;
  progress?: MoveProgressState | null;
  /** 1-based page. */
  page?: number | null;
  pageCount?: number | null;
  readerMode?: ReaderMode | null;
  otherReaderSettings?: boolean | null;
  linkState?: SeriesLinkState | null;
  recordTitle?: string | null;
  provider?: string | null;
  updatedAt?: string | null;
}

/** GET /admin/move-conflicts items. */
export interface MoveConflictDto {
  id: string;
  kind: MoveConflictKind;
  state: MoveConflictState;
  /** Null for a series link (an admin row). */
  userId?: string | null;
  userName?: string | null;
  /** The new copy (live). */
  nodeId: string;
  title: string;
  isFolder: boolean;
  parentTitle?: string | null;
  libraryId: string;
  libraryName: string;
  /** Where the old copy was. */
  fromTitle: string;
  fromLibraryName: string;
  old: MoveConflictSideDto;
  new: MoveConflictSideDto;
  createdAt: string;
  resolvedAt?: string | null;
}

export interface MoveConflictPageDto {
  items: MoveConflictDto[];
  openCount: number;
  nextCursor?: string | null;
}

/** GET /admin/move-conflicts/count (the admin link badge). */
export interface MoveConflictCountDto {
  open: number;
}

/** POST /admin/move-conflicts/resolve: the listed ids, or every open conflict (`all`, optionally of one `kind`). */
export interface MoveConflictResolveRequest {
  ids?: string[] | null;
  all?: boolean;
  kind?: MoveConflictKind | null;
  resolution: MoveConflictResolution;
}

export interface MoveConflictResolveResultDto {
  resolved: number;
  skipped: number;
}

// --- Empty trash + Clean bundles (1.31.0): /admin/trash ---

/** GET /admin/trash: settings, what "Empty trash now" removes per library (with holds), what "Clean bundles" removes, last runs. */
export interface TrashOverviewDto {
  settings: TrashSettingsDto;
  /** Tombstones from before this time are past the window. */
  windowStart: string;
  libraries: TrashLibraryDto[];
  /** What "Empty trash now" for all libraries removes (libraries without a hold). */
  total: TrashCountsDto;
  bundles: TrashFilesDto;
  lastEmpty?: TrashRunDto | null;
  lastBundleClean?: TrashRunDto | null;
}

export interface TrashSettingsDto {
  /** "Turn automatic cleaning on": once a day at automaticHour (server time). Off by default. */
  automaticCleaning: boolean;
  /** The move window, which is also the trash retention, in days. */
  retentionDays: number;
  /** Daily, Weekly, Monthly, Quarterly, Yearly as days. */
  allowedRetentionDays: number[];
  automaticHour: number;
}

/** PUT /admin/trash/settings: a missing field keeps its value. */
export interface UpdateTrashSettingsRequest {
  automaticCleaning?: boolean | null;
  retentionDays?: number | null;
  /** The server-local hour (0-23) of the daily automatic run (1.31.0). */
  automaticHour?: number | null;
}

/** Why a library keeps its trash this pass. */
export type TrashHold = 'scan_running' | 'root_unavailable' | 'burst';

export interface TrashLibraryDto {
  libraryId: string;
  name: string;
  /** What emptying this library removes now (when held: what releasing the hold would remove). */
  eligible: TrashCountsDto;
  /** Removed items still inside the window, or kept by move recognition. */
  waiting: number;
  libraryNodes: number;
  hold?: TrashHold | null;
  holdReleasable: boolean;
}

export interface TrashCountsDto {
  nodes: number;
  archives: number;
  folders: number;
  /** Reading progress, read marks, bookmarks, reader overrides and favorites, all users. */
  userStateRows: number;
  files: number;
  bytes: number;
}

export interface TrashFilesDto {
  files: number;
  bytes: number;
}

export interface TrashRunDto {
  at: string;
  automatic: boolean;
  /** Nodes removed (Empty trash) or files removed (Clean bundles). */
  count: number;
  bytes: number;
  heldLibraries: number;
}

/** POST /admin/trash/empty: every library without a hold, or one library (releaseHold empties it although held). */
export interface EmptyTrashRequest {
  libraryId?: string | null;
  releaseHold?: boolean;
}

export interface EmptyTrashResultDto {
  removed: TrashCountsDto;
  held: TrashHeldLibraryDto[];
}

export interface TrashHeldLibraryDto {
  libraryId: string;
  hold: TrashHold;
}

// --- Wikipedia companion (1.32.0) ---

/** Where a volume list was read from: a small source line with a link. */
export interface ListCreditDto {
  name: string;
  url: string;
  title: string;
}

export type WikipediaListState = 'Found' | 'NotFound' | 'Rejected' | 'None' | 'Failed';
export type WikipediaListMethod = 'Wikidata' | 'Title' | 'Admin';

export interface WikipediaPageDto {
  title: string;
  url: string;
  revision?: number;
}

export interface WikipediaVolumeDto {
  volume: string;
  /** The earliest English release date as the page states it: 2026-12-08, 2002-02 or 2002. May be in the future (announced). */
  englishDate?: string | null;
  /** The first valid English ISBN, digits only. */
  englishIsbn?: string | null;
}

/** The Wikipedia companion of a linked series (GET /admin/metadata/nodes/{nodeId}/wikipedia). */
export interface WikipediaListDto {
  state: WikipediaListState;
  method: WikipediaListMethod;
  /** A sanitized code for why the last list was refused or the last attempt failed (no_page, no_list, disagrees_with_mangadex ...). */
  code?: string | null;
  adminTitle?: string | null;
  pages?: WikipediaPageDto[];
  volumes?: number;
  details?: WikipediaVolumeDto[];
  checkedAt?: string | null;
  nextCheckAt?: string | null;
}

export interface WikipediaPageRequest {
  page: string;
}

/** A folder's cover preference (1.32.0): web covers when available, or the file's own cover. No row = inherit. */
export type FolderCoverPreference = 'Web' | 'File';

/** GET/PUT/DELETE /admin/folders/{nodeId}/cover-preference (admin). */
export interface FolderCoverPreferenceDto {
  nodeId: string;
  /** The folder's own preference; null = it inherits. */
  preference?: FolderCoverPreference | null;
  /** What the works below the folder get: its own value, else `inherited`. */
  effective: FolderCoverPreference;
  /** What the folder gets when it inherits: the nearest ancestor's value, else the library's "Show saved web covers" switch. */
  inherited: FolderCoverPreference;
  /** The ancestor folder `inherited` comes from; null = the library's switch. */
  inheritedSourceNodeId?: string | null;
  inheritedSourceName?: string | null;
}

/** PUT /admin/folders/{nodeId}/cover-preference. DELETE clears the folder's own value. */
export interface SetFolderCoverPreferenceRequest {
  preference: FolderCoverPreference;
}

// --- Scheduled jobs (1.32.0): /admin/jobs ---

export type ScheduledJobKind = 'daily' | 'weekly' | 'interval' | 'continuous' | 'startup' | 'onDemand';

/** GET /admin/jobs: every job MangaPixer runs on its own, on the server's clock. */
export interface ScheduledJobsDto {
  serverTime: string;
  /** IANA zone id of the server (every hour is in this zone). */
  timeZone: string;
  utcOffsetMinutes: number;
  jobs: ScheduledJobDto[];
  refresh: RefreshCadenceDto;
}

export interface ScheduledJobDto {
  key: string;
  /** The library of a `library-scan` row. */
  libraryId?: string | null;
  libraryName?: string | null;
  kind: ScheduledJobKind;
  enabled: boolean;
  configurable: boolean;
  managedByConfig?: boolean;
  hour?: number | null;
  defaultHour?: number | null;
  weekday?: number | null;
  intervalHours?: number | null;
  scanSchedule?: string | null;
  lastStartedAt?: string | null;
  lastFinishedAt?: string | null;
  lastOutcome?: string | null;
  lastDetail?: string | null;
  nextRunAt?: string | null;
  waitingCode?: string | null;
  running?: boolean;
}

export interface RefreshCadenceDto {
  ongoingDays: number;
  finishedDays: number;
  followPace: boolean;
  allowedOngoingDays: number[];
  allowedFinishedDays: number[];
  usedToday: number;
  overdue: number;
  byDays: RefreshCadenceCountDto[];
}

export interface RefreshCadenceCountDto {
  days: number;
  count: number;
}

/** PUT /admin/jobs/{key}: the hour of a daily job; null = its default (backups: any time). */
export interface UpdateJobScheduleRequest {
  hour?: number | null;
}

/** PUT /admin/jobs/metadata-refresh/cadence: a missing field keeps its value. */
export interface UpdateRefreshCadenceRequest {
  ongoingDays?: number | null;
  finishedDays?: number | null;
  followPace?: boolean | null;
}

/** GET /admin/jobs/metadata-refresh/series/{nodeId}: one linked series' refresh cadence (admin only). */
export interface SeriesRefreshCadenceDto {
  days: number;
  reason: 'finished' | 'choice' | 'pace' | 'paused';
  volumeIntervalDays?: number | null;
  fetchedAt: string;
  nextCheckAt: string;
}

// --- Metadata export (1.33.0, for MangaList): GET /api/v1/export/*. Not used by the web client; mirrored for the contract check. ---

export interface ExportErrorDto {
  error: string;
}

export interface ExportLibrariesDto {
  schemaVersion: number;
  serverTime: string;
  libraries: ExportLibraryDto[];
}

export interface ExportLibraryDto {
  id: string;
  displayName: string;
  kind?: string | null;
  folderCount?: number | null;
  itemCount?: number | null;
  lastScanAt?: string | null;
}

export interface ExportLibraryRefDto {
  id: string;
  displayName: string;
  kind?: string | null;
}

export interface ExportMetadataPageDto {
  schemaVersion: number;
  serverTime: string;
  library: ExportLibraryRefDto;
  items: ExportItemDto[];
  removed: ExportRemovalDto[];
  nextCursor?: string | null;
}

export interface ExportRemovalDto {
  nodeId: string;
  reason: 'nodeGone' | 'linkCleared' | 'movedToOtherLibrary';
  at: string;
}

export interface ExportItemDto {
  nodeId: string;
  nodeKind: 'folder' | 'archive';
  carriedFrom?: string | null;
  trail: string[];
  updatedAt: string;
  link: ExportLinkDto;
  record?: ExportRecordDto | null;
  companions: ExportCompanionsDto;
  officialLinks: ExportOfficialLinkDto[];
  volumes?: ExportVolumesDto | null;
  completion?: ExportCompletionDto | null;
  refresh?: ExportRefreshDto | null;
}

export interface ExportLinkDto {
  state: 'Confirmed' | 'Auto' | 'NeedsReview' | 'DontMatch';
  method?: string | null;
  score?: number | null;
  updatedAt: string;
}

export interface ExportRecordDto {
  provider: string;
  externalId: string;
  siteUrl?: string | null;
  title: string;
  altTitles: string[];
  type?: string | null;
  originStatus?: string | null;
  originVolumes?: number | null;
  latestChapter?: string | null;
  totalChapters?: number | null;
  statusText?: string | null;
  licensedEn?: boolean | null;
  translationComplete?: boolean | null;
  completedInOrigin?: boolean | null;
  englishPublishers: ExportPublisherDto[];
  fetchedAt: string;
}

export interface ExportPublisherDto {
  name: string;
  volumes?: number | null;
  chapters?: number | null;
  status?: string | null;
  omnibus: boolean;
}

export interface ExportCompanionsDto {
  mangadex?: string | null;
  anilist?: ExportAniListDto | null;
}

export interface ExportAniListDto {
  id: number;
  chapters?: number | null;
  volumes?: number | null;
}

export interface ExportOfficialLinkDto {
  kind: 'publisher' | 'store';
  label: string;
  url: string;
  source: 'mangadex';
}

export interface ExportVolumesDto {
  source: 'mangadex' | 'wikipedia' | 'merged';
  fetchedAt?: string | null;
  items: ExportVolumeDto[];
}

export interface ExportVolumeDto {
  volume: string;
  title?: string | null;
  chapters?: ExportChapterRangeDto | null;
  englishDate?: string | null;
  englishDateKind?: 'released' | 'announced' | null;
  isbn?: string | null;
  sources: string[];
}

export interface ExportChapterRangeDto {
  from: string;
  to: string;
}

export interface ExportCompletionDto {
  answer: 'CantTell' | 'HaveItAll' | 'FinishedMissing' | 'UpToDate' | 'MissingSome';
  reason: string;
  upgradeAvailable: boolean;
  upgradeVolumes: number[];
  computedAt: string;
  basedOnScanAt?: string | null;
}

export interface ExportRefreshDto {
  lastFetchedAt: string;
  nextDueAt: string;
  intervalDays: number;
}

// --- Personal access tokens + export ping (1.33.0) ---

/** GET /admin/tokens: one personal access token. Never carries the secret. */
export interface ApiTokenDto {
  id: string;
  name: string;
  /** The first characters of the token (e.g. "mpx_Ab3x"), to tell tokens apart. */
  prefix: string;
  scopes: string[];
  ownerUserName: string;
  createdAt: string;
  /** Null = never expires. */
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  revokedAt?: string | null;
  status: 'active' | 'expired' | 'revoked' | 'ownerInactive';
}

/** POST /admin/tokens. expiresInDays: 30, 90 or 365, or null for never (always sent). */
export interface CreateApiTokenRequest {
  name: string;
  expiresInDays: number | null;
}

/** The answer to POST /admin/tokens: the secret is shown this one time only. */
export interface CreateApiTokenResponse {
  token: ApiTokenDto;
  secret: string;
}

/** GET /export/ping: which credential was accepted, and the server's clock. */
export interface ExportPingDto {
  ok: boolean;
  serverTime: string;
  auth: 'token' | 'cookie';
}
