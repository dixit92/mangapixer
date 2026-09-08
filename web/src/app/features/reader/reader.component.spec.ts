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
