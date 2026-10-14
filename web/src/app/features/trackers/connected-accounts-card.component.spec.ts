import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';

import { TrackerSummaryDto } from '../../core/api/api-types';
import { ConnectedAccountsCardComponent } from './connected-accounts-card.component';
import { TrackerAccountPanelComponent } from './tracker-account-panel.component';
import { TrackerNavigationService } from './tracker-navigation.service';
import { TrackerStateService } from './tracker-state.service';
import { summary, trackerAccount } from './trackers.testing';

const BASE = '/api/v1/trackers';

const MU = (o: Partial<TrackerSummaryDto> = {}) => summary({ slug: 'mangaupdates', name: 'MangaUpdates', connectKind: 'Password', ...o });

/**
 * Settings > Connected accounts (1.43.0): the user consent, Connect (MyAnimeList: the browser goes to the sign-in address the
 * server gives; MangaUpdates: username + password, never echoed, cleared after submit, an empty password refused without a
 * request), "Connected as", Disconnect after a confirm, and the user's settings. Mocked HTTP backend and navigation.
 */
describe('ConnectedAccountsCardComponent', () => {
  const openSignIn = vi.fn((url: string) => url.startsWith('https://'));

  function create(trackers: TrackerSummaryDto[]) {
    openSignIn.mockClear();
    TestBed.configureTestingModule({
      imports: [ConnectedAccountsCardComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: TrackerNavigationService, useValue: { openSignIn } },
      ],
    });
    const fixture = TestBed.createComponent(ConnectedAccountsCardComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(BASE).flush({ trackers });
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;
  const panel = (fixture: ReturnType<typeof create>['fixture'], slug: string) =>
    fixture.debugElement.queryAll((d) => d.componentInstance instanceof TrackerAccountPanelComponent)
      .map((d) => d.componentInstance as TrackerAccountPanelComponent).find((p) => p.tracker().slug === slug)!;

  // Reset first: a failed verify must not leave the module instantiated for the next test (one failure, one red).
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('says when no tracker is allowed, and links the user docs without a referrer', () => {
    const { el } = create([summary({ allowed: false }), MU({ allowed: false })]);
    expect(q(el, 'connected-accounts-none').textContent).toContain('No tracker is allowed');
    const help = q(el, 'connected-accounts-help') as HTMLAnchorElement;
    expect(help.href).toBe('https://mangapixer.com/tracker-sync/');
    expect(help.rel).toContain('noreferrer');
  });

  it('MyAnimeList: shows the user consent, waits for the tick, then opens the sign-in address the server gives', () => {
    const { fixture, http, el } = create([summary()]);
    expect(q(el, 'tracker-consent-user-mal').textContent).toContain('MangaPixer never sees your password');
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

  it('MangaUpdates: a successful connect reloads the overview', () => {
    const { fixture, http, el } = create([MU()]);
    const p = panel(fixture, 'mangaupdates');
    p.consentTicked.set(true);
    p.username.set('reader');
    p.password.set('pw');
    fixture.detectChanges();
    q(el, 'tracker-connect-mangaupdates').click();
    http.expectOne(`${BASE}/mangaupdates/connect/password`).flush(trackerAccount({ tracker: 'mangaupdates' }));
    http.expectOne(BASE).flush({ trackers: [MU({ account: trackerAccount({ tracker: 'mangaupdates' }) })] });
    fixture.detectChanges();
    http.expectOne(`${BASE}/mangaupdates/entries`).flush([]);
    fixture.detectChanges();
    expect(q(el, 'tracker-connected-as-mangaupdates').textContent).toContain('Connected as reader');
    expect(TestBed.inject(TrackerStateService).hasConnected()).toBe(true);
  });

  it('connected: shows the account, the settings (private entries only where supported) and disconnects after a confirm', () => {
    const { fixture, http, el } = create([summary({ account: trackerAccount() }), MU({ account: trackerAccount({ tracker: 'mangaupdates' }), supportsPrivateEntries: true })]);
    http.expectOne(`${BASE}/mal/entries`).flush([]);
    http.expectOne(`${BASE}/mangaupdates/entries`).flush([]);
    fixture.detectChanges();
    expect(q(el, 'tracker-connected-as-mal').textContent).toContain('Connected as reader');
    expect(q(el, 'tracker-consent-user-mal')).toBeNull();
    expect(q(el, 'tracker-private-entries-mal')).toBeNull();
    expect(q(el, 'tracker-private-entries-mangaupdates')).not.toBeNull();

    q(el, 'tracker-add-started-mal').querySelector('button')!.click();
    const settings = http.expectOne(`${BASE}/mal/settings`);
    expect(settings.request.body).toEqual({ addStartedSeries: false });
    settings.flush(trackerAccount({ addStartedSeries: false }));
    http.expectOne(BASE).flush({ trackers: [summary({ account: trackerAccount({ addStartedSeries: false }) })] });
    fixture.detectChanges();

    q(el, 'tracker-disconnect-mal').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-disconnect-confirm-mal').textContent).toContain('MyAnimeList offers apps no way to end their own sign-in');
    q(el, 'tracker-disconnect-yes-mal').click();
    http.expectOne(`${BASE}/mal/disconnect`).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(BASE).flush({ trackers: [summary()] });
    fixture.detectChanges();
    expect(q(el, 'tracker-notice-mal').textContent).toContain('Disconnected from MyAnimeList');
    expect(q(el, 'tracker-consent-user-mal')).not.toBeNull();
  });

  it('an account disconnected by an admin asks to connect again', () => {
    const { fixture, http, el } = create([summary({ account: trackerAccount({ state: 'DisconnectedByAdmin', displayName: null }) })]);
    http.expectOne(`${BASE}/mal/entries`).flush([]);
    fixture.detectChanges();
    expect(q(el, 'tracker-problem-mal').textContent).toContain('An admin disconnected');
    expect(q(el, 'tracker-connect-mal')).not.toBeNull();
  });
});
