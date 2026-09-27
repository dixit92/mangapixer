import { Injectable, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';

/**
 * The per-user "Series information on hover" option (1.27.0) on the client: stored
 * server-side in the library-view preferences (`seriesInfoOnHover`, default ON), loaded
 * once per signed-in user the first time a hover zone appears, and updated by Settings
 * as soon as the user changes it. Until it is loaded the hover stays off.
 */
@Injectable({ providedIn: 'root' })
export class SeriesInfoHoverPreferenceService {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);

  private readonly value = signal<boolean | null>(null);
  private userId: string | null = null;
  private loading = false;
  private readonly userChanged = new Subject<void>();

  /** Emits when a different user is seen (the controller drops its cache then). */
  readonly userChanged$ = this.userChanged.asObservable();

  enabled(): boolean {
    return this.value() === true;
  }

  /** Settings: the option changed (after an optimistic save, or its revert). */
  set(value: boolean): void {
    this.syncUser();
    this.value.set(value);
  }

  /** Loads the option once per signed-in user. */
  ensureLoaded(): void {
    this.syncUser();
    if (!this.userId || this.value() !== null || this.loading) return;
    this.loading = true;
    const forUser = this.userId;
    this.api.getLibraryPreferences().subscribe({
      next: (p) => {
        this.loading = false;
        if (forUser === this.userId && this.value() === null) this.value.set(p.seriesInfoOnHover ?? true);
      },
      error: () => { this.loading = false; },
    });
  }

  /** Another user signing in on the same tab gets their own option. */
  private syncUser(): void {
    const id = this.auth.currentUser()?.id ?? null;
    if (id === this.userId) return;
    this.userId = id;
    this.value.set(null);
    this.loading = false;
    this.userChanged.next();
  }
}
