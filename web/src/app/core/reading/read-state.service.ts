import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';

/**
 * Cross-component signal for "this item's read/progress state may have
 * changed server-side" (1.7.1). The reader notifies on exit (chapter change,
 * Back, unmount); `LibraryBrowseComponent` observes it to patch the affected
 * card in place from a fresh server fetch.
 *
 * Exists because the 1.6.2 `LibraryBrowseReuseStrategy` RETAINS the browse
 * component instance across a reader round-trip (to preserve scroll and avoid
 * the black-frame/top-reset regression) instead of destroying and re-creating
 * it. That retention means the browse view's `ngOnInit` never re-runs on
 * return from the reader, so nothing re-fetches the list — the just-finished
 * item's card is left showing its stale pre-reading state until this service
 * tells it to refresh itself.
 */
@Injectable({ providedIn: 'root' })
export class ReadStateService {
  private readonly changed = new Subject<string>();
  readonly itemChanged$ = this.changed.asObservable();

  /** Announce that `itemId`'s read/progress state may no longer match what's rendered. */
  notifyChanged(itemId: string): void {
    if (itemId) this.changed.next(itemId);
  }
}
