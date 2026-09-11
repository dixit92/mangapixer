import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { Location } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { ManifestPageEntry, CatalogNodeDto } from '../../core/api/api-types';

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

function makeNode(overrides: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
  return {
    id: 'item-1',
    parentId: 'folder-9',
    libraryId: 'lib-1',
    kind: 'Archive',
    displayName: 'Chapter 1',
    availability: 'Available',
    coverUrl: null,
    childFolderCount: null,
    childArchiveCount: null,
    pageCount: null,
    readingState: null,
    lastReadPage: null,
    readerDefault: null,
    isRead: false,
    readRollup: null,
    ...overrides,
  };
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

  /**
   * 1.6.1 owner iPad fix (bug G): in spread view `currentPage` holds the FIRST
   * index of the displayed pair, so on a final pair like [3,4] (5-page chapter,
   * standalone cover) it can sit at 3 while the true last manifest index is 4 —
   * the server's completion check (`pageIndex >= pageCount - 1`) never fires.
   * `effectivePageIndex()` is what `saveProgress()` actually persists.
   */
  describe('effectivePageIndex (spread completion, bug G)', () => {
    function effectiveIndex(c: ReaderComponent): number {
      return (c as unknown as { effectivePageIndex: () => number }).effectivePageIndex();
    }

    it('is the LAST index of the pair on the final (odd-first-index) spread', () => {
      const c = create();
      c.pages.set(makePages(5)); // spreads with a standalone cover: [0],[1,2],[3,4]
      c.view.set('spread');
      c.coverIsStandalone.set(true);
      c.currentPage.set(3); // the group's first index, per nextIndexFrom
      expect(effectiveIndex(c)).toBe(4); // the manifest's true last index
    });

    it('matches currentPage when the pair already starts at the last index', () => {
      const c = create();
      c.pages.set(makePages(5)); // no standalone cover: [0,1],[2,3],[4]
      c.view.set('spread');
      c.coverIsStandalone.set(false);
      c.currentPage.set(2); // group [2,3] — currentPage isn't the group's max here
      expect(effectiveIndex(c)).toBe(3);
    });

    it('is just currentPage outside of spread view (paged/webtoon are unaffected)', () => {
      const c = create();
      c.pages.set(makePages(5));
      c.view.set('paged');
      c.currentPage.set(2);
      expect(effectiveIndex(c)).toBe(2);
    });
  });

  it('persists the true last manifest index when the final spread pair is reached (bug G)', () => {
    const c = create();
    const httpMock = TestBed.inject(HttpTestingController);
    c.itemId.set('item-1');
    c.pages.set(makePages(5)); // standalone cover: [0],[1,2],[3,4]
    c.view.set('spread');
    c.coverIsStandalone.set(true);
    c.phase.set('ready');
    c.nextNeighbor.set(null);
    c.currentPage.set(2); // showing [1,2]

    c.nextPage(); // advances into the final pair [3,4]; currentPage becomes 3 (group's first index)
    expect(c.currentPage()).toBe(3);

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.pageIndex).toBe(4); // not 3 — the completion trigger needs the true last index
    req.flush({ revision: 1, alreadyApplied: false });
  });
});

/**
 * Exit navigation (1.6.1 owner iPad fix, bug B): goBack() must return to the
 * browse view the reader was opened from, never unconditionally to Home.
 */
describe('ReaderComponent exit navigation (goBack)', () => {
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

  afterEach(() => vi.restoreAllMocks());

  it('walks back through browser history when the reader was reached via in-app navigation', () => {
    const c = create();
    history.pushState({ navigationId: 2 }, ''); // simulates a 2nd+ Angular Router navigation
    const back = vi.spyOn(TestBed.inject(Location), 'back').mockReturnValue(undefined);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    c.goBack();

    expect(back).toHaveBeenCalled();
    expect(nav).not.toHaveBeenCalled();
  });

  it('falls back to the resolved parent-folder route when deep-linked (no in-app history)', () => {
    const c = create();
    history.pushState({ navigationId: 1 }, ''); // the session's first navigation
    const back = vi.spyOn(TestBed.inject(Location), 'back').mockReturnValue(undefined);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.fallbackBackRoute.set(['/libraries', 'lib-1', 'browse', 'folder-9']);

    c.goBack();

    expect(back).not.toHaveBeenCalled();
    expect(nav).toHaveBeenCalledWith(['/libraries', 'lib-1', 'browse', 'folder-9']);
  });

  it('falls back to the library root browse route, not Home, when the item has no resolved parent yet', () => {
    const c = create();
    history.pushState(null, ''); // no navigationId at all — no history to walk back to
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.fallbackBackRoute.set(['/libraries', 'lib-1', 'browse']);

    c.goBack();

    expect(nav).toHaveBeenCalledWith(['/libraries', 'lib-1', 'browse']);
  });

  it('resolves the parent-folder browse route from the catalog node', () => {
    const c = create();
    const httpMock = TestBed.inject(HttpTestingController);
    (c as unknown as { loadFallbackBackRoute: (id: string) => void }).loadFallbackBackRoute('item-1');
    httpMock.expectOne('/api/v1/nodes/item-1').flush(makeNode({ parentId: 'folder-9', libraryId: 'lib-1' }));

    expect(c.fallbackBackRoute()).toEqual(['/libraries', 'lib-1', 'browse', 'folder-9']);
  });

  it('resolves the library root browse route when the item sits at the library root', () => {
    const c = create();
    const httpMock = TestBed.inject(HttpTestingController);
    (c as unknown as { loadFallbackBackRoute: (id: string) => void }).loadFallbackBackRoute('item-1');
    // CatalogBrowseService encodes a library-root item's parent as "" (empty), not null.
    httpMock.expectOne('/api/v1/nodes/item-1').flush(makeNode({ parentId: '', libraryId: 'lib-1' }));

    expect(c.fallbackBackRoute()).toEqual(['/libraries', 'lib-1', 'browse']);
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

  it('chooseView("webtoon") switches the view but does NOT persist a device-global preference', () => {
    // Option A fix (reader-mode-sticky bug): webtoon is content orientation, not a
    // device layout choice, so picking it from the menu is per-item/session only.
    const c = create();
    c.chooseView('webtoon');
    expect(c.viewPref()).toBeNull();
    expect(c.view()).toBe('webtoon');
    expect(localStorage.getItem('mangaplex-reader-view')).toBeNull();
  });

  it('a paged-layout preference set on a paged item does not survive a stale localStorage "webtoon" value', () => {
    // Pre-Option-A localStorage could hold 'webtoon' (from the old sticky bug).
    // loadViewPref must not resurrect it as a device pref.
    localStorage.setItem('mangaplex-reader-view', 'webtoon');
    const c = create();
    expect(c.viewPref()).toBeNull();
  });

  it('applyDeviceViewPreference never forces webtoon content back to the stored paged layout', () => {
    // The core regression: a device-global paged/spread/auto preference from a
    // previously read manga item must not stomp the server-resolved webtoon mode
    // when the next item opened is a webtoon.
    const c = create();
    c.chooseView('paged'); // persists a device-global paged preference
    c.view.set('webtoon'); // simulates the server resolving THIS item to webtoon
    (c as unknown as { applyDeviceViewPreference: () => void }).applyDeviceViewPreference();
    expect(c.view()).toBe('webtoon');
  });

  it('an explicit paged/auto pick still applies immediately to a currently-open webtoon item (session-only)', () => {
    // Hard constraint: paged must stay reachable on a webtoon item, per item/session.
    const c = create();
    c.view.set('webtoon');
    c.chooseView('paged');
    expect(c.view()).toBe('paged');
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

/**
 * Webtoon scroll-to-bottom completion (1.6.1 owner iPad fix, bug G). The
 * centre-crossing heuristic alone can never resolve the final page when it lays
 * out shorter than half the viewport (a short last page, or one whose aspect
 * ratio isn't reserved yet) — the viewport centre never crosses into it even at
 * maximum scroll, so the chapter's completion trigger (which needs
 * `currentPage === pageCount - 1`) silently never fires. onWebtoonScroll now
 * detects "scrolled to the true bottom" directly and forces the last page.
 */
describe('ReaderComponent webtoon scroll-to-bottom completion', () => {
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

  /** Wires a fake `.webtoon-page` scroller onto the component's private viewChild. */
  function useFakeScroller(
    c: ReaderComponent,
    opts: { scrollTop: number; clientHeight: number; scrollHeight: number; pageHeights: number[] },
  ): void {
    let top = 0;
    const imgs = opts.pageHeights.map((h) => {
      const img = { offsetTop: top, offsetHeight: h } as unknown as HTMLElement;
      top += h;
      return img;
    });
    const el = {
      scrollTop: opts.scrollTop,
      clientHeight: opts.clientHeight,
      scrollHeight: opts.scrollHeight,
      querySelectorAll: () => imgs,
    } as unknown as HTMLElement;
    (c as unknown as { scroller: () => { nativeElement: HTMLElement } }).scroller = () => ({ nativeElement: el });
  }

  it('resolves to the last page at the true scroll bottom even when the final page is too short to cross the viewport centre', () => {
    const c = create();
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    // Pages 1000/1000/20 tall, 160px viewport: at max scroll the centre-crossing
    // heuristic alone would still resolve to page index 1 (the 20px final page
    // never reaches the centre) — this is exactly the bug.
    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });

    c.onWebtoonScroll();

    expect(c.currentPage()).toBe(2);
  });

  it('still uses the centre-crossing heuristic when not at the bottom', () => {
    const c = create();
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(0);
    useFakeScroller(c, { scrollTop: 1000, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    // Not scrolled to bottom (1000+160=1160 << 2020); centre = 1000+80=1080, inside page 1's [1000,2000) span.

    c.onWebtoonScroll();

    expect(c.currentPage()).toBe(1);
  });
});

/**
 * 1.7.0 reader touch-UX lane. Four additive features, all through the component's
 * public surface: direction-aware swipe page-turning, the draggable page scrubber,
 * reader-bar chapter arrows, and webtoon auto next/prev chapter.
 */

/** A minimal PointerEvent stand-in carrying only the fields the handlers read. */
function pointer(overrides: Partial<{
  pointerId: number; clientX: number; clientY: number; timeStamp: number;
  currentTarget: unknown;
}> = {}): PointerEvent {
  return {
    pointerId: 1, clientX: 0, clientY: 0, timeStamp: 0, currentTarget: null,
    preventDefault: () => { /* noop */ },
    ...overrides,
  } as unknown as PointerEvent;
}

function baseProviders() {
  return [
    provideRouter([]),
    provideHttpClient(),
    provideHttpClientTesting(),
    provideNoopAnimations(),
    { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
  ];
}

describe('ReaderComponent swipe gestures (requirement 1)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    return TestBed.createComponent(ReaderComponent).componentInstance;
  }

  describe('resolveSwipe direction-aware resolution', () => {
    it('LTR: a leftward swipe is next, a rightward swipe is previous', () => {
      const c = create();
      c.direction.set('ltr');
      expect(c.resolveSwipe(-80, 4, 120)).toBe('next');
      expect(c.resolveSwipe(80, 4, 120)).toBe('prev');
    });

    it('RTL (manga): mirrors — a leftward swipe is previous, rightward is next', () => {
      const c = create();
      c.direction.set('rtl');
      expect(c.resolveSwipe(-80, 4, 120)).toBe('prev');
      expect(c.resolveSwipe(80, 4, 120)).toBe('next');
    });

    it('rejects a predominantly vertical drag (never steals vertical scroll/pinch)', () => {
      const c = create();
      expect(c.resolveSwipe(30, 120, 120)).toBeNull();
    });

    it('rejects a short slow drag but accepts a short fast flick', () => {
      const c = create();
      expect(c.resolveSwipe(-22, 0, 1000)).toBeNull();     // 22px, velocity 0.022 — neither far nor a flick
      expect(c.resolveSwipe(-25, 0, 30)).toBe('next');      // 25px in 30ms → 0.83px/ms flick
    });
  });

  describe('pointer handling', () => {
    function paged(n = 5) {
      const c = create();
      c.pages.set(makePages(n));
      c.view.set('paged');
      c.phase.set('ready');
      c.direction.set('ltr');
      c.currentPage.set(1);
      return c;
    }

    it('a leftward swipe turns to the next page; a rightward swipe turns back', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 90, clientY: 305, timeStamp: 100 }));
      expect(c.currentPage()).toBe(2);

      c.onReaderPointerDown(pointer({ pointerId: 2, clientX: 90, clientY: 300, timeStamp: 200 }));
      c.onReaderPointerUp(pointer({ pointerId: 2, clientX: 200, clientY: 300, timeStamp: 300 }));
      expect(c.currentPage()).toBe(1);
    });

    it('ignores a vertical drag (no page turn)', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 150, clientY: 100, timeStamp: 0 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 150, clientY: 320, timeStamp: 120 }));
      expect(c.currentPage()).toBe(1);
    });

    it('ignores a two-finger gesture (pinch-zoom is never hijacked)', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerDown(pointer({ pointerId: 2, clientX: 100, clientY: 300, timeStamp: 10 })); // second finger
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 300, timeStamp: 100 }));
      c.onReaderPointerUp(pointer({ pointerId: 2, clientX: 260, clientY: 300, timeStamp: 110 }));
      expect(c.currentPage()).toBe(1);
    });

    it('does not swipe-navigate in webtoon (native vertical scroll is preserved)', () => {
      const c = paged();
      c.view.set('webtoon');
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 305, timeStamp: 100 }));
      expect(c.currentPage()).toBe(1);
    });

    it('suppresses the ghost edge/center tap that follows a completed swipe', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 90, clientY: 300, timeStamp: 100 }));
      expect(c.currentPage()).toBe(2);
      // The synthesized click on the edge zone must NOT turn a second page.
      c.onEdge('next');
      expect(c.currentPage()).toBe(2);
    });
  });
});

describe('ReaderComponent reader-bar chapter arrows (requirement 3)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    return TestBed.createComponent(ReaderComponent).componentInstance;
  }
  afterEach(() => vi.restoreAllMocks());

  it('chapter availability reflects the fetched neighbors', () => {
    const c = create();
    expect(c.hasPrevChapter()).toBe(false);
    expect(c.hasNextChapter()).toBe(false);
    c.nextNeighbor.set({ id: 'n', displayName: 'Chapter 2' });
    c.prevNeighbor.set({ id: 'p', displayName: 'Chapter 0' });
    expect(c.hasPrevChapter()).toBe(true);
    expect(c.hasNextChapter()).toBe(true);
  });

  it('nextChapter()/prevChapter() reuse the existing chapter-navigation path', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.phase.set('ready');
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    c.prevNeighbor.set({ id: 'prev-item', displayName: 'Chapter 0' });

    c.nextChapter();
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item']);

    c.prevChapter();
    expect(nav).toHaveBeenCalledWith(['/reader', 'prev-item'], { queryParams: { at: 'end' } });
  });

  it('renders the arrows disabled when there is no neighbor, enabled when there is', () => {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit
    const c = fixture.componentInstance;
    c.pages.set(makePages(3));
    c.view.set('paged');
    c.phase.set('ready');
    fixture.detectChanges();

    const arrows = () => Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll('.reader-toolbar button'),
    ).filter((b) => /skip_previous|skip_next/.test(b.textContent ?? '')) as HTMLButtonElement[];

    // No neighbors yet → both chapter arrows disabled.
    expect(arrows().length).toBe(2);
    expect(arrows().every((b) => b.disabled)).toBe(true);

    c.nextNeighbor.set({ id: 'n', displayName: 'Chapter 2' });
    fixture.detectChanges();
    const [prevBtn, nextBtn] = arrows();
    expect(prevBtn.disabled).toBe(true);  // still no previous
    expect(nextBtn.disabled).toBe(false); // next now available
  });
});

describe('ReaderComponent page scrubber (requirement 2)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.itemId.set('item-1');
    return c;
  }

  function railFraction(c: ReaderComponent, frac: number): number {
    return (c as unknown as { pageForRailFraction: (f: number) => number }).pageForRailFraction(frac);
  }

  /** A fake rail element with a 100px-wide box for deterministic fraction math. */
  function fakeRail(): HTMLElement {
    return {
      getBoundingClientRect: () => ({ left: 0, width: 100 }) as DOMRect,
      setPointerCapture: () => { /* noop */ },
      releasePointerCapture: () => { /* noop */ },
    } as unknown as HTMLElement;
  }

  it('maps a rail fraction to a page index (direction-aware)', () => {
    const c = create();
    c.pages.set(makePages(5)); // indices 0..4
    c.direction.set('ltr');
    expect(railFraction(c, 0)).toBe(0);
    expect(railFraction(c, 0.5)).toBe(2);
    expect(railFraction(c, 1)).toBe(4);
    // RTL mirrors: the left end of the rail is the LAST page.
    c.direction.set('rtl');
    expect(railFraction(c, 0)).toBe(4);
    expect(railFraction(c, 1)).toBe(0);
  });

  it('dragging updates the current page live and shows the prominent bubble', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('paged');
    c.phase.set('ready');
    c.currentPage.set(0);
    const rail = fakeRail();

    c.onScrubStart(pointer({ pointerId: 1, clientX: 50, currentTarget: rail })); // frac .5 → page 2
    expect(c.scrubbing()).toBe(true);
    expect(c.currentPage()).toBe(2);

    c.onScrubMove(pointer({ pointerId: 1, clientX: 100, currentTarget: rail })); // frac 1 → page 4
    expect(c.currentPage()).toBe(4);
  });

  it('releasing ends the scrub and persists progress once', () => {
    const c = create();
    const httpMock = TestBed.inject(HttpTestingController);
    c.pages.set(makePages(5));
    c.view.set('paged');
    c.phase.set('ready');
    const rail = fakeRail();

    c.onScrubStart(pointer({ pointerId: 1, clientX: 50, currentTarget: rail }));
    c.onScrubEnd(pointer({ pointerId: 1, clientX: 50, currentTarget: rail }));
    expect(c.scrubbing()).toBe(false);

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.pageIndex).toBe(2);
    req.flush({ revision: 1, alreadyApplied: false });
  });
});

describe('ReaderComponent webtoon auto next/prev chapter (requirement 4)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.itemId.set('item-1');
    return c;
  }

  function useFakeScroller(
    c: ReaderComponent,
    opts: { scrollTop: number; clientHeight: number; scrollHeight: number; pageHeights: number[] },
  ): void {
    let top = 0;
    const imgs = opts.pageHeights.map((h) => {
      const img = { offsetTop: top, offsetHeight: h } as unknown as HTMLElement;
      top += h;
      return img;
    });
    const el = {
      scrollTop: opts.scrollTop,
      clientHeight: opts.clientHeight,
      scrollHeight: opts.scrollHeight,
      querySelectorAll: () => imgs,
    } as unknown as HTMLElement;
    (c as unknown as { scroller: () => { nativeElement: HTMLElement } }).scroller = () => ({ nativeElement: el });
  }

  function armed(c: ReaderComponent): 'next' | 'prev' | null {
    return (c as unknown as { webtoonEdgeArmed: 'next' | 'prev' | null }).webtoonEdgeArmed;
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); vi.restoreAllMocks(); });

  it('arms and (after a dwell) fires next-chapter navigation at the true bottom', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });

    c.onWebtoonScroll();
    expect(armed(c)).toBe('next'); // dwell timer armed, not yet fired

    vi.advanceTimersByTime(900);
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item']);
  });

  it('does not arm at the bottom when there is no next chapter', () => {
    const c = create();
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    c.nextNeighbor.set(null);
    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });

    c.onWebtoonScroll();
    expect(armed(c)).toBeNull();
  });

  it('does not arm from a programmatic scroll (resume-on-entry / scrubber seek)', () => {
    // Re-opening a finished webtoon chapter resumes at its last page via a
    // programmatic scroll; that must NOT be treated as the reader scrolling to the
    // bottom, or it would instantly auto-advance. Same guard covers scrubber seeks.
    const c = create();
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    (c as unknown as { lastProgrammaticScrollAt: number }).lastProgrammaticScrollAt = Date.now();
    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });

    c.onWebtoonScroll();
    expect(armed(c)).toBeNull(); // programmatic scroll ignored by the edge evaluator
  });

  it('arms previous-chapter only after scrolling back up to the very top', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.prevNeighbor.set({ id: 'prev-item', displayName: 'Chapter 0' });

    // First scroll down (records that the reader has actually scrolled).
    useFakeScroller(c, { scrollTop: 1000, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    expect(armed(c)).toBeNull();

    // Then scroll up to the top → arms 'prev'.
    useFakeScroller(c, { scrollTop: 0, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    expect(armed(c)).toBe('prev');

    vi.advanceTimersByTime(900);
    expect(nav).toHaveBeenCalledWith(['/reader', 'prev-item'], { queryParams: { at: 'end' } });
  });

  it('moving away from the edge disarms a pending advance (no accidental jump)', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });

    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    expect(armed(c)).toBe('next');

    // Reader scrolls back up before the dwell elapses → disarm.
    useFakeScroller(c, { scrollTop: 900, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    expect(armed(c)).toBeNull();

    vi.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
  });
});
