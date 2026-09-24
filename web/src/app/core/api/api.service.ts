import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { BYPASS_INCOGNITO } from '../incognito/incognito.interceptor';
import {
  ActivateAccountRequest,
  AddBookmarkRequest,
  AddBookmarkResult,
  AnalyticsOverviewDto,
  AnalyticsUserRowDto,
  ApiError,
  AdminUserDto,
  AuthUserDto,
  BookmarkDto,
  CatalogNodeDto,
  ChangePasswordRequest,
  ContinueReadingEntry,
  CreateUserRequest,
  CreateUserResponse,
  EffectiveReaderModeDto,
  ReadMarkDto,
  BulkReadMarkResultDto,
  LibraryViewPreferencesDto,
  LibrarySortOrder,
  LibrarySortDirection,
  LibraryReadStateFilter,
  YacReaderDetectDto,
  YacReaderImportRequest,
  YacReaderImportPreviewDto,
  YacReaderImportResultDto,
  ReaderMode,
  CsrfTokenDto,
  DirectoryListingDto,
  ItemManifest,
  SetSpreadLayoutRequest,
  SpreadLayoutDto,
  ItemReadiness,
  JumpIndexDto,
  LibraryDto,
  LibraryScanSchedule,
  LogCategoryOverride,
  LogLevelDto,
  PrivateLibrariesDto,
  HomeLibraryVisibility,
  RotatingBackupStatusDto,
  RotatingBackupListDto,
  RestoreFromBackupRequest,
  RestoreStageResponseDto,
  AuditTrailPageDto,
  LoginRequest,
  PageResponse,
  ProgressUpdateResult,
  ReadingProgressDto,
  RecentChaptersDto,
  RegisterLibraryRequest,
  ReissueActivationResponse,
  ResetPasswordResponse,
  ScanRunDto,
  ScanTriggeredDto,
  ScanAllResultDto,
  SetLibraryScanScheduleRequest,
  SetPrivateLibrariesRequest,
  ThumbnailRegenerateResponse,
  SearchResultsDto,
  SetupRequest,
  SetupStatusDto,
  SystemInfoDto,
  UpdateCheckSettingsRequest,
  UpdateCheckStatusDto,
  BackupSettingsDto,
  BackupSettingsUpdateResultDto,
  BackupSnapshotMoveStatusDto,
  UpdateBackupSettingsRequest,
  UpdateLibraryRequest,
  UpdateLogLevelRequest,
  UpdateProgressRequest,
  UpdateUserRequest,
  UserGrantsDto,
  UserPreferencesDto,
} from './api-types';

/**
 * HttpClient-based API service. All requests go through /api/v1 prefix.
 * XSRF token is handled by the XsrfInterceptor.
 * No tokens are stored in localStorage. Session uses httpOnly cookies.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  // --- Auth ---

  getCsrfToken(): Observable<CsrfTokenDto> {
    return this.get<CsrfTokenDto>('/auth/csrf');
  }

  getSetupStatus(): Observable<SetupStatusDto> {
    return this.get<SetupStatusDto>('/auth/setup-status');
  }

  setup(request: SetupRequest): Observable<AuthUserDto> {
    return this.post<AuthUserDto>('/auth/setup', request);
  }

  login(request: LoginRequest): Observable<AuthUserDto> {
    return this.post<AuthUserDto>('/auth/login', request);
  }

  logout(): Observable<void> {
    return this.post<void>('/auth/logout', {});
  }

  getCurrentUser(): Observable<AuthUserDto> {
    return this.get<AuthUserDto>('/auth/me');
  }

  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.post<void>('/auth/change-password', request);
  }

  // --- Catalog ---

  getLibraries(): Observable<LibraryDto[]> {
    return this.get<LibraryDto[]>('/libraries');
  }

  /**
   * The full set of libraries the user can manage, ignoring the session's
   * current Incognito toggle (1.4.0). Use for the Private-libraries settings
   * list and the Administration page — management surfaces must keep showing an
   * already-Private library (to un-mark or administer it), unlike the discovery
   * surfaces that hide it under Incognito.
   */
  getAllLibraries(): Observable<LibraryDto[]> {
    return this.get<LibraryDto[]>('/libraries', undefined, new HttpContext().set(BYPASS_INCOGNITO, true));
  }

  browseLibrary(
    libraryId: string,
    parentId: string | null,
    cursor: string | null = null,
    pageSize = 50,
    sort: LibrarySortOrder | null = null,
    direction: LibrarySortDirection | null = null,
    readState: LibraryReadStateFilter | null = null,
    hideEmpty = false,
    before: string | null = null,
    favoritesOnly = false,
  ): Observable<PageResponse<CatalogNodeDto>> {
    let params = new HttpParams().set('pageSize', pageSize.toString());
    if (cursor) params = params.set('cursor', cursor);
    // Backward page (1.11.0): fetch the page BEFORE `before` (upward scroll after a jump).
    // Mutually exclusive with `cursor` at call sites (forward vs backward paging).
    if (before) params = params.set('before', before);
    if (parentId) params = params.set('parentId', parentId);
    // Omitted → the server uses the caller's stored LibrarySort/direction preference.
    if (sort) params = params.set('sort', sort);
    if (direction) params = params.set('direction', direction);
    // Read-state filter (1.10.0). Omitted or 'all' → no filter (server default).
    if (readState && readState !== 'all') params = params.set('readState', readState);
    // Hide-empty-folders filter (1.11.0). Omitted/false → folders with no archive
    // descendants are kept (server default). Composes with the read-state filter.
    if (hideEmpty) params = params.set('hideEmpty', 'true');
    // Favorites-only filter (1.21.0). Omitted/false → no filter. Composes with the
    // read-state / hide-empty filters and keyset paging server-side.
    if (favoritesOnly) params = params.set('favoritesOnly', 'true');
    return this.get<PageResponse<CatalogNodeDto>>(
      `/libraries/${libraryId}/browse`,
      params,
    );
  }

  getNode(nodeId: string): Observable<CatalogNodeDto> {
    return this.get<CatalogNodeDto>(`/nodes/${nodeId}`);
  }

  /** Per-library A–Z/script jump index. */
  getJumpIndex(libraryId: string): Observable<JumpIndexDto> {
    return this.get<JumpIndexDto>(`/libraries/${libraryId}/jump-index`);
  }

  getBreadcrumbs(nodeId: string): Observable<{ nodeId: string; trail: { id: string; displayName: string }[] }> {
    return this.get(`/nodes/${nodeId}/breadcrumbs`);
  }

  getNeighbors(nodeId: string): Observable<{ previous: { id: string; displayName: string } | null; next: { id: string; displayName: string } | null }> {
    return this.get(`/nodes/${nodeId}/neighbors`);
  }

  search(query: string, libraryId?: string): Observable<SearchResultsDto> {
    let params = new HttpParams().set('q', query);
    if (libraryId) params = params.set('libraryId', libraryId);
    return this.get<SearchResultsDto>('/search', params);
  }

  /**
   * Home "New chapters": the most-recently-added archives
   * grouped by visible library, newest first, capped per library. Respects
   * Incognito/Private visibility server-side (the X-Incognito header is set
   * by the incognito interceptor like every other discovery call).
   *
   * `readState` (1.17.0) optionally restricts stacks to Reading/Read/Unread by
   * their top-level rollup, mirroring the library browse filter. Reuses
   * `LibraryReadStateFilter` (same wire values) rather than a separate type — a
   * transient toolbar control, not a persisted preference; 'all' sends no param
   * (server default = unfiltered).
   */
  getRecentChapters(perLibrary = 12, readState: LibraryReadStateFilter = 'all'): Observable<RecentChaptersDto> {
    let params = new HttpParams().set('perLibrary', perLibrary.toString());
    if (readState !== 'all') params = params.set('readState', readState);
    return this.get<RecentChaptersDto>('/home/recent-chapters', params);
  }

  // --- Reading ---

  getProgress(itemId: string): Observable<ReadingProgressDto> {
    return this.get<ReadingProgressDto>(`/reading/progress/${itemId}`);
  }

  updateProgress(
    itemId: string,
    request: UpdateProgressRequest,
    revision = 0,
  ): Observable<ProgressUpdateResult> {
    // Optimistic concurrency (D32): If-Match the known revision, or If-None-Match:*
    // for the first write against an unread item (revision 0).
    const headers = revision > 0
      ? new HttpHeaders({ 'If-Match': `"${revision}"` })
      : new HttpHeaders({ 'If-None-Match': '*' });
    return this.http
      .put<ProgressUpdateResult>(`${this.baseUrl}/reading/progress/${itemId}`, request, {
        headers,
        withCredentials: true,
      })
      .pipe(catchError(this.handleError));
  }

  resetProgress(itemId: string): Observable<void> {
    return this.delete<void>(`/reading/progress/${itemId}`);
  }

  getContinueReading(limit = 20): Observable<ContinueReadingEntry[]> {
    const params = new HttpParams().set('limit', limit.toString());
    return this.get<ContinueReadingEntry[]>('/reading/continue', params);
  }

  /** Remove an item from the continue-reading strip (1.2.0), without marking it read. */
  dismissContinueReading(itemId: string): Observable<void> {
    return this.delete<void>(`/reading/continue/${itemId}`);
  }

  /** Continue-reading entries scoped to a single library (1.4.0 sidebar grouping). */
  getContinueReadingByLibrary(libraryId: string, limit = 20): Observable<ContinueReadingEntry[]> {
    const params = new HttpParams().set('limit', limit.toString());
    return this.get<ContinueReadingEntry[]>(`/reading/continue/by-library/${libraryId}`, params);
  }

  /** The current user's Private library designations (1.4.0). */
  getPrivateLibraries(): Observable<PrivateLibrariesDto> {
    return this.get<PrivateLibrariesDto>('/reading/private-libraries');
  }

  /** Replaces the current user's Private library set (1.4.0, replacement semantics). */
  setPrivateLibraries(libraryIds: string[]): Observable<void> {
    return this.put<void>('/reading/private-libraries', { libraryIds } as SetPrivateLibrariesRequest);
  }

  /** The current user's home-excluded libraries (1.12.0): hidden from the home "New chapters" surface. */
  getHomeLibraries(): Observable<HomeLibraryVisibility> {
    return this.get<HomeLibraryVisibility>('/reading/home-libraries');
  }

  /** Replaces the current user's home-excluded library set (1.12.0, replacement semantics). */
  putHomeLibraries(excludedLibraryIds: string[]): Observable<void> {
    return this.put<void>('/reading/home-libraries', { excludedLibraryIds } as HomeLibraryVisibility);
  }

  /** Per-user library browse presentation preferences (1.2.0). */
  getLibraryPreferences(): Observable<LibraryViewPreferencesDto> {
    return this.get<LibraryViewPreferencesDto>('/reading/library-preferences');
  }

  setLibraryPreferences(prefs: LibraryViewPreferencesDto): Observable<void> {
    return this.put<void>('/reading/library-preferences', prefs);
  }

  // --- YACReader import (1.2.0, admin-only) ---

  detectYacReader(libraryId: string): Observable<YacReaderDetectDto> {
    const params = new HttpParams().set('libraryId', libraryId);
    return this.get<YacReaderDetectDto>('/admin/import/yacreader/detect', params);
  }

  previewYacReaderImport(request: YacReaderImportRequest): Observable<YacReaderImportPreviewDto> {
    return this.post<YacReaderImportPreviewDto>('/admin/import/yacreader/preview', request);
  }

  applyYacReaderImport(request: YacReaderImportRequest): Observable<YacReaderImportResultDto> {
    return this.post<YacReaderImportResultDto>('/admin/import/yacreader/apply', request);
  }

  getPreferences(): Observable<UserPreferencesDto> {
    return this.get<UserPreferencesDto>('/reading/preferences');
  }

  setPreferences(request: UserPreferencesDto): Observable<void> {
    return this.put<void>('/reading/preferences', request);
  }

  /** Resolved effective default reader mode for an item (1.2.0). */
  getEffectiveReaderMode(itemId: string): Observable<EffectiveReaderModeDto> {
    return this.get<EffectiveReaderModeDto>(`/reading/${itemId}/effective-mode`);
  }

  // --- Sticky read-marks (1.2.0) ---

  getReadMark(itemId: string): Observable<ReadMarkDto> {
    return this.get<ReadMarkDto>(`/reading/${itemId}/read`);
  }

  /** Marks an item read (sticky), or clears it, without opening it. */
  setItemRead(itemId: string, read: boolean): Observable<ReadMarkDto> {
    return read
      ? this.put<ReadMarkDto>(`/reading/${itemId}/read`, {})
      : this.delete<ReadMarkDto>(`/reading/${itemId}/read`);
  }

  /** Bulk set/clear read-marks over every descendant archive of a folder. */
  setFolderRead(nodeId: string, read: boolean): Observable<BulkReadMarkResultDto> {
    return read
      ? this.put<BulkReadMarkResultDto>(`/reading/folders/${nodeId}/read`, {})
      : this.delete<BulkReadMarkResultDto>(`/reading/folders/${nodeId}/read`);
  }

  // --- Bookmarks (1.17.0) ---

  /** Per-page bookmarks for an item, ordered by page. */
  getBookmarks(itemId: string): Observable<BookmarkDto[]> {
    return this.get<BookmarkDto[]>(`/reading/${itemId}/bookmarks`);
  }

  addBookmark(itemId: string, request: AddBookmarkRequest): Observable<AddBookmarkResult> {
    return this.post<AddBookmarkResult>(`/reading/${itemId}/bookmarks`, request);
  }

  /** NOTE: not nested under itemId — the server addresses a bookmark by its own id. */
  removeBookmark(bookmarkId: string): Observable<void> {
    return this.delete<void>(`/reading/bookmarks/${bookmarkId}`);
  }

  // --- Admin ---

  registerLibrary(request: RegisterLibraryRequest): Observable<LibraryDto> {
    return this.post<LibraryDto>('/admin/libraries', request);
  }

  browseLibraryPaths(path?: string): Observable<DirectoryListingDto> {
    let params = new HttpParams();
    if (path) params = params.set('path', path);
    return this.get<DirectoryListingDto>('/admin/libraries/browse', params);
  }

  getLibrary(id: string): Observable<LibraryDto> {
    return this.get<LibraryDto>(`/admin/libraries/${id}`);
  }

  updateLibrary(id: string, request: UpdateLibraryRequest): Observable<LibraryDto> {
    return this.post<LibraryDto>(`/admin/libraries/${id}/update`, request);
  }

  unregisterLibrary(id: string): Observable<void> {
    return this.delete<void>(`/admin/libraries/${id}`);
  }

  // Global default reader mode (1.2.0) — admin-set library/folder defaults.
  setLibraryReaderDefault(libraryId: string, mode: ReaderMode): Observable<LibraryDto> {
    return this.put<LibraryDto>(`/admin/libraries/${libraryId}/reader-default`, { readerMode: mode });
  }

  clearLibraryReaderDefault(libraryId: string): Observable<LibraryDto> {
    return this.delete<LibraryDto>(`/admin/libraries/${libraryId}/reader-default`);
  }

  setFolderReaderDefault(nodeId: string, mode: ReaderMode): Observable<void> {
    return this.put<void>(`/admin/folders/${nodeId}/reader-default`, { readerMode: mode });
  }

  clearFolderReaderDefault(nodeId: string): Observable<void> {
    return this.delete<void>(`/admin/folders/${nodeId}/reader-default`);
  }

  // Library icon (1.22.0) — admin-picked icon name, or null to clear back to the default.
  setLibraryIcon(libraryId: string, icon: string | null): Observable<LibraryDto> {
    return this.put<LibraryDto>(`/admin/libraries/${libraryId}/icon`, { icon });
  }

  // Library scan schedule (1.23.0) — automatic scan preset, or null to clear back to the daily default.
  setLibraryScanSchedule(libraryId: string, scanSchedule: LibraryScanSchedule | null): Observable<LibraryDto> {
    const body: SetLibraryScanScheduleRequest = { scanSchedule };
    return this.put<LibraryDto>(`/admin/libraries/${libraryId}/scan-schedule`, body);
  }

  triggerScan(libraryId: string): Observable<ScanTriggeredDto> {
    return this.post<ScanTriggeredDto>(`/admin/libraries/${libraryId}/scan`, {});
  }

  /**
   * Trigger a scan for every registered library at once (1.8.0). Libraries
   * already scanning are skipped; the response reports started/skipped counts.
   */
  scanAllLibraries(): Observable<ScanAllResultDto> {
    return this.post<ScanAllResultDto>(`/admin/libraries/scan-all`, {});
  }

  cancelScan(scanRunId: string): Observable<void> {
    return this.post<void>(`/admin/scans/${scanRunId}/cancel`, {});
  }

  getScanHistory(libraryId: string): Observable<ScanRunDto[]> {
    return this.get<ScanRunDto[]>(`/admin/libraries/${libraryId}/scans`);
  }

  /** Enqueue durable thumbnail (re)generation for items lacking a current one (1.2.0). */
  regenerateThumbnails(libraryId: string): Observable<ThumbnailRegenerateResponse> {
    return this.post<ThumbnailRegenerateResponse>(`/admin/libraries/${libraryId}/thumbnails/regenerate`, {});
  }

  listUsers(): Observable<AdminUserDto[]> {
    return this.get<AdminUserDto[]>('/admin/users');
  }

  createUser(request: CreateUserRequest): Observable<CreateUserResponse> {
    return this.post<CreateUserResponse>('/admin/users', request);
  }

  activateAccount(request: ActivateAccountRequest): Observable<AuthUserDto> {
    return this.post<AuthUserDto>('/auth/activate', request);
  }

  getUser(id: string): Observable<AdminUserDto> {
    return this.get<AdminUserDto>(`/admin/users/${id}`);
  }

  updateUser(id: string, request: UpdateUserRequest): Observable<AdminUserDto> {
    return this.post<AdminUserDto>(`/admin/users/${id}/update`, request);
  }

  resetUserPassword(id: string): Observable<ResetPasswordResponse> {
    return this.post<ResetPasswordResponse>(`/admin/users/${id}/reset-password`, {});
  }

  deleteUser(id: string): Observable<void> {
    return this.delete<void>(`/admin/users/${id}`);
  }

  reissueActivation(id: string): Observable<ReissueActivationResponse> {
    return this.post<ReissueActivationResponse>(`/admin/users/${id}/reissue-activation`, {});
  }

  revokeUserSessions(id: string): Observable<void> {
    return this.delete<void>(`/admin/users/${id}/sessions`);
  }

  getUserGrants(userId: string): Observable<UserGrantsDto> {
    return this.get<UserGrantsDto>(`/admin/users/${userId}/grants`);
  }

  grantAccess(userId: string, libraryId: string): Observable<void> {
    return this.put<void>(`/admin/users/${userId}/grants/${libraryId}`, {});
  }

  revokeAccess(userId: string, libraryId: string): Observable<void> {
    return this.delete<void>(`/admin/users/${userId}/grants/${libraryId}`);
  }

  // --- Operations / Diagnostics ---

  getLoggingLevel(): Observable<LogLevelDto> {
    return this.get<LogLevelDto>('/operations/logging');
  }

  setLoggingLevel(level: string): Observable<LogLevelDto> {
    return this.put<LogLevelDto>('/operations/logging', { level } as UpdateLogLevelRequest);
  }

  setLoggingCategories(categories: LogCategoryOverride[]): Observable<LogLevelDto> {
    return this.put<LogLevelDto>('/operations/logging', { categories } as UpdateLogLevelRequest);
  }

  getRotatingBackupStatus(): Observable<RotatingBackupStatusDto> {
    return this.get<RotatingBackupStatusDto>('/operations/backups');
  }

  runRotatingBackupNow(): Observable<RotatingBackupStatusDto> {
    return this.post<RotatingBackupStatusDto>('/operations/backups/rotating', {});
  }

  /** Lists the on-disk rotating snapshots available to restore from. */
  listRotatingBackups(): Observable<RotatingBackupListDto> {
    return this.get<RotatingBackupListDto>('/operations/backups/files');
  }

  /** Stages a restore from a chosen on-disk rotating snapshot (apply on restart). */
  restoreFromBackup(fileName: string): Observable<RestoreStageResponseDto> {
    return this.post<RestoreStageResponseDto>('/operations/backups/restore', { fileName } as RestoreFromBackupRequest);
  }

  /** Stages a restore from an uploaded backup file (multipart; apply on restart). */
  restoreFromUpload(file: File): Observable<RestoreStageResponseDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.post<RestoreStageResponseDto>('/operations/restore', form);
  }

  /** Effective backup settings with their sources (admin, 1.22.0). */
  getBackupSettings(): Observable<BackupSettingsDto> {
    return this.get<BackupSettingsDto>('/operations/backups/settings');
  }

  /**
   * Updates the backup settings (partial). A request with `location` needs the
   * admin's `currentPassword`; `validateOnly` runs every check and saves nothing.
   */
  updateBackupSettings(request: UpdateBackupSettingsRequest): Observable<BackupSettingsUpdateResultDto> {
    return this.put<BackupSettingsUpdateResultDto>('/operations/backups/settings', request);
  }

  /** Progress / result of the background snapshot move after a location change (admin, 1.23.0). */
  getBackupSnapshotMove(): Observable<BackupSnapshotMoveStatusDto> {
    return this.get<BackupSnapshotMoveStatusDto>('/operations/backups/move');
  }

  /** Update Checker status (admin). Pass force=true for the "Check now" action. */
  getUpdateCheck(force = false): Observable<UpdateCheckStatusDto> {
    const params = force ? new HttpParams().set('force', 'true') : undefined;
    return this.get<UpdateCheckStatusDto>('/operations/update-check', params);
  }

  /** Sets the Update Checker opt-in (admin). Enabling triggers an immediate check. */
  setUpdateCheckEnabled(enabled: boolean): Observable<UpdateCheckStatusDto> {
    return this.put<UpdateCheckStatusDto>(
      '/operations/update-check/settings',
      { enabled } as UpdateCheckSettingsRequest);
  }

  /** Admin analytics overview: library/content/processing/engagement counts. */
  getAnalyticsOverview(): Observable<AnalyticsOverviewDto> {
    return this.get<AnalyticsOverviewDto>('/admin/analytics/overview');
  }

  /** Admin analytics per-user table (the admin's own row included). */
  getAnalyticsUsers(): Observable<AnalyticsUserRowDto[]> {
    return this.get<AnalyticsUserRowDto[]>('/admin/analytics/users');
  }

  /** One page of the administrative audit trail (newest first). */
  getAuditTrail(page: number, pageSize: number): Observable<AuditTrailPageDto> {
    return this.get<AuditTrailPageDto>('/admin/audit', new HttpParams()
      .set('page', String(page))
      .set('pageSize', String(pageSize)));
  }

  // --- System info ---

  /** Read-only product version (unauthenticated; shown in the app footer). */
  getSystemInfo(): Observable<SystemInfoDto> {
    return this.get<SystemInfoDto>('/system/info');
  }

  // --- Manifest / Readiness ---

  getManifest(itemId: string): Observable<ItemManifest> {
    return this.get<ItemManifest>(`/items/${itemId}/manifest`);
  }

  /** Replace the archive's shared double-page pairing (1.23.0); read back via the manifest. */
  setSpreadLayout(itemId: string, request: SetSpreadLayoutRequest): Observable<SpreadLayoutDto> {
    return this.put<SpreadLayoutDto>(`/items/${itemId}/spread-layout`, request);
  }

  getReadiness(itemId: string): Observable<ItemReadiness> {
    return this.get<ItemReadiness>(`/items/${itemId}/readiness`);
  }

  prepareItem(itemId: string): Observable<{ status: string; contentVersion: number }> {
    return this.post<{ status: string; contentVersion: number }>(`/items/${itemId}/prepare`, {});
  }

  // --- Favorites (1.21.0) ---

  /**
   * The current user's favorites, keyset-paged and ordered recently-favorited (newest
   * first). Respects Incognito/Private visibility server-side (the X-Incognito header is
   * added by the incognito interceptor like every other discovery call).
   */
  getFavorites(cursor: string | null = null, pageSize = 50): Observable<PageResponse<CatalogNodeDto>> {
    let params = new HttpParams().set('pageSize', pageSize.toString());
    if (cursor) params = params.set('cursor', cursor);
    return this.get<PageResponse<CatalogNodeDto>>('/favorites', params);
  }

  /** Stars a catalog node as a favorite (idempotent), or removes the star. */
  setFavorite(nodeId: string, favorite: boolean): Observable<void> {
    return favorite
      ? this.post<void>(`/nodes/${nodeId}/favorite`, {})
      : this.delete<void>(`/nodes/${nodeId}/favorite`);
  }

  // --- HTTP helpers ---

  private get<T>(path: string, params?: HttpParams, context?: HttpContext): Observable<T> {
    return this.http
      .get<T>(this.baseUrl + path, { params, context, withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private post<T>(path: string, body: unknown): Observable<T> {
    return this.http
      .post<T>(this.baseUrl + path, body, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private put<T>(path: string, body: unknown): Observable<T> {
    return this.http
      .put<T>(this.baseUrl + path, body, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private delete<T>(path: string): Observable<T> {
    return this.http
      .delete<T>(this.baseUrl + path, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: HttpErrorResponse): Observable<never> {
    let apiError: ApiError | null = null;
    if (error.error instanceof ErrorEvent) {
      // Client-side error
      return throwError(() => ({
        error: 'client_error',
        message: error.error.message,
        detail: null,
        correlationId: null,
      } as ApiError));
    }

    // Server-side error — may contain an ApiError body
    if (error.error && typeof error.error === 'object' && 'error' in error.error) {
      apiError = error.error as ApiError;
    } else {
      apiError = {
        error: 'http_error',
        message: error.statusText || 'Unknown error',
        detail: null,
        correlationId: null,
      };
    }

    return throwError(() => apiError);
  }
}
