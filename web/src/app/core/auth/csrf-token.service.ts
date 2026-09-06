import { Injectable, inject } from '@angular/core';
import { Observable, of } from 'rxjs';
import { map, catchError } from 'rxjs/operators';

import { ApiService } from '../api/api.service';

/**
 * Holds the antiforgery request token in memory (never a cookie).
 *
 * The server issues the token as the JSON body of `GET /api/v1/auth/csrf`
 * (`IAntiforgery.GetAndStoreTokens`) and also sets an httpOnly antiforgery
 * cookie that the browser returns automatically. The double-submit pair is
 * completed by echoing this request token in the `X-MangaPlex-Csrf` header on
 * mutating requests — see {@link xsrfInterceptor}.
 *
 * The token is bound to the current identity, so it must be refreshed after any
 * sign-in transition (setup, login, logout). Reading it from `document.cookie`
 * is impossible by design (the cookie is httpOnly) and was the root cause of
 * audit finding A0 — do not reintroduce cookie parsing here.
 */
@Injectable({ providedIn: 'root' })
export class CsrfTokenService {
  private readonly api = inject(ApiService);
  private token: string | null = null;

  /** The current request token, or null if none has been fetched yet. */
  getToken(): string | null {
    return this.token;
  }

  /** Discards the in-memory token (e.g. on logout before re-fetching). */
  clear(): void {
    this.token = null;
  }

  /**
   * Fetches a fresh request token and stores it in memory. Never throws —
   * a failure resolves to `null` so app initialization and auth transitions
   * still complete; the next mutating request simply goes without a header
   * and the server rejects it, which the caller can surface.
   */
  refresh(): Observable<string | null> {
    return this.api.getCsrfToken().pipe(
      map((dto) => {
        this.token = dto.token;
        return this.token;
      }),
      catchError(() => {
        this.token = null;
        return of(null);
      }),
    );
  }
}
