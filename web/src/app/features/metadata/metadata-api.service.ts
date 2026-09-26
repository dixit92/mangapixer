import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  ApiError,
  FolderMetadataPrecedenceDto,
  IdentifyContextDto,
  IdentifyPreviewDto,
  IdentifyPreviewRequest,
  IdentifySearchRequest,
  IdentifySearchResultDto,
  LinkSeriesRequest,
  MetadataPrecedence,
  MetadataPurgeResultDto,
  MetadataRefreshResultDto,
  MetadataSettingsDto,
  NodeSeriesLinkChangeDto,
  SeriesInfoDto,
  UpdateMetadataLibraryRequest,
  UpdateMetadataSettingsRequest,
} from '../../core/api/api-types';

/**
 * Series metadata API (1.24.0, stage 1). Kept out of the shared `ApiService` so the
 * metadata surfaces load it lazily with their own chunk. Same conventions: `/api/v1`
 * prefix, cookie session (`withCredentials`), the XSRF interceptor adds the header,
 * errors are mapped to `ApiError`.
 *
 * Lane B1's calls manage links, precedence and the two toggles with no network. Lane B2
 * adds the identify calls: the SERVER makes every provider request (gated, admin-only);
 * the browser only ever talks to MangaPixer, and candidate images come back through
 * MangaPixer by short-lived token.
 */
@Injectable({ providedIn: 'root' })
export class MetadataApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  /** Resolved series information for a node; `includeItems` adds the ComicInfo item table. */
  getSeriesInfo(nodeId: string, includeItems = false): Observable<SeriesInfoDto> {
    const params = includeItems ? new HttpParams().set('includeItems', 'true') : undefined;
    return this.get<SeriesInfoDto>(`/nodes/${encodeURIComponent(nodeId)}/series-info`, params);
  }

  // --- Admin: settings + toggles ---

  getSettings(): Observable<MetadataSettingsDto> {
    return this.get<MetadataSettingsDto>('/admin/metadata/settings');
  }

  updateSettings(request: UpdateMetadataSettingsRequest): Observable<MetadataSettingsDto> {
    return this.put<MetadataSettingsDto>('/admin/metadata/settings', request);
  }

  updateLibrary(libraryId: string, request: UpdateMetadataLibraryRequest): Observable<MetadataSettingsDto> {
    return this.put<MetadataSettingsDto>(`/admin/metadata/libraries/${encodeURIComponent(libraryId)}`, request);
  }

  /** Library precedence override; null clears it (default: web first). */
  setLibraryPrecedence(libraryId: string, precedence: MetadataPrecedence | null): Observable<void> {
    return this.put<void>(`/admin/metadata/libraries/${encodeURIComponent(libraryId)}/precedence`, { precedence });
  }

  /** "Delete fetched web data" - one library, or everything when libraryId is null. */
  purge(libraryId: string | null): Observable<MetadataPurgeResultDto> {
    return this.post<MetadataPurgeResultDto>('/admin/metadata/purge', { libraryId });
  }

  // --- Admin: node links ---

  link(nodeId: string, request: LinkSeriesRequest): Observable<NodeSeriesLinkChangeDto> {
    return this.put<NodeSeriesLinkChangeDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/link`, request);
  }

  /** Removes the node's own link row (a link or a Don't match); inheritance resumes. */
  unlink(nodeId: string): Observable<NodeSeriesLinkChangeDto> {
    return this.delete<NodeSeriesLinkChangeDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/link`);
  }

  setDontMatch(nodeId: string): Observable<NodeSeriesLinkChangeDto> {
    return this.put<NodeSeriesLinkChangeDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/dont-match`, {});
  }

  clearDontMatch(nodeId: string): Observable<NodeSeriesLinkChangeDto> {
    return this.delete<NodeSeriesLinkChangeDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/dont-match`);
  }

  // --- Admin: identify (lane B2) ---

  /** Availability, suggestions, ComicInfo hint and budget for the identify dialog. No network. */
  getIdentifyContext(nodeId: string): Observable<IdentifyContextDto> {
    return this.get<IdentifyContextDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/identify`);
  }

  /**
   * Sends `query` (exactly what the admin confirmed) to the provider, via the server.
   * `hideDoujinshiAndNovels` adds the provider's fixed type filter (no user data).
   */
  search(nodeId: string, query: string, page = 1, hideDoujinshiAndNovels = false): Observable<IdentifySearchResultDto> {
    const body: IdentifySearchRequest = { query, page, hideDoujinshiAndNovels };
    return this.post<IdentifySearchResultDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/search`, body);
  }

  /** A pasted URL / `mu:` shortcode; the server parses it locally and sends only the id. */
  lookup(nodeId: string, reference: string): Observable<IdentifyPreviewDto> {
    return this.post<IdentifyPreviewDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/lookup`, { reference });
  }

  preview(nodeId: string, request: IdentifyPreviewRequest): Observable<IdentifyPreviewDto> {
    return this.post<IdentifyPreviewDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/preview`, request);
  }

  refresh(nodeId: string): Observable<MetadataRefreshResultDto> {
    return this.post<MetadataRefreshResultDto>(`/admin/metadata/nodes/${encodeURIComponent(nodeId)}/refresh`, {});
  }

  /** Same-origin URL of a candidate image (served by MangaPixer; the browser never contacts a provider). */
  candidateImageUrl(token: string): string {
    return `${this.baseUrl}/admin/metadata/candidates/${encodeURIComponent(token)}/image`;
  }

  // --- Admin: folder precedence ---

  setFolderPrecedence(nodeId: string, precedence: MetadataPrecedence): Observable<FolderMetadataPrecedenceDto> {
    return this.put<FolderMetadataPrecedenceDto>(`/admin/metadata/folders/${encodeURIComponent(nodeId)}/precedence`, { precedence });
  }

  clearFolderPrecedence(nodeId: string): Observable<void> {
    return this.delete<void>(`/admin/metadata/folders/${encodeURIComponent(nodeId)}/precedence`);
  }

  private get<T>(path: string, params?: HttpParams): Observable<T> {
    return this.http.get<T>(this.baseUrl + path, { params, withCredentials: true }).pipe(catchError(toApiError));
  }

  private put<T>(path: string, body: unknown): Observable<T> {
    return this.http.put<T>(this.baseUrl + path, body, { withCredentials: true }).pipe(catchError(toApiError));
  }

  private post<T>(path: string, body: unknown): Observable<T> {
    return this.http.post<T>(this.baseUrl + path, body, { withCredentials: true }).pipe(catchError(toApiError));
  }

  private delete<T>(path: string): Observable<T> {
    return this.http.delete<T>(this.baseUrl + path, { withCredentials: true }).pipe(catchError(toApiError));
  }
}

/** Maps an HTTP failure to the server's `ApiError` shape (status kept for 404 handling). */
function toApiError(error: HttpErrorResponse): Observable<never> {
  const body = error.error;
  const apiError: ApiError & { status?: number } =
    body && typeof body === 'object' && 'error' in body
      ? { ...(body as ApiError), status: error.status }
      : {
          error: error.status === 404 ? 'not_found' : 'http_error',
          message: error.statusText || 'Unknown error',
          detail: null,
          correlationId: null,
          status: error.status,
        };
  return throwError(() => apiError);
}
