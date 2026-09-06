import { Injectable, inject, signal, computed } from '@angular/core';
import { Observable, tap, catchError, of } from 'rxjs';

import { ApiService } from '../api/api.service';
import { AuthUserDto, LoginRequest, ChangePasswordRequest } from '../api/api-types';

/**
 * Authentication service. Manages the current user state.
 *
 * Rules:
 * - No token in localStorage. Session uses httpOnly cookies set by the server.
 * - User state is held in memory only (signal + BehaviorSubject).
 * - Logout clears all in-memory state.
 * - Route guards check this service but are NOT the security boundary.
 *   The server enforces authorization on every request.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);

  private readonly _currentUser = signal<AuthUserDto | null>(null);
  private readonly _isAuthenticated = signal(false);

  /** Current user signal (null if not logged in). */
  readonly currentUser = this._currentUser.asReadonly();

  /** Whether the user is authenticated. */
  readonly isAuthenticated = this._isAuthenticated.asReadonly();

  /** Whether the current user is an admin. */
  readonly isAdmin = computed(() => this._currentUser()?.isAdmin ?? false);

  /**
   * Initializes auth state by checking the current session.
   * Called on app startup. Returns null on error (not authenticated)
   * so the app initializer completes successfully and routing can
   * redirect to /login.
   */
  initialize(): Observable<AuthUserDto | null> {
    return this.api.getCurrentUser().pipe(
      tap((user) => this.setUser(user)),
      catchError(() => {
        this.clearUser();
        return of(null);
      }),
    );
  }

  /**
   * Logs in with username and password.
   */
  login(request: LoginRequest): Observable<AuthUserDto> {
    return this.api.login(request).pipe(
      tap((user) => this.setUser(user)),
    );
  }

  /**
   * Logs out and clears all in-memory state.
   */
  logout(): Observable<void> {
    return this.api.logout().pipe(
      tap(() => this.clearUser()),
    );
  }

  /**
   * Changes the current user's password.
   */
  changePassword(request: ChangePasswordRequest): Observable<void> {
    return this.api.changePassword(request);
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
