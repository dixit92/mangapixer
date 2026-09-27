import { Subject, of, throwError } from 'rxjs';
import { MatSnackBar } from '@angular/material/snack-bar';

import { DeferredCommitQueue } from './deferred-commit';

/** A snackbar stand-in whose Undo / dismissal the test drives. */
function fakeSnackBar() {
  const refs: { action: Subject<void>; dismissed: Subject<{ dismissedByAction: boolean }>; label: string }[] = [];
  const bar = {
    open: vi.fn((label: string) => {
      // A new snackbar dismisses the previous one (MatSnackBar behaviour).
      const prev = refs[refs.length - 1];
      if (prev) prev.dismissed.next({ dismissedByAction: false });
      const ref = { action: new Subject<void>(), dismissed: new Subject<{ dismissedByAction: boolean }>(), label };
      refs.push(ref);
      return { onAction: () => ref.action, afterDismissed: () => ref.dismissed };
    }),
  };
  return { bar: bar as unknown as MatSnackBar, refs };
}

/** Undo for the review dashboard: requests are sent only once the Undo window closed without Undo. */
describe('DeferredCommitQueue', () => {
  it('sends nothing until the snackbar closes, then sends once', () => {
    const { bar, refs } = fakeSnackBar();
    const q = new DeferredCommitQueue(bar);
    const commit = vi.fn(() => of('ok'));
    const committed = vi.fn();
    q.run({ label: 'Accepted', commit, undone: vi.fn(), committed });
    expect(commit).not.toHaveBeenCalled();
    expect(q.hasPending).toBe(true);
    refs[0].dismissed.next({ dismissedByAction: false });
    expect(commit).toHaveBeenCalledTimes(1);
    expect(committed).toHaveBeenCalledWith('ok');
    expect(q.hasPending).toBe(false);
    q.flush();
    expect(commit).toHaveBeenCalledTimes(1);
  });

  it('Undo sends nothing and calls undone', () => {
    const { bar, refs } = fakeSnackBar();
    const q = new DeferredCommitQueue(bar);
    const commit = vi.fn(() => of(1));
    const undone = vi.fn();
    q.run({ label: 'x', commit, undone });
    refs[0].action.next();
    refs[0].dismissed.next({ dismissedByAction: true });
    expect(undone).toHaveBeenCalledTimes(1);
    expect(commit).not.toHaveBeenCalled();
  });

  it('a new action sends the previous one first; only the last can be undone', () => {
    const { bar, refs } = fakeSnackBar();
    const q = new DeferredCommitQueue(bar);
    const first = vi.fn(() => of(1));
    const second = vi.fn(() => of(2));
    const undoneFirst = vi.fn();
    q.run({ label: 'one', commit: first, undone: undoneFirst });
    q.run({ label: 'two', commit: second, undone: vi.fn() });
    expect(first).toHaveBeenCalledTimes(1);
    refs[0].action.next(); // too late for the first
    expect(undoneFirst).not.toHaveBeenCalled();
    expect(second).not.toHaveBeenCalled();
    q.flush();
    expect(second).toHaveBeenCalledTimes(1);
  });

  it('reports a failed request', () => {
    const { bar } = fakeSnackBar();
    const q = new DeferredCommitQueue(bar);
    const failed = vi.fn();
    q.run({ label: 'x', commit: () => throwError(() => ({ message: 'boom' })), undone: vi.fn(), failed });
    q.flush();
    expect(failed).toHaveBeenCalledWith({ message: 'boom' });
  });

  /** Regression (1.27.0): a caller (a tab/library switch) must be able to wait for the commit to land. */
  describe('flush() as a completion signal', () => {
    it('completes once a pending commit settles, after committed() ran', () => {
      const { bar } = fakeSnackBar();
      const q = new DeferredCommitQueue(bar);
      const commit$ = new Subject<string>();
      const committed = vi.fn();
      q.run({ label: 'x', commit: () => commit$, undone: vi.fn(), committed });
      const after = vi.fn();
      q.flush().subscribe(after);
      expect(after).not.toHaveBeenCalled();
      expect(committed).not.toHaveBeenCalled();
      commit$.next('ok');
      commit$.complete();
      expect(committed).toHaveBeenCalledWith('ok');
      expect(after).toHaveBeenCalledTimes(1);
    });

    it('completes once a pending commit settles even when it fails', () => {
      const { bar } = fakeSnackBar();
      const q = new DeferredCommitQueue(bar);
      const commit$ = new Subject<never>();
      const failed = vi.fn();
      q.run({ label: 'x', commit: () => commit$, undone: vi.fn(), failed });
      const after = vi.fn();
      q.flush().subscribe(after);
      commit$.error({ message: 'boom' });
      expect(failed).toHaveBeenCalledWith({ message: 'boom' });
      expect(after).toHaveBeenCalledTimes(1);
    });

    it('completes at once when nothing is pending', () => {
      const { bar } = fakeSnackBar();
      const q = new DeferredCommitQueue(bar);
      const after = vi.fn();
      q.flush().subscribe(after);
      expect(after).toHaveBeenCalledTimes(1);
    });

    it('still sends the pending commit when flush() is not subscribed to (leaving the page)', () => {
      const { bar } = fakeSnackBar();
      const q = new DeferredCommitQueue(bar);
      const commit = vi.fn(() => of('ok'));
      q.run({ label: 'x', commit, undone: vi.fn() });
      q.flush(); // no .subscribe()
      expect(commit).toHaveBeenCalledTimes(1);
    });
  });
});
