import { vi } from 'vitest';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, ParamMap, Router, convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BreakpointObserver } from '@angular/cdk/layout';
import { BehaviorSubject, of } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { WebtoonNavPreferencesService } from './webtoon-nav.service';
import { ManifestPageEntry } from '../../core/api/api-types';

/**
 * Reader top bar + gestures (1.41.0, owner decisions 5-7):
 *  - a double tap / double click in the CENTRE zone toggles fullscreen; a single centre tap still shows / hides
 *    the controls, but only once the double-tap window has passed without a second tap;
 *  - the bar offers "Series info" when the open archive resolves to series information, and opens the series page.
 * Written against the reader's public surface and its DOM only, so the same file runs on the code before the change.
 */

const DoubleTapWindowMs = 280; // the reader's one constant (any value in 250-300 ms passes these checks)

function makePages(n: number): ManifestPageEntry[] {
  return Array.from({ length: n }, (_, i) => ({
    entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: 800, height: 1200,
    animationState: 'None', byteSize: 1000,
  }));
}

/** Desktop / tablet width: the full bar (not the phone bar). */
const desktop = { observe: () => of({ matches: false, breakpoints: {} }), isMatched: () => false };

function baseProviders() {
  return [
    provideRouter([]),
    provideHttpClient(),
    provideHttpClientTesting(),
    provideNoopAnimations(),
    { provide: BreakpointObserver, useValue: desktop },
  ];
}

/** A minimal PointerEvent stand-in carrying only the fields the handlers read. */
function pointer(o: Partial<{ pointerId: number; clientX: number; clientY: number; timeStamp: number }>): PointerEvent {
  return { pointerId: 1, clientX: 0, clientY: 0, timeStamp: 0, preventDefault: () => { /* noop */ }, ...o } as unknown as PointerEvent;
}

describe('Reader centre double tap: fullscreen (1.41.0)', () => {
  function paged() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [...baseProviders(), { provide: ActivatedRoute, useValue: {
        paramMap: of(convertToParamMap({ itemId: 'item-1' })), snapshot: { queryParamMap: convertToParamMap({}) },
      } }],
    });
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit (its requests stay unanswered)
    const c = fixture.componentInstance;
    c.pages.set(makePages(6));
    c.view.set('paged');
    c.phase.set('ready');
    c.currentPage.set(1);
    // Fullscreen, controls showing: a centre tap would hide them.
    c.isFullscreen.set(true);
    c.chromeVisible.set(true);
    const fullscreen = vi.spyOn(c, 'toggleFullscreen').mockImplementation(() => { /* the browser API is not under test */ });
    fixture.detectChanges();
    const centre = () => (fixture.nativeElement as HTMLElement).querySelector('.tap-toggle') as HTMLButtonElement;
    return { fixture, c, fullscreen, centre };
  }

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth'); // no GPU in jsdom: keep the "not available here" notice out
    localStorage.setItem('mangapixer-reader-help-seen', '1');
    vi.useFakeTimers();
  });
  afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); localStorage.clear(); });

  it('a single centre tap toggles the controls only after the double-tap window, and never fullscreen', () => {
    const { c, fullscreen, centre } = paged();
    centre().click();
    vi.advanceTimersByTime(DoubleTapWindowMs - 80);
    expect(c.chromeVisible(), 'still waiting for a possible second tap').toBe(true);
    vi.advanceTimersByTime(100);
    expect(c.chromeVisible()).toBe(false);
    expect(fullscreen).not.toHaveBeenCalled();
    expect(c.currentPage()).toBe(1);
  });

  it('a double tap (or double click) in the centre toggles fullscreen and does NOT toggle the controls', () => {
    const { c, fullscreen, centre } = paged();
    centre().click();
    vi.advanceTimersByTime(120);
    centre().click();
    expect(fullscreen).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000);
    expect(c.chromeVisible()).toBe(true);
    expect(fullscreen).toHaveBeenCalledTimes(1);
    expect(c.currentPage(), 'a double click never turns the page').toBe(1);
  });

  it('two taps further apart than the window are two single taps', () => {
    const { c, fullscreen, centre } = paged();
    centre().click();
    vi.advanceTimersByTime(DoubleTapWindowMs + 50);
    expect(c.chromeVisible()).toBe(false);
    centre().click();
    vi.advanceTimersByTime(DoubleTapWindowMs + 50);
    expect(c.chromeVisible()).toBe(true);
    expect(fullscreen).not.toHaveBeenCalled();
  });

  it('the ghost click after a swipe is neither a tap nor the second half of a double tap', () => {
    const { c, fullscreen, centre } = paged();
    centre().click(); // a real tap ...
    vi.advanceTimersByTime(50);
    // ... then a swipe that turns the page and ends over the centre zone.
    c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 400, clientY: 300, timeStamp: 0 }));
    c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 300, clientY: 302, timeStamp: 40 }));
    c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 200, clientY: 305, timeStamp: 80 }));
    expect(c.currentPage()).toBe(2);
    centre().click(); // the click the browser synthesizes on release
    vi.advanceTimersByTime(1000);
    expect(fullscreen).not.toHaveBeenCalled();
    expect(c.chromeVisible(), 'only the real tap counted').toBe(false);
  });

  it('the edge zones still turn the page at once (no double-tap delay there)', () => {
    const { c, fullscreen, fixture } = paged();
    const next = (fixture.nativeElement as HTMLElement).querySelector('.edge.next') as HTMLButtonElement;
    next.click();
    expect(c.currentPage()).toBe(2);
    next.click();
    expect(c.currentPage()).toBe(3);
    vi.advanceTimersByTime(1000);
    expect(fullscreen).not.toHaveBeenCalled();
    expect(c.chromeVisible()).toBe(true);
  });

  describe('webtoon centre band', () => {
    function webtoon(stepOff = false) {
      const ctx = paged();
      if (stepOff) TestBed.inject(WebtoonNavPreferencesService).setTapStep(0);
      ctx.c.view.set('webtoon');
      const calls: ScrollToOptions[] = [];
      const el = {
        scrollTop: 1000, clientHeight: 600, scrollHeight: 5000,
        getBoundingClientRect: () => ({ top: 100, height: 600 }) as DOMRect,
        scrollTo: (opts: ScrollToOptions) => calls.push(opts),
        querySelectorAll: () => [],
      } as unknown as HTMLElement;
      (ctx.c as unknown as { scroller: () => { nativeElement: HTMLElement } }).scroller = () => ({ nativeElement: el });
      const tap = (clientY: number) => ctx.c.onWebtoonTap({ clientY, target: { closest: () => null } } as unknown as MouseEvent);
      return { ...ctx, calls, tap };
    }

    it('a double tap in the centre band toggles fullscreen, not the controls', () => {
      const { c, fullscreen, calls, tap } = webtoon();
      tap(100 + 300);
      vi.advanceTimersByTime(100);
      tap(100 + 300);
      vi.advanceTimersByTime(1000);
      expect(fullscreen).toHaveBeenCalledTimes(1);
      expect(c.chromeVisible()).toBe(true);
      expect(calls.length).toBe(0);
    });

    it('a single centre-band tap toggles the controls after the window', () => {
      const { c, fullscreen, tap } = webtoon();
      tap(100 + 300);
      expect(c.chromeVisible()).toBe(true);
      vi.advanceTimersByTime(DoubleTapWindowMs + 20);
      expect(c.chromeVisible()).toBe(false);
      expect(fullscreen).not.toHaveBeenCalled();
    });

    it('the forward / back bands keep scrolling at once', () => {
      const { fullscreen, calls, tap } = webtoon();
      tap(100 + 550);
      tap(100 + 550);
      expect(calls.length).toBe(2);
      vi.advanceTimersByTime(1000);
      expect(fullscreen).not.toHaveBeenCalled();
    });

    it('with Tap to scroll off the whole strip is the centre: double tap = fullscreen, and the browser zoom is kept off it', () => {
      const { c, fullscreen, tap } = webtoon(true);
      tap(100 + 590);
      tap(100 + 590);
      vi.advanceTimersByTime(1000);
      expect(fullscreen).toHaveBeenCalledTimes(1);
      expect(c.chromeVisible()).toBe(true);
      // touch-action without double-tap-to-zoom (pan + pinch stay native).
      expect(c.webtoonTouchAction()).toBe('manipulation');
    });
  });
});

describe('Reader bar: Series info (1.41.0)', () => {
  const SeriesUrl = (id: string) => `/api/v1/nodes/${id}/series-info`;
  /** Only the fields the reader reads (the DTO grows elsewhere this cycle). */
  const info = (state: string, anchorNodeId = 'series-7') => ({ state, anchorNodeId, nodeId: 'item-1' });

  function open(ids = new BehaviorSubject<ParamMap>(convertToParamMap({ itemId: 'item-1' }))) {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        ...baseProviders(),
        { provide: ActivatedRoute, useValue: { paramMap: ids, snapshot: { queryParamMap: convertToParamMap({}) } } },
      ],
    });
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit
    const http = TestBed.inject(HttpTestingController);
    const button = () => (fixture.nativeElement as HTMLElement)
      .querySelector('.reader-toolbar button[aria-label="Series info"]') as HTMLButtonElement | null;
    /** Answer the manifest + progress for an archive, so its first page is on screen. */
    const ready = (id: string) => {
      http.expectOne(`/api/v1/items/${id}/manifest`).flush({ itemId: id, contentVersion: 1, pages: makePages(5) });
      http.expectOne(`/api/v1/reading/progress/${id}`).flush({ pageIndex: 0, revision: 1 });
      fixture.detectChanges();
    };
    /** Answer every pending series-info request for an archive (none is fine: the control then stays away). */
    const answer = (id: string, body: object | null, status = 200) => {
      for (const req of http.match(SeriesUrl(id))) {
        if (status === 200) req.flush(body); else req.flush({ error: 'x' }, { status, statusText: 'Err' });
      }
      fixture.detectChanges();
    };
    return { fixture, c: fixture.componentInstance, http, button, ready, answer, ids };
  }

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth');
    localStorage.setItem('mangapixer-reader-help-seen', '1');
  });
  afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

  it('shows "Series info" (between the archive arrows and the bookmarks) when the archive has series information', () => {
    const { button, ready, answer, fixture } = open();
    ready('item-1');
    answer('item-1', info('Web'));
    const b = button();
    expect(b, 'the Series info control').not.toBeNull();
    expect(b!.getAttribute('aria-label')).toBe('Series info');
    expect(b!.textContent).toContain('info_outline');
    const labels = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.reader-toolbar button'))
      .map((x) => x.getAttribute('aria-label'));
    expect(labels.indexOf('Series info')).toBe(labels.indexOf('No next archive') + 1);
  });

  it('asks once per archive, only after the manifest is in (it never delays the first page)', () => {
    const { http, ready, button, fixture } = open();
    expect(http.match(SeriesUrl('item-1')).length, 'nothing before the manifest').toBe(0);
    ready('item-1');
    const reqs = http.match(SeriesUrl('item-1'));
    expect(reqs.length).toBe(1);
    expect(reqs[0].request.params.get('includeItems')).toBeNull(); // the light form
    reqs[0].flush(info('ComicInfo'));
    fixture.detectChanges();
    expect(http.match(SeriesUrl('item-1')).length, 'asked once').toBe(0);
    expect(button()).not.toBeNull();
  });

  it('stays hidden when there is no series information (state None, e.g. "Show series information" off)', () => {
    const { button, ready, answer } = open();
    ready('item-1');
    answer('item-1', info('None'));
    expect(button()).toBeNull();
  });

  it('stays hidden when the request fails', () => {
    const { button, ready, answer } = open();
    ready('item-1');
    answer('item-1', null, 500);
    expect(button()).toBeNull();
  });

  it('opens the series page of the anchor', () => {
    const { button, ready, answer } = open();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    ready('item-1');
    answer('item-1', info('Web', 'anchor-9'));
    button()!.click();
    expect(nav).toHaveBeenCalledWith(['/series', 'anchor-9']);
  });

  it('ignores a late answer for the archive the reader has moved on from', () => {
    const { button, ready, answer, ids, http } = open();
    ready('item-1');
    const late = http.match(SeriesUrl('item-1'));
    ids.next(convertToParamMap({ itemId: 'item-2' })); // the next archive opens in the same reader
    for (const req of late) if (!req.cancelled) req.flush(info('Web'));
    answer('item-1', info('Web'));
    expect(button(), 'the previous archive\'s answer is not shown').toBeNull();
    ready('item-2');
    answer('item-2', info('None'));
    expect(button()).toBeNull();
  });
});

@Component({ selector: 'app-series-stub', standalone: true, template: 'series' })
class SeriesStubComponent {}

describe('Reader bar: Series info saves the reading position on the way out (1.41.0)', () => {
  it('opening the series page leaves the reader through its destroy path, which saves the page', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'reader/:itemId', component: ReaderComponent },
          { path: 'series/:nodeId', component: SeriesStubComponent },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: BreakpointObserver, useValue: desktop },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const c = await harness.navigateByUrl('/reader/item-1', ReaderComponent);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/items/item-1/manifest').flush({ itemId: 'item-1', contentVersion: 3, pages: makePages(5) });
    http.expectOne('/api/v1/reading/progress/item-1').flush({ pageIndex: 2, revision: 4 });
    for (const req of http.match('/api/v1/nodes/item-1/series-info')) req.flush({ state: 'Web', anchorNodeId: 'anchor-9', nodeId: 'item-1' });
    harness.detectChanges();
    expect(c.currentPage()).toBe(2);

    const button = harness.routeNativeElement!.querySelector('.reader-toolbar button[aria-label="Series info"]') as HTMLButtonElement | null;
    expect(button, 'the Series info control').not.toBeNull();
    button!.click();
    await harness.fixture.whenStable();

    expect(TestBed.inject(Router).url).toBe('/series/anchor-9');
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/reading/progress/item-1' });
    expect(put.request.body.pageIndex).toBe(2);
    expect(put.request.body.expectedContentVersion).toBe(3);
  });
});
