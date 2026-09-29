import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

import { CardCoverSource } from '../../core/api/api-types';

/** A node's card cover changed (an admin choice): its new URL and source. */
export interface CoverChange {
  nodeId: string;
  coverUrl: string | null;
  coverSource: CardCoverSource | null;
}

/**
 * Announces cover changes (1.29.0) so every card showing the node (browse, the series page) patches its cover in place,
 * as `MetadataStateService` does for the (i). The new URL is versioned, so the browser fetches the new image.
 */
@Injectable({ providedIn: 'root' })
export class CoverStateService {
  private readonly changes = new Subject<CoverChange>();
  readonly changed$ = this.changes.asObservable();

  announce(change: CoverChange): void {
    this.changes.next(change);
  }
}
