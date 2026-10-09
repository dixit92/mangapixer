import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { ApiError, AuthorAliasStatusDto } from '../../../core/api/api-types';

/**
 * Artists' other names (1.38.0, admin): start / status / cancel of the look-up of MangaUpdates author records. The SERVER makes
 * every request (only when an admin starts it, one per second, counted in the daily budget); the browser only talks to MangaPixer.
 * Match folders by name can link to the same card or call `start()` itself.
 */
@Injectable({ providedIn: 'root' })
export class AuthorAliasesApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/metadata/authors';

  /** Known authors, fetched, left to look up, the running / last look-up. No provider request. */
  status(): Observable<AuthorAliasStatusDto> {
    return this.http.get<AuthorAliasStatusDto>(this.baseUrl, { withCredentials: true }).pipe(catchError(toApiError));
  }

  /** "Look up the rest": starts the look-up in the background (or answers the running one). Refusals map to `ApiError`. */
  start(): Observable<AuthorAliasStatusDto> {
    return this.http.post<AuthorAliasStatusDto>(`${this.baseUrl}/lookup`, null, { withCredentials: true }).pipe(catchError(toApiError));
  }

  /** Stops the running look-up after its current request. */
  cancel(): Observable<AuthorAliasStatusDto> {
    return this.http.post<AuthorAliasStatusDto>(`${this.baseUrl}/lookup/cancel`, null, { withCredentials: true }).pipe(catchError(toApiError));
  }
}

/** Same mapping as the metadata API service: the server's `ApiError` body, else a generic error with the status. */
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
