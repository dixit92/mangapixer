import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  ApiError,
  AdminUserDto,
  AuthUserDto,
  CatalogNodeDto,
  ChangePasswordRequest,
  ContinueReadingEntry,
  CreateUserRequest,
  CsrfTokenDto,
  DirectoryListingDto,
  ItemManifest,
  ItemReadiness,
  LibraryDto,
  LoginRequest,
  PageResponse,
  ProgressUpdateResult,
  ReadingProgressDto,
  RegisterLibraryRequest,
  ResetPasswordResponse,
  ScanRunDto,
  ScanTriggeredDto,
  SearchResultsDto,
  SetupRequest,
  SetupStatusDto,
  UpdateLibraryRequest,
  UpdateProgressRequest,
  UpdateUserRequest,
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

  browseLibrary(
    libraryId: string,
    parentId: string | null,
    cursor: string | null = null,
    pageSize: number = 50,
  ): Observable<PageResponse<CatalogNodeDto>> {
    let params = new HttpParams().set('pageSize', pageSize.toString());
    if (cursor) params = params.set('cursor', cursor);
    if (parentId) params = params.set('parentId', parentId);
    return this.get<PageResponse<CatalogNodeDto>>(
      `/libraries/${libraryId}/browse`,
      params,
    );
  }

  getNode(nodeId: string): Observable<CatalogNodeDto> {
    return this.get<CatalogNodeDto>(`/nodes/${nodeId}`);
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

  getPreferences(): Observable<UserPreferencesDto> {
    return this.get<UserPreferencesDto>('/reading/preferences');
  }

  setPreferences(request: UserPreferencesDto): Observable<void> {
    return this.put<void>('/reading/preferences', request);
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

  triggerScan(libraryId: string): Observable<ScanTriggeredDto> {
    return this.post<ScanTriggeredDto>(`/admin/libraries/${libraryId}/scan`, {});
  }

  cancelScan(scanRunId: string): Observable<void> {
    return this.post<void>(`/admin/scans/${scanRunId}/cancel`, {});
  }

  getScanHistory(libraryId: string): Observable<ScanRunDto[]> {
    return this.get<ScanRunDto[]>(`/admin/libraries/${libraryId}/scans`);
  }

  listUsers(): Observable<AdminUserDto[]> {
    return this.get<AdminUserDto[]>('/admin/users');
  }

  createUser(request: CreateUserRequest): Observable<AdminUserDto> {
    return this.post<AdminUserDto>('/admin/users', request);
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

  revokeUserSessions(id: string): Observable<void> {
    return this.delete<void>(`/admin/users/${id}/sessions`);
  }

  grantAccess(userId: string, libraryId: string): Observable<void> {
    return this.put<void>(`/admin/users/${userId}/grants/${libraryId}`, {});
  }

  revokeAccess(userId: string, libraryId: string): Observable<void> {
    return this.delete<void>(`/admin/users/${userId}/grants/${libraryId}`);
  }

  // --- Manifest / Readiness ---

  getManifest(itemId: string): Observable<ItemManifest> {
    return this.get<ItemManifest>(`/items/${itemId}/manifest`);
  }

  getReadiness(itemId: string): Observable<ItemReadiness> {
    return this.get<ItemReadiness>(`/items/${itemId}/readiness`);
  }

  prepareItem(itemId: string): Observable<{ status: string; contentVersion: number }> {
    return this.post<{ status: string; contentVersion: number }>(`/items/${itemId}/prepare`, {});
  }

  // --- HTTP helpers ---

  private get<T>(path: string, params?: HttpParams): Observable<T> {
    return this.http
      .get<T>(this.baseUrl + path, { params, withCredentials: true })
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
