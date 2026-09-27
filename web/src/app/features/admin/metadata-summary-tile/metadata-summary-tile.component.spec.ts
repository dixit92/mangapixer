import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { settings, summary } from '../../metadata/admin-metadata/metadata-admin.testing';
import { MetadataSummaryTileComponent } from './metadata-summary-tile.component';

/**
 * The admin page's series-metadata summary tile (stage 2): to review, open flags, today's
 * requests / budget, automatic matching on/off, each linking into /admin/metadata.
 * Mocked at the HTTP layer (local reads only).
 */
describe('MetadataSummaryTileComponent', () => {
  let http: HttpTestingController;

  function create() {
    TestBed.configureTestingModule({
      imports: [MetadataSummaryTileComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(MetadataSummaryTileComponent);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const text = (id: string) => Array.from(el.querySelector(`[data-testid="${id}"]`)!.children)
      .map((c) => c.textContent!.replace(/\s+/g, ' ').trim()).join(' ');
    return { fixture, el, text };
  }

  afterEach(() => http.verify());

  it('shows the four numbers and links each to its tab', () => {
    const { fixture, el, text } = create();
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(summary({ needsReview: 97, openFlags: 2 }));
    http.expectOne('/api/v1/admin/metadata/settings').flush(settings({ budgetUsedToday: 1234, autoMatchEnabled: true }));
    fixture.detectChanges();
    expect(text('tile-review')).toBe('97 to review');
    expect(text('tile-flags')).toBe('2 open flags');
    expect(text('tile-budget')).toBe('1,234 / 5,000 requests today');
    expect(text('tile-auto')).toBe('On automatic matching');
    expect(el.querySelector('[data-testid="tile-review"]')!.getAttribute('href')).toBe('/admin/metadata?tab=review');
    expect(el.querySelector('[data-testid="tile-flags"]')!.getAttribute('href')).toBe('/admin/metadata?tab=flags');
    expect(el.querySelector('[data-testid="tile-open"]')!.getAttribute('href')).toBe('/admin/metadata');
  });

  it('shows dashes when the server has no stage-2 summary yet (501)', () => {
    const { fixture, text } = create();
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(null, { status: 501, statusText: 'Not Implemented' });
    http.expectOne('/api/v1/admin/metadata/settings').flush(settings());
    fixture.detectChanges();
    expect(text('tile-review')).toBe('– to review');
    expect(text('tile-auto')).toBe('Off automatic matching');
  });
});
