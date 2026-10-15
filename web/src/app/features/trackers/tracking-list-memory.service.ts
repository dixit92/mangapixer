import { Injectable } from '@angular/core';

/** What the Tracking list keeps for a way back: the chip, the search, how many pages were shown and where the page was scrolled. */
export interface TrackingListState {
  filter: string;
  search: string;
  pages: number;
  scrollY: number;
}

/**
 * The Tracking list's place for the browser's Back (UX review UX11): "⋯ > Open the series" and then Back used to rebuild the list at
 * page 1 with no chip and no search. The list saves its state per tracker when it goes away; the Tracking page arms a restore only
 * when it is shown again by a Back / Forward (a `popstate` navigation), so a fresh visit always starts at page 1. In memory only -
 * nothing is stored, and a reload forgets it.
 */
@Injectable({ providedIn: 'root' })
export class TrackingListMemory {
  private readonly saved = new Map<string, TrackingListState>();
  private armed = false;

  save(slug: string, state: TrackingListState): void {
    this.saved.set(slug, state);
  }

  /** The Tracking page was created by a Back / Forward (true) or by any other navigation (false). */
  arm(restore: boolean): void {
    this.armed = restore;
  }

  /** The saved state of `slug` once, for the first list shown after an armed page; null otherwise. */
  take(slug: string): TrackingListState | null {
    if (!this.armed) return null;
    this.armed = false;
    return this.saved.get(slug) ?? null;
  }
}
