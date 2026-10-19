import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { ApiError, AuthorSearchResultDto } from '../../../core/api/api-types';

/**
 * Author search by name (1.45.0), admin only: `POST /api/v1/admin/metadata/nodes/{id}/author-search` sends the confirmed text - nothing
 * else - to MangaUpdates' author search (page 1, 10 per page), counted in the daily budget. The answer is only offered; marking the folder
 * with a picked author sends nothing.
 */
@Injectable({ providedIn: 'root' })
export class AuthorSearchApiService {
  private readonly http = inject(HttpClient);

  /** One author search for `nodeId`'s folder (its library's switches apply). Errors carry the HTTP status (409 / 429 / 503 stop a run). */
  search(nodeId: string, text: string): Observable<AuthorSearchResultDto> {
    return this.http
      .post<AuthorSearchResultDto>(`/api/v1/admin/metadata/nodes/${encodeURIComponent(nodeId)}/author-search`, { text }, { withCredentials: true })
      .pipe(catchError(toApiError));
  }
}

function toApiError(error: HttpErrorResponse): Observable<never> {
  const body = error.error;
  const apiError: ApiError & { status?: number } =
    body && typeof body === 'object' && 'error' in body
      ? { ...(body as ApiError), status: error.status }
      : { error: 'http_error', message: error.statusText || 'Unknown error', detail: null, correlationId: null, status: error.status };
  return throwError(() => apiError);
}
