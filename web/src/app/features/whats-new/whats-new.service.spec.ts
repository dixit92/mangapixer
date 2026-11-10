import { Component, WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';

import { AuthUserDto, WhatsNewDto } from '../../core/api/api-types';
import { AuthService } from '../../core/auth/auth.service';
import { WhatsNewService, isReaderUrl } from './whats-new.service';

@Component({ standalone: true, template: 'page' })
class PageComponent {}

const URL = '/api/v1/admin/whats-new';

const NOTES: WhatsNewDto = {
  current: '2.2.0',
  sections: [
    {
      version: '2.2.0', date: '2030-03-01',
      groups: [{ name: 'Added', items: [{ parts: [{ text: 'Shared lanterns', bold: true }, { text: ' are here.' }] }] }],
    },
  ],
};

function admin(id = 'a1', extra: Partial<AuthUserDto> = {}): AuthUserDto {
  return { id, username: 'admin', role: 'Admin', isAdmin: true, ...extra };
}

/**
 * 1.53.0 lane W: the once-after-an-update check. An admin's first page asks `GET /admin/whats-new`; notes show in a dialog once; closing
 * it ("Got it", Escape, a tap outside) posts `seen`; nothing shows for an empty answer or a failed request (asked again after the next
 * sign-in); never over the reader (it waits for the next page); never for a reader or an account that must change its password; the
 * menu's `openCurrent()` shows the running version's notes and posts nothing.
 */
describe('WhatsNewService', () => {
  let user: WritableSignal<AuthUserDto | null>;

  function setup(initial: AuthUserDto | null = admin()) {
    user = signal<AuthUserDto | null>(initial);
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: '', component: PageComponent },
          { path: 'libraries', component: PageComponent },
          { path: 'admin', component: PageComponent },
          { path: 'reader/:id', component: PageComponent },
        ]),
        { provide: AuthService, useValue: { currentUser: user.asReadonly() } },
      ],
    });
    const service = TestBed.inject(WhatsNewService);
    return { service, router: TestBed.inject(Router), http: TestBed.inject(HttpTestingController) };
  }

  const dialog = () => document.querySelector('app-whats-new-dialog');
  async function settle(): Promise<void> {
    TestBed.tick();
    await new Promise((r) => setTimeout(r, 0));
    TestBed.tick();
    await new Promise((r) => setTimeout(r, 0));
  }

  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('an admin sees the notes once after the first page; "Got it" marks them seen', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne((r) => r.url === URL && !r.params.has('current') && r.method === 'GET').flush(NOTES);
    await settle();
    expect(dialog()).not.toBeNull();
    expect(document.querySelector('[data-testid="whats-new-title"]')!.textContent!.trim()).toBe("What's new in MangaPixer 2.2.0");

    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    expect(dialog()).toBeNull();
    const seen = http.expectOne(`${URL}/seen`);
    expect(seen.request.method).toBe('POST');
    seen.flush(null, { status: 204, statusText: 'No Content' });

    // Later pages of the same session ask nothing.
    await router.navigateByUrl('/libraries');
    await router.navigateByUrl('/admin');
    http.expectNone(URL);
  });

  it('Escape marks them seen too', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne(URL).flush(NOTES);
    await settle();
    expect(dialog()).not.toBeNull();
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true } as KeyboardEventInit));
    await settle();
    expect(dialog()).toBeNull();
    http.expectOne(`${URL}/seen`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('a tap outside marks them seen too', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne(URL).flush(NOTES);
    await settle();
    (document.querySelector('.cdk-overlay-backdrop') as HTMLElement).click();
    await settle();
    expect(dialog()).toBeNull();
    http.expectOne(`${URL}/seen`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('nothing new: no dialog and nothing posted', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne(URL).flush({ current: '2.2.0', sections: [] });
    await settle();
    expect(dialog()).toBeNull();
    http.expectNone(`${URL}/seen`);
  });

  it('a failed request shows nothing; the next sign-in asks again', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne(URL).flush({ error: 'x' }, { status: 500, statusText: 'Server Error' });
    await settle();
    expect(dialog()).toBeNull();
    await router.navigateByUrl('/libraries');
    http.expectNone(URL);

    user.set(null);          // sign-out
    TestBed.tick();
    user.set(admin());       // sign-in again
    TestBed.tick();
    http.expectOne(URL).flush(NOTES);
    await settle();
    expect(dialog()).not.toBeNull();
    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    http.expectOne(`${URL}/seen`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('never over the reader: a first page in the reader waits for the next page that is not', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/reader/i1');
    http.expectNone(URL);
    expect(dialog()).toBeNull();

    await router.navigateByUrl('/libraries');
    http.expectOne(URL).flush(NOTES);
    await settle();
    expect(dialog()).not.toBeNull();
    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    http.expectOne(`${URL}/seen`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('notes that arrive after the admin opened the reader wait for the next page', async () => {
    const { router, http } = setup();
    await router.navigateByUrl('/');
    const pending = http.expectOne(URL);
    await router.navigateByUrl('/reader/i1');
    pending.flush(NOTES);
    await settle();
    expect(dialog()).toBeNull();

    await router.navigateByUrl('/libraries');
    await settle();
    expect(dialog()).not.toBeNull();
    http.expectNone(URL);
    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    http.expectOne(`${URL}/seen`).flush(null, { status: 204, statusText: 'No Content' });
  });

  it('a reader, an account that must change its password, and nobody signed in ask nothing', async () => {
    for (const who of [{ ...admin(), isAdmin: false, role: 'Reader' }, admin('a2', { forcePasswordChange: true }), null]) {
      const { router, http } = setup(who);
      await router.navigateByUrl('/');
      await router.navigateByUrl('/libraries');
      http.expectNone(URL);
      expect(dialog()).toBeNull();
      TestBed.resetTestingModule();
    }
    setup(null);
  });

  it("the menu's What's new shows the running version's notes and posts nothing on close", async () => {
    const { service, router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne((r) => r.url === URL && !r.params.has('current')).flush({ current: '2.2.0', sections: [] });

    service.openCurrent();
    const req = http.expectOne((r) => r.url === URL && r.params.get('current') === 'true');
    req.flush(NOTES);
    await settle();
    expect(dialog()).not.toBeNull();
    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    expect(dialog()).toBeNull();
    http.expectNone(`${URL}/seen`);
  });

  it("the menu's What's new says so when the notes cannot be loaded", async () => {
    const { service, router, http } = setup();
    await router.navigateByUrl('/');
    http.expectOne(URL).flush({ current: '2.2.0', sections: [] });

    service.openCurrent();
    http.expectOne((r) => r.params.get('current') === 'true').flush(null, { status: 500, statusText: 'Server Error' });
    await settle();
    expect(document.querySelector('[data-testid="whats-new-error"]')).not.toBeNull();
    (document.querySelector('[data-testid="whats-new-got-it"]') as HTMLButtonElement).click();
    await settle();
    http.expectNone(`${URL}/seen`);
  });

  it('isReaderUrl matches the reader route only', () => {
    expect(isReaderUrl('/reader/abc?page=2')).toBe(true);
    expect(isReaderUrl('/reader')).toBe(true);
    expect(isReaderUrl('/libraries/x/browse')).toBe(false);
    expect(isReaderUrl('/?next=/reader/x')).toBe(false);
  });
});
