import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';

import {
  BackPopState, BackSurface, OVERLAY_BACK_HISTORY, OVERLAY_BACK_MARK, OVERLAY_BACK_PAGE, OVERLAY_BACK_STEP_TIMEOUT_MS, OVERLAY_BACK_TIDY_MS,
  OverlayBackHistory, OverlayBackService, overlayBackDepth,
} from './overlay-back.service';

/**
 * A same-document session history: `go()` is asynchronous as in a browser (the step lands later, as one `popstate`); the user's
 * Back / Forward land at once. `routerSaw` lists the `popstate`s the service let through (the router's listener would see those).
 */
class FakeHistory implements OverlayBackHistory {
  entries: { state: unknown; url: string }[] = [{ state: { navigationId: 1 }, url: 'https://mp.test/a' }];
  index = 0;
  routerSaw: string[] = [];
  private listener: ((e: BackPopState) => void) | null = null;
  private steps: number[] = [];

  get state(): unknown { return this.entries[this.index].state; }
  get href(): string { return this.entries[this.index].url; }
  get length(): number { return this.entries.length; }
  pushState(state: unknown, url: string): void {
    this.entries.splice(this.index + 1);
    this.entries.push({ state, url });
    this.index++;
  }
  replaceState(state: unknown, url: string): void { this.entries[this.index] = { state, url }; }
  go(delta: number): void { this.steps.push(delta); }
  listen(listener: (e: BackPopState) => void): void { this.listener = listener; }
  onLeave(listener: () => void): void { this.leaveListener = listener; }
  /** The document starts to go away (a reload, a full navigation). */
  leave(): void { this.leaveListener?.(); }
  private leaveListener: (() => void) | null = null;

  /** The user's Back (-1) / Forward (+1). */
  user(delta: number): void { this.land(delta); }
  /** Lands the steps the service asked for, one `popstate` each. */
  async settle(): Promise<void> {
    for (let round = 0; round < 10; round++) {
      await Promise.resolve();
      await Promise.resolve();
      const step = this.steps.shift();
      if (step !== undefined) this.land(step);
    }
  }
  get pendingSteps(): number { return this.steps.length; }
  /** What the history holds, from the first entry: the URL path and `*<depth>` for a marked entry; `>` marks the current one. */
  get picture(): string {
    return this.entries.map((e, i) => `${i === this.index ? '>' : ''}${new URL(e.url).pathname}${overlayBackDepth(e.state) ? '*' + overlayBackDepth(e.state) : ''}`).join(' ');
  }
  private land(delta: number): void {
    this.index = Math.max(0, Math.min(this.entries.length - 1, this.index + delta));
    let stopped = false;
    this.listener?.({ state: this.state, stopImmediatePropagation: () => { stopped = true; } });
    if (!stopped) this.routerSaw.push(new URL(this.href).pathname);
  }
}

class Surface implements BackSurface {
  closes = 0;
  constructor(private readonly service: OverlayBackService, private readonly closable = true) {}
  /** Back asked it to close: it does, as a tap outside would (the overlay layer reports the close later). */
  close(): boolean {
    this.closes++;
    if (this.closable) queueMicrotask(() => this.service.closed(this));
    return this.closable;
  }
}

/**
 * The Back stack of 1.52.0 (m1): one marked history entry per open surface; Back closes the top surface and stays; a surface closed
 * any other way leaves its entry SPENT (no history step of the service's own - nothing can race a reload), reused by the next surface
 * or skipped by a Back that finds nothing open; the router never pushes on top of a marked entry (it replaces it); the app's own Back
 * leaves the page.
 */
describe('OverlayBackService', () => {
  let history: FakeHistory;
  let service: OverlayBackService;

  beforeEach(() => {
    history = new FakeHistory();
    history.pushState({ navigationId: 2 }, 'https://mp.test/b');
    TestBed.configureTestingModule({ providers: [{ provide: OVERLAY_BACK_HISTORY, useValue: history }] });
    service = TestBed.inject(OverlayBackService);
  });

  /** The router's push / replace of `path` (as `OverlayBackLocationStrategy` hands them over). */
  const routerPush = (path: string, navigationId: number) => service.routerPush(
    () => history.pushState({ navigationId }, `https://mp.test${path}`),
    () => history.replaceState({ navigationId }, `https://mp.test${path}`));
  const routerReplace = (path: string, navigationId: number) =>
    service.routerReplace(() => history.replaceState({ navigationId }, `https://mp.test${path}`));
  /** Opens a surface and lets its entry be written. */
  const openSurface = async (closable = true) => {
    const s = new Surface(service, closable);
    service.opened(s);
    await history.settle();
    return s;
  };

  it('gives an open surface one entry of its own, with the router state, the page key and a depth mark', async () => {
    await openSurface();
    expect(history.picture).toBe('/a /b >/b*1');
    expect(history.state).toEqual({ navigationId: 2, [OVERLAY_BACK_PAGE]: expect.any(String), [OVERLAY_BACK_MARK]: 1 });
    expect(service.depth).toBe(1);
  });

  it('Back closes the open surface and stays on the page; the next Back reaches the router', async () => {
    const menu = await openSurface();
    history.user(-1);
    expect(menu.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1');
    expect(history.pendingSteps).toBe(0);

    history.user(-1);
    expect(history.routerSaw).toEqual(['/a']);
  });

  it('a surface closed otherwise leaves its entry spent - no step of its own - and one Back then leaves the page', async () => {
    const menu = await openSurface();
    service.closed(menu);
    await history.settle();
    expect(history.picture).toBe('/a /b >/b*1');
    expect(history.pendingSteps).toBe(0);

    history.user(-1); // lands on the page's own entry, nothing open: on past it, to the page before
    expect(history.routerSaw).toEqual([]);
    await history.settle();
    expect(history.routerSaw).toEqual(['/a']);
    expect(service.depth).toBe(0);
  });

  it('the next surface reuses a spent entry: opening and closing menus does not grow the history', async () => {
    const first = await openSurface();
    service.closed(first);
    const second = await openSurface();
    expect(history.picture).toBe('/a /b >/b*1');
    history.user(-1);
    expect([first.closes, second.closes]).toEqual([0, 1]);
  });

  it('nested surfaces (a menu opened from a sheet) take one Back each', async () => {
    const sheet = await openSurface();
    const menu = await openSurface();
    expect(history.picture).toBe('/a /b /b*1 >/b*2');

    history.user(-1);
    expect([sheet.closes, menu.closes]).toEqual([0, 1]);
    await history.settle();
    history.user(-1);
    expect([sheet.closes, menu.closes]).toEqual([1, 1]);
    await history.settle();
    expect(history.routerSaw).toEqual([]);
    history.user(-1);
    expect(history.routerSaw).toEqual(['/a']);
  });

  it('a spent entry above an open sheet does not swallow the Back: the sheet closes', async () => {
    const sheet = await openSurface();
    const menu = await openSurface();
    service.closed(menu); // Escape on the menu
    history.user(-1);
    expect(sheet.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
  });

  it('a menu that hands over to a dialog: Back closes the dialog', async () => {
    const menu = await openSurface();
    const dialog = new Surface(service);
    service.opened(dialog); // the dialog attaches while the menu animates out
    service.closed(menu);
    await history.settle();
    history.user(-1);
    expect(dialog.closes).toBe(1);
    expect(menu.closes).toBe(0);
  });

  it('a choice that navigates: the router REPLACES the surface entry - one Back returns to the page', async () => {
    const sheet = await openSurface();
    routerPush('/search', 3);
    service.closed(sheet); // the sheet closes on the way
    await history.settle();
    expect(history.picture).toBe('/a /b >/search');
    expect(history.pendingSteps).toBe(0);

    history.user(-1);
    expect(history.routerSaw).toEqual(['/b']);
  });

  it('nested surfaces when the router navigates: the old page keeps a marked entry, skipped after the router has the Back', async () => {
    await openSurface();
    await openSurface();
    routerPush('/c', 3);
    await history.settle();
    expect(history.picture).toBe('/a /b /b*1 >/c');
    history.user(-1); // onto the left-behind entry: the router navigates to /b, then the service steps down to /b's own entry
    expect(history.routerSaw).toEqual(['/b']);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1 /c');
    expect(history.routerSaw).toEqual(['/b']);
  });

  it('the router replacing the URL with a surface open rewrites the top entry and keeps its mark; Back lands on the new address', async () => {
    const menu = await openSurface();
    routerReplace('/b2', 3);
    expect(history.picture).toBe('/a /b >/b2*1');
    expect(history.state).toEqual({ navigationId: 3, [OVERLAY_BACK_PAGE]: expect.any(String), [OVERLAY_BACK_MARK]: 1 });

    history.user(-1);
    expect(menu.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
    expect(history.picture).toBe('/a >/b2 /b2*1');
    expect(history.state).toEqual({ navigationId: 3, [OVERLAY_BACK_PAGE]: expect.any(String) });
  });

  it("the app's own Back (Location.back) steps over the surface entries to the previous page", async () => {
    const menu = await openSurface();
    const go = vi.fn((d: number) => history.go(d));
    service.appStep(-1, go);
    expect(go).toHaveBeenCalledWith(-2);
    await history.settle();
    expect(history.routerSaw).toEqual(['/a']);
    expect(menu.closes).toBe(0);
    expect(service.depth).toBe(0);
  });

  it("the app's own Back with nothing open is the plain step", async () => {
    const go = vi.fn((d: number) => history.go(d));
    service.appStep(-1, go);
    expect(go).toHaveBeenCalledWith(-1);
    await history.settle();
    expect(history.routerSaw).toEqual(['/a']);
  });

  it('a surface that refuses to close (disableClose) keeps the page and its entry', async () => {
    const dialog = await openSurface(false);
    history.user(-1);
    expect(dialog.closes).toBe(1);
    await history.settle();
    expect(history.picture).toBe('/a /b >/b*1');
    expect(history.routerSaw).toEqual([]);
  });

  it('Forward onto an entry a surface closed by Back left behind steps back down, unseen', async () => {
    await openSurface();
    history.user(-1);
    await history.settle();
    history.user(1);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1');
    expect(history.routerSaw).toEqual([]);
  });

  it('several steps back at once (the history menu) reach the router and forget the page surfaces', async () => {
    const menu = await openSurface();
    history.user(-2);
    expect(history.routerSaw).toEqual(['/a']);
    expect(service.depth).toBe(0);
    service.closed(menu); // the page went away
    await history.settle();
    expect(history.picture).toBe('>/a /b /b*1');
  });

  it('stops waiting for a step the browser never takes', async () => {
    vi.useFakeTimers();
    try {
      const go = vi.fn(); // the app's own Back, never landing
      await openSurface();
      service.appStep(-1, go);
      routerPush('/c', 3);
      expect(history.picture).toBe('/a /b >/b*1');
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_STEP_TIMEOUT_MS);
      expect(history.picture).toBe('/a /b /b*1 >/c');
    } finally {
      vi.useRealTimers();
    }
  });

  it('a reload on a marked entry clears the marks', () => {
    const h = new FakeHistory();
    h.pushState({ navigationId: 2, [OVERLAY_BACK_PAGE]: 'v1', [OVERLAY_BACK_MARK]: 1 }, 'https://mp.test/b');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: OVERLAY_BACK_HISTORY, useValue: h }] });
    TestBed.inject(OverlayBackService);
    expect(h.state).toEqual({ navigationId: 2 });
  });

  describe('tidying spent entries', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    it('once the page has been quiet for a moment, a spent entry is stepped over unseen; Back then reaches the router directly', async () => {
      const menu = await openSurface();
      service.closed(menu);
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_TIDY_MS);
      await history.settle();
      expect(history.picture).toBe('/a >/b /b*1');
      expect(history.routerSaw).toEqual([]);
      expect(service.depth).toBe(0);
      history.user(-1);
      expect(history.routerSaw).toEqual(['/a']);
    });

    it('the document going away (a reload, a full navigation) right after a close: no step of its own', async () => {
      const menu = await openSurface();
      service.closed(menu);
      history.leave();
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_TIDY_MS * 4);
      expect(history.pendingSteps).toBe(0);
      expect(history.picture).toBe('/a /b >/b*1');
    });

    it('a surface opened within the moment reuses the spent entry instead', async () => {
      const menu = await openSurface();
      service.closed(menu);
      const dialog = await openSurface();
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_TIDY_MS * 4);
      expect(history.pendingSteps).toBe(0);
      expect(history.picture).toBe('/a /b >/b*1');
      history.user(-1);
      expect(dialog.closes).toBe(1);
    });

    it('a navigation after the spent entry was stepped over: one Back returns to the page, not to the page before it', async () => {
      const menu = await openSurface();
      service.closed(menu);
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_TIDY_MS);
      await history.settle();
      routerPush('/c', 3); // a link on the page, once it has been quiet (1.53.0: Favorites -> the reader)
      await history.settle();
      expect(history.picture).toBe('/a /b >/c');

      history.user(-1);
      expect(history.routerSaw).toEqual(['/b']);
      await history.settle();
      expect(history.picture).toBe('/a >/b /c');
      expect(history.routerSaw).toEqual(['/b']);
      expect(history.pendingSteps).toBe(0);
    });

    it('a spent entry above an open sheet is stepped over; the sheet keeps its own', async () => {
      const sheet = await openSurface();
      const menu = await openSurface();
      service.closed(menu);
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_TIDY_MS);
      await history.settle();
      expect(history.picture).toBe('/a /b >/b*1 /b*2');
      history.user(-1);
      expect(sheet.closes).toBe(1);
      expect(history.routerSaw).toEqual([]);
    });
  });
});
