import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { MetadataReviewSummaryDto, MetadataSettingsDto } from '../../../core/api/api-types';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { ADMIN_METADATA_TABS, AdminMetadataComponent } from './admin-metadata.component';
import { summary } from './metadata-admin.testing';

/**
 * /admin/metadata (stage 2, decision 4c): four tabs, the tab from the query string, tab
 * counts from the shared review summary, and the URL kept in sync (replaceUrl).
 * Tab contents are real components against a mocked HTTP backend.
 */
describe('AdminMetadataComponent', () => {
  function create(query: Record<string, string> = {}) {
    const settingsState = signal<MetadataSettingsDto | null>(null);
    const state = {
      summary: signal<MetadataReviewSummaryDto | null>(summary({ needsReview: 7, openFlags: 2 })), refresh: vi.fn(),
      settings: settingsState, refreshSettings: vi.fn(), setSettings: vi.fn((v: MetadataSettingsDto) => settingsState.set(v)),
    };
    TestBed.configureTestingModule({
      imports: [AdminMetadataComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: ApiService, useValue: { getAllLibraries: vi.fn(() => of([{ id: 'lib1', name: 'Library One' }])) } },
        { provide: MetadataReviewStateService, useValue: state },
      ],
    });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(AdminMetadataComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, c: fixture.componentInstance, state, navigate, http };
  }

  it('opens on Settings by default, shows the six tabs, the counts and the summary tile', () => {
    const { el, c, state, http } = create();
    expect(c.tab()).toBe('settings');
    expect(state.refresh).toHaveBeenCalled();
    const labels = Array.from(el.querySelectorAll('[role="tab"]')).map((t) => t.textContent!.replace(/\s+/g, ' ').trim());
    expect(labels).toEqual(['Settings', 'Review 7', 'Flags 2', 'Runs', 'Missing', 'Completion']);
    // The summary tile (owner decision 2, 1.27.0) sits above the tabs and replaces the old one-line summary.
    expect(el.querySelector('[data-testid="metadata-summary-tile"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="tile-review"]')!.textContent).toContain('7');
    // The Settings tab reads the settings; the summary card shares them through the state service (1.27.0)
    // instead of a second, independent read that never saw later changes.
    expect(http.match('/api/v1/admin/metadata/settings')).toHaveLength(1);
    expect(state.refreshSettings).toHaveBeenCalled();
  });

  it('opens the tab, review list and library named in the query string', () => {
    const { c, http } = create({ tab: 'review', list: 'AutoLinked', library: 'lib1' });
    expect(c.tab()).toBe('review');
    expect(c.list()).toBe('AutoLinked');
    expect(c.library()).toBe('lib1');
    const req = http.expectOne((r) => r.url === '/api/v1/admin/metadata/review');
    expect(req.request.params.get('tab')).toBe('AutoLinked');
    expect(req.request.params.get('library')).toBe('lib1');
  });

  it('opens the Completion tab from the query string - also by its 1.30.0 key official - and keeps its library (1.32.0)', () => {
    const { c, http, navigate } = create({ tab: 'official', library: 'lib1' });
    expect(c.tab()).toBe('completion');
    const req = http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases');
    expect(req.request.params.get('library')).toBe('lib1');
    expect(req.request.params.get('answer')).toBe('HaveItAll');
    c.select(ADMIN_METADATA_TABS.indexOf('missing'));
    c.select(ADMIN_METADATA_TABS.indexOf('completion'));
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { tab: 'completion', list: null, library: 'lib1' } }));
  });

  it('ignores unknown query values', () => {
    const { c } = create({ tab: 'nope', list: 'Nope' });
    expect(c.tab()).toBe('settings');
    expect(c.list()).toBe('NeedsReview');
  });

  it('keeps the URL in sync when the tab or the review list changes', () => {
    const { c, navigate } = create();
    c.select(3);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({
      replaceUrl: true, queryParams: { tab: 'runs', list: null, library: null } }));
    c.select(1);
    c.onReviewState({ tab: 'Unmatched', library: 'lib1' });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({
      queryParams: { tab: 'review', list: 'Unmatched', library: 'lib1' } }));
  });

  it('the summary tile\'s stats switch tabs in place instead of navigating away', () => {
    const { c, el, navigate } = create();
    (el.querySelector('[data-testid="tile-auto"]') as HTMLButtonElement).click();
    expect(c.tab()).toBe('runs');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { tab: 'runs', list: null, library: null } }));
  });
});
