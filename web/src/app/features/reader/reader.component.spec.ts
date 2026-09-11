import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { ManifestPageEntry } from '../../core/api/api-types';

function makePages(n: number): ManifestPageEntry[] {
  return Array.from({ length: n }, (_, i) => ({
    entryKey: `p${i}`,
    pageIndex: i,
    mediaType: 'image/png',
    width: 800,
    height: 1200,
    animationState: 'None',
    byteSize: 1000,
  }));
}

/**
 * These tests read the public `spreads()` computed directly. We deliberately do
 * NOT call detectChanges(), so ngOnInit (which fires the preferences/manifest
 * HTTP calls) never runs — the pairing logic is pure and testable in isolation.
 */
describe('ReaderComponent double-spread pairing', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
      ],
    });
    return TestBed.createComponent(ReaderComponent).componentInstance;
  }

  it('pairs after a standalone cover: [0],[1,2],[3,4]', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    expect(c.spreads()).toEqual([[0], [1, 2], [3, 4]]);
  });

  it('leaves an odd trailing page alone: [0],[1,2],[3,4],[5]', () => {
    const c = create();
    c.pages.set(makePages(6));
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    expect(c.spreads()).toEqual([[0], [1, 2], [3, 4], [5]]);
  });

  it('pairs from the first page when there is no standalone cover: [0,1],[2,3],[4]', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('spread');
    c.coverIsStandalone.set(false);
    expect(c.spreads()).toEqual([[0, 1], [2, 3], [4]]);
  });

  it('direction does not change the pairing data (RTL is presentation-only)', () => {
    const c = create();
    c.pages.set(makePages(4));
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    const ltr = c.spreads();
    c.direction.set('rtl');
    expect(c.spreads()).toEqual(ltr); // grouping identical; only CSS flow differs
  });

  it('currentSpreadEntries returns both pages of the active pair', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    c.currentPage.set(2); // page 2 is in the [1,2] pair
    expect(c.currentSpreadEntries().map((e) => e.entryKey)).toEqual(['p1', 'p2']);
  });

  it('paged view always shows exactly one page', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('paged');
    c.currentPage.set(3);
    expect(c.currentSpreadEntries().map((e) => e.entryKey)).toEqual(['p3']);
  });

  it('setSpread(false/true) selects double-page view and toggles the offset', () => {
    const c = create();
    c.pages.set(makePages(5));

    c.setSpread(false); // no offset: pair from the first page
    expect(c.view()).toBe('spread');
    expect(c.coverIsStandalone()).toBe(false);
    expect(c.spreads()).toEqual([[0, 1], [2, 3], [4]]);

    c.setSpread(true); // offset: cover standalone, then pairs
    expect(c.view()).toBe('spread');
    expect(c.coverIsStandalone()).toBe(true);
    expect(c.spreads()).toEqual([[0], [1, 2], [3, 4]]);
  });

  it('chrome only hides in fullscreen; windowed stays visible', () => {
    const c = create();
    expect(c.chromeVisible()).toBe(true); // shown on entry

    // Windowed: toggling never hides — immersion is fullscreen-only.
    c.isFullscreen.set(false);
    c.toggleChrome();
    expect(c.chromeVisible()).toBe(true);

    // Fullscreen: centre tap / 'm' hides, then shows again.
    c.isFullscreen.set(true);
    c.toggleChrome();
    expect(c.chromeVisible()).toBe(false);
    c.toggleChrome();
    expect(c.chromeVisible()).toBe(true);
  });

  it('progressPct reflects the current page within the chapter', () => {
    const c = create();
    c.pages.set(makePages(4));
    c.currentPage.set(0);
    expect(c.progressPct()).toBe(25);
    c.currentPage.set(3);
    expect(c.progressPct()).toBe(100);
  });

  it('renders a wide (stitched-spread) page solo and resumes pairing after it', () => {
    const c = create();
    const pages = makePages(5);
    pages[2] = { ...pages[2], width: 2000, height: 1200 }; // aspect 1.67 → wide
    c.pages.set(pages);
    c.view.set('spread');
    c.coverIsStandalone.set(false);
    expect(c.spreads()).toEqual([[0, 1], [2], [3, 4]]);
  });

  it('treats a wide cover as its own spread, then pairs the rest', () => {
    const c = create();
    const pages = makePages(3);
    pages[0] = { ...pages[0], width: 2000, height: 1000 }; // wide cover
    c.pages.set(pages);
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    expect(c.spreads()).toEqual([[0], [1, 2]]);
  });

  it('toggleHelp / closeHelp control the help overlay', () => {
    const c = create();
    expect(c.helpVisible()).toBe(false);
    c.toggleHelp();
    expect(c.helpVisible()).toBe(true);
    c.closeHelp();
    expect(c.helpVisible()).toBe(false);
  });

  it('help zone labels follow the reading direction', () => {
    const c = create();
    c.direction.set('ltr');
    expect(c.leftZoneLabel()).toBe('Previous page');
    expect(c.rightZoneLabel()).toBe('Next page');
    c.direction.set('rtl');
    expect(c.leftZoneLabel()).toBe('Next page');
    expect(c.rightZoneLabel()).toBe('Previous page');
  });

  it('aspectRatioFor reserves the manifest aspect, or null when unknown', () => {
    const c = create();
    const [p] = makePages(1); // 800 x 1200
    expect(c.aspectRatioFor(p)).toBe('800 / 1200');
    expect(c.aspectRatioFor({ ...p, width: 0, height: 0 })).toBeNull();
  });

  it('auto-advances to the next chapter when paging past the last page', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('paged');
    c.phase.set('ready');
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    c.currentPage.set(2); // last page

    c.nextPage();
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item']);
  });

  it('does not navigate past the last page when there is no next chapter', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('paged');
    c.phase.set('ready');
    c.nextNeighbor.set(null);
    c.currentPage.set(2);

    c.nextPage();
    expect(nav).not.toHaveBeenCalled();
    expect(c.currentPage()).toBe(2); // stays put
  });
});

/**
 * Render tests for the reader controls. detectChanges() runs ngOnInit (which
 * fires HTTP calls to the testing backend — left pending, never flushed), then we
 * drive phase/fit/fullscreen signals and assert the rendered DOM.
 */
describe('ReaderComponent controls rendering', () => {
  function renderReady() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
      ],
    });
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit
    const c = fixture.componentInstance;
    c.pages.set(makePages(3));
    c.view.set('paged');
    c.phase.set('ready');
    fixture.detectChanges();
    return { fixture, c };
  }

  it('has no on-screen prev/next chevrons; navigation is via edge zones + keyboard', () => {
    // The FAB chevrons were removed (2026-09-08) as redundant with the edge tap
    // zones (documented in the Help overlay) and keyboard arrows.
    const { fixture } = renderReady();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.reader-controls')).toBeNull();
    expect(el.querySelector('.edge.prev')).toBeTruthy();
    expect(el.querySelector('.edge.next')).toBeTruthy();
  });

  it('applies the selected fit class to the page image', () => {
    const { fixture, c } = renderReady();
    const img = () => fixture.nativeElement.querySelector('.spread-row img') as HTMLElement;

    c.setFitMode('screen');
    fixture.detectChanges();
    expect(img().classList.contains('fit-screen')).toBe(true);

    c.setFitMode('width');
    fixture.detectChanges();
    expect(img().classList.contains('fit-width')).toBe(true);
    expect(img().classList.contains('fit-screen')).toBe(false);

    c.setFitMode('original');
    fixture.detectChanges();
    expect(img().classList.contains('original')).toBe(true);
  });
});

/**
 * Per-device default page mode (1.2.x). The preference lives in localStorage and is
 * a device-local override of the layout only. As with the pairing tests we avoid
 * detectChanges() so ngOnInit's HTTP calls never fire — we drive the public methods
 * directly. window dimensions are stubbed to simulate device orientation.
 */
describe('ReaderComponent per-device page mode', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
      ],
    });
    return TestBed.createComponent(ReaderComponent).componentInstance;
  }

  function setOrientation(width: number, height: number): void {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: height });
  }

  beforeEach(() => localStorage.clear());

  it('chooseView persists the device preference and switches the view', () => {
    const c = create();
    c.chooseView('webtoon');
    expect(c.viewPref()).toBe('webtoon');
    expect(c.view()).toBe('webtoon');
    expect(localStorage.getItem('mangaplex-reader-view')).toBe('webtoon');
  });

  it('chooseView("auto") resolves paged in portrait and spread in landscape', () => {
    const c = create();

    setOrientation(800, 1200); // portrait
    c.chooseView('auto');
    expect(c.viewPref()).toBe('auto');
    expect(c.view()).toBe('paged');

    setOrientation(1200, 800); // landscape
    c.chooseView('auto');
    expect(c.view()).toBe('spread');
  });

  it('chooseSpread persists the cover offset alongside the spread preference', () => {
    const c = create();

    c.chooseSpread(false);
    expect(c.viewPref()).toBe('spread');
    expect(c.coverIsStandalone()).toBe(false);
    expect(localStorage.getItem('mangaplex-reader-cover-standalone')).toBe('0');

    c.chooseSpread(true);
    expect(c.coverIsStandalone()).toBe(true);
    expect(localStorage.getItem('mangaplex-reader-cover-standalone')).toBe('1');
  });

  it('loads a stored preference on construction (view + cover offset)', () => {
    localStorage.setItem('mangaplex-reader-view', 'spread');
    localStorage.setItem('mangaplex-reader-cover-standalone', '0');
    const c = create();
    expect(c.viewPref()).toBe('spread');
    expect(c.coverIsStandalone()).toBe(false);
  });

  it('defaults to no preference (follow the server) when nothing is stored', () => {
    const c = create();
    expect(c.viewPref()).toBeNull();
    expect(c.coverIsStandalone()).toBe(true); // cover-standalone default
  });

  it('live-switches paged↔spread on rotation only while auto and ready', () => {
    const c = create();
    c.phase.set('ready');
    c.chooseView('auto');

    setOrientation(1200, 800); // landscape
    c.onViewportChange();
    expect(c.view()).toBe('spread');

    setOrientation(800, 1200); // portrait
    c.onViewportChange();
    expect(c.view()).toBe('paged');

    // A non-auto preference is left untouched by rotation.
    c.chooseView('webtoon');
    setOrientation(1200, 800);
    c.onViewportChange();
    expect(c.view()).toBe('webtoon');
  });
});

/**
 * Webtoon scroll-driven prefetch (post-1.3.0 lane D). The paged/spread prefetch
 * is skipped in webtoon; instead onWebtoonScroll warms the next N pages ahead of
 * the scroll position. These tests drive prefetchWebtoonAhead directly (it's
 * private, accessed via bracket notation) and assert on the shared prefetchedUrls
 * dedup set — the same pool the paged prefetch uses.
 */
describe('ReaderComponent webtoon scroll prefetch', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
      ],
    });
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.itemId.set('item-1');
    return c;
  }

  /** Read the private prefetchedUrls dedup set as an array of strings. */
  function warmed(c: ReaderComponent): string[] {
    return Array.from((c as unknown as { prefetchedUrls: Set<string> }).prefetchedUrls);
  }

  it('warms the next N pages ahead of the scroll position', () => {
    const c = create();
    c.pages.set(makePages(10));
    c.view.set('webtoon');
    (c as unknown as { prefetchWebtoonAhead: (i: number) => void }).prefetchWebtoonAhead(2);
    // idx 2 → warm 3, 4, 5, 6 (WebtoonPrefetchAhead = 4)
    expect(warmed(c)).toEqual([
      '/api/v1/items/item-1/pages/p3',
      '/api/v1/items/item-1/pages/p4',
      '/api/v1/items/item-1/pages/p5',
      '/api/v1/items/item-1/pages/p6',
    ]);
  });

  it('does not prefetch across the chapter boundary', () => {
    const c = create();
    c.pages.set(makePages(5)); // indices 0–4
    c.view.set('webtoon');
    (c as unknown as { prefetchWebtoonAhead: (i: number) => void }).prefetchWebtoonAhead(3);
    // idx 3 → only page 4 is ahead (3+2=5 is out of bounds)
    expect(warmed(c)).toEqual(['/api/v1/items/item-1/pages/p4']);
  });

  it('warms nothing ahead of the last page', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('webtoon');
    (c as unknown as { prefetchWebtoonAhead: (i: number) => void }).prefetchWebtoonAhead(4);
    expect(warmed(c)).toEqual([]);
  });

  it('deduplicates: calling twice does not re-warm the same pages', () => {
    const c = create();
    c.pages.set(makePages(10));
    c.view.set('webtoon');
    const call = () => (c as unknown as { prefetchWebtoonAhead: (i: number) => void }).prefetchWebtoonAhead(2);
    call();
    const first = warmed(c);
    call(); // same position — no new URLs
    expect(warmed(c)).toEqual(first);
    expect(warmed(c).length).toBe(4);
  });

  it('shares the dedup pool with the paged prefetch', () => {
    const c = create();
    c.pages.set(makePages(10));
    c.view.set('webtoon');
    // Warm webtoon ahead from idx 0 → pages 1,2,3,4
    (c as unknown as { prefetchWebtoonAhead: (i: number) => void }).prefetchWebtoonAhead(0);
    expect(warmed(c).length).toBe(4);
    // Switch to paged and prefetch around idx 0 → pages 1–6 ahead, 0 behind.
    // Pages 1–4 are already warmed (dedup); only 5,6 are new.
    c.view.set('paged');
    (c as unknown as { prefetchAround: (i: number) => void }).prefetchAround(0);
    expect(warmed(c).length).toBe(6); // 1,2,3,4 (webtoon) + 5,6 (paged)
  });
});
