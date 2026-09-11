import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { IncognitoService } from './incognito.service';

/**
 * Set on a request's `HttpContext` to skip the `X-Incognito` header even while
 * incognito mode is on. Use only for a management surface where the user must
 * always see everything they can manage regardless of the session's current
 * Incognito toggle — e.g. the Private-libraries settings list, which would
 * otherwise hide a library the moment it's marked Private, making it
 * impossible to un-mark without first turning Incognito off.
 */
export const BYPASS_INCOGNITO = new HttpContextToken<boolean>(() => false);

/**
 * Attaches the `X-Incognito` header to every request while incognito mode is on
 * (see {@link IncognitoService}). The server reads it only where it matters — the
 * listing/discovery surfaces (continue-reading, search, browse-root, library
 * list) subtract the user's Private libraries when the header is present — so
 * sending it on every request is harmless and keeps the client simple.
 *
 * Unlike {@link xsrfInterceptor} this also applies to GET requests: the surfaces
 * being filtered are reads. Per-item access (reader manifest/pages) is NOT gated
 * by this header server-side, so a direct reader URL still opens while incognito.
 */
export const incognitoInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.context.get(BYPASS_INCOGNITO) || !inject(IncognitoService).isIncognito()) {
    return next(req);
  }

  return next(
    req.clone({
      setHeaders: { 'X-Incognito': '1' },
    }),
  );
};
