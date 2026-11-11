import { DestroyRef, computed, inject, signal } from '@angular/core';

/** How long a first request may take before its page shows the spinner (audit V2): a fast answer shows nothing in between. */
export const FIRST_ANSWER_SPINNER_DELAY_MS = 300;

export type FirstAnswerState = 'pending' | 'loaded' | 'failed';

/**
 * The state of a page's FIRST request (1.53.0, visual audit V2): browse's first page of a listing, Home's libraries, a search's first
 * page. Infinite scroll, "Load more" and the browse reuse strategy keep their own paths - only the request that fills an empty page
 * goes through this. Until the answer: nothing for {@link FIRST_ANSWER_SPINNER_DELAY_MS}, then `slow` (the series page's centred
 * spinner). A failed answer is `failed` (the reader's error block with "Try again"); the empty line shows only once `loaded`.
 *
 * Not a component: each page keeps its own markup (the existing `.state` spinner and `.status.error` patterns). Create it in a field
 * initializer (it needs the injection context to clear its timer with the page).
 */
export class FirstAnswer {
  readonly state = signal<FirstAnswerState>('pending');
  private readonly pendingLong = signal(false);
  /** Still waiting after the delay: show the spinner. */
  readonly slow = computed(() => this.state() === 'pending' && this.pendingLong());
  readonly loaded = computed(() => this.state() === 'loaded');
  readonly failed = computed(() => this.state() === 'failed');
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(private readonly delayMs = FIRST_ANSWER_SPINNER_DELAY_MS) {
    inject(DestroyRef).onDestroy(() => this.clearTimer());
  }

  /**
   * A first request starts (again): back to "not loaded", the spinner only after the delay. 1.53.0 pre-cut (guide finding 3): while the
   * page is still waiting (a page that waits for its settings first, then for its list), a start keeps the clock running - the spinner
   * neither restarts its delay nor blinks off.
   */
  start(): void {
    if (this.state() === 'pending' && (this.timer !== null || this.pendingLong())) return;
    this.clearTimer();
    this.state.set('pending');
    this.pendingLong.set(false);
    this.timer = setTimeout(() => { this.timer = null; this.pendingLong.set(true); }, this.delayMs);
  }

  /** The first request answered. */
  succeed(): void {
    this.settle('loaded');
  }

  /** The first request failed (any error, incl. a timeout or no network). */
  fail(): void {
    this.settle('failed');
  }

  private settle(state: FirstAnswerState): void {
    this.clearTimer();
    this.pendingLong.set(false);
    this.state.set(state);
  }

  private clearTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
