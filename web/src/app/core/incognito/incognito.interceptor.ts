import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { IncognitoService } from './incognito.service';

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
  if (!inject(IncognitoService).isIncognito()) {
    return next(req);
  }

  return next(
    req.clone({
      setHeaders: { 'X-Incognito': '1' },
    }),
  );
};
