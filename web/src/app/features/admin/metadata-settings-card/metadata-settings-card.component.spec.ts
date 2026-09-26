import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { MetadataSettingsDto } from '../../../core/api/api-types';
import { MetadataSettingsCardComponent, parseDailyBudget } from './metadata-settings-card.component';

/**
 * "Series metadata" admin card (1.24.0, lane B2), mocked at the HTTP layer so the real
 * MetadataApiService request shapes are asserted. The web switch is consent-gated;
 * enabling it is ONE settings PUT - no other request (nothing is looked up).
 */
describe('MetadataSettingsCardComponent', () => {
  const SETTINGS = '/api/v1/admin/metadata/settings';
  let http: HttpTestingController;

  const settings = (overrides: Partial<MetadataSettingsDto> = {}): MetadataSettingsDto => ({
    showSeriesInfo: true,
    fetchEnabled: false,
    networkDisabledByConfig: false,
    acceptedConsentVersion: null,
    currentConsentVersion: 1,
    consentAt: null,
    dailyBudget: 5000,
    defaultDailyBudget: 5000,
    budgetUsedToday: 12,
    backoffUntil: null,
    lastErrorAt: null,
    lastErrorCode: null,
    comicInfo: { archivesRead: 90, archivesTotal: 100, archivesWithComicInfo: 7 },
    webRecordCount: 2,
    libraries: [
      { libraryId: 'lib1', name: 'Manga', fetchEnabled: false, showSeriesInfo: true, precedence: null, linkCount: 2 },
    ],
    ...overrides,
  });

  function create(initial = settings()) {
    TestBed.configureTestingModule({
      imports: [MetadataSettingsCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(MetadataSettingsCardComponent);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(initial);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, c: fixture.componentInstance, el, q: (s: string) => el.querySelector(s) as HTMLElement | null };
  }

  afterEach(() => http?.verify());

  it('shows the approved consent text and the status line', () => {
    const { q } = create();
    const text = q('[data-testid="md-consent-text"]')!.textContent!;
    expect(text).toContain('What is sent:');
    expect(text).toContain('What is never sent:');
    expect(text).toContain('Nothing happens automatically.');
    expect(q('[data-testid="md-status"]')!.textContent).toContain('Requests today: 12 / 5000');
    expect(q('[data-testid="md-comicinfo"]')!.textContent).toContain('90 of 100 archives read');
  });

  it('keeps the Fetch switch disabled until consent is ticked', () => {
    const { c } = create();
    expect(c.canToggleFetch()).toBe(false);
    c.consentTicked.set(true);
    expect(c.canToggleFetch()).toBe(true);
  });

  it('enabling sends ONE settings PUT with the consent version and nothing else', () => {
    const { c } = create();
    c.consentTicked.set(true);
    c.setFetch(true);
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ fetchEnabled: true, acceptedConsentVersion: 1 });
    put.flush(settings({ fetchEnabled: true, acceptedConsentVersion: 1, consentAt: '2026-09-25T00:00:00Z' }));
    http.expectNone(() => true);
    expect(c.consentCurrent()).toBe(true);
  });

  it('turning it off needs no consent', () => {
    const { c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: 1 }));
    expect(c.canToggleFetch()).toBe(true);
    c.setFetch(false);
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ fetchEnabled: false });
  });

  it('re-prompts on a stale consent version and honours the config kill', () => {
    const stale = create(settings({ acceptedConsentVersion: 0 }));
    expect(stale.c.consentCurrent()).toBe(false);
    expect(stale.q('[data-testid="md-consent"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const killed = create(settings({ networkDisabledByConfig: true }));
    killed.c.consentTicked.set(true);
    expect(killed.c.canToggleFetch()).toBe(false);
    expect(killed.q('[data-testid="md-config-kill"]')).not.toBeNull();
  });

  it('saves an integer daily budget only', () => {
    const { c } = create();
    c.budgetText.set('1.5');
    expect(c.parsedBudget()).toBeNull();
    c.budgetText.set('250');
    c.saveBudget();
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ dailyBudget: 250 });
  });

  it('toggles a library and deletes its fetched data after confirmation', () => {
    const { c, fixture, el } = create();
    const lib = c.settings()!.libraries[0];
    c.setLibrary(lib, { fetchEnabled: true });
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/admin/metadata/libraries/lib1' });
    expect(put.request.body).toEqual({ fetchEnabled: true });
    put.flush(settings());

    c.confirming.set('lib1');
    fixture.detectChanges();
    expect(el.textContent).toContain('Delete 2 links?');
    c.purge('lib1');
    const purge = http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/purge' });
    expect(purge.request.body).toEqual({ libraryId: 'lib1' });
    purge.flush({ linksRemoved: 2, recordsRemoved: 1 });
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(settings({ webRecordCount: 1 }));
    expect(c.message()).toContain('Deleted 2 link(s)');
  });

  it('sets a library precedence (default clears it)', () => {
    const { c } = create();
    c.setPrecedence(c.settings()!.libraries[0], 'default');
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/admin/metadata/libraries/lib1/precedence' });
    expect(put.request.body).toEqual({ precedence: null });
    put.flush(null);
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(settings());
  });

  it('shows a refused change and re-syncs', () => {
    const { c } = create();
    c.setFetch(true);
    http.expectOne({ method: 'PUT', url: SETTINGS }).flush(
      { error: 'consent_required', message: 'Consent required', detail: null, correlationId: null },
      { status: 400, statusText: 'Bad Request' },
    );
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(settings());
    expect(c.error()).toBe('Consent required');
  });
});

describe('parseDailyBudget', () => {
  it('accepts whole numbers 1..1,000,000 only', () => {
    expect(parseDailyBudget('5000')).toBe(5000);
    expect(parseDailyBudget(' 1 ')).toBe(1);
    expect(parseDailyBudget('1000000')).toBe(1_000_000);
    for (const bad of ['0', '-1', '1e3', '2.5', '1000001', '', 'abc', null]) expect(parseDailyBudget(bad)).toBeNull();
  });
});
