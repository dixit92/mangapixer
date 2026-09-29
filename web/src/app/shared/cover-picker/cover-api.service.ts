import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { ApiError, CoverChoiceRequest, CoverOptionsDto, CoverStateDto, DeleteVolumeCoversResult } from '../../core/api/api-types';

/**
 * Cover layer API (1.29.0), admin surfaces only: the "Choose cover..." picker's options, setting / clearing a node's
 * cover choice, and "Delete stored volume covers". Kept out of the shared `ApiService` so the picker chunk carries it.
 * Local data only: none of these calls makes the server contact a provider.
 */
@Injectable({ providedIn: 'root' })
export class CoverApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  getOptions(nodeId: string): Observable<CoverOptionsDto> {
    return this.http.get<CoverOptionsDto>(`${this.baseUrl}/nodes/${encodeURIComponent(nodeId)}/cover-options`, { withCredentials: true })
      .pipe(catchError(mapError));
  }

  setChoice(nodeId: string, request: CoverChoiceRequest): Observable<CoverStateDto> {
    return this.http.put<CoverStateDto>(`${this.baseUrl}/nodes/${encodeURIComponent(nodeId)}/cover-choice`, request, { withCredentials: true })
      .pipe(catchError(mapError));
  }

  /** Back to Automatic. */
  clearChoice(nodeId: string): Observable<CoverStateDto> {
    return this.http.delete<CoverStateDto>(`${this.baseUrl}/nodes/${encodeURIComponent(nodeId)}/cover-choice`, { withCredentials: true })
      .pipe(catchError(mapError));
  }

  deleteStoredVolumeCovers(): Observable<DeleteVolumeCoversResult> {
    return this.http.delete<DeleteVolumeCoversResult>(`${this.baseUrl}/admin/metadata/volume-covers`, { withCredentials: true })
      .pipe(catchError(mapError));
  }
}

function mapError(err: HttpErrorResponse): Observable<never> {
  const body = err.error as ApiError | null;
  const apiError: ApiError = body && typeof body === 'object' && 'error' in body
    ? body
    : { error: 'http_' + err.status, message: err.message || 'The request failed.', detail: null, correlationId: null };
  return throwError(() => apiError);
}
