import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { ApiError } from '../../../core/api/api-types';
import {
  CreatorDto,
  CreatorListDto,
  CreatorMergeRequest,
  CreatorRenameRequest,
  CreatorSplitRequest,
  FacetBucketDto,
  FacetBucketName,
  InfoPanelDto,
} from './info-types';
import { bucketToken } from './info-labels';

/**
 * The Info panel's server calls (1.45.0 lane P): the node's tags (lane Q's `GET nodes/{id}/info`) and the admin creator routes (lane F,
 * `/admin/metadata/creators/...`). Cookie session + CSRF (the interceptor), never a token.
 */
@Injectable({ providedIn: 'root' })
export class InfoApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  /** The node's tags for this viewer; a container's buckets hold at most 12 chips each, with `more` for the rest. */
  getInfo(nodeId: string): Observable<InfoPanelDto> {
    return this.get<InfoPanelDto>(`/nodes/${encodeURIComponent(nodeId)}/info`);
  }

  /** Every chip of one bucket ("More" on a container). */
  getInfoBucket(nodeId: string, bucket: FacetBucketName): Observable<FacetBucketDto> {
    const params = new HttpParams().set('bucket', bucketToken(bucket));
    return this.get<FacetBucketDto>(`/nodes/${encodeURIComponent(nodeId)}/info/bucket`, params);
  }

  // --- Admin: creators (lane F) ---

  /** A creator by id; a merged id answers with its target and `redirectedFrom`. */
  getCreator(id: string): Observable<CreatorDto> {
    return this.get<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}`);
  }

  /** Creators whose names match the text (the "Merge into..." picker). */
  findCreators(text: string): Observable<CreatorListDto> {
    return this.get<CreatorListDto>('/admin/metadata/creators', new HttpParams().set('q', text));
  }

  renameCreator(id: string, body: CreatorRenameRequest): Observable<CreatorDto> {
    return this.put<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}/display-name`, body);
  }

  /** Merges `id` into `body.targetId`; answers with the target. */
  mergeCreator(id: string, body: CreatorMergeRequest): Observable<CreatorDto> {
    return this.post<CreatorDto>(`/admin/metadata/creators/${encodeURIComponent(id)}/merge`, body);
  }

  /** Moves the chosen provider ids and names to a NEW creator; answers with the new creator. */
  splitCreator(id: string, body: CreatorSplitRequest): Observable<CreatorDto> {
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
