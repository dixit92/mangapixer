import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { MetadataApiService } from './metadata-api.service';

/**
 * Identify calls of MetadataApiService (1.24.0, lane B2): every request goes to
 * MangaPixer's own /api/v1 (the server makes the provider calls); errors map to ApiError.
 */
describe('MetadataApiService identify calls', () => {
  let api: MetadataApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(MetadataApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uses the admin identify routes with the documented bodies', () => {
    api.getIdentifyContext('a b').subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/nodes/a%20b/identify' }).flush({});

    api.search('n1', 'Berserk', 2).subscribe();
    const search = http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/nodes/n1/search' });
    expect(search.request.body).toEqual({ query: 'Berserk', page: 2, hideDoujinshiAndNovels: false });
    search.flush({});

    api.search('n1', 'Berserk', 1, true).subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/search').request.body)
      .toEqual({ query: 'Berserk', page: 1, hideDoujinshiAndNovels: true });

    api.lookup('n1', 'mu:1').subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/lookup').request.body).toEqual({ reference: 'mu:1' });

    api.preview('n1', { provider: 'mangaupdates', externalId: '7' }).subscribe();
    expect(http.expectOne('/api/v1/admin/metadata/nodes/n1/preview').request.body).toEqual({ provider: 'mangaupdates', externalId: '7' });

    api.refresh('n1').subscribe();
    expect(http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/nodes/n1/refresh' })).toBeTruthy();
    http.match(() => true).forEach((r) => r.flush({}));

    expect(api.candidateImageUrl('t/1')).toBe('/api/v1/admin/metadata/candidates/t%2F1/image');
  });

  it('maps a refusal to the server ApiError with its status', () => {
    let error: unknown;
    api.search('n1', 'x').subscribe({ error: (e) => (error = e) });
    http.expectOne('/api/v1/admin/metadata/nodes/n1/search').flush(
      { error: 'budget_exhausted', message: 'Budget used up', detail: null, correlationId: null },
      { status: 429, statusText: 'Too Many Requests' },
    );
    expect(error).toMatchObject({ error: 'budget_exhausted', status: 429 });
  });
});
