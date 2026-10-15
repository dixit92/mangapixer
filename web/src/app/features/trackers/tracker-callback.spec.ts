import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, convertToParamMap, provideRouter } from '@angular/router';
import { vi } from 'vitest';

import { TrackerCallbackComponent } from './tracker-callback.component';
import { captureTrackerCallback, takeTrackerCallback, trackerCallbackGuard } from './tracker-callback';

const CODE = 'CODE-SECRET-123';
const STATE = 'STATE-SECRET-456';

function fakeLocation(path: string, search: string) {
  const hist = { state: { navigationId: 1 }, replaceState: vi.fn() };
  return { loc: { pathname: path, search, hash: '' }, hist };
}

/**
 * The tracker sign-in callback (1.43.0): the code and state leave the address bar before anything else, stay in memory only
 * until the page takes them once, are posted to the server, and are never shown; the server's refusal is shown in plain words.
 */
describe('tracker callback capture', () => {
  afterEach(() => takeTrackerCallback('mal'));

  it('strips code and state from the address with replaceState and keeps them for the page, once', () => {
    const { loc, hist } = fakeLocation('/trackers/callback/mal', `?code=${CODE}&state=${STATE}`);
    expect(captureTrackerCallback(loc, hist)).toBe(true);
    expect(hist.replaceState).toHaveBeenCalledTimes(1);
    const [, , url] = hist.replaceState.mock.calls[0];
    expect(url).toBe('/trackers/callback/mal');
    expect(String(url)).not.toContain(CODE);
    expect(takeTrackerCallback('mangaupdates')).toBeNull();

    captureTrackerCallback(loc, hist);
    expect(takeTrackerCallback('mal')).toEqual({ slug: 'mal', code: CODE, state: STATE, error: null });
    expect(takeTrackerCallback('mal')).toBeNull();
  });

  it('keeps unrelated query parameters and leaves other pages alone', () => {
    const { loc, hist } = fakeLocation('/trackers/callback/mal', `?code=${CODE}&state=${STATE}&keep=1`);
    captureTrackerCallback(loc, hist);
    expect(hist.replaceState.mock.calls[0][2]).toBe('/trackers/callback/mal?keep=1');

    const other = fakeLocation('/settings', `?code=${CODE}`);
    expect(captureTrackerCallback(other.loc, other.hist)).toBe(false);
    expect(other.hist.replaceState).not.toHaveBeenCalled();
    const clean = fakeLocation('/trackers/callback/mal', '');
    expect(captureTrackerCallback(clean.loc, clean.hist)).toBe(false);
  });

  it('the route guard captures parameters the router still sees and re-enters the page without them', () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const route = {
      paramMap: convertToParamMap({ slug: 'mal' }),
      queryParamMap: convertToParamMap({ code: CODE, state: STATE }),
    } as unknown as ActivatedRouteSnapshot;
    const result = TestBed.runInInjectionContext(() => trackerCallbackGuard(route, {} as RouterStateSnapshot));
    expect(result instanceof UrlTree).toBe(true);
    expect(String(result)).toBe('/trackers/callback/mal');
    expect(takeTrackerCallback('mal')).toEqual({ slug: 'mal', code: CODE, state: STATE, error: null });

    const clean = { paramMap: convertToParamMap({ slug: 'mal' }), queryParamMap: convertToParamMap({}) } as unknown as ActivatedRouteSnapshot;
    expect(TestBed.runInInjectionContext(() => trackerCallbackGuard(clean, {} as RouterStateSnapshot))).toBe(true);
  });
});

describe('TrackerCallbackComponent', () => {
  function create(search: string | null) {
    if (search !== null) {
      const { loc, hist } = fakeLocation('/trackers/callback/mal', search);
      captureTrackerCallback(loc, hist);
    }
    TestBed.configureTestingModule({
      imports: [TrackerCallbackComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ slug: 'mal' }) } } },
      ],
    });
    const fixture = TestBed.createComponent(TrackerCallbackComponent);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => takeTrackerCallback('mal'));

  it('posts the code and state once and says the account is connected', () => {
    const { fixture, http, el } = create(`?code=${CODE}&state=${STATE}`);
    const req = http.expectOne('/api/v1/trackers/mal/connect');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ code: CODE, state: STATE });
    req.flush({ tracker: 'mal', state: 'Ok', displayName: 'reader', connectedAt: '2026-10-13T00:00:00Z', consentRenewalNeeded: false,
      addStartedSeries: true, privateEntries: true, requestsToday: 1, dailyRequestCap: 300 });
    http.expectOne('/api/v1/trackers').flush({ trackers: [] });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="tracker-callback-connected"]')!.textContent).toContain('connected as reader');
    expect(el.textContent).not.toContain(CODE);
    expect(el.textContent).not.toContain(STATE);
    expect(el.querySelector('[data-testid="tracker-callback-settings"]')!.getAttribute('href')).toBe('/tracking');
    http.verify();
  });

  it.each([
    ['invalid_state', 400, 'not valid any more'],
    ['connect_rejected', 400, 'MyAnimeList did not accept this sign-in'],
    ['tracker_unavailable', 503, 'MyAnimeList could not be reached'],
  ])('shows %s in plain words, never the code or state', (code, status, words) => {
    const { fixture, http, el } = create(`?code=${CODE}&state=${STATE}`);
    http.expectOne('/api/v1/trackers/mal/connect').flush({ error: code, message: 'server text' }, { status, statusText: 'x' });
    fixture.detectChanges();
    const error = el.querySelector('[data-testid="tracker-callback-error"]')!.textContent!;
    expect(error).toContain(words);
    expect(el.textContent).not.toContain(CODE);
    expect(el.textContent).not.toContain(STATE);
    http.verify();
  });

  it('a refusal on the tracker or a page opened without a sign-in makes no request', () => {
    let t = create('?error=access_denied&state=' + STATE);
    expect(t.el.querySelector('[data-testid="tracker-callback-error"]')!.textContent).toContain('You did not allow MangaPixer');
    t.http.verify();
    TestBed.resetTestingModule();
    t = create(null);
    expect(t.el.querySelector('[data-testid="tracker-callback-error"]')!.textContent).toContain('no MyAnimeList sign-in to finish');
    t.http.verify();
  });
});
