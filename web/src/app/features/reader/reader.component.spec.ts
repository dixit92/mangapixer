import { vi } from 'vitest';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Location } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { MatBottomSheet, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, Subject } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { ReaderOptionsSheetComponent } from './reader-settings-menu.component';
import { ReadStateService } from '../../core/reading/read-state.service';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import { WebtoonNavPreferencesService } from './webtoon-nav.service';
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

  describe('currentPageIndicator (double-page page numbers)', () => {
    it('shows both page numbers in spread mode with two distinct pages: "12-13"', () => {
      const c = create();
      c.pages.set(makePages(6));
      c.view.set('spread');
      c.coverIsStandalone.set(true); // [0],[1,2],[3,4],[5]
      c.currentPage.set(1); // in the [1,2] pair
      expect(c.currentPageIndicator()).toBe('2-3');
    });

    it('shows single page in spread mode with one wide page', () => {
      const c = create();
      const pages = makePages(5);
      pages[2] = { ...pages[2], width: 2000, height: 1200 }; // wide
      c.pages.set(pages);
      c.view.set('spread');
      c.coverIsStandalone.set(false); // [0,1],[2],[3,4]
      c.currentPage.set(2); // the wide page, alone
      expect(c.currentPageIndicator()).toBe('3');
    });

    it('shows single page in spread mode with standalone cover', () => {
      const c = create();
      c.pages.set(makePages(3));
      c.view.set('spread');
      c.coverIsStandalone.set(true); // [0],[1,2]
      c.currentPage.set(0); // the cover
      expect(c.currentPageIndicator()).toBe('1');
    });

    it('shows single page in paged mode regardless of spread pairing', () => {
      const c = create();
      c.pages.set(makePages(4));
      c.view.set('paged');
      c.coverIsStandalone.set(false); // would be [0,1],[2,3]
      c.currentPage.set(1);
      expect(c.currentPageIndicator()).toBe('2');
    });

    it('shows single page in webtoon mode', () => {
      const c = create();
      c.pages.set(makePages(5));
      c.view.set('webtoon');
      c.currentPage.set(3);
      expect(c.currentPageIndicator()).toBe('4');
    });

    it('shows both page numbers for the last pair in spread mode', () => {
      const c = create();
      c.pages.set(makePages(5));
      c.view.set('spread');
      c.coverIsStandalone.set(true); // [0],[1,2],[3,4]
      c.currentPage.set(3); // final pair [3,4]
      expect(c.currentPageIndicator()).toBe('4-5');
    });

    it('handles odd trailing page in spread mode (shows single number)', () => {
      const c = create();
      c.pages.set(makePages(5));
      c.view.set('spread');
      c.coverIsStandalone.set(false); // [0,1],[2,3],[4]
      c.currentPage.set(4); // odd trailing page
      expect(c.currentPageIndicator()).toBe('5');
    });
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
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item'], { replaceUrl: true });
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
 * Stale read-status after Back (1.7.1 fix). The reader is the notifying half of
 * the fix — on exit it tells `ReadStateService` which item's read/progress state
 * may have changed, so the retained browse view (see the 1.6.2
 * `LibraryBrowseReuseStrategy`) can patch that one card without a full re-fetch.
 * The browse-side patching is covered in `library-browse.component.spec.ts`.
 */
describe('ReaderComponent read-state notification on exit (1.7.1)', () => {
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

  it('notifies ReadStateService with the exiting item id on ngOnDestroy', () => {
    const c = create();
    c.itemId.set('item-1');
    const notify = vi.spyOn(TestBed.inject(ReadStateService), 'notifyChanged');

    c.ngOnDestroy();

    expect(notify).toHaveBeenCalledWith('item-1');
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

  it('labels the back button "Back to folder" (1.7.1: Back always exits to the folder)', () => {
    const { fixture } = renderReady();
    const back = (fixture.nativeElement as HTMLElement).querySelector('.reader-toolbar button') as HTMLElement;
    expect(back.getAttribute('aria-label')).toBe('Back to folder');
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
 * 1.7.0 reader touch-UX lane, all through the component's public surface:
 * direction-aware swipe page-turning, the draggable page scrubber, and
 * reader-bar chapter arrows. (The fourth 1.7.0 feature, webtoon auto
 * next/prev chapter, was reverted in 1.7.1 — see the describe block above.)
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

    it('never turns a PAGE in webtoon; with tap-to-scroll off it claims nothing at all (1.11.0)', () => {
      const c = paged();
      c.view.set('webtoon');
      TestBed.inject(WebtoonNavPreferencesService).setTapStep(0);
      const step = vi.spyOn(c, 'scrollWebtoonBy');
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 305, timeStamp: 100 }));
      expect(c.currentPage()).toBe(1);
      expect(step).not.toHaveBeenCalled();
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

  /**
   * 1.8.0 full-surface swipe refinements: the gesture is followed live (swipeDx),
   * axis-locked, and only claimed when the browser does not own the drag.
   */
  describe('1.8.0 full-surface swipe', () => {
    function paged(n = 5, at = 1) {
      const c = create();
      c.pages.set(makePages(n));
      c.view.set('paged');
      c.phase.set('ready');
      c.direction.set('ltr');
      c.currentPage.set(at);
      return c;
    }

    it('the spread row follows the finger during a horizontal drag and springs back on release', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 196, clientY: 301, timeStamp: 10 })); // inside slop
      expect(c.swipeDx()).toBe(0);
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 140, clientY: 304, timeStamp: 60 }));
      expect(c.swipeDx()).toBe(-60);
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 90, clientY: 305, timeStamp: 100 }));
      expect(c.swipeDx()).toBe(0);
      expect(c.currentPage()).toBe(2);
    });

    it('a gesture that starts vertical stays vertical: no follow, no page turn even if it drifts sideways', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 202, clientY: 340, timeStamp: 30 })); // locks 'y'
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 60, clientY: 345, timeStamp: 80 }));
      expect(c.swipeDx()).toBe(0);
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 345, timeStamp: 100 }));
      expect(c.currentPage()).toBe(1);
    });

    it('a short drag that does not turn the page still swallows the ghost centre tap', () => {
      const c = paged();
      c.isFullscreen.set(true);
      c.chromeVisible.set(true);
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 180, clientY: 300, timeStamp: 500 }));
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 180, clientY: 300, timeStamp: 1000 })); // 20px, slow
      expect(c.currentPage()).toBe(1);
      c.onCenterTap(); // the click the browser synthesizes on release
      expect(c.chromeVisible()).toBe(true);
    });

    it('rubber-bands the follow when there is no page or chapter in that direction', () => {
      const c = paged(5, 4); // last page, no next chapter
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 100, clientY: 300, timeStamp: 50 }));
      expect(c.swipeDx()).toBeCloseTo(-35, 5);
      // Backwards there IS a page: full follow.
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 300, clientY: 300, timeStamp: 90 }));
      expect(c.swipeDx()).toBe(100);
    });

    it('does not claim the drag when the browser owns it (overflowing or pinch-zoomed page)', () => {
      const c = paged();
      c.overflowsX.set(true);
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 100, clientY: 300, timeStamp: 50 }));
      expect(c.swipeDx()).toBe(0);
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 300, timeStamp: 100 }));
      expect(c.currentPage()).toBe(1);
    });

    it('a pointercancel (browser/OS took the gesture) abandons the swipe and resets the follow', () => {
      const c = paged();
      c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
      c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 140, clientY: 300, timeStamp: 50 }));
      expect(c.swipeDx()).toBe(-60);
      c.onReaderPointerCancel(pointer({ pointerId: 1, clientX: 140, clientY: 300, timeStamp: 60 }));
      expect(c.swipeDx()).toBe(0);
      c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 300, timeStamp: 100 }));
      expect(c.currentPage()).toBe(1);
    });

    it('touch-action: claims horizontal drags unless the page overflows sideways or is zoomed', () => {
      const c = paged();
      expect(c.touchAction()).toBe('pinch-zoom');           // fit-screen: nothing overflows
      c.overflowsY.set(true);
      expect(c.touchAction()).toBe('pan-y pinch-zoom');     // fit-width: keep native vertical scroll
      c.overflowsX.set(true);
      expect(c.touchAction()).toBe('auto');                 // original/zoomed: native panning
      c.overflowsX.set(false); c.overflowsY.set(false);
      c.zoomed.set(true);
      expect(c.touchAction()).toBe('auto');
      c.zoomed.set(false);
      c.view.set('webtoon');
      expect(c.touchAction()).toBeNull();                   // webtoon: untouched native scroll
    });

    it('measureOverflow compares the page LAYOUT boxes (transform-immune) with the viewport', () => {
      const c = paged();
      const pages = [{ offsetWidth: 1200, offsetHeight: 600 }];
      // scrollWidth is deliberately misleading here (as it is mid spring-back): it must be ignored.
      const el = {
        clientWidth: 800, clientHeight: 600, scrollWidth: 800, scrollHeight: 600,
        querySelectorAll: () => pages,
      } as unknown as HTMLElement;
      (c as unknown as { viewport: () => { nativeElement: HTMLElement } }).viewport = () => ({ nativeElement: el });
      c.measureOverflow();
      expect(c.overflowsX()).toBe(true);
      expect(c.overflowsY()).toBe(false);
      pages[0] = { offsetWidth: 800, offsetHeight: 600 };
      (el as unknown as { scrollWidth: number }).scrollWidth = 960; // translated row mid-transition
      c.measureOverflow();
      expect(c.overflowsX()).toBe(false);
    });

    it('help legend swipe labels are by finger direction and mirror in RTL', () => {
      const c = paged();
      expect(c.swipeLeftLabel()).toBe('Next page');
      expect(c.swipeRightLabel()).toBe('Previous page');
      c.direction.set('rtl');
      expect(c.swipeLeftLabel()).toBe('Previous page');
      expect(c.swipeRightLabel()).toBe('Next page');
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
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item'], { replaceUrl: true });

    c.prevChapter();
    expect(nav).toHaveBeenCalledWith(['/reader', 'prev-item'], { queryParams: { at: 'end' }, replaceUrl: true });
  });

  /**
   * 1.7.3 fix (gap b): chapter-advance reuses this SAME component instance —
   * the route only changes :itemId, so `ngOnDestroy` never runs for the
   * chapter being left. Before this fix, the browse card and Continue row for
   * that just-finished chapter stayed stale until the reader was closed
   * entirely. `notifyChanged` must fire with the LEAVING item's id (not the
   * neighbor being navigated to), and while `itemId()` still holds it — i.e.
   * before the paramMap subscription would flip it to the new item.
   */
  it('notifies ReadStateService with the LEAVING item id when advancing chapters in-reader', () => {
    const c = create();
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const notify = vi.spyOn(TestBed.inject(ReadStateService), 'notifyChanged');
    c.pages.set(makePages(3));
    c.phase.set('ready');
    c.itemId.set('current-item');
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    c.prevNeighbor.set({ id: 'prev-item', displayName: 'Chapter 0' });

    c.nextChapter();
    expect(notify).toHaveBeenCalledWith('current-item');
    expect(notify).not.toHaveBeenCalledWith('next-item');

    notify.mockClear();
    c.prevChapter();
    expect(notify).toHaveBeenCalledWith('current-item');
    expect(notify).not.toHaveBeenCalledWith('prev-item');
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

  // 1.8.0 slider rework: the thumb travels an inset track (its radius at both
  // ends), the fill tracks the thumb, and both mirror in RTL.
  it('slider pointer math is inset by the thumb radius so finger and thumb align', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.view.set('paged');
    c.phase.set('ready');
    const rail = fakeRail(); // 100px wide → thumb travel is 11..89
    c.onScrubStart(pointer({ pointerId: 1, clientX: 11, currentTarget: rail }));
    expect(c.currentPage()).toBe(0);
    c.onScrubMove(pointer({ pointerId: 1, clientX: 89, currentTarget: rail }));
    expect(c.currentPage()).toBe(4);
    c.onScrubMove(pointer({ pointerId: 1, clientX: 5, currentTarget: rail })); // before the travel: clamps
    expect(c.currentPage()).toBe(0);
  });

  it('fill and thumb positions agree and mirror in RTL', () => {
    const c = create();
    c.pages.set(makePages(5));
    c.currentPage.set(1); // 25% along
    c.direction.set('ltr');
    expect(c.scrubThumbPct()).toBe(25);
    expect(c.scrubFillPct()).toBe(25);
    expect(c.scrubThumbLeft()).toBe('calc(11px + (100% - 22px) * 0.25)');
    c.direction.set('rtl');
    expect(c.scrubThumbPct()).toBe(75);
    expect(c.scrubFillPct()).toBe(25); // fill grows from the right, still 25% of the track
  });
});

describe('ReaderComponent webtoon auto-advance REMOVED (1.7.1 owner revert)', () => {
  // The 1.7.0 webtoon auto-next/auto-previous-on-scroll-up behavior is gone —
  // the scroll-up gesture fought the fullscreen-exit gesture on touch. These
  // are regression tests confirming scrolling to either edge never navigates;
  // the explicit chapter buttons (reader-bar arrows + end-of-chapter footer)
  // remain the only way to move between chapters in webtoon.
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

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); vi.restoreAllMocks(); });

  it('scrolling to the true bottom never navigates, even with a next chapter available', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.currentPage.set(1);
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    useFakeScroller(c, { scrollTop: 1860, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });

    c.onWebtoonScroll();
    vi.advanceTimersByTime(5000); // well past the old 900ms dwell — still nothing

    expect(nav).not.toHaveBeenCalled();
  });

  it('scrolling up to the very top never navigates, even with a previous chapter available', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.pages.set(makePages(3));
    c.view.set('webtoon');
    c.prevNeighbor.set({ id: 'prev-item', displayName: 'Chapter 0' });

    useFakeScroller(c, { scrollTop: 1000, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    useFakeScroller(c, { scrollTop: 0, clientHeight: 160, scrollHeight: 2020, pageHeights: [1000, 1000, 20] });
    c.onWebtoonScroll();
    vi.advanceTimersByTime(5000);

    expect(nav).not.toHaveBeenCalled();
  });

  it('the explicit chapter buttons still navigate from webtoon (unaffected by the revert)', () => {
    const c = create();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    c.view.set('webtoon');
    c.nextNeighbor.set({ id: 'next-item', displayName: 'Chapter 2' });
    c.prevNeighbor.set({ id: 'prev-item', displayName: 'Chapter 0' });

    c.nextChapter();
    expect(nav).toHaveBeenCalledWith(['/reader', 'next-item'], { replaceUrl: true });

    c.prevChapter();
    expect(nav).toHaveBeenCalledWith(['/reader', 'prev-item'], { queryParams: { at: 'end' }, replaceUrl: true });
  });
});

/**
 * 1.9.0 page-navigation transition. The transition is presentation-only (it never
 * changes which page is shown), reading-direction aware, and gated off where it
 * would be wrong (webtoon, scrubbing, zoomed/overflowing pages, the 'none' pref).
 */
describe('ReaderComponent page-navigation transition (1.9.0)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    localStorage.clear();
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.pages.set(makePages(6));
    c.view.set('paged');
    return c;
  }

  describe('enterSideForNav (reading-direction aware)', () => {
    it('LTR: forward enters from the right, backward from the left', () => {
      const c = create();
      c.direction.set('ltr');
      expect(c.enterSideForNav(true)).toBe('from-right');
      expect(c.enterSideForNav(false)).toBe('from-left');
    });

    it('RTL (manga): forward enters from the left, backward from the right', () => {
      const c = create();
      c.direction.set('rtl');
      expect(c.enterSideForNav(true)).toBe('from-left');
      expect(c.enterSideForNav(false)).toBe('from-right');
    });
  });

  it('goToPage sets navEnter from the travel direction before the page swaps', () => {
    const c = create();
    c.direction.set('ltr');
    c.currentPage.set(2);
    (c as unknown as { goToPage: (n: number) => void }).goToPage(3); // forward
    expect(c.currentPage()).toBe(3);
    expect(c.navEnter()).toBe('from-right');

    (c as unknown as { goToPage: (n: number) => void }).goToPage(1); // backward
    expect(c.navEnter()).toBe('from-left');
  });

  describe('pageAnimActive gating', () => {
    it('is active for slide on a normal (non-overflowing) paged view', () => {
      const c = create();
      TestBed.inject(ReaderPreferencesService).setPageAnimation('slide');
      c.overflowsX.set(false);
      c.zoomed.set(false);
      c.scrubbing.set(false);
      expect(c.pageAnimActive()).toBe(true);
    });

    it('is off when the preference is none', () => {
      const c = create();
      TestBed.inject(ReaderPreferencesService).setPageAnimation('none');
      expect(c.pageAnimActive()).toBe(false);
    });

    it('is off while scrubbing, when zoomed, when overflowing horizontally, and in webtoon', () => {
      const c = create();
      const prefs = TestBed.inject(ReaderPreferencesService);
      prefs.setPageAnimation('slide');

      c.scrubbing.set(true);
      expect(c.pageAnimActive()).toBe(false);
      c.scrubbing.set(false);

      c.zoomed.set(true);
      expect(c.pageAnimActive()).toBe(false);
      c.zoomed.set(false);

      c.overflowsX.set(true);
      expect(c.pageAnimActive()).toBe(false);
      c.overflowsX.set(false);

      c.view.set('webtoon');
      expect(c.pageAnimActive()).toBe(false);
    });
  });

  describe('swipe commit suppresses the spring-back (committing)', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

    it('beginCommit holds committing true then clears it after the transition window', () => {
      const c = create();
      expect(c.committing()).toBe(false);
      (c as unknown as { beginCommit: () => void }).beginCommit();
      expect(c.committing()).toBe(true);
      vi.advanceTimersByTime(250);
      expect(c.committing()).toBe(false);
    });
  });
});

/**
 * 1.9.0 onboarding: the reader help overlay auto-shows on the FIRST reader open on
 * a device (localStorage-persisted, per-device) and never again, while the manual
 * '?' control still reopens it any time.
 */
describe('ReaderComponent onboarding help auto-show (1.9.0)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    return TestBed.createComponent(ReaderComponent).componentInstance;
  }

  beforeEach(() => localStorage.clear());

  it('auto-shows help the first time and records it seen (per-device)', () => {
    const c = create();
    const prefs = TestBed.inject(ReaderPreferencesService);
    expect(prefs.hasSeenHelp()).toBe(false);

    (c as unknown as { maybeAutoShowHelp: () => void }).maybeAutoShowHelp();

    expect(c.helpVisible()).toBe(true);
    expect(prefs.hasSeenHelp()).toBe(true);
    expect(localStorage.getItem(ReaderPreferencesService.HelpSeenKey)).toBe('1');
  });

  it('does not auto-show again within the same instance (per-instance guard)', () => {
    const c = create();
    (c as unknown as { maybeAutoShowHelp: () => void }).maybeAutoShowHelp();
    c.closeHelp();
    (c as unknown as { maybeAutoShowHelp: () => void }).maybeAutoShowHelp();
    expect(c.helpVisible()).toBe(false);
  });

  it('does not auto-show for a device that has already seen it', () => {
    // Seed the per-device flag before the component (and its service) exist.
    localStorage.setItem(ReaderPreferencesService.HelpSeenKey, '1');
    const c = create();
    expect(TestBed.inject(ReaderPreferencesService).hasSeenHelp()).toBe(true);
    (c as unknown as { maybeAutoShowHelp: () => void }).maybeAutoShowHelp();
    expect(c.helpVisible()).toBe(false);
  });
});

/**
 * 1.10.0 phone controls (finding F2) + menu highlight (finding F4).
 *
 * At handset width (CDK XSmall) the bar keeps only Next chapter + Fullscreen +
 * a "Reader options" trigger that opens the options bottom sheet; everywhere
 * else the full 8-control bar is UNCHANGED (guarded here so a later change
 * cannot silently drift the desktop/tablet chrome). The reader menus mark the
 * active option with the `selected-option` highlight, not a checkmark; they
 * render in a CDK overlay so the tests open them and query `document` (same
 * approach as the 1.8.1 browse View-menu tests).
 */
describe('ReaderComponent phone controls + menu highlight (1.10.0)', () => {
  function render(compact: boolean) {
    const breakpoints = {
      observe: () => of({ matches: compact, breakpoints: {} }),
      isMatched: () => compact,
    };
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [...baseProviders(), { provide: BreakpointObserver, useValue: breakpoints }],
    });
    const fixture = TestBed.createComponent(ReaderComponent);
    fixture.detectChanges(); // ngOnInit
    const c = fixture.componentInstance;
    c.pages.set(makePages(3));
    c.view.set('paged');
    c.phase.set('ready');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const labels = () => Array.from(el.querySelectorAll<HTMLElement>('.reader-toolbar button'))
      .map((b) => b.getAttribute('aria-label'));
    return { fixture, c, el, labels };
  }
  afterEach(() => vi.restoreAllMocks());

  it('PHONE: the bar keeps only Back, Next chapter, Fullscreen and the Reader options trigger', () => {
    const { c, labels } = render(true);
    expect(c.compact()).toBe(true);
    expect(labels()).toEqual(['Back to folder', 'No next chapter', 'Enter fullscreen', 'Reader options']);
  });

  it('PHONE: the options trigger is an accessible menu button (haspopup + expanded state)', () => {
    const { c, el, fixture } = render(true);
    const trigger = el.querySelector('.options-trigger') as HTMLElement;
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    c.optionsOpen.set(true);
    fixture.detectChanges();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
  });

  it('DESKTOP / TABLET: the full bar is unchanged (all eight controls, no overflow trigger)', () => {
    const { c, labels } = render(false);
    expect(c.compact()).toBe(false);
    expect(labels()).toEqual([
      'Back to folder',
      'No previous chapter', 'No next chapter',
      'Reading mode', 'Image fit', 'Switch to right-to-left', 'Page transition',
      'Reading help', 'Enter fullscreen',
    ]);
  });

  it('DESKTOP webtoon: the settings slot offers Tap to scroll instead of Page transition (1.11.0)', () => {
    const { c, fixture, labels } = render(false);
    c.view.set('webtoon');
    fixture.detectChanges();
    expect(labels()).toContain('Tap to scroll');
    expect(labels()).not.toContain('Page transition');
    expect(labels().filter((l) => l === 'Tap to scroll' || l === 'Page transition').length).toBe(1); // one slot
  });

  it('PHONE webtoon: the bar width slider moves into the sheet too', () => {
    const { c, el, fixture } = render(true);
    c.view.set('webtoon');
    fixture.detectChanges();
    expect(el.querySelector('.reader-toolbar .width-slider')).toBeNull();
    expect(el.querySelector('.options-trigger')).toBeTruthy();
  });

  it('openOptions opens the sheet with the reader as host, pins the chrome, and releases it on dismiss', () => {
    const { c } = render(true);
    const dismissed = new Subject<void>();
    const open = vi.spyOn(TestBed.inject(MatBottomSheet), 'open')
      .mockReturnValue({ afterDismissed: () => dismissed.asObservable() } as unknown as MatBottomSheetRef<ReaderOptionsSheetComponent>);

    c.openOptions();
    expect(open).toHaveBeenCalledTimes(1);
    const [component, config] = open.mock.calls[0];
    expect(component).toBe(ReaderOptionsSheetComponent);
    expect(config?.data).toBe(c);
    expect(config?.panelClass).toBe('reader-options-sheet');
    expect(c.optionsOpen()).toBe(true);
    expect(c.menuOpen()).toBe(true);

    c.openOptions(); // a re-entrant tap while open is a no-op
    expect(open).toHaveBeenCalledTimes(1);

    dismissed.next();
    expect(c.optionsOpen()).toBe(false);
    expect(c.menuOpen()).toBe(false);
  });

  it('setDirection sets an explicit direction (the sheet radio pair); toggleDirection still flips', () => {
    const { c } = render(false);
    c.setDirection('rtl');
    expect(c.direction()).toBe('rtl');
    c.setDirection('rtl');
    expect(c.direction()).toBe('rtl');
    c.toggleDirection();
    expect(c.direction()).toBe('ltr');
  });

  /** Open an overlay menu through its bar trigger and return the panel. */
  function openMenu(el: HTMLElement, fixture: ComponentFixture<ReaderComponent>, triggerLabel: string): HTMLElement {
    (el.querySelector(`.reader-toolbar button[aria-label="${triggerLabel}"]`) as HTMLElement).click();
    fixture.detectChanges();
    const panel = document.querySelector('.reader-options-menu') as HTMLElement;
    expect(panel, 'the reader-options-menu overlay panel').not.toBeNull();
    return panel;
  }
  function items(panel: HTMLElement): HTMLElement[] {
    return Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
  }
  function itemByLabel(panel: HTMLElement, label: string): HTMLElement {
    const match = items(panel).find((i) => (i.textContent ?? '').replace(/\s+/g, ' ').trim().endsWith(label));
    expect(match, `menu item ending with "${label}"`).toBeDefined();
    return match!;
  }

  it('F4 reading-mode menu: menuitemradio items, the active one highlighted with its OWN glyph (no tick)', () => {
    const { c, el, fixture } = render(false);
    c.viewPref.set('spread');
    c.coverIsStandalone.set(true);
    c.view.set('spread');
    fixture.detectChanges();
    const panel = openMenu(el, fixture, 'Reading mode');

    expect(items(panel).length).toBe(5);
    for (const item of items(panel)) expect(item.getAttribute('role')).toBe('menuitemradio');
    const on = itemByLabel(panel, 'Double page (offset cover)');
    expect(on.classList.contains('selected-option')).toBe(true);
    expect(on.getAttribute('aria-checked')).toBe('true');
    expect(on.querySelector('mat-icon')?.textContent?.trim()).toBe('auto_stories');
    for (const item of items(panel)) {
      expect(item.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
      if (item !== on) {
        expect(item.classList.contains('selected-option')).toBe(false);
        expect(item.getAttribute('aria-checked')).toBe('false');
      }
    }
  });

  it('F4 image-fit menu: the active fit is highlighted (it previously had no selected-state at all)', () => {
    const { c, el, fixture } = render(false);
    c.setFitMode('width');
    fixture.detectChanges();
    const panel = openMenu(el, fixture, 'Image fit');
    expect(items(panel).map((i) => i.getAttribute('role'))).toEqual(Array(4).fill('menuitemradio'));
    expect(itemByLabel(panel, 'Fit width').classList.contains('selected-option')).toBe(true);
    expect(itemByLabel(panel, 'Fit width').getAttribute('aria-checked')).toBe('true');
    expect(itemByLabel(panel, 'Fit screen').classList.contains('selected-option')).toBe(false);
    expect(itemByLabel(panel, 'Fit screen').getAttribute('aria-checked')).toBe('false');
    expect(panel.querySelectorAll('mat-icon').length).toBe(4); // every option keeps a glyph
  });
});

/**
 * 1.11.0 Lane B - webtoon tap-to-scroll (requirement 11). Free scroll stays the
 * default; on top of it a tap resolves by vertical thirds (top = back a screen,
 * bottom = forward, centre = toggle chrome) and a horizontal swipe steps a screen.
 * The step is a per-device preference (`WebtoonNavPreferencesService`); 0 = off
 * restores the pre-1.11.0 "tap anywhere toggles chrome" reader. Driven through
 * the public handlers with a fake scroller (deterministic geometry).
 */
describe('ReaderComponent webtoon tap-to-scroll (1.11.0)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    localStorage.clear();
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.itemId.set('item-1');
    c.pages.set(makePages(10));
    c.view.set('webtoon');
    c.phase.set('ready');
    return c;
  }

  /** A fake scroller: 600px tall viewport over a 5000px strip, positioned at y=100 on screen. */
  function useFakeScroller(c: ReaderComponent, scrollTop = 1000) {
    const calls: ScrollToOptions[] = [];
    const el = {
      scrollTop, clientHeight: 600, scrollHeight: 5000,
      getBoundingClientRect: () => ({ top: 100, height: 600 }) as DOMRect,
      scrollTo: (opts: ScrollToOptions) => calls.push(opts),
      querySelectorAll: () => [],
    } as unknown as HTMLElement;
    (c as unknown as { scroller: () => { nativeElement: HTMLElement } }).scroller = () => ({ nativeElement: el });
    return { el, calls };
  }

  function tap(clientY: number, target: Partial<HTMLElement> = {}): MouseEvent {
    return { clientY, target: { closest: () => null, ...target } } as unknown as MouseEvent;
  }

  it('defaults to a 90% step (on) per device, persisted in localStorage', () => {
    create();
    const nav = TestBed.inject(WebtoonNavPreferencesService);
    expect(nav.tapStep()).toBe(90);
    expect(nav.tapZonesEnabled()).toBe(true);
    nav.setTapStep(80);
    expect(localStorage.getItem(WebtoonNavPreferencesService.TapStepKey)).toBe('80');
    nav.setTapStep(0);
    expect(nav.tapZonesEnabled()).toBe(false);
  });

  it('bottom third scrolls forward ~one screen (90% overlap step), top third scrolls back', () => {
    const c = create();
    const { calls } = useFakeScroller(c, 1000);
    c.onWebtoonTap(tap(100 + 550)); // bottom third of a 600px viewport starting at y=100
    expect(calls.at(-1)).toEqual({ top: 1540, behavior: 'smooth' }); // 1000 + 0.9 * 600
    c.onWebtoonTap(tap(100 + 50)); // top third
    expect(calls.at(-1)).toEqual({ top: 460, behavior: 'smooth' });  // 1000 - 540
  });

  it('centre third toggles the chrome (fullscreen) instead of scrolling', () => {
    const c = create();
    const { calls } = useFakeScroller(c);
    c.isFullscreen.set(true);
    c.chromeVisible.set(true);
    c.onWebtoonTap(tap(100 + 300));
    expect(calls.length).toBe(0);
    expect(c.chromeVisible()).toBe(false);
  });

  it('honours the chosen step and clamps at both ends of the strip', () => {
    const c = create();
    TestBed.inject(WebtoonNavPreferencesService).setTapStep(100);
    const { el, calls } = useFakeScroller(c, 4200); // 200px from the bottom (max 4400)
    c.onWebtoonTap(tap(100 + 590));
    expect(calls.at(-1)).toEqual({ top: 4400, behavior: 'smooth' }); // clamped, not 4800
    (el as unknown as { scrollTop: number }).scrollTop = 4400;
    c.onWebtoonTap(tap(100 + 590));
    expect(calls.length).toBe(1); // already at the bottom: nothing to do, and NO chapter advance
  });

  it('off: a tap anywhere toggles the chrome (the pre-1.11.0 reader) and the scroller stays fully native', () => {
    const c = create();
    TestBed.inject(WebtoonNavPreferencesService).setTapStep(0);
    const { calls } = useFakeScroller(c);
    c.isFullscreen.set(true);
    c.chromeVisible.set(true);
    c.onWebtoonTap(tap(100 + 590)); // bottom third would scroll if zones were on
    expect(calls.length).toBe(0);
    expect(c.chromeVisible()).toBe(false);
    expect(c.webtoonTouchAction()).toBeNull();
  });

  it('touch-action claims horizontal drags only while on and not pinch-zoomed', () => {
    const c = create();
    expect(c.webtoonTouchAction()).toBe('pan-y pinch-zoom');
    c.zoomed.set(true);
    expect(c.webtoonTouchAction()).toBeNull();
  });

  it('a tap on the end-of-chapter footer buttons is left to the button', () => {
    const c = create();
    const { calls } = useFakeScroller(c);
    const button = document.createElement('button');
    c.onWebtoonTap(tap(100 + 590, { closest: (sel: string) => (sel === 'button' ? button : null) } as Partial<HTMLElement>));
    expect(calls.length).toBe(0);
  });

  it('a horizontal swipe steps a screen (left = forward, right = back) and swallows its ghost click', () => {
    const c = create();
    const { calls } = useFakeScroller(c, 1000);
    c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
    c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 140, clientY: 302, timeStamp: 60 }));
    expect(c.swipeDx()).toBe(0); // the strip never follows the finger sideways
    c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 90, clientY: 305, timeStamp: 100 }));
    expect(calls.at(-1)).toEqual({ top: 1540, behavior: 'smooth' });
    c.onWebtoonTap(tap(100 + 590)); // the click the browser synthesizes on release
    expect(calls.length).toBe(1);

    c.onReaderPointerDown(pointer({ pointerId: 2, clientX: 90, clientY: 300, timeStamp: 1000 }));
    c.onReaderPointerUp(pointer({ pointerId: 2, clientX: 200, clientY: 300, timeStamp: 1100 }));
    expect(calls.at(-1)).toEqual({ top: 460, behavior: 'smooth' });
  });

  it('a vertical drag in webtoon is never ours (native scroll), and currentPage is untouched by gestures', () => {
    const c = create();
    const { calls } = useFakeScroller(c);
    c.currentPage.set(3);
    c.onReaderPointerDown(pointer({ pointerId: 1, clientX: 200, clientY: 300, timeStamp: 0 }));
    c.onReaderPointerMove(pointer({ pointerId: 1, clientX: 202, clientY: 340, timeStamp: 30 })); // locks 'y'
    c.onReaderPointerUp(pointer({ pointerId: 1, clientX: 60, clientY: 345, timeStamp: 100 }));
    expect(calls.length).toBe(0);
    expect(c.currentPage()).toBe(3);
  });

  it('scrolls instantly under prefers-reduced-motion', () => {
    const c = create();
    const { calls } = useFakeScroller(c, 1000);
    const original = window.matchMedia;
    window.matchMedia = (() => ({ matches: true })) as unknown as typeof window.matchMedia;
    try {
      c.scrollWebtoonBy(1);
    } finally {
      window.matchMedia = original;
    }
    expect(calls.at(-1)).toEqual({ top: 1540, behavior: 'auto' });
  });
});

/**
 * 1.11.0 Lane B - adaptive double page (requirement 12). A synthetic two-up
 * spread on a narrow PORTRAIT screen (CDK HandsetPortrait) makes each page tiny,
 * so the reader renders single pages there while the CHOSEN mode stays "Double
 * page" (menus keep highlighting it, the preference persists) and the pairing
 * comes back the moment the device is rotated / widened. Only the app's own
 * pairing is gated: a wide source page (a stitched spread) renders exactly as it
 * always did, alone and full width. Breakpoints are stubbed per query.
 */
describe('ReaderComponent adaptive double page on narrow portrait (1.11.0)', () => {
  function create(narrowPortrait: boolean) {
    const matches = (q: string | readonly string[]) => q === Breakpoints.HandsetPortrait && narrowPortrait;
    const breakpoints = {
      observe: (q: string | readonly string[]) => of({ matches: matches(q), breakpoints: {} }),
      isMatched: (q: string | readonly string[]) => matches(q),
    };
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [...baseProviders(), { provide: BreakpointObserver, useValue: breakpoints }],
    });
    localStorage.clear();
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.itemId.set('item-1');
    c.pages.set(makePages(6)); // standalone cover: [0],[1,2],[3,4],[5]
    c.coverIsStandalone.set(true);
    c.view.set('spread');
    c.phase.set('ready');
    return c;
  }
  const effectiveIndex = (c: ReaderComponent) =>
    (c as unknown as { effectivePageIndex: () => number }).effectivePageIndex();

  it('renders single pages, steps one page at a time and persists a single-page index, while view() stays spread', () => {
    const c = create(true);
    expect(c.narrowPortrait()).toBe(true);
    expect(c.view()).toBe('spread');          // the choice (menus highlight it)
    expect(c.effectiveView()).toBe('paged');  // what is on screen
    c.currentPage.set(1);
    expect(c.currentSpreadEntries().map((e) => e.entryKey)).toEqual(['p1']);
    c.nextPage();
    expect(c.currentPage()).toBe(2);          // not 3 (the next pair)
    expect(effectiveIndex(c)).toBe(2);        // progress is the page shown, not a pair's last index
    c.currentPage.set(5);
    expect((c as unknown as { isAtEnd: () => boolean }).isAtEnd()).toBe(true);
  });

  it('keeps the two-up pairing on a wide / landscape screen', () => {
    const c = create(false);
    expect(c.effectiveView()).toBe('spread');
    c.currentPage.set(1);
    expect(c.currentSpreadEntries().map((e) => e.entryKey)).toEqual(['p1', 'p2']);
    c.nextPage();
    expect(c.currentPage()).toBe(3);
    expect(effectiveIndex(c)).toBe(4);
  });

  it('is live: the pairing comes straight back when the screen stops being narrow portrait', () => {
    const narrow = new Subject<{ matches: boolean; breakpoints: Record<string, boolean> }>();
    const breakpoints = {
      observe: (q: string | readonly string[]) =>
        q === Breakpoints.HandsetPortrait ? narrow : of({ matches: false, breakpoints: {} }),
      isMatched: (q: string | readonly string[]) => q === Breakpoints.HandsetPortrait,
    };
    TestBed.configureTestingModule({
      imports: [ReaderComponent],
      providers: [...baseProviders(), { provide: BreakpointObserver, useValue: breakpoints }],
    });
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    c.pages.set(makePages(6));
    c.view.set('spread');
    c.currentPage.set(1);
    expect(c.currentSpreadEntries().length).toBe(1);
    narrow.next({ matches: false, breakpoints: {} }); // rotated to landscape
    expect(c.narrowPortrait()).toBe(false);
    expect(c.currentSpreadEntries().length).toBe(2);
  });

  it('never touches the pairing model itself, nor single-page / webtoon views', () => {
    const c = create(true);
    expect(c.spreads()).toEqual([[0], [1, 2], [3, 4], [5]]); // spreads() is the model; only rendering is gated
    c.view.set('paged');
    expect(c.effectiveView()).toBe('paged');
    c.view.set('webtoon');
    expect(c.effectiveView()).toBe('webtoon');
  });

  it('leaves a natural wide page (stitched spread) exactly as before: alone, in either orientation', () => {
    for (const narrow of [true, false]) {
      const c = create(narrow);
      const pages = makePages(4);
      pages[2] = { ...pages[2], width: 2000, height: 1200 }; // a landscape source page
      c.pages.set(pages);
      c.coverIsStandalone.set(false);
      expect(c.spreads()).toEqual([[0, 1], [2], [3]]);
      c.currentPage.set(2);
      expect(c.currentSpreadEntries().map((e) => e.entryKey)).toEqual(['p2']);
      TestBed.resetTestingModule();
    }
  });

  it('chooseSpread on a narrow portrait screen keeps the choice (persisted) and says why the page did not change', () => {
    const c = create(true);
    // The reader's own instance: MatSnackBarModule provides MatSnackBar in the
    // standalone component's environment injector, not the TestBed root.
    const snack = vi.spyOn((c as unknown as { snackBar: MatSnackBar }).snackBar, 'open')
      .mockImplementation(() => ({}) as never);
    c.view.set('paged');
    c.chooseSpread(true);
    expect(c.view()).toBe('spread');
    expect(c.viewPref()).toBe('spread');
    expect(localStorage.getItem('mangaplex-reader-view')).toBe('spread');
    expect(snack).toHaveBeenCalledTimes(1);
    expect(String(snack.mock.calls[0][0])).toContain('landscape');
    // The phone sheet carries the note inline, so no toast is stacked under it.
    c.optionsOpen.set(true);
    c.chooseSpread(false);
    expect(snack).toHaveBeenCalledTimes(1);
  });

  it('does not toast on a wide screen', () => {
    const c = create(false);
    // The reader's own instance: MatSnackBarModule provides MatSnackBar in the
    // standalone component's environment injector, not the TestBed root.
    const snack = vi.spyOn((c as unknown as { snackBar: MatSnackBar }).snackBar, 'open')
      .mockImplementation(() => ({}) as never);
    c.chooseSpread(true);
    expect(snack).not.toHaveBeenCalled();
  });
});

/**
 * 1.11.0 Lane B - page-turn ghost (requirement 13). The page(s) just left stay
 * rendered underneath the incoming row for the length of the Slide / Reveal
 * transition, so the wipe / push runs over the OLD page instead of the dark
 * background, then they are dropped. Never held where no transition plays.
 */
describe('ReaderComponent page-turn ghost (1.11.0)', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  function create(animation: 'slide' | 'reveal' | 'none') {
    TestBed.configureTestingModule({ imports: [ReaderComponent], providers: baseProviders() });
    localStorage.clear();
    const c = TestBed.createComponent(ReaderComponent).componentInstance;
    TestBed.inject(ReaderPreferencesService).setPageAnimation(animation);
    c.itemId.set('item-1');
    c.pages.set(makePages(6));
    c.view.set('paged');
    c.phase.set('ready');
    c.currentPage.set(2);
    return c;
  }
  const go = (c: ReaderComponent, n: number) => (c as unknown as { goToPage: (n: number) => void }).goToPage(n);

  it('Slide: holds the outgoing page for the 220ms transition, then drops it', () => {
    const c = create('slide');
    go(c, 3);
    expect(c.currentPage()).toBe(3);
    expect(c.outgoing().map((e) => e.entryKey)).toEqual(['p2']);
    vi.advanceTimersByTime(219);
    expect(c.outgoing().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(c.outgoing()).toEqual([]);
  });

  it('Reveal: holds for the 300ms wipe (the durations mirror the CSS)', () => {
    const c = create('reveal');
    go(c, 1);
    expect(c.outgoing().map((e) => e.entryKey)).toEqual(['p2']);
    vi.advanceTimersByTime(299);
    expect(c.outgoing().length).toBe(1);
    vi.advanceTimersByTime(1);
    expect(c.outgoing()).toEqual([]);
  });

  it('a second turn inside the window replaces the ghost and restarts the clock', () => {
    const c = create('slide');
    go(c, 3);
    vi.advanceTimersByTime(150);
    go(c, 4);
    expect(c.outgoing().map((e) => e.entryKey)).toEqual(['p3']);
    vi.advanceTimersByTime(150); // 300ms after the first turn: the second is still playing
    expect(c.outgoing().length).toBe(1);
    vi.advanceTimersByTime(70);
    expect(c.outgoing()).toEqual([]);
  });

  it('Double page: the whole outgoing pair is held', () => {
    const c = create('slide');
    c.view.set('spread');
    c.coverIsStandalone.set(true); // [0],[1,2],[3,4],[5]
    c.currentPage.set(1);
    c.nextPage();
    expect(c.currentPage()).toBe(3);
    expect(c.outgoing().map((e) => e.entryKey)).toEqual(['p1', 'p2']);
  });

  it('is never held for None, where the animation is gated off, or under reduced motion', () => {
    const none = create('none');
    go(none, 3);
    expect(none.outgoing()).toEqual([]);
    TestBed.resetTestingModule();

    const zoomed = create('slide');
    zoomed.zoomed.set(true); // pageAnimActive false: no transition, so no ghost either
    go(zoomed, 3);
    expect(zoomed.outgoing()).toEqual([]);
    TestBed.resetTestingModule();

    const reduced = create('reveal');
    const original = window.matchMedia;
    window.matchMedia = (() => ({ matches: true })) as unknown as typeof window.matchMedia;
    try {
      go(reduced, 3);
    } finally {
      window.matchMedia = original;
    }
    expect(reduced.outgoing()).toEqual([]);
  });

  it('ngOnDestroy clears a pending ghost timer', () => {
    const c = create('reveal');
    go(c, 3);
    const before = vi.getTimerCount();
    expect(before).toBeGreaterThan(0);
    c.ngOnDestroy();
    expect(vi.getTimerCount()).toBeLessThan(before);
  });
});
