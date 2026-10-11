import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { ReadingModeApplyRequest, ReadingModeApplyResultDto, ReadingModeSuggestionsDto } from '../../../core/api/api-types';

/**
 * Reading-mode suggestions (1.41.0), admin-only: folders whose local evidence (page shapes, ComicInfo, the linked record as a hint)
 * points to another reader mode, and Apply, which writes the folders' reader defaults. Local data only - nothing contacts a provider.
 */
@Injectable({ providedIn: 'root' })
export class ReadingModeSuggestionsApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/v1/admin/reading-mode-suggestions';

  list(): Observable<ReadingModeSuggestionsDto> {
    return this.http.get<ReadingModeSuggestionsDto>(this.baseUrl, { withCredentials: true });
  }

  apply(request: ReadingModeApplyRequest): Observable<ReadingModeApplyResultDto> {
    return this.http.post<ReadingModeApplyResultDto>(`${this.baseUrl}/apply`, request, { withCredentials: true });
  }
}
