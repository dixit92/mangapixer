import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { ApiService } from './api.service';
import { UpdateProgressRequest } from './api-types';

describe('ApiService.updateProgress (D32 idempotency headers)', () => {
  let api: ApiService;
  let httpMock: HttpTestingController;

  const request: UpdateProgressRequest = {
    pageIndex: 3,
    expectedContentVersion: 1,
    mutationId: 'm-1',
    entryKey: 'p3',
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(ApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('sends If-None-Match:* for the first write of an unread item (revision 0)', () => {
    api.updateProgress('item-1', request, 0).subscribe();

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.headers.get('If-None-Match')).toBe('*');
    expect(req.request.headers.has('If-Match')).toBe(false);
    req.flush({ revision: 1, alreadyApplied: false });
  });

  it('sends If-Match with the known revision for a subsequent write', () => {
    api.updateProgress('item-1', request, 7).subscribe();

    const req = httpMock.expectOne('/api/v1/reading/progress/item-1');
    expect(req.request.headers.get('If-Match')).toBe('"7"');
    expect(req.request.headers.has('If-None-Match')).toBe(false);
    req.flush({ revision: 8, alreadyApplied: false });
  });
});
