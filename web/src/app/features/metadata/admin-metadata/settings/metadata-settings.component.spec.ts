import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { MetadataSettingsDto } from '../../../../core/api/api-types';
import { estimate, settings } from '../metadata-admin.testing';
import { AUTO_CONSENT_TEXT_VERSION, CONSENT_TEXT_VERSION, MetadataSettingsComponent, parseDailyBudget, validateThresholds } from './metadata-settings.component';

/**
 * Settings tab of /admin/metadata, mocked at the HTTP layer so the real
 * MetadataApiService request shapes are asserted. Stage 1 (moved from the admin card):
 * the web switch is consent-gated and enabling it is ONE settings PUT. Stage 2: the
 * global Automatic matching switch is gated by the separate automatic-lookups consent
 * and Fetch; turning it on is ONE settings PUT and nothing else.
 */
describe('MetadataSettingsComponent', () => {
  const SETTINGS = '/api/v1/admin/metadata/settings';
  let http: HttpTestingController;

  function create(initial: MetadataSettingsDto = settings()) {
    TestBed.configureTestingModule({
      imports: [MetadataSettingsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(MetadataSettingsComponent);
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    http.expectOne({ method: 'GET', url: SETTINGS }).flush(initial);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, c: fixture.componentInstance, el, q: (s: string) => el.querySelector(s) as HTMLElement | null };
  }

  afterEach(() => http?.verify());

  // --- Stage 1 behaviour, unchanged by the move ---

  it('shows the approved consent text and the status line', () => {
    const { q } = create();
    const text = q('[data-testid="md-consent-text"]')!.textContent!;
    expect(text).toContain('What is sent:');
    expect(text).toContain('What is never sent:');
    expect(text).toContain('Nothing happens automatically unless you also turn on Automatic matching.');
    expect(q('[data-testid="md-status"]')!.textContent).toContain('Requests today: 12 / 5000');
    expect(q('[data-testid="md-comicinfo"]')!.textContent).toContain('90 of 100 archives read');
  });

  it('keeps the Fetch switch disabled until consent is ticked', () => {
    const { c } = create();
    expect(c.canToggleFetch()).toBe(false);
    c.consentTicked.set(true);
    expect(c.canToggleFetch()).toBe(true);
  });

  it('enabling Fetch sends ONE settings PUT with the consent version and nothing else', () => {
    const { c } = create();
    c.consentTicked.set(true);
    c.setFetch(true);
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION });
    put.flush(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION, consentAt: '2026-09-25T00:00:00Z' }));
    http.expectNone(() => true);
    expect(c.consentCurrent()).toBe(true);
  });

  it('turning Fetch off needs no consent', () => {
    const { c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }));
    expect(c.canToggleFetch()).toBe(true);
    c.setFetch(false);
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ fetchEnabled: false });
  });

  it('after an update that bumped the consent, Fetch reads as off until the admin accepts the new text (1.28.0)', () => {
    const { c, q } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION - 1, consentRenewalNeeded: true }));
    expect(c.consentCurrent()).toBe(false);
    expect(q('[data-testid="md-consent-text"]')!.textContent).toContain('AniList');
    expect(q('[data-testid="md-fetch"] [role="switch"]')!.getAttribute('aria-checked')).toBe('false');
    expect(c.canToggleFetch()).toBe(false);
    c.consentTicked.set(true);
    c.setFetch(true);
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION });
  });

  it('shows the allowed sites as chips in the Web lookups card (1.28.0)', () => {
    const { el } = create();
    expect(Array.from(el.querySelectorAll('[data-testid="md-provider-chip"]')).map((e) => e.getAttribute('data-provider')))
      .toEqual(['mangaupdates', 'mangadex', 'anilist']);
  });

  // --- 1.29.0: volume covers (MangaDex) ---

  it('volume covers: the switch and the preferred language are ONE settings PUT each; the MangaDex credit shows', () => {
    const { c, q } = create(settings({ volumeCoversEnabled: true, preferredCoverLanguage: 'en' }));
    expect(q('[data-testid="md-mangadex-credit"]')!.textContent).toContain('MangaDex (mangadex.org)');
    expect(q('[data-testid="md-volume-covers"]')!.textContent).toContain('Preferred language (covers and releases)');
    expect(q('[data-testid="md-language-note"]')!.textContent).toContain('count as released');
    c.setVolumeCovers(false);
    const off = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(off.request.body).toEqual({ volumeCoversEnabled: false });
    off.flush(settings({ volumeCoversEnabled: false }));
    c.setCoverLanguage('ja');
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ preferredCoverLanguage: 'ja' });
  });

  it('volume covers: an unusual stored language stays selectable, and the config switch is shown', () => {
    const { c, q } = create(settings({ preferredCoverLanguage: 'sv', volumeCoversDisabledByConfig: true }));
    expect(c.coverLanguages().some((o) => o.code === 'sv')).toBe(true);
    expect(q('[data-testid="md-volume-covers-config"]')).not.toBeNull();
    expect(q('[data-testid="md-volume-covers-switch"] [role="switch"]')!.getAttribute('aria-checked')).toBe('false');
  });

  it('volume covers: progress loads on demand, and deleting stored covers asks first', () => {
    const { c, fixture, q } = create();
    http.expectNone('/api/v1/admin/metadata/volume-covers/status');
    q('[data-testid="md-volume-covers-progress"]')!.click();
    http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/volume-covers/status' })
      .flush({ seriesPending: 3, coversListed: 40, coversStored: 12, waiting: 'provider_not_allowed' });
    fixture.detectChanges();
    const status = q('[data-testid="md-volume-covers-status"]')!.textContent!;
    expect(status).toContain('12 covers stored');
    expect(status).toContain('3 series to check');
    expect(status).toContain('MangaDex is not on the allowed sites.');
    q('[data-testid="md-volume-covers-delete"]')!.click();
    fixture.detectChanges();
    http.expectNone(() => true);
    q('[data-testid="md-volume-covers-delete-confirm"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/admin/metadata/volume-covers' })
      .flush({ seriesPending: 3, coversListed: 0, coversStored: 0, waiting: null });
    expect(c.coverStatus()!.coversStored).toBe(0);
    expect(c.message()).toBe('Stored volume covers deleted');
  });

  it('volume covers: an idle pass reads "nothing waiting" and the listed covers read as not needed (1.30.0)', () => {
    const { fixture, q } = create();
    q('[data-testid="md-volume-covers-progress"]')!.click();
    http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/volume-covers/status' })
      .flush({ seriesPending: 0, coversListed: 5947, coversStored: 3054, waiting: null });
    fixture.detectChanges();
    const status = q('[data-testid="md-volume-covers-status"]')!.textContent!.replace(/\s+/g, ' ');
    expect(status).toContain('3054 covers stored');
    expect(status).toContain('nothing waiting');
    expect(status).not.toContain('series to check');
    expect(status).toContain('5947 more listed on MangaDex, not needed');
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

  // --- Stage 2 ---

  it('shows the budget as usage only, without an explanation paragraph', () => {
    const { q, c } = create(settings({ budgetUsedToday: 2500 }));
    expect(q('[data-testid="md-status"]')!.textContent).toContain('Requests today: 2500 / 5000');
    expect(q('[data-testid="md-budget-explain"]')).toBeNull();
    expect(c.budgetPercent()).toBe(50);
  });

  it('folds both consent texts behind "What is sent?" once accepted', () => {
    const { q, c, fixture } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION, autoMatchEnabled: true,
      acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION }));
    expect(q('[data-testid="md-consent-text"]')).toBeNull();
    expect(q('[data-testid="md-auto-consent-text"]')).toBeNull();
    q('[data-testid="md-consent-toggle"]')!.click();
    q('[data-testid="md-auto-consent-toggle"]')!.click();
    fixture.detectChanges();
    expect(q('[data-testid="md-consent-text"]')!.textContent).toContain('What is never sent:');
    expect(q('[data-testid="md-auto-consent-text"]')!.textContent).toContain('What is sent automatically:');
    expect(c.showConsent()).toBe(true);
  });

  it('shows no automatic-lookups consent while Fetch is off (it cannot be given yet)', () => {
    const { q } = create(settings());
    expect(q('[data-testid="md-auto-consent-text"]')).toBeNull();
    expect(q('[data-testid="md-auto-consent"]')).toBeNull();
    expect(q('[data-testid="md-auto-needs-fetch"]')).not.toBeNull();
  });

  it('shows the automatic-lookups consent (v2) text: what is sent automatically and never', () => {
    const { q } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }));
    const text = q('[data-testid="md-auto-consent-text"]')!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('in every library whose Fetch switch is on');
    expect(text).toContain('What is sent automatically:');
    expect(text).toContain('nobody reviews before it is sent');
    // 1.31.0: the same names are sent again for folders that are still waiting (no consent version bump).
    expect(text).toContain('The same names may be sent again for folders that are still waiting: a folder left unmatched is tried again '
      + 'after 30, 90 and 180 days, and when a MangaPixer update changes how matches are scored, folders waiting under Needs review are '
      + 'checked once more under the new rules.');
    // 1.30.0: a declared type is no longer sent as a search filter, so the consent text no longer names it.
    expect(text).not.toContain('declared manga');
    expect(text).toContain('Cover comparison:');
    expect(text).toContain('download the cover images of those two series from MangaUpdates\' image server (cdn.mangaupdates.com)');
    expect(text).toContain('These downloads carry nothing from your library.');
    expect(text).toContain('What is never sent:');
    expect(text).toContain('Don\'t match');
    expect(q('[data-testid="md-auto-consent"]')).not.toBeNull();
  });

  it('keeps Automatic matching disabled until Fetch is on AND the automatic consent is ticked', () => {
    const off = create(settings());
    off.c.autoConsentTicked.set(true);
    expect(off.c.canToggleAuto()).toBe(false); // Fetch off
    expect(off.q('[data-testid="md-auto-needs-fetch"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const { c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }));
    expect(c.canToggleAuto()).toBe(false);
    c.autoConsentTicked.set(true);
    expect(c.canToggleAuto()).toBe(true);
  });

  it('turning Automatic matching on is ONE settings PUT with the automatic consent version - no lookup', () => {
    const { c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }));
    c.autoConsentTicked.set(true);
    c.setAuto(true);
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ autoMatchEnabled: true, acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION });
    put.flush(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION, autoMatchEnabled: true, acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION,
      autoConsentAt: '2026-09-26T00:00:00Z' }));
    http.expectNone(() => true);
    expect(c.autoConsentCurrent()).toBe(true);
    expect(c.message()).toBe('Automatic matching is on');
  });

  it('turning Automatic matching off needs no consent; a stale automatic consent re-prompts', () => {
    const { c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION, autoMatchEnabled: true,
      acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION }));
    expect(c.canToggleAuto()).toBe(true);
    c.setAuto(false);
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ autoMatchEnabled: false });
    TestBed.resetTestingModule();
    // An earlier consent is not carried over (owner, 1.28.0; again for v3 in 1.29.0).
    const stale = create(settings({ fetchEnabled: true, acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION - 1, currentAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION }));
    expect(stale.c.autoConsentCurrent()).toBe(false);
    expect(stale.q('[data-testid="md-auto-consent"]')).not.toBeNull();
  });

  it('the Automatic matching card says "Waiting for consent" while a renewed consent is pending (1.28.0)', () => {
    const waiting = create(settings({ fetchEnabled: true, autoMatchEnabled: true, consentRenewalNeeded: true }));
    expect(waiting.q('[data-testid="md-auto-state"]')!.textContent!.trim()).toBe('Waiting for consent');
    TestBed.resetTestingModule();
    const on = create(settings({ fetchEnabled: true, autoMatchEnabled: true }));
    expect(on.q('[data-testid="md-auto-state"]')!.textContent!.trim()).toBe('On');
  });

  it('"Compare covers" is ONE settings PUT, only while Automatic matching is on, and honours the config switch', () => {
    const { q, c } = create(settings({ fetchEnabled: true, acceptedConsentVersion: 1, autoMatchEnabled: true,
      acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION, compareCoversEnabled: true }));
    const box = q('[data-testid="md-compare-covers"] input[type="checkbox"]') as HTMLInputElement;
    expect(box.checked).toBe(true);
    expect(box.disabled).toBe(false);
    c.setCompareCovers(false);
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ compareCoversEnabled: false });
    put.flush(settings({ fetchEnabled: true, acceptedConsentVersion: 1, autoMatchEnabled: true, acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION,
      compareCoversEnabled: false }));
    http.expectNone(() => true);
    expect(c.message()).toBe('Covers will not be compared');
    TestBed.resetTestingModule();

    const off = create(settings({ fetchEnabled: true, acceptedConsentVersion: 1 }));
    expect((off.q('[data-testid="md-compare-covers"] input[type="checkbox"]') as HTMLInputElement).disabled).toBe(true);
    TestBed.resetTestingModule();

    const held = create(settings({ fetchEnabled: true, acceptedConsentVersion: 1, autoMatchEnabled: true,
      acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION, compareCoversDisabledByConfig: true }));
    expect((held.q('[data-testid="md-compare-covers"] input[type="checkbox"]') as HTMLInputElement).disabled).toBe(true);
    expect(held.q('[data-testid="md-compare-covers-config"]')).not.toBeNull();
  });

  it('shows which libraries automatic matching covers (every library with Fetch on)', () => {
    const { q } = create(settings({
      fetchEnabled: true, autoMatchEnabled: true,
      libraries: [
        { libraryId: 'a', name: 'Alpha', fetchEnabled: true, showSeriesInfo: true, linkCount: 0, autoMatchActive: true },
        { libraryId: 'b', name: 'Beta', fetchEnabled: false, showSeriesInfo: true, linkCount: 0 },
      ],
    }));
    const text = q('[data-testid="md-auto-coverage"]')!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('Applies to: Alpha');
    expect(text).toContain('not Beta (Fetch off)');
  });

  it('validates thresholds against the bounds and saves or resets them, as whole percents', () => {
    const { c, q } = create();
    // The stored 0.92/0.10/0.60 (fractions) round-trip exactly to the percents the inputs show.
    expect(c.thresholdText()).toEqual({ autoTitle: '92', margin: '10', reviewFloor: '60' });
    c.setThreshold('reviewFloor', '95');
    expect(c.thresholdError()).toContain('Review floor must be from 40% to 90%');
    c.setThreshold('reviewFloor', '70');
    c.setThreshold('autoTitle', '95');
    expect(c.thresholdError()).toBeNull();
    expect(c.thresholdsDirty()).toBe(true);
    expect(q('[data-testid="md-th-auto"]')!.closest('mat-form-field')!.textContent).toContain('%');
    c.saveThresholds();
    const put = http.expectOne({ method: 'PUT', url: SETTINGS });
    expect(put.request.body).toEqual({ thresholds: { autoTitle: 0.95, margin: 0.1, reviewFloor: 0.7 } });
    put.flush(settings({ thresholds: { autoTitle: 0.95, margin: 0.1, reviewFloor: 0.7 }, thresholdsAreDefault: false }));
    expect(c.thresholdsDirty()).toBe(false);
    expect(c.thresholdText()).toEqual({ autoTitle: '95', margin: '10', reviewFloor: '70' }); // round-tripped back to percents
    c.resetThresholds();
    expect(http.expectOne({ method: 'PUT', url: SETTINGS }).request.body).toEqual({ resetThresholds: true });
  });

  it('opens "Match now" for a library with Fetch on and reports a queued run', () => {
    const { c, fixture, q } = create(settings({
      fetchEnabled: true,
      libraries: [{ libraryId: 'lib1', name: 'Library One', fetchEnabled: true, showSeriesInfo: true, linkCount: 0 }],
    }));
    const runs: string[] = [];
    c.runStarted.subscribe((r) => runs.push(r.runId));
    (q('[data-testid="md-match-now"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne('/api/v1/admin/metadata/libraries/lib1/match/estimate').flush(estimate());
    fixture.detectChanges();
    expect(q('[data-testid="match-estimate"]')!.textContent).toContain('120 folders');
    (q('[data-testid="match-start"]') as HTMLButtonElement).click();
    const post = http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/libraries/lib1/match' });
    expect(post.request.body).toEqual({ reviewFirst: false, retryUnmatched: false });
    post.flush({ runId: 'r9', libraryName: 'Library One' });
    expect(runs).toEqual(['r9']);
    expect(c.matching()).toBeNull();
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

describe('validateThresholds', () => {
  const bounds = settings().thresholdBounds!;
  it('accepts the defaults and the bounds themselves, as whole percents', () => {
    expect(validateThresholds({ autoTitle: '92', margin: '10', reviewFloor: '60' }, bounds))
      .toEqual({ value: { autoTitle: 0.92, margin: 0.1, reviewFloor: 0.6 } });
    expect('value' in validateThresholds({ autoTitle: '99', margin: '5', reviewFloor: '90' }, bounds)).toBe(true);
  });
  it('rejects out-of-bounds, non-numbers and a floor at or above the auto score', () => {
    expect(validateThresholds({ autoTitle: '80', margin: '10', reviewFloor: '60' }, bounds)).toEqual(
      { error: 'Auto-link title score must be from 85% to 99%.' });
    expect(validateThresholds({ autoTitle: '90', margin: 'x', reviewFloor: '60' }, bounds)).toEqual(
      { error: 'Lead over the runner-up must be from 5% to 30%.' });
    expect(validateThresholds({ autoTitle: '86', margin: '10', reviewFloor: '90' }, bounds)).toEqual(
      { error: 'The review floor must be below the auto-link title score.' });
  });
});
