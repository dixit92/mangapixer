import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';

import { BYPASS_INCOGNITO, incognitoInterceptor } from './incognito.interceptor';
import { IncognitoService } from './incognito.service';

const HEADER = 'X-Incognito';

describe('incognitoInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let incognito: IncognitoService;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([incognitoInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    incognito = TestBed.inject(IncognitoService);
  });

  afterEach(() => httpMock.verify());

  it('adds the header by default (incognito is ON each session)', () => {
    http.get('/api/v1/libraries').subscribe();

    const req = httpMock.expectOne('/api/v1/libraries');
    expect(req.request.headers.get(HEADER)).toBe('1');
    req.flush({});
  });

  it('adds the header on a GET — discovery surfaces are reads', () => {
    incognito.setIncognito(true);

    http.get('/api/v1/reading/continue').subscribe();

    const req = httpMock.expectOne('/api/v1/reading/continue');
    expect(req.request.headers.get(HEADER)).toBe('1');
    req.flush({});
  });

  it('omits the header once incognito is turned off', () => {
    incognito.setIncognito(false);

    http.get('/api/v1/libraries').subscribe();

    const req = httpMock.expectOne('/api/v1/libraries');
    expect(req.request.headers.has(HEADER)).toBe(false);
    req.flush({});
  });

  it('reflects a live toggle between requests', () => {
    incognito.setIncognito(false);
    http.get('/api/v1/a').subscribe();
    const first = httpMock.expectOne('/api/v1/a');
    expect(first.request.headers.has(HEADER)).toBe(false);
    first.flush({});

    incognito.toggle();
    http.get('/api/v1/b').subscribe();
    const second = httpMock.expectOne('/api/v1/b');
    expect(second.request.headers.get(HEADER)).toBe('1');
    second.flush({});
  });

  it('omits the header when BYPASS_INCOGNITO is set, even while incognito is on', () => {
    incognito.setIncognito(true);

    http.get('/api/v1/libraries', { context: new HttpContext().set(BYPASS_INCOGNITO, true) }).subscribe();

    const req = httpMock.expectOne('/api/v1/libraries');
    expect(req.request.headers.has(HEADER)).toBe(false);
    req.flush({});
  });
});
