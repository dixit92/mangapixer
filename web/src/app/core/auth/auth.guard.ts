import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';

/**
 * Auth guard. Redirects to /login if not authenticated.
 *
 * IMPORTANT: Guards are NOT the security boundary. The server enforces
 * authorization on every API request. Guards only improve UX by
 * preventing flash-of-content for unauthenticated users.
 */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  router.navigate(['/login']);
  return false;
};

/**
 * Admin guard. Redirects to / if authenticated but not admin.
 * Also redirects to /login if not authenticated.
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    router.navigate(['/login']);
    return false;
  }

  if (!auth.isAdmin()) {
    router.navigate(['/']);
    return false;
  }

  return true;
};
