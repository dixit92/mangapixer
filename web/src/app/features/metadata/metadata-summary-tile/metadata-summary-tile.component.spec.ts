import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { settings, summary } from '../admin-metadata/metadata-admin.testing';
import { AdminMetadataTab } from '../admin-metadata/metadata-admin-labels';
import { MetadataSummaryTileComponent } from './metadata-summary-tile.component';

/**
 * The Metadata Manager summary tile (stage 2, moved to the top of `/admin/metadata`
 * itself in 1.27.0): to review, open flags, today's requests / budget, automatic
 * matching on/off. Link mode (the default) links each into `/admin/metadata`; `inPage`
 * (how the Metadata Manager page itself uses it) switches that page's tabs instead.
 * Mocked at the HTTP layer (local reads only).
 */
describe('MetadataSummaryTileComponent', () => {
  let http: HttpTestingController;

  function create(inPage = false) {
    TestBed.configureTestingModule({
      imports: [MetadataSummaryTileComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(MetadataSummaryTileComponent);
    fixture.componentRef.setInput('inPage', inPage);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const text = (id: string) => Array.from(el.querySelector(`[data-testid="${id}"]`)!.children)
      .map((c) => c.textContent!.replace(/\s+/g, ' ').trim()).join(' ');
    const tabs: AdminMetadataTab[] = [];
    fixture.componentInstance.tabSelect.subscribe((t) => tabs.push(t));
    return { fixture, el, text, tabs };
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
    expect(el.querySelector('mat-card-title')!.textContent).toBe('Metadata Manager');
  });

  it('shows dashes when the server has no stage-2 summary yet (501)', () => {
    const { fixture, text } = create();
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(null, { status: 501, statusText: 'Not Implemented' });
    http.expectOne('/api/v1/admin/metadata/settings').flush(settings());
    fixture.detectChanges();
    expect(text('tile-review')).toBe('– to review');
    expect(text('tile-auto')).toBe('Off automatic matching');
  });

  it('inPage: has no header/open link, and its stats switch tabs instead of navigating', () => {
    const { fixture, el, tabs } = create(true);
    http.expectOne('/api/v1/admin/metadata/review/summary').flush(summary({ needsReview: 3, openFlags: 1 }));
    http.expectOne('/api/v1/admin/metadata/settings').flush(settings());
    fixture.detectChanges();
    expect(el.querySelector('mat-card-header')).toBeNull();
    expect(el.querySelector('[data-testid="tile-open"]')).toBeNull();
    expect(el.querySelector('[data-testid="tile-review"]')!.tagName).toBe('BUTTON');
    (el.querySelector('[data-testid="tile-review"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="tile-flags"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="tile-budget"]') as HTMLButtonElement).click();
    (el.querySelector('[data-testid="tile-auto"]') as HTMLButtonElement).click();
    expect(tabs).toEqual(['review', 'flags', 'settings', 'runs']);
  });
});
