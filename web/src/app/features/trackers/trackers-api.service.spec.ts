import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { TrackersApiService } from './trackers-api.service';
import { TrackerNavigationService } from './tracker-navigation.service';

/** The tracker routes the web client calls (1.43.0) - lane E's contract - all on this server, with the cookie. */
describe('TrackersApiService', () => {
  function create() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { api: TestBed.inject(TrackersApiService), http: TestBed.inject(HttpTestingController) };
  }

  // Reset first: a failed verify must not leave the module instantiated for the next test (one failure, one red).
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('maps every call to its route and method', () => {
    const { api, http } = create();
    const calls: [() => unknown, string, string, unknown][] = [
      [() => api.overview(), 'GET', '/api/v1/trackers', null],
      [() => api.connectStart('mal', 1), 'POST', '/api/v1/trackers/mal/connect/start', { acceptedConsentVersion: 1 }],
      [() => api.connectFinish('mal', 'c', 's'), 'POST', '/api/v1/trackers/mal/connect', { code: 'c', state: 's' }],
      [() => api.connectPassword('mangaupdates', 'u', 'p', 1), 'POST', '/api/v1/trackers/mangaupdates/connect/password',
        { username: 'u', password: 'p', acceptedConsentVersion: 1 }],
      [() => api.disconnect('mal'), 'POST', '/api/v1/trackers/mal/disconnect', {}],
      [() => api.updateSettings('mal', { addStartedSeries: false }), 'PUT', '/api/v1/trackers/mal/settings', { addStartedSeries: false }],
      [() => api.entries('mal'), 'GET', '/api/v1/trackers/mal/entries', null],
      [() => api.syncNow('mal', 's 1'), 'POST', '/api/v1/trackers/mal/entries/s%201/sync', {}],
      [() => api.send('mal', 's1'), 'POST', '/api/v1/trackers/mal/entries/s1/send', {}],
      [() => api.setProgress('mal', 's1', 3, null), 'POST', '/api/v1/trackers/mal/entries/s1/set', { chapters: 3, volumes: null }],
      [() => api.setDontSync('mal', 's1', true), 'PUT', '/api/v1/trackers/mal/entries/s1/dont-sync', { dontSync: true }],
      [() => api.preview('mal'), 'POST', '/api/v1/trackers/mal/preview', {}],
      [() => api.batch('mal', 'FromTracker', ['s1', 's2']), 'POST', '/api/v1/trackers/mal/entries/batch',
        { action: 'FromTracker', seriesIds: ['s1', 's2'] }],
      [() => api.syncOverrides(), 'GET', '/api/v1/trackers/sync-overrides', null],
      [() => api.setSyncOverrides(['n1'], null), 'PUT', '/api/v1/trackers/sync-overrides', { nodeIds: ['n1'], enabled: null }],
      [() => api.attention(), 'GET', '/api/v1/trackers/attention', null],
      [() => api.summary('n 1'), 'GET', '/api/v1/trackers/nodes/n%201/summary', null],
      [() => api.fromTracker('mal', 's1'), 'POST', '/api/v1/trackers/mal/entries/s1/from-tracker', {}],
      [() => api.setDates('mal', 's1', { startedOn: '2026-10-01', finishedOn: null }), 'PUT', '/api/v1/trackers/mal/entries/s1/dates',
        { startedOn: '2026-10-01', finishedOn: null }],
      [() => api.adminList(), 'GET', '/api/v1/admin/trackers', null],
      [() => api.adminUpdate('mal', { enabled: false }), 'PUT', '/api/v1/admin/trackers/mal', { enabled: false }],
      [() => api.adminAccounts(), 'GET', '/api/v1/admin/trackers/accounts', null],
      [() => api.adminDisconnect('mal', 'u1'), 'POST', '/api/v1/admin/trackers/mal/accounts/u1/disconnect', {}],
      [() => api.seriesIds('n1'), 'GET', '/api/v1/admin/trackers/nodes/n1/ids', null],
      [() => api.setSeriesId('n1', 'mal', '2'), 'PUT', '/api/v1/admin/trackers/nodes/n1/ids/mal', { remoteId: '2' }],
    ];
    // 1.43.2: Send all is gone - the Select mode's batch replaces it.
    expect((api as unknown as Record<string, unknown>)['sendAll']).toBeUndefined();
    for (const [call, method, url, body] of calls) {
      (call() as { subscribe: () => void }).subscribe();
      const req = http.expectOne(url);
      expect(req.request.method).toBe(method);
      expect(req.request.withCredentials).toBe(true);
      if (body !== null) expect(req.request.body).toEqual(body);
      req.flush({});
    }
  });
});

describe('TrackersApiService.seriesPage (the Tracking list, 1.43.0)', () => {
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('sends the page, the chip (not for All) and the trimmed search as query parameters', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const api = TestBed.inject(TrackersApiService);
    const http = TestBed.inject(HttpTestingController);
    const cases: [number, string, string, Record<string, string>][] = [
      [1, 'All', '', { page: '1' }],
      [2, 'TrackerAhead', '', { page: '2', filter: 'TrackerAhead' }],
      [1, 'Attention', '  Saga ', { page: '1', filter: 'Attention', q: 'Saga' }],
    ];
    for (const [page, filter, q, expected] of cases) {
      api.seriesPage('mal', page, filter, q).subscribe();
      const req = http.expectOne((r) => r.url === '/api/v1/trackers/mal/series');
      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBe(true);
      expect(Object.fromEntries(req.request.params.keys().map((k) => [k, req.request.params.get(k)]))).toEqual(expected);
      req.flush({});
    }
  });
});

describe('TrackersApiService.selectableSeries ("Select all in this chip", 1.43.2)', () => {
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('asks the ids route with the chip (not for All) and the trimmed search', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const api = TestBed.inject(TrackersApiService);
    const http = TestBed.inject(HttpTestingController);
    const cases: [string, string, Record<string, string>][] = [
      ['All', '', {}],
      ['Held', '', { filter: 'Held' }],
      ['TrackerAhead', ' Saga  ', { filter: 'TrackerAhead', q: 'Saga' }],
    ];
    for (const [filter, q, expected] of cases) {
      api.selectableSeries('mal', filter, q).subscribe();
      const req = http.expectOne((r) => r.url === '/api/v1/trackers/mal/series/ids');
      expect(req.request.method).toBe('GET');
      expect(req.request.withCredentials).toBe(true);
      expect(Object.fromEntries(req.request.params.keys().map((k) => [k, req.request.params.get(k)]))).toEqual(expected);
      req.flush({ items: [], totalCount: 0, truncated: false });
    }
  });
});

describe('TrackerNavigationService', () => {
  it('follows only an https sign-in address', () => {
    const nav = new TrackerNavigationService();
    expect(nav.openSignIn('javascript:alert(1)')).toBe(false);
    expect(nav.openSignIn('http://myanimelist.net/v1/oauth2/authorize')).toBe(false);
    expect(nav.openSignIn('not a url')).toBe(false);
  });
});
