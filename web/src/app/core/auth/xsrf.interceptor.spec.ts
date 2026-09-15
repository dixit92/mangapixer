import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { xsrfInterceptor } from './xsrf.interceptor';
import { CsrfTokenService } from './csrf-token.service';

const HEADER = 'X-MangaPixer-Csrf';

describe('xsrfInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let csrf: CsrfTokenService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([xsrfInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    csrf = TestBed.inject(CsrfTokenService);
  });

  afterEach(() => httpMock.verify());

  it('adds the CSRF header to a POST when a token is present', () => {
    vi.spyOn(csrf, 'getToken').mockReturnValue('tok-42');

    http.post('/api/v1/thing', {}).subscribe();

    const req = httpMock.expectOne('/api/v1/thing');
    expect(req.request.headers.get(HEADER)).toBe('tok-42');
    req.flush({});
  });

  it('does NOT add the header to a safe GET request', () => {
    vi.spyOn(csrf, 'getToken').mockReturnValue('tok-42');

    http.get('/api/v1/thing').subscribe();

    const req = httpMock.expectOne('/api/v1/thing');
    expect(req.request.headers.has(HEADER)).toBe(false);
    req.flush({});
  });

  it('proceeds without the header when no token has been fetched', () => {
    vi.spyOn(csrf, 'getToken').mockReturnValue(null);

    http.post('/api/v1/thing', {}).subscribe();

    const req = httpMock.expectOne('/api/v1/thing');
    expect(req.request.headers.has(HEADER)).toBe(false);
    req.flush({});
  });

  it('uses the service token, not a cookie value (A0 regression guard)', () => {
    // A decoy cookie must never be the header source — the antiforgery cookie
    // is httpOnly by design and reading document.cookie for the token was the
    // A0 defect. The header must equal the in-memory service token.
    vi.spyOn(document, 'cookie', 'get').mockReturnValue('XSRF-TOKEN=cookie-decoy');
    vi.spyOn(csrf, 'getToken').mockReturnValue('service-token');

    http.post('/api/v1/thing', {}).subscribe();

    const req = httpMock.expectOne('/api/v1/thing');
    expect(req.request.headers.get(HEADER)).toBe('service-token');
    expect(req.request.headers.get(HEADER)).not.toContain('cookie-decoy');
    req.flush({});
  });
});
