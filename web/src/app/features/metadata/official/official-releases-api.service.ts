import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { OfficialReleasesFilter, OfficialReleasesPageDto, CompletionBasis, SeriesAnswer } from '../../../core/api/api-types';

/**
 * The Completion tab (1.32.0; the Official releases tab of 1.30.0), admin-only. Built on the server from stored data (archive names,
 * each linked series' stored record and volume list): reading it never contacts a provider.
 */
@Injectable({ providedIn: 'root' })
export class OfficialReleasesApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/metadata/official-releases';

  list(libraryId: string | null, filter: OfficialReleasesFilter, cursor: string | null = null, limit = 50,
    basis: CompletionBasis | null = null, answer: SeriesAnswer | null = null, upgrades = false): Observable<OfficialReleasesPageDto> {
    let params = new HttpParams().set('limit', limit).set('filter', filter);
    if (basis) params = params.set('basis', basis);
    if (answer) params = params.set('answer', answer);
    if (upgrades) params = params.set('upgrades', true);
    if (libraryId) params = params.set('library', libraryId);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<OfficialReleasesPageDto>(this.baseUrl, { params, withCredentials: true });
  }
}
