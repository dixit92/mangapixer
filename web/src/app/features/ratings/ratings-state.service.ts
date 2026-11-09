import { Injectable, inject } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { tap } from 'rxjs/operators';

import { ApiService } from '../../core/api/api.service';

/** A rating set or cleared somewhere (1.52.0): `score` null = cleared. */
export interface RatingChange {
  nodeId: string;
  score: number | null;
}

/**
 * Cross-component channel for ratings (1.52.0), the twin of `FavoritesStateService`: one `app-rating-stars` persists a change through
 * here, and every other control or badge of the same node - the Info sheet, the series page, the reader's Rate sheet, browse / search
 * / Favorites cards and rows - observes `changed$` and updates in place without a re-fetch (the browse view is retained across a
 * reader round trip, so nothing else would refresh its cards).
 */
@Injectable({ providedIn: 'root' })
export class RatingsStateService {
  private readonly api = inject(ApiService);
  private readonly changed = new Subject<RatingChange>();
  readonly changed$ = this.changed.asObservable();

  /** Persists a rating (1-10) or clears it (null) and, on success, announces it. The caller updates its own UI optimistically. */
  setRating(nodeId: string, score: number | null): Observable<void> {
    return this.api.setRating(nodeId, score).pipe(tap(() => this.changed.next({ nodeId, score })));
  }
}
