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
});
