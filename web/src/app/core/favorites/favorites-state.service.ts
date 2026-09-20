import { Injectable, inject } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { tap } from 'rxjs/operators';

import { ApiService } from '../api/api.service';

/** A favorite toggle that other views can react to (1.21.0). */
export interface FavoriteChange {
  nodeId: string;
  favorite: boolean;
}

/**
 * Cross-component channel for favorite toggles (1.21.0). A single
 * `StarToggleComponent` persists the change through here; every other star for the
 * same node — on browse cards, list rows, the reader toolbar, search results, the
 * favorites view — observes `changed$` and updates in place without a re-fetch. Mirrors
 * `ReadStateService`'s role for read/progress state, and exists for the same reason: the
 * browse component instance is retained across a reader round-trip, so nothing re-runs
 * its `ngOnInit` to pick up a star toggled inside the reader.
 */
@Injectable({ providedIn: 'root' })
export class FavoritesStateService {
  private readonly api = inject(ApiService);
  private readonly changed = new Subject<FavoriteChange>();
  readonly changed$ = this.changed.asObservable();

  /**
   * Persists a favorite toggle and, on success, announces it so other views sync.
   * The caller flips its own UI optimistically and reverts on error.
   */
  setFavorite(nodeId: string, favorite: boolean): Observable<void> {
    return this.api.setFavorite(nodeId, favorite).pipe(
      tap(() => this.changed.next({ nodeId, favorite })),
    );
  }
}
