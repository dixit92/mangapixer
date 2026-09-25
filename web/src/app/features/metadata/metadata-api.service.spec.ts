import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { MetadataApiService } from './metadata-api.service';

/** The series-metadata API client (1.24.0): routes, methods, bodies, error mapping. */
describe('MetadataApiService', () => {
  let api: MetadataApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(MetadataApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('gets series info, with and without the item table', () => {
    api.getSeriesInfo('abc').subscribe();
    const plain = http.expectOne('/api/v1/nodes/abc/series-info');
    expect(plain.request.method).toBe('GET');
    expect(plain.request.withCredentials).toBe(true);
    plain.flush({});

    api.getSeriesInfo('abc', true).subscribe();
    http.expectOne('/api/v1/nodes/abc/series-info?includeItems=true').flush({});
  });

  it('sends the admin link, Don\'t match and precedence calls to the right routes', () => {
    api.link('n1', { provider: 'mangaupdates', externalId: '42' }).subscribe();
    const link = http.expectOne('/api/v1/admin/metadata/nodes/n1/link');
    expect(link.request.method).toBe('PUT');
    expect(link.request.body).toEqual({ provider: 'mangaupdates', externalId: '42' });
    link.flush({});

    api.unlink('n1').subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/link').request.method).toBe('DELETE');

    api.setDontMatch('n1').subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/dont-match').request.method).toBe('PUT');

    api.clearDontMatch('n1').subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/dont-match').request.method).toBe('DELETE');

    api.setFolderPrecedence('f1', 'ComicInfoFirst').subscribe();
    const folder = http.expectOne('/api/v1/admin/metadata/folders/f1/precedence');
    expect(folder.request.body).toEqual({ precedence: 'ComicInfoFirst' });

    api.clearFolderPrecedence('f1').subscribe();
    expect(http.expectOne((r) => r.method === 'DELETE' && r.url === '/api/v1/admin/metadata/folders/f1/precedence')).toBeTruthy();
  });

  it('sends the settings, library toggle, library precedence and purge calls', () => {
    api.getSettings().subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/settings').request.method).toBe('GET');

    api.updateSettings({ showSeriesInfo: false }).subscribe();
    const settings = http.expectOne((r) => r.method === 'PUT' && r.url === '/api/v1/admin/metadata/settings');
    expect(settings.request.body).toEqual({ showSeriesInfo: false });

    api.updateLibrary('lib1', { fetchEnabled: true }).subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/libraries/lib1').request.body).toEqual({ fetchEnabled: true });

    api.setLibraryPrecedence('lib1', null).subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/libraries/lib1/precedence').request.body).toEqual({ precedence: null });

    api.purge(null).subscribe();
    const purge = http.expectOne('/api/v1/admin/metadata/purge');
    expect(purge.request.method).toBe('POST');
    expect(purge.request.body).toEqual({ libraryId: null });
  });

  it('maps a bodyless 404 to a not_found ApiError and keeps server error codes', () => {
    let first: unknown;
    api.getSeriesInfo('gone').subscribe({ error: (e) => (first = e) });
    http.expectOne('/api/v1/nodes/gone/series-info').flush(null, { status: 404, statusText: 'Not Found' });
    expect(first).toMatchObject({ error: 'not_found', status: 404 });

    let second: unknown;
    api.link('n1', { provider: 'mangaupdates', externalId: '1' }).subscribe({ error: (e) => (second = e) });
    http.expectOne('/api/v1/admin/metadata/nodes/n1/link')
      .flush({ error: 'record_not_found', message: 'No stored record', detail: null, correlationId: null }, { status: 404, statusText: 'Not Found' });
    expect(second).toMatchObject({ error: 'record_not_found', status: 404 });
  });

  it('escapes node ids in the path', () => {
    api.getSeriesInfo('a/b').subscribe();
    http.expectOne('/api/v1/nodes/a%2Fb/series-info').flush({});
  });
});
