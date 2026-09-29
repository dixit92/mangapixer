import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CoverApiService } from './cover-api.service';

/** The cover layer's admin calls (1.29.0): paths, verbs, bodies, typed errors. */
describe('CoverApiService', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { api: TestBed.inject(CoverApiService), http: TestBed.inject(HttpTestingController) };
  }

  it('reads the options and sets / clears a choice', () => {
    const { api, http } = setup();
    api.getOptions('f 1').subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/nodes/f%201/cover-options' }).flush({});
    api.setChoice('f1', { mode: 'Archive', archiveId: 'a2' }).subscribe();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/nodes/f1/cover-choice' });
    expect(put.request.body).toEqual({ mode: 'Archive', archiveId: 'a2' });
    put.flush({ mode: 'Archive' });
    api.clearChoice('f1').subscribe();
    http.expectOne({ method: 'DELETE', url: '/api/v1/nodes/f1/cover-choice' }).flush({ mode: 'Automatic' });
    http.verify();
  });

  it('maps errors to ApiError', () => {
    const { api, http } = setup();
    let error: unknown;
    api.setChoice('f1', { mode: 'VolumeCover', volumeCoverId: 'vc1' }).subscribe({ error: (e) => (error = e) });
    http.expectOne('/api/v1/nodes/f1/cover-choice')
      .flush({ error: 'cover_not_stored', message: 'This cover has not been downloaded yet.', detail: null, correlationId: null },
        { status: 409, statusText: 'Conflict' });
    expect(error).toEqual(expect.objectContaining({ error: 'cover_not_stored' }));
  });
});
