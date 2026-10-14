import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { AdminTrackerAccountDto, AdminTrackerSettingsDto } from '../../core/api/api-types';
import { AdminTrackersCardComponent } from './admin-trackers-card.component';
import { AdminTrackerPanelComponent } from './admin-tracker-panel.component';

const LIST = '/api/v1/admin/trackers';

function adminTracker(overrides: Partial<AdminTrackerSettingsDto> = {}): AdminTrackerSettingsDto {
  return {
    slug: 'mal', name: 'MyAnimeList', enabled: false, acceptedConsentVersion: null, currentConsentVersion: 1, consentAt: null,
    consentRenewalNeeded: false, networkDisabledByConfig: false, connectKind: 'OAuthPkce', needsAppRegistration: true,
    clientId: null, clientSecretSet: false, redirectUri: null, connectedUsers: 0, ...overrides,
  };
}

const MU = (o: Partial<AdminTrackerSettingsDto> = {}) => adminTracker({
  slug: 'mangaupdates', name: 'MangaUpdates', connectKind: 'Password', needsAppRegistration: false, ...o,
});

function account(o: Partial<AdminTrackerAccountDto> = {}): AdminTrackerAccountDto {
  return { userId: 'u1', username: 'reader', tracker: 'mal', state: 'Ok', connectedAt: '2026-10-13T00:00:00Z', ...o };
}

/**
 * Administration > Trackers (1.43.0): allow a tracker only after its consent text, the MyAnimeList app registration (write-only
 * secret, proposed redirect URL), and the connected accounts with an admin Disconnect. Mocked HTTP backend.
 */
describe('AdminTrackersCardComponent', () => {
  function create(trackers = [adminTracker(), MU()], accounts: AdminTrackerAccountDto[] = []) {
    TestBed.configureTestingModule({
      imports: [AdminTrackersCardComponent],
      providers: [provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(AdminTrackersCardComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne(LIST).flush(trackers);
    http.expectOne(`${LIST}/accounts`).flush(accounts);
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;

  // Reset first: a failed verify must not leave the module instantiated for the next test (one failure, one red).
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('shows each tracker off with its consent text and a help link without a referrer', () => {
    const { el } = create();
    expect(q(el, 'admin-tracker-state-mal').textContent).toContain('Off');
    expect(q(el, 'tracker-consent-admin-mal').textContent).toContain('For personal servers only');
    expect(q(el, 'tracker-consent-admin-mal').textContent).toContain('personal, self-hosted server or a personal desktop install');
    expect(q(el, 'tracker-consent-admin-mangaupdates').textContent).toContain('username and password once');
    const help = q(el, 'admin-trackers-help') as HTMLAnchorElement;
    expect(help.href).toBe('https://mangapixer.com/tracker-setup/');
    expect(help.rel).toContain('noreferrer');
    expect(q(el, 'admin-trackers-no-accounts').textContent).toContain('No user has connected');
    // The allow switch waits for the tick.
    expect(q(el, 'admin-tracker-allow-mal').querySelector('button')!.disabled).toBe(true);
  });

  it('allows a tracker with the consent version this page shows, then folds the text away', () => {
    const { fixture, http, el } = create();
    const panel = fixture.debugElement.queryAll((d) => d.componentInstance instanceof AdminTrackerPanelComponent)[0]
      .componentInstance as AdminTrackerPanelComponent;
    panel.consentTicked.set(true);
    fixture.detectChanges();
    const toggle = q(el, 'admin-tracker-allow-mal').querySelector('button')!;
    expect(toggle.disabled).toBe(false);
    toggle.click();
    const req = http.expectOne(`${LIST}/mal`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ enabled: true, acceptedConsentVersion: 1 });
    req.flush(adminTracker({ enabled: true, acceptedConsentVersion: 1, consentAt: '2026-10-14T00:00:00Z' }));
    fixture.detectChanges();
    expect(q(el, 'admin-tracker-state-mal').textContent).toContain('Allowed - app not set up');
    expect(q(el, 'tracker-consent-admin-mal')).toBeNull();
    expect(q(el, 'admin-tracker-consent-toggle-mal').textContent).toContain('What is sent?');
  });

  it('refuses to accept a text the server no longer shows', () => {
    const { el } = create([adminTracker({ currentConsentVersion: 2 }), MU()]);
    expect(el.textContent).toContain('older text than the server');
    expect(q(el, 'admin-tracker-consent-mal').querySelector('input')!.disabled).toBe(true);
  });

  it('saves the MyAnimeList registration with the proposed redirect URL; the secret is write-only and cleared', async () => {
    const { fixture, http, el } = create();
    const redirect = q(el, 'admin-tracker-redirect-mal') as HTMLInputElement;
    await fixture.whenStable();
    expect(redirect.value).toBe(`${location.origin}/trackers/callback/mal`);
    const id = q(el, 'admin-tracker-client-id-mal') as HTMLInputElement;
    id.value = ' client-123 ';
    id.dispatchEvent(new Event('input'));
    const secret = q(el, 'admin-tracker-client-secret-mal') as HTMLInputElement;
    expect(secret.type).toBe('password');
    secret.value = 'S3CRET';
    secret.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    q(el, 'admin-tracker-save-mal').click();
    const req = http.expectOne(`${LIST}/mal`);
    expect(req.request.body).toEqual({ clientId: 'client-123', redirectUri: `${location.origin}/trackers/callback/mal`, clientSecret: 'S3CRET' });
    req.flush(adminTracker({ clientId: 'client-123', clientSecretSet: true, redirectUri: `${location.origin}/trackers/callback/mal` }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect((q(el, 'admin-tracker-client-secret-mal') as HTMLInputElement).value).toBe('');
    expect(el.textContent).not.toContain('S3CRET');
    expect(el.textContent).toContain('A client secret is saved. It is never shown again.');
  });

  it('removes the saved secret with an empty value', () => {
    const { http, el } = create([adminTracker({ clientId: 'c', clientSecretSet: true, redirectUri: 'https://x.test/trackers/callback/mal' }), MU()]);
    q(el, 'admin-tracker-remove-secret-mal').click();
    const req = http.expectOne(`${LIST}/mal`);
    expect(req.request.body).toEqual({ clientSecret: '' });
    req.flush(adminTracker({ clientId: 'c', redirectUri: 'https://x.test/trackers/callback/mal' }));
  });

  it('shows a refused save in plain words', () => {
    const { fixture, http, el } = create();
    const redirect = q(el, 'admin-tracker-redirect-mal') as HTMLInputElement;
    redirect.value = 'not a url';
    redirect.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    q(el, 'admin-tracker-save-mal').click();
    http.expectOne(`${LIST}/mal`).flush({ error: 'invalid_redirect_uri', message: 'x' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();
    expect(q(el, 'admin-tracker-error-mal').textContent).toContain('exactly as registered');
  });

  it('lists connected accounts and disconnects one after a confirm', () => {
    const { fixture, http, el } = create([adminTracker({ enabled: true, acceptedConsentVersion: 1 }), MU()],
      [account(), account({ userId: 'u2', username: 'other', tracker: 'mangaupdates', state: 'DisconnectedByAdmin' })]);
    expect(q(el, 'admin-tracker-account-mal-u1').textContent).toContain('Syncing');
    expect(q(el, 'admin-tracker-account-mangaupdates-u2').textContent).toContain('Disconnected by an admin');
    expect(q(el, 'admin-tracker-disconnect-mangaupdates-u2')).toBeNull();
    q(el, 'admin-tracker-disconnect-mal-u1').click();
    fixture.detectChanges();
    expect(q(el, 'admin-tracker-disconnect-confirm').textContent).toContain("Disconnect reader's MyAnimeList account?");
    q(el, 'admin-tracker-disconnect-yes').click();
    const req = http.expectOne(`${LIST}/mal/accounts/u1/disconnect`);
    expect(req.request.method).toBe('POST');
    req.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne(`${LIST}/accounts`).flush([account({ state: 'DisconnectedByAdmin' })]);
    fixture.detectChanges();
    expect(q(el, 'admin-tracker-account-mal-u1').textContent).toContain('Disconnected by an admin');
  });
});
