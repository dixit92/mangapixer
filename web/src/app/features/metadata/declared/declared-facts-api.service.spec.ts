import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { DeclaredFactsScopeDto } from '../../../core/api/api-types';
import { DeclaredFactsApiService } from './declared-facts-api.service';

const SCOPE: DeclaredFactsScopeDto = {
  nodeId: 'f 1', libraryId: 'lib1', displayName: 'Series', own: { type: 'Manga', creators: [] }, inherited: {},
};

/** Declared facts API (1.28.0): URLs per scope, the version bump after a change, errors mapped to ApiError. */
describe('DeclaredFactsApiService', () => {
  let api: DeclaredFactsApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(DeclaredFactsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads a folder and a library scope', () => {
    api.get({ kind: 'folder', id: 'f 1' }).subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/folders/f%201/declared' }).flush(SCOPE);
    api.get({ kind: 'library', id: 'lib1' }).subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/libraries/lib1/declared' }).flush(SCOPE);
    expect(api.version()).toBe(0);
  });

  it('bumps the version after a saved change, not after a failed one', () => {
    api.set({ kind: 'folder', id: 'f1' }, { type: 'Manhwa', creators: [{ name: 'A', role: 'artist' }] }).subscribe();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/admin/metadata/folders/f1/declared' });
    expect(put.request.body).toEqual({ type: 'Manhwa', creators: [{ name: 'A', role: 'artist' }] });
    put.flush(SCOPE);
    expect(api.version()).toBe(1);

    let error: unknown;
    api.clear({ kind: 'library', id: 'lib1' }).subscribe({ error: (e) => (error = e) });
    http.expectOne({ method: 'DELETE', url: '/api/v1/admin/metadata/libraries/lib1/declared' })
      .flush({ error: 'invalid_request', message: 'No.' }, { status: 400, statusText: 'Bad Request' });
    expect(error).toEqual(expect.objectContaining({ error: 'invalid_request', status: 400 }));
    expect(api.version()).toBe(1);
  });

  it('saves a folder\'s own edition facts at their own URL (1.39.0)', () => {
    api.setEdition('f 1', { volumeTotal: 12, edition: 'Omnibus', tracking: false }).subscribe();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/admin/metadata/folders/f%201/declared/edition' });
    expect(put.request.body).toEqual({ volumeTotal: 12, edition: 'Omnibus', tracking: false });
    put.flush(SCOPE);
    expect(api.version()).toBe(1);
  });

  it('reads the node view', () => {
    api.forNode('n/1').subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/nodes/n%2F1/declared-facts' }).flush({ nodeId: 'n/1', effective: {} });
  });
});
