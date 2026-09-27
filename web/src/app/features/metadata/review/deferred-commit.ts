import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, ReplaySubject, of } from 'rxjs';

/** One review action whose request waits until its Undo window is over. */
export interface DeferredAction<T = unknown> {
  /** The snackbar text ("Accepted X"). */
  label: string;
  /** Sends the request; called once, when the Undo window closes without Undo. */
  commit: () => Observable<T>;
  /** Undo pressed: nothing was sent; put the rows back. */
  undone: () => void;
  committed?: (result: T) => void;
  failed?: (error: unknown) => void;
}

interface Pending {
  action: DeferredAction<never>;
  settled: boolean;
}

/**
 * Undo for the review dashboard (stage 2): the action is applied to the view at once,
 * but its request is sent only when the "Undo" snackbar closes without Undo (timeout,
 * dismissal, the next action, or leaving the page via `flush`). Undo therefore needs no
 * restore endpoint and works the same for Accept, Don't match and bulk actions: a
 * review row cannot be put back into "Needs review" once accepted (its stored
 * candidates are gone), so un-sending is the only honest Undo.
 *
 * Only the LAST action can be undone: starting a new one sends the previous one first.
 */
export class DeferredCommitQueue {
  private pending: Pending | null = null;

  constructor(
    private readonly snackBar: MatSnackBar,
    private readonly durationMs = 6000,
  ) {}

  get hasPending(): boolean {
    return this.pending !== null;
  }

  run<T>(action: DeferredAction<T>): void {
    this.flush();
    const p: Pending = { action: action as DeferredAction<never>, settled: false };
    this.pending = p;
    const ref = this.snackBar.open(action.label, 'Undo', { duration: this.durationMs });
    ref.onAction().subscribe(() => this.undo(p));
    ref.afterDismissed().subscribe(({ dismissedByAction }) => {
      if (!dismissedByAction) this.commit(p);
    });
  }

  /**
   * Sends the pending action now (a new action, a tab change, leaving the page) and
   * completes once it has settled - `committed`/`failed` have run and the caller can
   * safely reload. The commit is started eagerly (not on subscribe), so a caller that
   * only wants "flush and forget" (leaving the page) can call this without subscribing
   * and the request still goes out.
   */
  flush(): Observable<void> {
    return this.pending ? this.commit(this.pending) : of(undefined);
  }

  private undo(p: Pending): void {
    if (p.settled) return;
    p.settled = true;
    if (this.pending === p) this.pending = null;
    p.action.undone();
  }

  private commit(p: Pending): Observable<void> {
    if (p.settled) return of(undefined);
    p.settled = true;
    if (this.pending === p) this.pending = null;
    // A ReplaySubject, not a plain Subject: `p.action.commit()` may resolve synchronously
    // (as in tests, or a fast local response), completing this notification before
    // `flush()`'s caller has had a chance to subscribe to it - a plain Subject would
    // silently drop that emission for a subscriber that arrives after `complete()`.
    const settled = new ReplaySubject<void>(1);
    p.action.commit().subscribe({
      next: (r) => {
        p.action.committed?.(r);
        settled.next();
        settled.complete();
      },
      error: (e: unknown) => {
        p.action.failed?.(e);
        settled.next();
        settled.complete();
      },
    });
    return settled.asObservable();
  }
}
