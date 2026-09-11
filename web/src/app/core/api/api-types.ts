/**
 * API DTO types matching the .NET contracts in MangaPlex.Core/Api/ApiDtos.cs.
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

/** One bucket of the per-library jump index (1.4.0 Lane E). */
export interface JumpIndexBucketDto {
  label: string;
  count: number;
  firstCursor: string | null;
}

/** Per-library A–Z/script rail (1.4.0 Lane E). */
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
}

export interface ContinueReadingEntry {
  itemId: string;
  /** Opaque public ID of the item's library (1.4.0 Lane B). Enables sidebar grouping. */
  libraryId: string;
  /** Display name of the item's library (1.4.0 Lane B). */
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
export type LibrarySortOrder = 'name' | 'recentlyAdded' | 'recentlyRead';

/**
 * Browse sort direction (1.5.0). Optional/tolerant like the other library-view
 * preference fields: absent or unrecognized means "use the sort-specific
 * default" (name -> asc; recentlyAdded/recentlyRead -> desc), resolved by the
 * server (`CatalogController.ParseDirection`) and mirrored client-side in
 * LibraryBrowseComponent so the UI shows the right toggle state before the
 * first preferences round-trip completes.
 */
export type LibrarySortDirection = 'asc' | 'desc';

/** Per-user library browse presentation preferences (1.2.0). Strings for tolerance. */
export interface LibraryViewPreferencesDto {
  viewMode: string;
  density: string;
  sort: string;
  /** Optional (1.5.0): omitted/unrecognized falls back to the sort-specific default. */
  direction?: string;
  /**
   * Optional (1.6.0): stringified min card column width in px for the merged Card
   * view (e.g. "150"). Omitted/unrecognized → the frontend derives an initial size
   * from the legacy viewMode + density, so pre-1.6.0 stored prefs keep their size.
   */
  cardSize?: string;
}

// --- YACReader progress import (1.2.0, admin-only) ---

/** Whether a YACReader library was detected inside a MangaPlex library's root. */
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

// --- System info (post-1.3.0 lane D) ---

/** Read-only product version info from GET /api/v1/system/info. */
export interface SystemInfoDto {
  version: string;
}
