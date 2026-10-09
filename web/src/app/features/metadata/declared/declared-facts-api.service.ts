import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, tap, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  ApiError, DeclaredFactsScopeDto, NodeDeclaredFactsDto, SetDeclaredEditionRequest, SetDeclaredFactsRequest,
} from '../../../core/api/api-types';

/** A folder (by node id) or a whole library (by library id). */
export interface DeclaredScope {
  kind: 'folder' | 'library';
  id: string;
}

/**
 * Declared facts API (1.28.0). Admin edits per scope (`/admin/metadata/{folders|libraries}/{id}/declared`)
 * and the node view the Info panel reads (`/nodes/{id}/declared-facts`). No provider is ever contacted:
 * declarations are local settings. `version` bumps after every successful change so open Info panels and
 * series pages re-read their "Declared" line.
 */
@Injectable({ providedIn: 'root' })
export class DeclaredFactsApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1';

  /** Incremented after each saved change (any scope). */
  readonly version = signal(0);

  get(scope: DeclaredScope): Observable<DeclaredFactsScopeDto> {
    return this.http.get<DeclaredFactsScopeDto>(this.url(scope), { withCredentials: true }).pipe(catchError(toApiError));
  }

  set(scope: DeclaredScope, request: SetDeclaredFactsRequest): Observable<DeclaredFactsScopeDto> {
    return this.http.put<DeclaredFactsScopeDto>(this.url(scope), request, { withCredentials: true })
      .pipe(catchError(toApiError), tap(() => this.version.update((v) => v + 1)));
  }

  /**
   * 1.39.0: replaces a folder's own edition facts (volumes in this edition, edition label, track completion) at
   * `/admin/metadata/folders/{id}/declared/edition`; the folder's type and creators stay.
   */
  setEdition(nodeId: string, request: SetDeclaredEditionRequest): Observable<DeclaredFactsScopeDto> {
    const url = `${this.url({ kind: 'folder', id: nodeId })}/edition`;
    return this.http.put<DeclaredFactsScopeDto>(url, request, { withCredentials: true })
      .pipe(catchError(toApiError), tap(() => this.version.update((v) => v + 1)));
  }

  clear(scope: DeclaredScope): Observable<DeclaredFactsScopeDto> {
    return this.http.delete<DeclaredFactsScopeDto>(this.url(scope), { withCredentials: true })
      .pipe(catchError(toApiError), tap(() => this.version.update((v) => v + 1)));
  }

  /** Effective facts at a node (folder or archive) plus a conflict with its linked record, if any. */
  forNode(nodeId: string): Observable<NodeDeclaredFactsDto> {
    return this.http.get<NodeDeclaredFactsDto>(`${this.baseUrl}/nodes/${encodeURIComponent(nodeId)}/declared-facts`, { withCredentials: true })
      .pipe(catchError(toApiError));
  }

  private url(scope: DeclaredScope): string {
    const kind = scope.kind === 'folder' ? 'folders' : 'libraries';
    return `${this.baseUrl}/admin/metadata/${kind}/${encodeURIComponent(scope.id)}/declared`;
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
