import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { MissingReportPageDto, MissingSeriesDto } from '../../../core/api/api-types';

/**
 * The missing volumes / chapters report (1.28.0), admin-only. Built on the server from stored data (archive
 * names + each linked series' stored record): reading it never contacts a provider.
 */
@Injectable({ providedIn: 'root' })
export class MissingReportApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/metadata/missing';

  list(libraryId: string | null, onlyMissing: boolean, cursor: string | null = null, limit = 50): Observable<MissingReportPageDto> {
    let params = new HttpParams().set('limit', limit);
    if (libraryId) params = params.set('library', libraryId);
    if (onlyMissing) params = params.set('onlyMissing', true);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<MissingReportPageDto>(this.baseUrl, { params, withCredentials: true });
  }

  forNode(nodeId: string): Observable<MissingSeriesDto> {
    return this.http.get<MissingSeriesDto>(`${this.baseUrl}/${encodeURIComponent(nodeId)}`, { withCredentials: true });
  }
}
