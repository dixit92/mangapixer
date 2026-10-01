import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  MoveConflictCountDto, MoveConflictPageDto, MoveConflictResolveRequest, MoveConflictResolveResultDto,
} from '../../../core/api/api-types';

/**
 * Move conflicts (1.31.0), admin-only: items moved to another library while both copies had their own state. Local data
 * only - nothing here contacts a provider.
 */
@Injectable({ providedIn: 'root' })
export class MoveConflictsApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/move-conflicts';

  list(resolved: boolean, cursor: string | null = null, limit = 50): Observable<MoveConflictPageDto> {
    let params = new HttpParams().set('limit', limit).set('state', resolved ? 'resolved' : 'open');
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<MoveConflictPageDto>(this.baseUrl, { params, withCredentials: true });
  }

  /** The number of open conflicts (the admin page's link badge). */
  count(): Observable<MoveConflictCountDto> {
    return this.http.get<MoveConflictCountDto>(`${this.baseUrl}/count`, { withCredentials: true });
  }

  resolve(request: MoveConflictResolveRequest): Observable<MoveConflictResolveResultDto> {
    return this.http.post<MoveConflictResolveResultDto>(`${this.baseUrl}/resolve`, request, { withCredentials: true });
  }
}
