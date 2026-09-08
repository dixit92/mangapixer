import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';

import { AuthService } from './auth.service';

/**
 * Auth guard for the authenticated app shell. Redirects to first-run setup
 * when the instance has no users yet, otherwise to /login when unauthenticated.
 *
 * IMPORTANT: Guards are NOT the security boundary. The server enforces
 * authorization on every API request. Guards only improve UX by preventing
 * flash-of-content for unauthenticated users.
 */
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.setupRequired()) {
    router.navigate(['/setup']);
    return false;
  }

  if (auth.isAuthenticated()) {
    // A signed-in account with a temporary password can only change it — the
    // server rejects everything else, so route there instead of a broken shell.
    if (auth.mustChangePassword()) {
      router.navigate(['/password-change']);
      return false;
    }
    return true;
  }

  router.navigate(['/login']);
  return false;
};

/**
 * Admin guard. Redirects to / if authenticated but not admin.
 * Also redirects to /login (or /setup) if not authenticated.
 */
export const adminGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.setupRequired()) {
    router.navigate(['/setup']);
    return false;
  }

  if (!auth.isAuthenticated()) {
    router.navigate(['/login']);
    return false;
  }

  if (auth.mustChangePassword()) {
    router.navigate(['/password-change']);
    return false;
  }

  if (!auth.isAdmin()) {
    router.navigate(['/']);
    return false;
  }

  return true;
};

/**
 * Guards the /setup route so it is reachable only while first-run setup is
 * pending. Once any user exists (or the current user is signed in) it bounces
 * to the normal entry points, so the wizard cannot be reused to create a
 * second admin.
 */
export const setupGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.setupRequired()) {
    return true;
  }

  router.navigate([auth.isAuthenticated() ? '/' : '/login']);
  return false;
};

/**
 * Guards the /login route so a fresh instance sends the user to /setup instead
 * of a login form they cannot yet use.
 */
export const loginGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.setupRequired()) {
    router.navigate(['/setup']);
    return false;
  }

  return true;
};

/**
 * Guards /password-change so it is reachable only by a signed-in account that
 * actually must change its password. Everyone else is bounced to the right place,
 * so the screen can't be used to change a password out of band.
 */
export const passwordChangeGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (!auth.isAuthenticated()) {
    router.navigate([auth.setupRequired() ? '/setup' : '/login']);
    return false;
  }

  if (!auth.mustChangePassword()) {
    router.navigate(['/']);
    return false;
  }

  return true;
};
