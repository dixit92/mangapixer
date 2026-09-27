import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';

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

  /** Sends the pending action now (a new action, a tab change, leaving the page). */
  flush(): void {
    if (this.pending) this.commit(this.pending);
  }

  private undo(p: Pending): void {
    if (p.settled) return;
    p.settled = true;
    if (this.pending === p) this.pending = null;
    p.action.undone();
  }

  private commit(p: Pending): void {
    if (p.settled) return;
    p.settled = true;
    if (this.pending === p) this.pending = null;
    p.action.commit().subscribe({
      next: (r) => p.action.committed?.(r),
      error: (e: unknown) => p.action.failed?.(e),
    });
  }
}
