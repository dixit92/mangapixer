import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { vi } from 'vitest';

import { TrackerSummaryDto } from '../../core/api/api-types';
import { TrackerAccountPanelComponent } from './tracker-account-panel.component';
import { TrackerNavigationService } from './tracker-navigation.service';
import { TrackerStateService } from './tracker-state.service';
import { TrackingPageComponent } from './tracking-page.component';
import { seriesPage, seriesRow, summary, trackerAccount } from './trackers.testing';

const BASE = '/api/v1/trackers';

const MU = (o: Partial<TrackerSummaryDto> = {}) => summary({ slug: 'mangaupdates', name: 'MangaUpdates', connectKind: 'Password', ...o });

/**
 * The Tracking page (1.43.0 owner redesign; it replaces Settings > Connected accounts): the account cards (the user consent behind
 * "Connect <tracker>…", Connect - MyAnimeList: the browser goes to the sign-in address the server gives; MangaUpdates: username +
 * password, never echoed, cleared after submit, an empty password refused without a request -, "Connected as", Disconnect after a
 * confirm, the user's settings), one series tab per connected tracker, and "Open in Tracking" (`?tracker=&series=`). Mocked HTTP
 * backend and navigation.
 */
describe('TrackingPageComponent', () => {
  const openSignIn = vi.fn((url: string) => url.startsWith('https://'));

  let query$: BehaviorSubject<ReturnType<typeof convertToParamMap>>;

  function create(trackers: TrackerSummaryDto[], query: Record<string, string> = {}) {
    openSignIn.mockClear();
    query$ = new BehaviorSubject(convertToParamMap(query));
    TestBed.configureTestingModule({
      imports: [TrackingPageComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: TrackerNavigationService, useValue: { openSignIn } },
        // The address as the router gives it: the snapshot at creation, and every later change of the query.
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) }, queryParamMap: query$ } },
      ],
    });
    const fixture = TestBed.createComponent(TrackingPageComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(BASE).flush({ trackers });
    http.expectOne(`${BASE}/attention`).flush({ total: 0, trackers: [] });
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;
  const panel = (fixture: ReturnType<typeof create>['fixture'], slug: string) =>
    fixture.debugElement.queryAll((d) => d.componentInstance instanceof TrackerAccountPanelComponent)
      .map((d) => d.componentInstance as TrackerAccountPanelComponent).find((p) => p.tracker().slug === slug)!;
  const openConnect = (fixture: ReturnType<typeof create>['fixture'], el: HTMLElement, slug: string) => {
    q(el, `tracker-connect-open-${slug}`).click();
    fixture.detectChanges();
  };

  // Reset first: a failed verify must not leave the module instantiated for the next test (one failure, one red).
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('says when no tracker is allowed, and links the user docs without a referrer', () => {
    const { el } = create([summary({ allowed: false }), MU({ allowed: false })]);
    expect(q(el, 'tracking-none').textContent).toContain('No tracker is allowed');
    const help = q(el, 'tracking-help') as HTMLAnchorElement;
    expect(help.href).toBe('https://mangapixer.com/tracker-sync/');
    expect(help.rel).toContain('noreferrer');
    expect(q(el, 'tracking-series')).toBeNull();
  });

  it('shows one card per allowed tracker, in a fixed order, and no series list before an account is connected', () => {
    const { el } = create([MU(), summary()]);
    const cards = [...el.querySelectorAll('[data-testid^="tracker-account-"]')].map((c) => c.getAttribute('data-testid'));
    expect(cards).toEqual(['tracker-account-mal', 'tracker-account-mangaupdates']);
    expect(q(el, 'tracker-status-mal').textContent).toContain('Not connected');
    expect(q(el, 'tracker-consent-user-mal')).toBeNull();
    expect(q(el, 'tracking-no-account').textContent).toContain('Connect an account above');
  });

  it('MyAnimeList: "Connect…" shows the user consent, waits for the tick, then opens the sign-in address the server gives', () => {
    const { fixture, http, el } = create([summary()]);
    openConnect(fixture, el, 'mal');
    expect(q(el, 'tracker-consent-user-mal').textContent).toContain('MangaPixer never sees your password');
    expect(q(el, 'tracker-consent-user-mal').textContent).toContain('the dates you started and finished it');
    const connect = q(el, 'tracker-connect-mal') as HTMLButtonElement;
    expect(connect.disabled).toBe(true);
    panel(fixture, 'mal').consentTicked.set(true);
    fixture.detectChanges();
    connect.click();
    const req = http.expectOne(`${BASE}/mal/connect/start`);
    expect(req.request.body).toEqual({ acceptedConsentVersion: 1 });
    const url = 'https://myanimelist.net/v1/oauth2/authorize?response_type=code&client_id=c&state=s';
    req.flush({ authorizeUrl: url });
    expect(openSignIn).toHaveBeenCalledWith(url);
  });

  it('MyAnimeList: a refused start is shown in plain words', () => {
    const { fixture, http, el } = create([summary()]);
    openConnect(fixture, el, 'mal');
    panel(fixture, 'mal').consentTicked.set(true);
    fixture.detectChanges();
    q(el, 'tracker-connect-mal').click();
    http.expectOne(`${BASE}/mal/connect/start`).flush({ error: 'not_configured', message: 'x' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(q(el, 'tracker-error-mal').textContent).toContain('has not finished setting up MyAnimeList');
    expect(openSignIn).not.toHaveBeenCalled();
  });

  it('MangaUpdates: refuses an empty password without a request and clears the password after every submit', async () => {
    const { fixture, http, el } = create([MU()]);
    openConnect(fixture, el, 'mangaupdates');
    const p = panel(fixture, 'mangaupdates');
    p.consentTicked.set(true);
    p.username.set('reader');
    fixture.detectChanges();
    expect((q(el, 'tracker-password-mangaupdates') as HTMLInputElement).type).toBe('password');
    q(el, 'tracker-connect-mangaupdates').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-error-mangaupdates').textContent).toContain('Enter your MangaUpdates password.');
    http.expectNone(`${BASE}/mangaupdates/connect/password`);

    p.password.set('PW-SECRET');
    fixture.detectChanges();
    q(el, 'tracker-connect-mangaupdates').click();
    expect(p.password()).toBe('');
    const req = http.expectOne(`${BASE}/mangaupdates/connect/password`);
    expect(req.request.body).toEqual({ username: 'reader', password: 'PW-SECRET', acceptedConsentVersion: 1 });
    req.flush({ error: 'connect_rejected', message: 'x' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect((q(el, 'tracker-password-mangaupdates') as HTMLInputElement).value).toBe('');
    expect(el.textContent).not.toContain('PW-SECRET');
    expect(q(el, 'tracker-error-mangaupdates').textContent).toContain('MangaUpdates did not accept this sign-in');
  });

  it('MangaUpdates: a successful connect reloads the overview and opens its series tab', () => {
    const { fixture, http, el } = create([MU()]);
    openConnect(fixture, el, 'mangaupdates');
    const p = panel(fixture, 'mangaupdates');
    p.consentTicked.set(true);
    p.username.set('reader');
    p.password.set('pw');
    fixture.detectChanges();
    q(el, 'tracker-connect-mangaupdates').click();
    http.expectOne(`${BASE}/mangaupdates/connect/password`).flush(trackerAccount({ tracker: 'mangaupdates' }));
    http.expectOne(BASE).flush({ trackers: [MU({ account: trackerAccount({ tracker: 'mangaupdates' }) })] });
    http.expectOne(`${BASE}/attention`).flush({ total: 0, trackers: [] });
    fixture.detectChanges();
    http.expectOne((r) => r.url === `${BASE}/mangaupdates/series`).flush(seriesPage([]));
    fixture.detectChanges();
    expect(q(el, 'tracker-connected-as-mangaupdates').textContent).toContain('Connected as reader');
    expect(q(el, 'tracking-tab-mangaupdates').getAttribute('aria-selected')).toBe('true');
    expect(TestBed.inject(TrackerStateService).hasConnected()).toBe(true);
  });

  it('connected: shows the account, the settings (private entries only where supported) and disconnects after a confirm', () => {
    const { fixture, http, el } = create([summary({ account: trackerAccount() }),
      MU({ account: trackerAccount({ tracker: 'mangaupdates' }), supportsPrivateEntries: true })]);
    http.expectOne((r) => r.url === `${BASE}/mal/series`).flush(seriesPage([seriesRow()]));
    fixture.detectChanges();
    expect(q(el, 'tracker-connected-as-mal').textContent).toContain('Connected as reader');
    expect(q(el, 'tracker-status-mal').textContent).toContain('Connected');
    expect(q(el, 'tracker-consent-user-mal')).toBeNull();
    expect(q(el, 'tracker-private-entries-mal')).toBeNull();
    expect(q(el, 'tracker-private-entries-mangaupdates')).not.toBeNull();
    expect([...el.querySelectorAll('[role="tab"]')].map((t) => t.textContent!.trim())).toEqual(['MyAnimeList', 'MangaUpdates']);

    q(el, 'tracker-add-started-mal').querySelector('button')!.click();
    const settings = http.expectOne(`${BASE}/mal/settings`);
    expect(settings.request.body).toEqual({ addStartedSeries: false });
    settings.flush(trackerAccount({ addStartedSeries: false }));
    http.expectOne(BASE).flush({ trackers: [summary({ account: trackerAccount({ addStartedSeries: false }) })] });
    http.expectOne(`${BASE}/attention`).flush({ total: 0, trackers: [] });
    fixture.detectChanges();

    q(el, 'tracker-disconnect-mal').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-disconnect-confirm-mal').textContent).toContain('MyAnimeList has no page to remove an app\'s access');
    q(el, 'tracker-disconnect-yes-mal').click();
    http.expectOne(`${BASE}/mal/disconnect`).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(BASE).flush({ trackers: [summary()] });
    http.expectOne(`${BASE}/attention`).flush({ total: 0, trackers: [] });
    fixture.detectChanges();
    expect(q(el, 'tracker-notice-mal').textContent).toContain('Disconnected from MyAnimeList');
    expect(q(el, 'tracker-connect-open-mal')).not.toBeNull();
    expect(q(el, 'tracking-series')).toBeNull();
  });

  it('an account disconnected by an admin asks to connect again', () => {
    const { fixture, http, el } = create([summary({ account: trackerAccount({ state: 'DisconnectedByAdmin', displayName: null }) })]);
    http.expectOne((r) => r.url === `${BASE}/mal/series`).flush(seriesPage([]));
    fixture.detectChanges();
    expect(q(el, 'tracker-problem-mal').textContent).toContain('An admin disconnected');
    expect(q(el, 'tracker-status-mal').textContent).toContain('Connect again');
    expect(q(el, 'tracker-connect-open-mal').textContent).toContain('Connect again…');
    // The list stays, its actions off (the account cannot sync).
    expect((q(el, 'tracker-preview-mal') as HTMLButtonElement).disabled).toBe(true);
  });

  it('"Open in Tracking" (?tracker=mangaupdates&series=s9) opens that tab with the series highlighted', () => {
    history.replaceState({ title: 'Synthetic Saga' }, '');
    try {
      const { fixture, http, el } = create([summary({ account: trackerAccount() }), MU({ account: trackerAccount({ tracker: 'mangaupdates' }) })],
        { tracker: 'mangaupdates', series: 's9' });
      const req = http.expectOne((r) => r.url === `${BASE}/mangaupdates/series`);
      expect(req.request.params.get('q')).toBe('Synthetic Saga');
      req.flush(seriesPage([seriesRow({ seriesId: 's9', tracker: 'mangaupdates' })]));
      fixture.detectChanges();
      expect(q(el, 'tracking-tab-mangaupdates').getAttribute('aria-selected')).toBe('true');
      expect(q(el, 'tracking-row-s9').classList).toContain('focused');
    } finally {
      history.replaceState(null, '');
    }
  });

  it('UX8: a new ?tracker= / ?series= while the page is shown switches the tab and highlights that series', () => {
    const { fixture, http, el } = create([summary({ account: trackerAccount() }), MU({ account: trackerAccount({ tracker: 'mangaupdates' }) })],
      { tracker: 'mal' });
    http.expectOne((r) => r.url === `${BASE}/mal/series`).flush(seriesPage([seriesRow()]));
    fixture.detectChanges();
    expect(q(el, 'tracking-tab-mal').getAttribute('aria-selected')).toBe('true');

    query$.next(convertToParamMap({ tracker: 'mangaupdates', series: 's9' }));
    fixture.detectChanges();
    http.expectOne((r) => r.url === `${BASE}/mangaupdates/series`).flush(seriesPage([seriesRow({ seriesId: 's9', tracker: 'mangaupdates' })]));
    fixture.detectChanges();
    expect(q(el, 'tracking-tab-mangaupdates').getAttribute('aria-selected')).toBe('true');
    expect(q(el, 'tracking-row-s9').classList).toContain('focused');

    // Another series on the same tab: searched for and highlighted in the list that is already there.
    query$.next(convertToParamMap({ tracker: 'mangaupdates', series: 's3' }));
    fixture.detectChanges();
    http.expectOne((r) => r.url === `${BASE}/mangaupdates/series`).flush(seriesPage([seriesRow({ seriesId: 's3', tracker: 'mangaupdates' })]));
    fixture.detectChanges();
    expect(q(el, 'tracking-row-s3').classList).toContain('focused');
  });
});
