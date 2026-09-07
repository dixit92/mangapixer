import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { CsrfTokenService } from './csrf-token.service';
import { ApiService } from '../api/api.service';

describe('CsrfTokenService', () => {
  let getCsrfToken: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getCsrfToken = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        CsrfTokenService,
        { provide: ApiService, useValue: { getCsrfToken } },
      ],
    });
  });

  it('starts with no token', () => {
    const service = TestBed.inject(CsrfTokenService);
    expect(service.getToken()).toBeNull();
  });

  it('stores the token fetched by refresh()', () => {
    getCsrfToken.mockReturnValue(of({ token: 'abc123' }));
    const service = TestBed.inject(CsrfTokenService);

    let emitted: string | null = 'unset';
    service.refresh().subscribe((t) => (emitted = t));

    expect(emitted).toBe('abc123');
    expect(service.getToken()).toBe('abc123');
  });

  it('resolves to null and clears the token when refresh fails (never throws)', () => {
    getCsrfToken.mockReturnValue(throwError(() => new Error('network')));
    const service = TestBed.inject(CsrfTokenService);
    // seed a stale token to prove a failed refresh clears it
    getCsrfToken.mockReturnValueOnce(of({ token: 'stale' }));
    service.refresh().subscribe();
    expect(service.getToken()).toBe('stale');

    getCsrfToken.mockReturnValue(throwError(() => new Error('network')));
    let emitted: string | null = 'unset';
    service.refresh().subscribe((t) => (emitted = t));

    expect(emitted).toBeNull();
    expect(service.getToken()).toBeNull();
  });

  it('clear() discards the in-memory token', () => {
    getCsrfToken.mockReturnValue(of({ token: 'abc123' }));
    const service = TestBed.inject(CsrfTokenService);
    service.refresh().subscribe();
    expect(service.getToken()).toBe('abc123');

    service.clear();
    expect(service.getToken()).toBeNull();
  });
});
