import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { CsrfTokenService } from './csrf-token.service';

/**
 * Attaches the antiforgery request token to mutating requests as the
 * `X-MangaPixer-Csrf` header — the header name the server validates via its
 * global `AutoValidateAntiforgeryTokenAttribute` (see Program.cs).
 *
 * The token comes from {@link CsrfTokenService} (in memory), NOT from a cookie:
 * the antiforgery cookie is httpOnly and unreadable to JS by design. The token
 * is sent back as a header while the browser returns the cookie automatically,
 * completing the double-submit pair.
 *
 * Safe methods (GET/HEAD/OPTIONS) are never modified. If no token has been
 * fetched yet the request proceeds without the header; the server rejects it
 * and the caller surfaces the error. Fixes audit finding A0 (the previous
 * interceptor read `document.cookie` and sent the wrong header name).
 */
export const xsrfInterceptor: HttpInterceptorFn = (req, next) => {
  const method = req.method.toUpperCase();
  if (method === 'GET' || method === 'HEAD' || method === 'OPTIONS') {
    return next(req);
  }

  const token = inject(CsrfTokenService).getToken();
  if (!token) {
    return next(req);
  }

  return next(
    req.clone({
      setHeaders: { 'X-MangaPixer-Csrf': token },
    }),
  );
};
