import { Injectable, InjectionToken, inject } from '@angular/core';

/**
 * Back closes an open surface, and only that (1.52.0, mobile sweep m1).
 *
 * A phone's back gesture and a browser's Back button step through the session history; with a menu, a bottom sheet, a dialog or a
 * select's list open they used to leave the page as well. This service gives every open surface ("surface": what the overlay layer
 * reports, see `overlay-back.providers.ts`, and the inline confirm panels) a history entry of its own - same URL, the router's state
 * plus a page key and a depth mark - so Back lands on the entry below, and the service closes the topmost surface instead of letting
 * the router navigate. Nested surfaces (a menu opened from a sheet) take one Back each. Every `popstate` the service handles is stopped
 * before the router's listener (a capturing listener on `window`), so the router, Material's close-on-navigation and the pages' own
 * Back handling (Favorites' route reuse, the reader's in-app Back, the tracking list's memory) never see one.
 *
 * The service never steps through history on its own while the page may be navigating: a surface closed another way (Escape, a tap
 * outside, a choice) leaves its entry in place, SPENT - the next surface reuses it, and a Back that finds nothing open goes on past
 * the page's own entries to the page before (one Back, as the user meant). So a reload or a full navigation right after a close never
 * races a history step of ours. The router never writes on top of a marked entry: its push (through `OverlayBackLocationStrategy`)
 * REPLACES the top marked entry, so the old page keeps exactly one entry a Back can land on; a replace (a query parameter) rewrites the
 * top entry and keeps its mark. `Location.back()` / `historyGo` (the reader's in-app Back) step over the marked entries to the real
 * previous page.
 *
 * Only history is touched; nothing is stored. Marked entries left behind by a reload or by a navigation away (a Back later lands on
 * them) are recognised by their page key: the router gets the landing as any Back, and the service then steps down to that page's own
 * entry.
 */
export const OVERLAY_BACK_MARK = 'mpOverlayBack';
/** The page visit a marked entry belongs to (also put on the page's own entry when its first surface opens). */
export const OVERLAY_BACK_PAGE = 'mpOverlayPage';

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

type State = Record<string, unknown>;

/** The depth mark of a history state: 1 for the first surface's entry, 2 for the next; 0 for any other entry. */
export function overlayBackDepth(state: unknown): number {
  const v = state && typeof state === 'object' ? (state as State)[OVERLAY_BACK_MARK] : undefined;
  return typeof v === 'number' && v > 0 ? v : 0;
}

function pageOf(state: unknown): string | null {
  const v = state && typeof state === 'object' ? (state as State)[OVERLAY_BACK_PAGE] : undefined;
  return typeof v === 'string' ? v : null;
}

/** `state` without the service's fields (the router's own state, as it wrote it). */
function unmarked(state: unknown): State {
  if (!state || typeof state !== 'object') return {};
  const copy = { ...(state as State) };
  delete copy[OVERLAY_BACK_MARK];
  delete copy[OVERLAY_BACK_PAGE];
  return copy;
}

let visits = 0;

@Injectable({ providedIn: 'root' })
export class OverlayBackService {
  private readonly history = inject(OVERLAY_BACK_HISTORY);

  /** Open surfaces, oldest first (the last one is on top). Each owns one of the marked entries; the ones above them are spent. */
  private readonly open: BackSurface[] = [];
  /** Marked entries above the page's own entry; the browser stands on the top one (or on the page's entry when 0). */
  private entries = 0;
  /** This page visit's key while it has marked entries (null otherwise). */
  private page: string | null = null;
  /** What the router last wrote for this page: the address and state every entry of the page gets. */
  private pageUrl = '';
  private pageState: State = {};
  /** A step of ours in flight: `drop` (its `popstate` is swallowed) or `leave` (the router gets it). */
  private pending: 'drop' | 'leave' | null = null;
  private pendingTimer: ReturnType<typeof setTimeout> | null = null;
  /** Work that waits for the step in flight to land. */
  private readonly queue: (() => void)[] = [];
  private scheduled = false;

  constructor() {
    this.history.listen((event) => this.onPopState(event));
    // A reload (or a restored tab) on an entry of ours: its surfaces are gone, so are the marks.
    const state = this.history.state;
    if (overlayBackDepth(state) > 0 || pageOf(state) !== null) this.history.replaceState(unmarked(state), this.history.href);
  }

  /** How many marked entries the page holds (for tests and diagnostics). */
  get depth(): number {
    return this.entries;
  }

  /** A surface opened: it gets an entry of its own (a spent one when there is one). */
  opened(surface: BackSurface): void {
    if (this.open.includes(surface)) return;
    this.open.push(surface);
    this.schedule();
  }

  /** A surface closed (by any means): its entry stays, spent, for the next surface or for a Back that leaves. */
  closed(surface: BackSurface): void {
    const i = this.open.indexOf(surface);
    if (i >= 0) this.open.splice(i, 1);
  }

  /**
   * The router adds an entry (`push`). With marked entries the top one is REPLACED by the new page (the old page keeps one entry
   * below it); the surfaces still open go with the old page.
   */
  routerPush(push: () => void, replace: () => void): void {
    this.whenIdle(() => {
      if (this.entries === 0) {
        push();
      } else {
        replace();
        this.entries = 0;
        this.open.length = 0;
        this.page = null;
      }
      this.remember();
    });
  }

  /** The router rewrites the current entry (`replace`): with marked entries the top one keeps its mark, with the new address. */
  routerReplace(replace: () => void): void {
    this.whenIdle(() => {
      replace();
      this.remember();
      if (this.entries > 0) this.history.replaceState(this.mark(this.entries), this.pageUrl);
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
      this.forget();
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
      this.drain();
      return;
    }
    const depth = overlayBackDepth(event.state);
    if (this.page !== null && pageOf(event.state) === this.page) {
      // An entry of this page: nothing reaches the router.
      event.stopImmediatePropagation();
      if (depth > this.entries) {
        // Forward onto an entry a surface closed by Back left behind: back down, unseen.
        this.step(this.entries - depth, 'drop');
        return;
      }
      const moved = this.entries - depth;
      this.entries = depth;
      // A router replace rewrote only the top entry: the one landed on gets the page's current address and state.
      if (this.history.href !== this.pageUrl) this.history.replaceState(this.mark(depth), this.pageUrl);
      if (this.open.length > 0) {
        // Back: the top surface closes (one per step, at least one - a spent entry above it does not swallow the Back).
        let n = Math.max(1, Math.min(moved, this.open.length));
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
      // Nothing open: this Back means to leave the page - past its own entries to the one before; the router gets that one.
      this.forget();
      this.pending = 'leave';
      this.armTimer();
      this.history.go(-(depth + 1));
      return;
    }
    // Another page's entry: the router navigates; this page's surfaces close with it.
    this.forget();
    // A marked entry an earlier visit left behind (a navigation away, a reload): once the router has it, down to that page's entry.
    if (depth > 0) this.step(-depth, 'drop');
  }

  /** Gives the open surfaces their entries (batched: a menu that hands over to a dialog nets out to the entry it had). */
  private schedule(): void {
    if (this.scheduled) return;
    this.scheduled = true;
    queueMicrotask(() => {
      this.scheduled = false;
      this.whenIdle(() => this.reconcile());
    });
  }

  private reconcile(): void {
    if (this.open.length <= this.entries) return;
    if (this.entries === 0) {
      this.remember();
      this.page = `v${++visits}.${Date.now().toString(36)}`;
      this.history.replaceState(this.mark(0), this.pageUrl);
    }
    while (this.entries < this.open.length) {
      this.entries++;
      this.history.pushState(this.mark(this.entries), this.pageUrl);
    }
  }

  /** The page's current address and router state, as the browser holds them now. */
  private remember(): void {
    this.pageUrl = this.history.href;
    this.pageState = unmarked(this.history.state);
  }

  /** The state of this page's entry at `depth` (0 = the page's own entry). */
  private mark(depth: number): State {
    const state: State = { ...this.pageState, [OVERLAY_BACK_PAGE]: this.page };
    if (depth > 0) state[OVERLAY_BACK_MARK] = depth;
    return state;
  }

  private forget(): void {
    this.entries = 0;
    this.open.length = 0;
    this.page = null;
  }

  private step(delta: number, kind: 'drop' | 'leave'): void {
    this.pending = kind;
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
