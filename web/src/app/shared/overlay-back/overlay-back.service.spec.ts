import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';

import {
  BackPopState, BackSurface, OVERLAY_BACK_HISTORY, OVERLAY_BACK_MARK, OVERLAY_BACK_STEP_TIMEOUT_MS, OverlayBackHistory,
  OverlayBackService, overlayBackDepth,
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
 * The Back stack of 1.52.0 (m1): one marked history entry per open surface; Back closes the top one and stays; any other close drops
 * its entry without anyone seeing a `popstate`; the router never writes on top of a marked entry; the app's own Back leaves the page.
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

  /** The router's push of `path` (as `OverlayBackLocationStrategy` hands it over). */
  const routerPush = (path: string, navigationId: number) =>
    service.routerWrite(() => history.pushState({ navigationId }, `https://mp.test${path}`));
  const routerReplace = (path: string, navigationId: number) =>
    service.routerWrite(() => history.replaceState({ navigationId }, `https://mp.test${path}`));

  it('gives an open surface one entry of its own, with the router state and a depth mark', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
    expect(history.picture).toBe('/a /b >/b*1');
    expect(history.state).toEqual({ navigationId: 2, [OVERLAY_BACK_MARK]: 1 });
    expect(service.depth).toBe(1);
  });

  it('Back closes the open surface and stays on the page; the next Back reaches the router', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();

    history.user(-1);
    expect(menu.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1');
    expect(history.pendingSteps).toBe(0);

    history.user(-1);
    expect(history.routerSaw).toEqual(['/a']);
  });

  it('a surface closed otherwise (Escape, a tap outside, a choice) drops its entry unseen: one Back then leaves', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
    service.closed(menu);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1');
    expect(history.routerSaw).toEqual([]);
    expect(service.depth).toBe(0);

    history.user(-1);
    expect(history.routerSaw).toEqual(['/a']);
  });

  it('nested surfaces (a menu opened from a sheet) take one Back each', async () => {
    const sheet = new Surface(service);
    const menu = new Surface(service);
    service.opened(sheet);
    await history.settle();
    service.opened(menu);
    await history.settle();
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

  it('a menu that hands over to a dialog leaves one entry, for the dialog', async () => {
    const menu = new Surface(service);
    const dialog = new Surface(service);
    service.opened(menu);
    await history.settle();
    service.opened(dialog); // the dialog attaches while the menu animates out
    service.closed(menu);
    await history.settle();
    expect(history.picture).toBe('/a /b >/b*1');

    history.user(-1);
    expect(dialog.closes).toBe(1);
    expect(menu.closes).toBe(0);
  });

  it('a choice that navigates: the router writes on the page entry, not on the surface entry - one Back returns', async () => {
    const sheet = new Surface(service);
    service.opened(sheet);
    await history.settle();
    routerPush('/search', 3);
    expect(history.picture).toBe('/a /b >/b*1'); // nothing written before the step back has landed
    service.closed(sheet); // the sheet closes on the way
    await history.settle();
    expect(history.picture).toBe('/a /b >/search');
    expect(history.routerSaw).toEqual([]);

    history.user(-1);
    expect(history.routerSaw).toEqual(['/b']);
  });

  it('a surface still open after a navigation keeps an entry on the new page', async () => {
    const dialog = new Surface(service);
    service.opened(dialog);
    await history.settle();
    routerPush('/c', 3);
    await history.settle();
    expect(history.picture).toBe('/a /b /c >/c*1');
    history.user(-1);
    expect(dialog.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
  });

  it('the router replacing the URL with a surface open rewrites the page entry and marks the surface again', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
    routerReplace('/b2', 3);
    await history.settle();
    expect(history.picture).toBe('/a /b2 >/b2*1');
    expect(history.state).toEqual({ navigationId: 3, [OVERLAY_BACK_MARK]: 1 });

    history.user(-1);
    expect(menu.closes).toBe(1);
    expect(history.routerSaw).toEqual([]);
  });

  it("the app's own Back (Location.back) steps over the surface entries to the previous page", async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
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
    const dialog = new Surface(service, false);
    service.opened(dialog);
    await history.settle();
    history.user(-1);
    expect(dialog.closes).toBe(1);
    await history.settle();
    expect(history.picture).toBe('/a /b >/b*1');
    expect(history.routerSaw).toEqual([]);
  });

  it('Forward onto an entry a closed surface left behind steps back down, unseen', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
    history.user(-1);
    await history.settle();
    history.user(1);
    await history.settle();
    expect(history.picture).toBe('/a >/b /b*1');
    expect(history.routerSaw).toEqual([]);
  });

  it('several steps back at once (the history menu) reach the router and forget the page surfaces', async () => {
    const menu = new Surface(service);
    service.opened(menu);
    await history.settle();
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
      const menu = new Surface(service);
      service.opened(menu);
      await vi.advanceTimersByTimeAsync(0);
      service.closed(menu);
      await vi.advanceTimersByTimeAsync(0); // the step back is asked for but never lands
      routerPush('/c', 3);
      expect(history.picture).toBe('/a /b >/b*1');
      await vi.advanceTimersByTimeAsync(OVERLAY_BACK_STEP_TIMEOUT_MS);
      expect(history.picture).toBe('/a /b /b*1 >/c');
    } finally {
      vi.useRealTimers();
    }
  });

  it('a reload on a marked entry clears the mark', () => {
    const h = new FakeHistory();
    h.pushState({ navigationId: 2, [OVERLAY_BACK_MARK]: 1 }, 'https://mp.test/b');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [{ provide: OVERLAY_BACK_HISTORY, useValue: h }] });
    TestBed.inject(OverlayBackService);
    expect(h.state).toEqual({ navigationId: 2 });
  });
});
