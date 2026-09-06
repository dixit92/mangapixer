import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

/**
 * XSRF interceptor. Reads the XSRF token from the cookie set by the server
 * and adds it as a header to all mutating requests.
 *
 * The token cookie is httpOnly-readable only by the server; the XSRF token
 * cookie is readable by JavaScript for the explicit purpose of sending it
 * back as a header. This is the double-submit cookie pattern.
 */
export const xsrfInterceptor: HttpInterceptorFn = (req, next) => {
  // Only add XSRF token to mutating requests
  const method = req.method.toUpperCase();
  if (method === 'GET' || method === 'HEAD' || method === 'OPTIONS') {
    return next(req);
  }

  // Read the XSRF token from the cookie
  const token = getXsrfTokenFromCookie();
  if (token) {
    const cloned = req.clone({
      setHeaders: { 'X-XSRF-TOKEN': token },
    });
    return next(cloned);
  }

  return next(req);
};

/**
 * Reads the XSRF token from the cookie named 'mangaplex-xsrf'.
 * This cookie is deliberately NOT httpOnly so client JS can read it.
 */
function getXsrfTokenFromCookie(): string | null {
  if (typeof document === 'undefined') return null;

  const cookies = document.cookie.split(';');
  for (const cookie of cookies) {
    const [name, value] = cookie.trim().split('=');
    if (name === 'mangaplex-xsrf') {
      return decodeURIComponent(value);
    }
  }
  return null;
}
