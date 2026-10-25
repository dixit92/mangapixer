import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BreakpointObserver } from '@angular/cdk/layout';
import { of } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { WebtoonNavPreferencesService } from './webtoon-nav.service';
import { DoubleTapWindowMs } from './double-tap';
import { ManifestPageEntry } from '../../core/api/api-types';

/**
 * Vertical-mode tap zones (edges 1.41.0, owner 2026-10-11; reshaped 1.48.0, owner 2026-10-24), through the reader's
 * public handlers:
 *  - the left and right edges are full-height columns, 20 % of the width each, corners included, stepping a screen
 *    by the per-device "Right goes forward" (default) / "Left goes forward" setting - at once, never after the
 *    double-tap window;
 *  - the centre column has a short top band (15 %) = back and a short bottom band (15 %) = forward;
 *  - the rest of the centre keeps show / hide controls (single tap) and fullscreen (double tap);
 *  - the horizontal swipe follows the same setting; Tap to scroll Off turns all of it off.
 * Geometry: a fake scroller whose viewport is 400 x 600 px at (left 0, top 100), over a 5000 px strip at 1000 px.
 */

function makePages(n: number): ManifestPageEntry[] {
  return Array.from({ length: n }, (_, i) => ({
    entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: 800, height: 3000,
    animationState: 'None', byteSize: 1000,
  }));
}

/** Desktop / tablet width: the full bar (not the phone bar). */
const desktop = { observe: () => of({ matches: false, breakpoints: {} }), isMatched: () => false };

/** A minimal PointerEvent stand-in carrying only the fields the handlers read. */
function pointer(o: Partial<{ pointerId: number; clientX: number; clientY: number; timeStamp: number }>): PointerEvent {
  return { pointerId: 1, clientX: 0, clientY: 0, timeStamp: 0, preventDefault: () => { /* noop */ }, ...o } as unknown as PointerEvent;
}

const Left = 0, Top = 100, Width = 400, Height = 600;
/** One step at the default 90 %: 540 px from 1000. */
const Forward = { top: 1540, behavior: 'smooth' };
const Back = { top: 460, behavior: 'smooth' };

describe('Webtoon tap zones (edges 1.41.0, full-height edges + short bands 1.48.0)', () => {
  function create(opts: { forwardEdge?: 'right' | 'left'; stepOff?: boolean } = {}) {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: BreakpointObserver, useValue: desktop },
        { provide: ActivatedRoute, useValue: {
          paramMap: of(convertToParamMap({ itemId: 'item-1' })), snapshot: { queryParamMap: convertToParamMap({}) },
        } },
      ],
    });
    const nav = TestBed.inject(WebtoonNavPreferencesService);
    if (opts.forwardEdge) nav.setForwardEdge(opts.forwardEdge);
    if (opts.stepOff) nav.setTapStep(0);
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit (its requests stay unanswered)
    const c = fixture.componentInstance;
    c.pages.set(makePages(10));
    c.view.set('webtoon');
    c.phase.set('ready');
    c.isFullscreen.set(true);
    c.chromeVisible.set(true);
    const fullscreen = vi.spyOn(c, 'toggleFullscreen').mockImplementation(() => { /* the browser API is not under test */ });
    const calls: ScrollToOptions[] = [];
    const el = {
      scrollTop: 1000, clientHeight: Height, scrollHeight: 5000,
      getBoundingClientRect: () => ({ left: Left, top: Top, width: Width, height: Height }) as DOMRect,
      scrollTo: (o: ScrollToOptions) => calls.push(o),
      querySelectorAll: () => [],
    } as unknown as HTMLElement;
    (c as unknown as { scroller: () => { nativeElement: HTMLElement } }).scroller = () => ({ nativeElement: el });
    /** A tap at a fraction of the viewport (x from the left, y from the top). */
    const tapAt = (x: number, y: number) => c.onWebtoonTap(
      { clientX: Left + x * Width, clientY: Top + y * Height, target: { closest: () => null } } as unknown as MouseEvent);
    return { fixture, c, nav, fullscreen, calls, el, tapAt };
  }

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth'); // no GPU in jsdom: keep the "not available here" notice out
    localStorage.setItem('mangapixer-reader-help-seen', '1');
    vi.useFakeTimers();
  });
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); localStorage.clear(); });

  it('default "Right goes forward": the left edge goes back a screen, the right edge forward', () => {
    const { calls, tapAt } = create();
    tapAt(0.1, 0.5);
    expect(calls).toEqual([Back]);
    tapAt(0.9, 0.5);
    expect(calls).toEqual([Back, Forward]);
  });

  it('"Left goes forward": the left edge goes forward, the right edge back', () => {
    const { calls, tapAt } = create({ forwardEdge: 'left' });
    tapAt(0.1, 0.5);
    expect(calls).toEqual([Forward]);
    tapAt(0.9, 0.5);
    expect(calls).toEqual([Forward, Back]);
  });

  it('the setting applies live, without reopening the reader', () => {
    const { calls, nav, tapAt } = create();
    tapAt(0.05, 0.4);
    nav.setForwardEdge('left');
    tapAt(0.05, 0.4);
    expect(calls).toEqual([Back, Forward]);
  });

  it('an edge tap steps at once: no double-tap wait, no chrome toggle, and two quick edge taps are two steps, never fullscreen', () => {
    const { c, fullscreen, calls, tapAt } = create();
    tapAt(0.95, 0.5);
    expect(calls.length, 'stepped before any timer ran').toBe(1);
    tapAt(0.95, 0.5);
    expect(calls.length).toBe(2);
    vi.advanceTimersByTime(DoubleTapWindowMs + 500);
    expect(fullscreen).not.toHaveBeenCalled();
    expect(c.chromeVisible()).toBe(true);
  });

  it('corners belong to the edges, whatever the setting: the full-height edge decides, top and bottom alike', () => {
    for (const forwardEdge of ['right', 'left'] as const) {
      TestBed.resetTestingModule();
      const { calls, tapAt } = create({ forwardEdge });
      tapAt(0.02, 0.05);  // top-left
      tapAt(0.98, 0.05);  // top-right
      tapAt(0.02, 0.95);  // bottom-left
      tapAt(0.98, 0.95);  // bottom-right
      const [l, r] = forwardEdge === 'right' ? [Back, Forward] : [Forward, Back];
      expect(calls, forwardEdge).toEqual([l, r, l, r]);
    }
  });

  it('an edge tap near the top steps by the edge (not back by a top band); the same height in the centre goes back', () => {
    const { calls, tapAt } = create();
    tapAt(0.95, 0.05); // right edge, top: forward (the edge), where 1.41.0's top third said back
    expect(calls).toEqual([Forward]);
    tapAt(0.5, 0.05); // centre column, top band: back
    expect(calls).toEqual([Forward, Back]);
    tapAt(0.5, 0.95); // centre column, bottom band: forward
    expect(calls).toEqual([Forward, Back, Forward]);
  });

  it('the centre column below the top band and above the bottom band only shows / hides the controls', () => {
    const { c, calls, tapAt } = create();
    for (const y of [0.15, 0.3, 0.5, 0.7, 0.84]) {
      c.chromeVisible.set(true);
      tapAt(0.5, y);
      vi.advanceTimersByTime(DoubleTapWindowMs + 20);
      expect(c.chromeVisible(), `y ${y}`).toBe(false);
    }
    expect(calls.length).toBe(0);
  });

  it('the centre between the edges keeps show / hide (after the window) and double tap = fullscreen; 20 % is the centre', () => {
    const { c, fullscreen, calls, tapAt } = create();
    tapAt(0.2, 0.5); // exactly at the edge's boundary: the centre's
    expect(c.chromeVisible()).toBe(true);
    vi.advanceTimersByTime(DoubleTapWindowMs + 20);
    expect(c.chromeVisible()).toBe(false);
    tapAt(0.5, 0.5);
    vi.advanceTimersByTime(100);
    tapAt(0.5, 0.5);
    expect(fullscreen).toHaveBeenCalledTimes(1);
    expect(calls.length).toBe(0);
  });

  it('Tap to scroll Off turns the edges off too: an edge tap is a centre tap (controls / fullscreen), nothing scrolls', () => {
    const { c, fullscreen, calls, tapAt } = create({ stepOff: true });
    tapAt(0.05, 0.5);
    vi.advanceTimersByTime(DoubleTapWindowMs + 20);
    expect(c.chromeVisible()).toBe(false);
    tapAt(0.95, 0.5);
    tapAt(0.95, 0.5);
    expect(fullscreen).toHaveBeenCalledTimes(1);
    expect(calls.length).toBe(0);
  });

  describe('the horizontal swipe follows the setting', () => {
    function swipe(c: ReaderComponent, id: number, fromX: number, toX: number): void {
      c.onReaderPointerDown(pointer({ pointerId: id, clientX: fromX, clientY: 400, timeStamp: id * 1000 }));
      c.onReaderPointerMove(pointer({ pointerId: id, clientX: (fromX + toX) / 2, clientY: 401, timeStamp: id * 1000 + 50 }));
      c.onReaderPointerUp(pointer({ pointerId: id, clientX: toX, clientY: 402, timeStamp: id * 1000 + 100 }));
    }

    it('"Right goes forward" (default): swipe left = forward, swipe right = back (the 1.11.0 rule)', () => {
      const { c, calls } = create();
      swipe(c, 1, 300, 150);
      expect(calls).toEqual([Forward]);
      vi.advanceTimersByTime(1000);
      swipe(c, 2, 150, 300);
      expect(calls).toEqual([Forward, Back]);
    });

    it('"Left goes forward": swipe right = forward, swipe left = back', () => {
      const { c, calls } = create({ forwardEdge: 'left' });
      swipe(c, 1, 150, 300);
      expect(calls).toEqual([Forward]);
      vi.advanceTimersByTime(1000);
      swipe(c, 2, 300, 150);
      expect(calls).toEqual([Forward, Back]);
    });

    it('the ghost click a swipe leaves on an edge is swallowed (no second step)', () => {
      const { c, calls, tapAt } = create({ forwardEdge: 'left' });
      swipe(c, 1, 20, 160);
      tapAt(0.4, 0.5); // the click the browser synthesizes on release (here: the centre)
      tapAt(0.05, 0.5); // ... or an edge
      expect(calls).toEqual([Forward]);
    });

    it('Tap to scroll Off: no swipe either', () => {
      const { c, calls } = create({ stepOff: true, forwardEdge: 'left' });
      swipe(c, 1, 150, 300);
      expect(calls.length).toBe(0);
    });
  });

  describe('help overlay', () => {
    /** The meaning under each edge's icon (the line after "Tap"). */
    const labels = (root: HTMLElement) => ({
      left: root.querySelector('.help-zones.vertical .edge-left span')?.lastChild?.textContent?.trim(),
      right: root.querySelector('.help-zones.vertical .edge-right span')?.lastChild?.textContent?.trim(),
    });

    it('the vertical diagram shows both edges with the meaning the setting gives them', () => {
      const { fixture, c, nav } = create();
      c.toggleHelp();
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(labels(root)).toEqual({ left: 'Back a screen', right: 'Forward a screen' });
      expect(root.querySelector('.help-panel')?.textContent?.replace(/\s+/g, ' '))
        .toContain('the right edge moves forward and the left edge back');
      nav.setForwardEdge('left');
      fixture.detectChanges();
      expect(labels(root)).toEqual({ left: 'Forward a screen', right: 'Back a screen' });
      expect(root.querySelector('.help-panel')?.textContent?.replace(/\s+/g, ' '))
        .toContain('Swipe right to move forward, left to go back');
    });

    it('the diagram is two full-height edges around a centre column: back band, toggle, forward band', () => {
      const { fixture, c } = create();
      c.toggleHelp();
      fixture.detectChanges();
      const zones = (fixture.nativeElement as HTMLElement).querySelector('.help-zones.vertical')!;
      const kids = Array.from(zones.children).map((k) => k.className);
      expect(kids[0]).toContain('edge-left');
      expect(kids[1]).toContain('help-col');
      expect(kids[2]).toContain('edge-right');
      const col = Array.from(zones.querySelector('.help-col')!.children);
      expect(col.map((k) => k.className.includes('top') ? 'top' : k.className.includes('bottom') ? 'bottom' : 'centre'))
        .toEqual(['top', 'centre', 'bottom']);
      expect(col.map((k) => k.textContent?.replace(/\s+/g, ' ').trim())).toEqual([
        'touch_app Tap Back a screen',
        'touch_app Tap: show / hide controls Double tap: fullscreen',
        'touch_app Tap Forward a screen',
      ]);
    });

    it('with Tap to scroll Off there is no zone diagram (taps only show / hide the controls)', () => {
      const { fixture, c } = create({ stepOff: true });
      c.toggleHelp();
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('.help-zones')).toBeNull();
    });
  });
});
