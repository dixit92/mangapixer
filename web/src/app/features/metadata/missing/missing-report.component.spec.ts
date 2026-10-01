import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';

import { MetadataSettingsDto } from '../../../core/api/api-types';
import { settings } from '../admin-metadata/metadata-admin.testing';
import { MetadataReviewStateService } from '../metadata-review-state.service';

import { MissingReportComponent } from './missing-report.component';
import { gap, missingPage, missingRow, ownerProgress } from './missing.testing';
import { CONSENT_TEXT_VERSION } from '../admin-metadata/settings/metadata-settings.component';

/**
 * Missing tab (1.28.0): loads "behind or with gaps" by default, switches filter and library, renders the
 * have / behind / missing lines with the series link, and pages with Load more. Mocked HTTP backend.
 */
describe('MissingReportComponent', () => {
  /** Fetch on with the current consent and both sites allowed, unless overridden. */
  function create(s: MetadataSettingsDto = settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION })) {
    TestBed.configureTestingModule({
      imports: [MissingReportComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: MetadataReviewStateService, useValue: { settings: signal(s), refreshSettings: vi.fn() } },
      ],
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

  it('asks AniList for one row and replaces it with the recomputed row', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([missingRow()]));
    fixture.detectChanges();
    (el.querySelector('[data-testid="missing-convert-one"]') as HTMLButtonElement).click();
    const post = http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/missing/series-1/conversion' });
    post.flush({
      outcome: 'Found',
      row: missingRow({
        conversion: { provider: 'anilist', providerName: 'AniList', externalId: '30025', title: 'Public Title', siteUrl: 'https://anilist.co/manga/30025',
          volumes: 27, chapters: 116, chaptersPerVolume: 4.3 },
      }),
    });
    fixture.detectChanges();
    const line = el.querySelector('[data-testid="missing-conversion"] a') as HTMLAnchorElement;
    expect(line.textContent).toContain('AniList: 116 chapters in 27 volumes, 4.3 per volume');
    expect(line.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(el.querySelector('[data-testid="missing-convert-one"]')).toBeNull();
    http.verify();
  });

  it('runs the batch for the selected library and reloads', () => {
    const { fixture, c, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    c.library.set('lib1');
    fixture.detectChanges();
    (el.querySelector('[data-testid="missing-convert-batch"]') as HTMLButtonElement).click();
    const post = http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/missing/conversions' });
    expect(post.request.body).toEqual({ library: 'lib1' });
    post.flush({ looked: 3, found: 2, noCounts: 0, noMatch: 1, remaining: 1, stoppedCode: 'budget_exhausted', stoppedMessage: 'Budget used up.' });
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="missing-convert-result"]')!.textContent)
      .toBe('Looked up 3: 2 found, 0 without final totals, 1 no match. 1 still without chapters per volume. Stopped: Budget used up.');
    http.verify();
  });

  it('offers no batch button while Automatic matching with volume covers looks the totals up in the background', () => {
    const auto = settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION });
    Object.assign(auto, { autoMatchEnabled: true, acceptedAutoConsentVersion: 3, currentAutoConsentVersion: 3, volumeCoversEnabled: true });
    const a = create(auto);
    a.http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    a.fixture.detectChanges();
    expect(a.el.querySelector('[data-testid="missing-convert-batch"]')).toBeNull();
    expect(a.el.querySelector('[data-testid="missing-convert-auto"]')!.textContent).toContain('looked up automatically');
    TestBed.resetTestingModule();

    // Volume covers off: the background does not ask AniList, so the button is back.
    const manual = create({ ...auto, volumeCoversEnabled: false });
    manual.http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    manual.fixture.detectChanges();
    expect(manual.el.querySelector('[data-testid="missing-convert-batch"]')).not.toBeNull();
    expect(manual.el.querySelector('[data-testid="missing-convert-auto"]')).toBeNull();
  });

  it('says why AniList cannot be asked (removed from the allowed sites, or Fetch off)', () => {
    const removed = settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION });
    removed.providers = removed.providers!.map((p) => ({ ...p, allowed: p.id !== 'anilist' }));
    const a = create(removed);
    a.http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([missingRow()]));
    a.fixture.detectChanges();
    expect(a.el.querySelector('[data-testid="missing-convert-off"]')!.textContent).toContain('not an allowed site');
    expect(a.el.querySelector('[data-testid="missing-convert-one"]')).toBeNull();
    TestBed.resetTestingModule();
    const off = create(settings());
    off.http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([]));
    off.fixture.detectChanges();
    expect(off.el.querySelector('[data-testid="missing-convert-off"]')!.textContent).toContain('Fetch from the web');
  });

  it('1.30.0: shows what the folder holds and the upgrades, and the summary opens the Official releases tab', () => {
    const { fixture, http, el, c } = create();
    const opened = vi.fn();
    c.openOfficial.subscribe(opened);
    const pageDto = missingPage([missingRow({ verdict: 'UpToDate', progress: ownerProgress() })]);
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush({ ...pageDto, summary: { ...pageDto.summary, upgrades: 1 } });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="missing-reach"]')!.textContent).toBe('You have volumes 1-14 + chapters 43-57');
    expect(el.querySelector('[data-testid="missing-upgrade"]')!.textContent).toBe('Volume 15 available in English (an upgrade, not missing)');
    (el.querySelector('[data-testid="missing-upgrades-link"]') as HTMLButtonElement).click();
    expect(opened).toHaveBeenCalled();
  });

  it('1.31.0: says which numbers sit in more than one file of a folder, and nothing for a clean series', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/missing').flush(missingPage([
      missingRow({ nodeId: 'series-1', duplicateCount: 2, duplicates: [
        { kind: 'Chapter', number: '1', files: 2 }, { kind: 'Chapter', number: '2', files: 3 }] }),
      missingRow({ nodeId: 'series-2', displayName: 'Clean Series', duplicates: [], duplicateCount: 0 }),
      missingRow({ nodeId: 'series-3', displayName: 'Capped Series', duplicateCount: 60, duplicates: [{ kind: 'Chapter', number: '1', files: 2 }] }),
    ]));
    fixture.detectChanges();

    const lines = Array.from(el.querySelectorAll('[data-testid="missing-duplicates"]')).map((p) => p.textContent!.replace(/\s+/g, ' ').trim());
    expect(lines).toEqual([
      'content_copy 2 duplicate chapters (Chapter 1: 2 files, Chapter 2: 3 files)',
      'content_copy 60 duplicate numbers (Chapter 1: 2 files and 59 more)',
    ]);
    expect(el.querySelectorAll('[data-testid="missing-row"]')).toHaveLength(3);
  });
});
