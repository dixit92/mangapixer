import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
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

  it('toggleChrome / revealChrome flip immersive chrome visibility', () => {
    const c = create();
    expect(c.chromeVisible()).toBe(true); // shown on entry
    c.toggleChrome();
    expect(c.chromeVisible()).toBe(false); // tap centre to hide
    c.toggleChrome();
    expect(c.chromeVisible()).toBe(true); // tap again to show
    c.chromeVisible.set(false);
    c.revealChrome();
    expect(c.chromeVisible()).toBe(true); // mouse-move reveal
  });

  it('progressPct reflects the current page within the chapter', () => {
    const c = create();
    c.pages.set(makePages(4));
    c.currentPage.set(0);
    expect(c.progressPct()).toBe(25);
    c.currentPage.set(3);
    expect(c.progressPct()).toBe(100);
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

  it('keeps the bottom prev/next controls visible in fullscreen (2026-09-08 revision)', () => {
    // Revised requirement 3: reader chrome no longer hides in fullscreen — the old
    // behavior was inconsistent (Fullscreen API button hid it, F11 did not).
    const { fixture, c } = renderReady();
    const el: HTMLElement = fixture.nativeElement;

    c.isFullscreen.set(false);
    fixture.detectChanges();
    expect(el.querySelector('.reader-controls')).toBeTruthy();

    c.isFullscreen.set(true);
    fixture.detectChanges();
    expect(el.querySelector('.reader-controls')).toBeTruthy();
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
