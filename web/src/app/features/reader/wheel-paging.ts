/**
 * Mouse-wheel page turns (1.36.0, owner decisions 2026-10-07), as a pure state machine so the reader stays thin and
 * every rule is testable as a concrete event sequence.
 *
 * The reader asks this only in the FIXED paged views (the page fits the screen, so vertical scrolling does nothing);
 * fit-width with a tall page, any overflow, zoom and the vertical (webtoon) view keep native scrolling and never get
 * here. Rules:
 *  - vertical wheel only: an event whose horizontal delta dominates is never ours (a trackpad sideways swipe, the
 *    browser's back / forward gesture);
 *  - down = next, up = previous, in reading order - the caller maps them to `nextPage()` / `prevPage()`;
 *  - ONE GESTURE = ONE PAGE: after a turn, every wheel event is swallowed until the wheel has been quiet for
 *    {@link WheelQuietGapMs}. A trackpad flick's inertia is a long stream of events a frame apart; it never turns a
 *    second page, and it can never count as the second input that opens the next archive at the end (the reader's
 *    edge rule) - only a fresh gesture after the quiet gap can. A mouse with notches still turns one page per notch
 *    once the gap has passed;
 *  - deltas are compared in pixels (`deltaMode` line / page units converted), and tiny ones - below
 *    {@link WheelMinDeltaPx}, a resting finger's jitter or an inertia tail - never START a turn.
 *
 * Pure: the caller passes the event's numbers and its timestamp.
 */

/** Quiet time (ms) that ends a wheel gesture: the next event after it may turn a page again. */
export const WheelQuietGapMs = 200;

/** Below this many pixels a wheel event does not start a page turn (jitter, inertia tails). */
export const WheelMinDeltaPx = 3;

/** CSS pixels per line for `WheelEvent.DOM_DELTA_LINE` (Firefox reports mouse notches in lines). */
export const WheelLinePx = 16;

/** `WheelEvent.deltaMode` values (named here: jsdom and old engines may lack the constants). */
const DeltaLine = 1;
const DeltaPage = 2;

/** The wheel event's numbers the pager needs. */
export interface WheelInput {
  deltaX: number;
  deltaY: number;
  deltaMode: number;
  /** Event time in ms (any monotonic clock; `event.timeStamp`). */
  at: number;
}

/**
 * What one wheel event does: turn to the `next` / `prev` page, `swallow` it (preventDefault, no turn - part of a
 * gesture that already turned), or `ignore` it (not ours: leave it to the browser).
 */
export type WheelStep = 'next' | 'prev' | 'swallow' | 'ignore';

/** A delta in CSS pixels, whatever unit the event reported. */
export function wheelPixels(delta: number, deltaMode: number, pagePx: number): number {
  if (!Number.isFinite(delta)) return 0;
  if (deltaMode === DeltaLine) return delta * WheelLinePx;
  if (deltaMode === DeltaPage) return delta * pagePx;
  return delta;
}

export class WheelPager {
  /** The time of the last event of a gesture that turned a page; null when no such gesture is running. */
  private gestureAt: number | null = null;

  /**
   * One wheel event. `fixed` says whether the page fits the screen right now: a scrollable page is native scrolling
   * and starts nothing - but a gesture that already turned keeps swallowing its tail even if the new page scrolls,
   * so leftover inertia does not scroll the page it just turned to.
   * `pagePx` converts `DOM_DELTA_PAGE` deltas (the viewport height).
   */
  input(e: WheelInput, fixed: boolean, pagePx = 800): WheelStep {
    const dx = wheelPixels(e.deltaX, e.deltaMode, pagePx);
    const dy = wheelPixels(e.deltaY, e.deltaMode, pagePx);
    const horizontal = Math.abs(dx) > Math.abs(dy);
    if (this.gestureAt !== null && e.at - this.gestureAt < WheelQuietGapMs && e.at >= this.gestureAt) {
      // The same gesture (its inertia included): extend it. Horizontal events are left to the browser.
      this.gestureAt = e.at;
      return horizontal ? 'ignore' : 'swallow';
    }
    this.gestureAt = null;
    if (!fixed || horizontal || Math.abs(dy) < WheelMinDeltaPx) return 'ignore';
    this.gestureAt = e.at;
    return dy > 0 ? 'next' : 'prev';
  }

  /** Forget a running gesture (a new archive, the view changed). */
  reset(): void {
    this.gestureAt = null;
  }
}
