import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import { ApiError } from '../../../core/api/api-types';
import {
  CreatorDto,
  CreatorSearchResultDto,
  FacetBucket,
  FacetBucketPageDto,
  InfoPanelDto,
  MergeCreatorRequest,
  SetCreatorDisplayNameRequest,
  SplitCreatorRequest,
} from './info-types';

/** What an Info panel is for (1.47.0): a node (a folder or archive) or a library root, which is not a node. */
export type InfoTarget = { nodeId: string; libraryId?: undefined } | { libraryId: string; nodeId?: undefined };

/**
 * The Info panel's server calls (1.45.0 lane P): the node's tags (lane Q's `GET nodes/{id}/info`) and the admin creator routes (lane F,
 * `/admin/metadata/creators/...`). Cookie session + CSRF (the interceptor), never a token.
 */
@Injectable({ providedIn: 'root' })
export class InfoApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  /** The node's tags for this viewer (404 when the node is not accessible; `mode: None` when nothing is indexed). */
  getInfo(nodeId: string): Observable<InfoPanelDto> {
    return this.get<InfoPanelDto>(`/nodes/${encodeURIComponent(nodeId)}/info`);
  }

  /** A library root's tags (the root is not a node). */
  getLibraryInfo(libraryId: string): Observable<InfoPanelDto> {
    return this.get<InfoPanelDto>(`/libraries/${encodeURIComponent(libraryId)}/info`);
  }

  /**
   * 1.47.0 "+N more": one bucket of the target's panel from `offset` on (the panel's chips are its first page), the same order and
   * counts as the panel.
   */
  getBucket(target: InfoTarget, bucket: FacetBucket, offset: number, limit = 100): Observable<FacetBucketPageDto> {
    const base = target.libraryId !== undefined
      ? `/libraries/${encodeURIComponent(target.libraryId)}`
      : `/nodes/${encodeURIComponent(target.nodeId)}`;
    const params = new HttpParams().set('offset', String(offset)).set('limit', String(limit));
    return this.get<FacetBucketPageDto>(`${base}/info/buckets/${encodeURIComponent(bucket)}`, params);
  }

  // --- Admin: creators (lane F) ---

  /** A creator by id; a merged id answers with its target and `redirectedFrom`. */
  getCreator(id: string): Observable<CreatorDto> {
    return this.get<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}`);
  }

  /** Live creators whose spellings contain the text (the "Merge into..." picker). */
  findCreators(text: string, limit = 20): Observable<CreatorDto[]> {
    const params = new HttpParams().set('q', text).set('limit', String(limit));
    return this.get<CreatorSearchResultDto>('/admin/metadata/creators', params).pipe(map((r) => r.items ?? []));
  }

  /**
   * The creator a creator chip stands for. 1.47.0: the chip carries its creator's id (`creatorId`), so the creator is read by id. An
   * answer without it (a server before 1.47.0) falls back to the chip's name: a creator names its value (`valueId`). Null when no
   * creator is found.
   */
  creatorForChip(chip: { id: string; display: string; creatorId?: string | null }): Observable<CreatorDto | null> {
    if (chip.creatorId) return this.getCreator(chip.creatorId);
    return this.findCreators(chip.display, 50).pipe(map((list) => list.find((c) => c.valueId === chip.id) ?? null));
  }

  /** null or blank clears the admin's name (the automatic rule applies on the next index pass). */
  renameCreator(id: string, body: SetCreatorDisplayNameRequest): Observable<CreatorDto> {
    return this.put<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}/display-name`, body);
  }

  /** Merges `id` into `body.intoId`; answers with the creator kept. */
  mergeCreator(id: string, body: MergeCreatorRequest): Observable<CreatorDto> {
    return this.post<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}/merge`, body);
  }

  /** Moves the chosen provider ids and spellings to a NEW creator; answers with the new creator. */
  splitCreator(id: string, body: SplitCreatorRequest): Observable<CreatorDto> {
    return this.post<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}/split`, body);
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
}

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
