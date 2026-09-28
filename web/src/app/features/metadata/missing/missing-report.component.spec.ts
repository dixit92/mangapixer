import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { MissingReportComponent } from './missing-report.component';
import { gap, missingPage, missingRow } from './missing.testing';

/**
 * Missing tab (1.28.0): loads "behind or with gaps" by default, switches filter and library, renders the
 * have / behind / missing lines with the series link, and pages with Load more. Mocked HTTP backend.
 */
describe('MissingReportComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [MissingReportComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(MissingReportComponent);
    fixture.componentRef.setInput('libraries', [{ id: 'lib1', name: 'Library One' }]);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  it('loads the series that are behind or have gaps and renders their lines', () => {
    const { fixture, http, el } = create();
    const req = http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing');
    expect(req.request.params.get('onlyMissing')).toBe('true');
    expect(req.request.params.has('library')).toBe(false);
    req.flush(missingPage([
      missingRow(),
      missingRow({
        nodeId: 'series-2', displayName: 'Other Series', verdict: 'Holes', englishTotalUnknown: true,
        volumes: gap({ behindBy: 0, have: 6, available: 6, missing: [3, 4], missingCount: 2, source: 'Origin', confidence: 'Medium' }),
      }),
    ], '2'));
    fixture.detectChanges();

    const rows = el.querySelectorAll('[data-testid="missing-row"]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('You have volumes 1-7 of 10 (English)');
    expect(rows[0].textContent).toContain('3 behind');
    expect(rows[0].querySelector('[data-testid="missing-series-link"]')!.getAttribute('href')).toBe('/series/series-1');
    expect(rows[1].textContent).toContain('missing 3-4');
    expect(rows[1].querySelector('[data-testid="missing-english-unknown"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="missing-summary"]')!.textContent).toContain('1 behind');

    (el.querySelector('[data-testid="missing-more"]') as HTMLButtonElement).click();
    const more = http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing');
    expect(more.request.params.get('cursor')).toBe('2');
    more.flush(missingPage([missingRow({ nodeId: 'series-3', displayName: 'Third' })]));
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="missing-row"]')).toHaveLength(3);
    http.verify();
  });

  it('switches to all linked series and filters by library', () => {
    const { fixture, c, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="missing-empty"]')!.textContent).toContain('Nothing behind');

    c.setFilter('all');
    const all = http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing');
    expect(all.request.params.has('onlyMissing')).toBe(false);
    all.flush(missingPage([]));

    c.setLibrary('lib1');
    const lib = http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing');
    expect(lib.request.params.get('library')).toBe('lib1');
    lib.flush(missingPage([]));
    http.verify();
  });

  it('shows an error when the report fails', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush({}, { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(el.querySelector('[role="alert"]')!.textContent).toContain('could not be loaded');
  });
});
