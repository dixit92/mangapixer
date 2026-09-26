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
});
