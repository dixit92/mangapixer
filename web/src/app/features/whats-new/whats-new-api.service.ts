import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { WhatsNewDto } from '../../core/api/api-types';

/**
 * "What's new" API (1.53.0, admins only): `GET /admin/whats-new` - the release notes since the version this admin last saw (or, with
 * `current`, the running version's notes) - and `POST /admin/whats-new/seen`. The notes are the server's own embedded CHANGELOG; no
 * other host is contacted.
 */
@Injectable({ providedIn: 'root' })
export class WhatsNewApiService {
  private readonly http = inject(HttpClient);
  private readonly url = '/api/v1/admin/whats-new';

  get(current = false): Observable<WhatsNewDto> {
    const params = current ? new HttpParams().set('current', 'true') : undefined;
    return this.http.get<WhatsNewDto>(this.url, { params, withCredentials: true });
  }

  markSeen(): Observable<void> {
    return this.http.post<void>(`${this.url}/seen`, null, { withCredentials: true });
  }
}
