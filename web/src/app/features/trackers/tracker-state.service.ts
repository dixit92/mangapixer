import { Injectable, computed, inject, signal } from '@angular/core';

import { TrackersOverviewDto } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';

/**
 * The caller's tracker overview (1.43.0), shared by Settings > Connected accounts and the browse selection's "Sync to trackers"
 * action, so that action shows only for a user with a connected tracker. Loaded on first use (one local GET), refreshed by the
 * settings page after a connect / disconnect. Nothing here contacts a tracker.
 */
@Injectable({ providedIn: 'root' })
export class TrackerStateService {
  private readonly api = inject(TrackersApiService);
  private loading = false;

  readonly overview = signal<TrackersOverviewDto | null>(null);

  /** The trackers the caller has an account on that an admin allows (any account state). */
  readonly connected = computed(() => (this.overview()?.trackers ?? []).filter((t) => t.allowed && !!t.account));
  readonly hasConnected = computed(() => this.connected().length > 0);

  /** Loads once; later calls reuse the answer until `refresh()`. */
  ensureLoaded(): void {
    if (this.overview() || this.loading) return;
    this.refresh();
  }

  refresh(): void {
    this.loading = true;
    this.api.overview().subscribe({
      next: (o) => { this.loading = false; this.overview.set(o); },
      error: () => { this.loading = false; },
    });
  }

  set(overview: TrackersOverviewDto): void {
    this.overview.set(overview);
  }
}
