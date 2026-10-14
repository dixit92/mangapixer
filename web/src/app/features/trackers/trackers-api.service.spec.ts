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
      [() => api.sendAll('mal'), 'POST', '/api/v1/trackers/mal/entries/send-all', {}],
      [() => api.syncOverrides(), 'GET', '/api/v1/trackers/sync-overrides', null],
      [() => api.setSyncOverrides(['n1'], null), 'PUT', '/api/v1/trackers/sync-overrides', { nodeIds: ['n1'], enabled: null }],
      [() => api.adminList(), 'GET', '/api/v1/admin/trackers', null],
      [() => api.adminUpdate('mal', { enabled: false }), 'PUT', '/api/v1/admin/trackers/mal', { enabled: false }],
      [() => api.adminAccounts(), 'GET', '/api/v1/admin/trackers/accounts', null],
      [() => api.adminDisconnect('mal', 'u1'), 'POST', '/api/v1/admin/trackers/mal/accounts/u1/disconnect', {}],
      [() => api.seriesIds('n1'), 'GET', '/api/v1/admin/trackers/nodes/n1/ids', null],
      [() => api.setSeriesId('n1', 'mal', '2'), 'PUT', '/api/v1/admin/trackers/nodes/n1/ids/mal', { remoteId: '2' }],
    ];
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

describe('TrackerNavigationService', () => {
  it('follows only an https sign-in address', () => {
    const nav = new TrackerNavigationService();
    expect(nav.openSignIn('javascript:alert(1)')).toBe(false);
    expect(nav.openSignIn('http://myanimelist.net/v1/oauth2/authorize')).toBe(false);
    expect(nav.openSignIn('not a url')).toBe(false);
  });
});
