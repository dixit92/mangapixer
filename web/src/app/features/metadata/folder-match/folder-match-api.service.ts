import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  ApiError,
  FolderMatchApplyRequest,
  FolderMatchApplyResultDto,
  FolderMatchPreviewDto,
  FolderMatchPreviewRequest,
} from '../../../core/api/api-types';

/**
 * "Match folders by name" (1.38.0), admin only. Both calls use stored data only - the preview sends nothing; apply may download a stored collection record's missing cover (owner, 1.38.0). The opt-in web search
 * for the rest is the Identify search (`MetadataApiService.search`), one request per ticked folder, driven by the dialog.
 */
@Injectable({ providedIn: 'root' })
export class FolderMatchApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/metadata/folder-match';

  /** What each selected node would be marked as (1-200 nodes); nothing changes. */
  preview(request: FolderMatchPreviewRequest): Observable<FolderMatchPreviewDto> {
    return this.http.post<FolderMatchPreviewDto>(`${this.baseUrl}/preview`, request, { withCredentials: true }).pipe(catchError(toApiError));
  }

  /** Marks the ticked folders (1-200), each through the single action; a collection only with a stored record. */
  apply(request: FolderMatchApplyRequest): Observable<FolderMatchApplyResultDto> {
    return this.http.post<FolderMatchApplyResultDto>(`${this.baseUrl}/apply`, request, { withCredentials: true }).pipe(catchError(toApiError));
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
