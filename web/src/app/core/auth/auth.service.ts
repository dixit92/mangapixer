import { Injectable, inject, signal, computed } from '@angular/core';
import { Observable, tap, catchError, of, switchMap, map } from 'rxjs';

import { ApiService } from '../api/api.service';
import { CsrfTokenService } from './csrf-token.service';
import {
  AuthUserDto,
  LoginRequest,
  ChangePasswordRequest,
  SetupRequest,
} from '../api/api-types';

/**
 * Authentication service. Manages the current user state.
 *
 * Rules:
 * - No token in localStorage. Session uses httpOnly cookies set by the server.
 * - User state is held in memory only (signals).
 * - Logout clears all in-memory state.
 * - Route guards check this service but are NOT the security boundary.
 *   The server enforces authorization on every request.
 * - The antiforgery request token is refreshed on every sign-in transition
 *   (setup, login, logout) because the token is identity-bound.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);
  private readonly csrf = inject(CsrfTokenService);

  private readonly _currentUser = signal<AuthUserDto | null>(null);
  private readonly _isAuthenticated = signal(false);
  private readonly _setupRequired = signal(false);

  /** Current user signal (null if not logged in). */
  readonly currentUser = this._currentUser.asReadonly();

  /** Whether the user is authenticated. */
  readonly isAuthenticated = this._isAuthenticated.asReadonly();

  /** Whether the current user is an admin. */
  readonly isAdmin = computed(() => this._currentUser()?.isAdmin ?? false);

  /**
   * Whether the current account must change its password before using the app
   * (admin-created accounts, admin password resets). While true the server rejects
   * every non-auth request, so guards route to the change-password screen.
   */
  readonly mustChangePassword = computed(() => this._currentUser()?.forcePasswordChange ?? false);

  /**
   * Whether the instance has no users yet and must run first-run setup.
   * Populated by {@link initialize} and cleared once setup completes.
   */
  readonly setupRequired = this._setupRequired.asReadonly();

  /**
   * Initializes auth state on app startup:
   *  1. checks whether first-run setup is required,
   *  2. fetches a CSRF token so later mutations can succeed,
   *  3. resolves the current session (if any).
   * Always completes so the app initializer never blocks routing.
   */
  initialize(): Observable<AuthUserDto | null> {
    return this.api.getSetupStatus().pipe(
      catchError(() => of({ setupRequired: false })),
      tap((status) => this._setupRequired.set(status.setupRequired)),
      switchMap(() => this.csrf.refresh()),
      switchMap(() =>
        this.api.getCurrentUser().pipe(
          tap((user) => this.setUser(user)),
          catchError(() => {
            this.clearUser();
            return of(null);
          }),
        ),
      ),
    );
  }

  /**
   * First-run setup: creates the first admin, signs in, and clears the
   * setup-required flag. Refreshes the CSRF token for the new identity.
   */
  setup(request: SetupRequest): Observable<AuthUserDto> {
    return this.api.setup(request).pipe(
      tap((user) => {
        this.setUser(user);
        this._setupRequired.set(false);
      }),
      switchMap((user) => this.refreshCsrfThen(user)),
    );
  }

  /** Logs in with username and password, then refreshes the CSRF token. */
  login(request: LoginRequest): Observable<AuthUserDto> {
    return this.api.login(request).pipe(
      tap((user) => this.setUser(user)),
      switchMap((user) => this.refreshCsrfThen(user)),
    );
  }

  /** Logs out, clears in-memory state, and refreshes the (anonymous) CSRF token. */
  logout(): Observable<void> {
    return this.api.logout().pipe(
      tap(() => this.clearUser()),
      switchMap(() => this.csrf.refresh().pipe(map(() => void 0))),
    );
  }

  /** Changes the current user's password. */
  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.api.changePassword(request);
  }

  private refreshCsrfThen(user: AuthUserDto): Observable<AuthUserDto> {
    return this.csrf.refresh().pipe(map(() => user));
  }

  private setUser(user: AuthUserDto): void {
    this._currentUser.set(user);
    this._isAuthenticated.set(true);
  }

  private clearUser(): void {
    this._currentUser.set(null);
    this._isAuthenticated.set(false);
  }
}
