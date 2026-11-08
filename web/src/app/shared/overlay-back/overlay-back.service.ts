import { Injectable, InjectionToken, inject } from '@angular/core';

/**
 * Back closes an open surface, and only that (1.52.0, mobile sweep m1).
 *
 * A phone's back gesture and a browser's Back button step through the session history; with a menu, a bottom sheet, a dialog or a
 * select's list open they used to leave the page as well. This service gives every open surface ("surface": anything the overlay
 * layer reports, see `overlay-back.providers.ts`) one history entry of its own - same URL, the router's state plus a depth mark - so
 * Back lands on the entry below, and the service closes the topmost surface instead of letting the router navigate. Nested surfaces
 * (a menu opened from a sheet) take one Back each.
 *
 * The bookkeeping, in one sentence: the browser holds `entries` marked entries above the page's own entry, and the service keeps
 * `entries` equal to the number of open surfaces. A surface closed any other way (Escape, a tap outside, a choice) leaves one entry
 * too many, so the service steps back over it (`history.go`) and swallows that `popstate`; nobody else sees it. Every `popstate` of
 * its own is stopped before the router's listener (a capturing listener on `window`), so the router, Material's close-on-navigation
 * and the pages' own Back handling (Favorites' route reuse, the reader's in-app Back, the tracking list's memory) never see one.
 *
 * The router never writes a URL on top of a marked entry: its push / replace (through `OverlayBackLocationStrategy`) first steps back
 * to the page's own entry, then writes, then marks the surfaces still open again. So a choice that navigates (a menu item, a chip in
 * the Info sheet, a dialog that opens a page on close) leaves exactly one entry for the old page - one Back returns to it. The app's
 * own Back (`Location.back()`, the reader's exit) steps over the marked entries to the real previous page.
 *
 * Only history is touched; nothing is stored. A reload on a marked entry keeps the entry (its mark is cleared) - the next Back then
 * reloads the same page once.
 */
export const OVERLAY_BACK_MARK = 'mpOverlayBack';

/** An open surface Back can close. `close()` answers false when the surface refuses (a dialog that cannot be dismissed). */
export interface BackSurface {
  close(): boolean;
}

/** The `popstate` event as this service uses it. */
export interface BackPopState {
  readonly state: unknown;
  stopImmediatePropagation(): void;
}

/** The browser history as this service uses it (a seam for the unit tests). */
export interface OverlayBackHistory {
  readonly state: unknown;
  readonly href: string;
  pushState(state: unknown, url: string): void;
  replaceState(state: unknown, url: string): void;
  go(delta: number): void;
  /** Listens to `popstate` before any other listener (capturing, on `window`). */
  listen(listener: (event: BackPopState) => void): void;
}

export const OVERLAY_BACK_HISTORY = new InjectionToken<OverlayBackHistory>('OVERLAY_BACK_HISTORY', {
  providedIn: 'root',
  factory: (): OverlayBackHistory => ({
    get state() { return globalThis.history?.state; },
    get href() { return globalThis.location?.href ?? ''; },
    pushState: (state, url) => globalThis.history?.pushState(state, '', url),
    replaceState: (state, url) => globalThis.history?.replaceState(state, '', url),
    go: (delta) => globalThis.history?.go(delta),
    listen: (listener) => globalThis.addEventListener?.('popstate', listener as unknown as EventListener, { capture: true }),
  }),
});

/** How long a step of ours may take before the service stops waiting for its `popstate` (a step the browser did not take). */
export const OVERLAY_BACK_STEP_TIMEOUT_MS = 1000;

/** The depth mark of a history state: 1 for the first surface's entry, 2 for the next; 0 for an entry that is not ours. */
export function overlayBackDepth(state: unknown): number {
  const v = state && typeof state === 'object' ? (state as Record<string, unknown>)[OVERLAY_BACK_MARK] : undefined;
  return typeof v === 'number' && v > 0 ? v : 0;
}

/** `state` without the depth mark (the router's own state, as it wrote it). */
function unmarked(state: unknown): Record<string, unknown> {
  if (!state || typeof state !== 'object') return {};
  const copy = { ...(state as Record<string, unknown>) };
  delete copy[OVERLAY_BACK_MARK];
  return copy;
}

@Injectable({ providedIn: 'root' })
export class OverlayBackService {
  private readonly history = inject(OVERLAY_BACK_HISTORY);

  /** Open surfaces, oldest first (the last one is on top). */
  private readonly open: BackSurface[] = [];
  /** Marked entries above the page's own entry, as the browser holds them once a step of ours has landed. */
  private entries = 0;
  /** The URL of the page the marked entries belong to. */
  private baseHref = '';
  /** The URL the page was last known at (router writes and `popstate`s that reached the router). */
  private here = '';
  /** A step of ours in flight: `drop` (its `popstate` is swallowed) or `leave` (the app's own Back; the router gets it). */
  private pending: 'drop' | 'leave' | null = null;
  private pendingTimer: ReturnType<typeof setTimeout> | null = null;
  /** Work that waits for the step in flight to land. */
  private readonly queue: (() => void)[] = [];
  private scheduled = false;

  constructor() {
    this.history.listen((event) => this.onPopState(event));
    this.here = this.history.href;
    // A reload on a marked entry: the surfaces are gone, so is the mark (the entries below belong to the old document).
    if (overlayBackDepth(this.history.state) > 0) this.history.replaceState(unmarked(this.history.state), this.history.href);
  }

  /** How many marked entries the browser holds (for tests and diagnostics). */
  get depth(): number {
    return this.entries;
  }

  /** A surface opened: it gets an entry of its own. */
  opened(surface: BackSurface): void {
    if (this.open.includes(surface)) return;
    this.open.push(surface);
    this.schedule();
  }

  /** A surface closed (by any means): its entry goes, unless Back already took it. */
  closed(surface: BackSurface): void {
    const i = this.open.indexOf(surface);
    if (i < 0) return;
    this.open.splice(i, 1);
    this.schedule();
  }

  /** The router writes a URL (`push` or `replace`): first back to the page's own entry, then the write, then the open surfaces' entries. */
  routerWrite(write: () => void): void {
    this.whenIdle(() => {
      if (this.entries === 0) {
        write();
        this.here = this.history.href;
        return;
      }
      const d = this.entries;
      this.entries = 0;
      this.step(-d, 'drop', () => {
        write();
        this.here = this.history.href;
        this.reconcile();
      });
    });
  }

  /** The app steps through history itself (`Location.back()` / `historyGo`): a step back goes past the marked entries. */
  appStep(delta: number, go: (delta: number) => void): void {
    this.whenIdle(() => {
      if (this.entries === 0 || delta >= 0) {
        go(delta);
        return;
      }
      const d = delta - this.entries;
      this.entries = 0;
      this.open.length = 0; // the router leaves the page; its surfaces close with it
      this.pending = 'leave';
      this.armTimer();
      go(d);
    });
  }

  private onPopState(event: BackPopState): void {
    if (this.pending) {
      const kind = this.pending;
      this.clearPending();
      if (kind === 'drop') event.stopImmediatePropagation();
      else this.here = this.history.href;
      this.drain();
      return;
    }
    const href = this.history.href;
    const depth = overlayBackDepth(event.state);
    if (this.entries > 0 && href === this.baseHref) {
      if (depth < this.entries) {
        // Back (one or more steps) on this page: close as many surfaces, top first.
        event.stopImmediatePropagation();
        let n = this.entries - depth;
        this.entries = depth;
        while (n-- > 0 && this.open.length) {
          const top = this.open.pop()!;
          if (!top.close()) {
            this.open.push(top); // it stays open: its entry is written again below
            break;
          }
        }
        this.schedule();
        return;
      }
      if (depth > this.entries) {
        // Forward onto an entry a surface closed by Back left behind: step back down.
        event.stopImmediatePropagation();
        this.step(this.entries - depth, 'drop');
        return;
      }
    }
    if (depth > 0 && this.entries === 0 && href === this.here) {
      // Forward onto a left-behind entry of this page: nothing to show, step back down.
      event.stopImmediatePropagation();
      this.step(-depth, 'drop');
      return;
    }
    // Another page (several steps at once, or the history menu): the router navigates; this page's surfaces close with it.
    this.entries = 0;
    this.open.length = 0;
    this.here = href;
    if (depth > 0) this.step(-depth, 'drop'); // landed on a left-behind entry of that page: down to its own entry
  }

  /** Brings the browser's marked entries in line with the open surfaces (batched: a menu that hands over to a dialog nets out). */
  private schedule(): void {
    if (this.scheduled) return;
    this.scheduled = true;
    queueMicrotask(() => {
      this.scheduled = false;
      this.whenIdle(() => this.reconcile());
    });
  }

  private reconcile(): void {
    const want = this.open.length;
    if (this.entries < want) {
      const href = this.history.href;
      if (this.entries === 0) this.baseHref = href;
      const base = unmarked(this.history.state);
      while (this.entries < want) {
        this.entries++;
        this.history.pushState({ ...base, [OVERLAY_BACK_MARK]: this.entries }, href);
      }
    } else if (this.entries > want) {
      const d = want - this.entries;
      this.entries = want;
      this.step(d, 'drop');
    }
  }

  private step(delta: number, kind: 'drop' | 'leave', then?: () => void): void {
    this.pending = kind;
    if (then) this.queue.unshift(then);
    this.armTimer();
    this.history.go(delta);
  }

  private armTimer(): void {
    if (this.pendingTimer !== null) clearTimeout(this.pendingTimer);
    this.pendingTimer = setTimeout(() => {
      this.pendingTimer = null;
      if (!this.pending) return;
      this.pending = null;
      this.drain();
    }, OVERLAY_BACK_STEP_TIMEOUT_MS);
  }

  private clearPending(): void {
    this.pending = null;
    if (this.pendingTimer !== null) clearTimeout(this.pendingTimer);
    this.pendingTimer = null;
  }

  private whenIdle(work: () => void): void {
    if (this.pending) this.queue.push(work);
    else work();
  }

  private drain(): void {
    while (!this.pending && this.queue.length) this.queue.shift()!();
  }
}
