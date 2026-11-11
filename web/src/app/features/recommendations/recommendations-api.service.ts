import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

import { RecommendationsDto } from '../../core/api/api-types';

/** The number of picks a row asks for (1.53.0). */
export const RECOMMENDATION_ROW_LIMIT = 12;

/**
 * Recommendations (1.53.0): content-based, computed on this server from the viewer's own ratings, favorites and finished series and the
 * tags of what is on it - nothing leaves the server. Incognito rides on the app's interceptor (`X-Incognito`).
 */
@Injectable({ providedIn: 'root' })
export class RecommendationsApiService {
  private readonly http = inject(HttpClient);

  /** Home "Recommended for you": empty without a seed or a pick above the floor. */
  forMe(limit = RECOMMENDATION_ROW_LIMIT): Observable<RecommendationsDto> {
    return this.http.get<RecommendationsDto>('/api/v1/recommendations', {
      params: new HttpParams().set('limit', String(limit)), withCredentials: true,
    });
  }

  /** The series page's "More like this": series like this node's (unread first). 404 for a node the viewer may not open. */
  similar(nodeId: string, limit = RECOMMENDATION_ROW_LIMIT): Observable<RecommendationsDto> {
    return this.http.get<RecommendationsDto>(`/api/v1/nodes/${encodeURIComponent(nodeId)}/similar`, {
      params: new HttpParams().set('limit', String(limit)), withCredentials: true,
    });
  }
}
