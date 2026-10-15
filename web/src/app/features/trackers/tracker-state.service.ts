import { Injectable, computed, inject, signal } from '@angular/core';

import { TrackerAttentionDto, TrackersOverviewDto } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';

/**
 * The caller's tracker overview and attention count (1.43.0), shared by the Tracking page, the account menu's "Tracking" entry and
 * its badge, the series header's Tracking button and the browse selection's "Sync to trackers" action (shown only for a user with a
 * connected tracker). Loaded on first use (local GETs only), refreshed by the Tracking page after a connect / disconnect / action
 * and by the account menu when it opens. Nothing here contacts a tracker.
 */
@Injectable({ providedIn: 'root' })
export class TrackerStateService {
  private readonly api = inject(TrackersApiService);
  private loading = false;
  private attentionLoading = false;

  readonly overview = signal<TrackersOverviewDto | null>(null);
  readonly attentionDto = signal<TrackerAttentionDto | null>(null);

  /** The trackers the caller has an account on that an admin allows (any account state). */
  readonly connected = computed(() => (this.overview()?.trackers ?? []).filter((t) => t.allowed && !!t.account));
  readonly hasConnected = computed(() => this.connected().length > 0);
  /** The account menu lists "Tracking" once a tracker is allowed on this server, or the user still has an account on one. */
  readonly pageAvailable = computed(() => (this.overview()?.trackers ?? []).some((t) => t.allowed || !!t.account));
  /** Series waiting for the user + series a tracker is ahead on (the menu badge; 0 hides it). */
  readonly attention = computed(() => this.attentionDto()?.total ?? 0);

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

  /** Re-reads the badge count (one local GET); skipped while one is in flight. */
  refreshAttention(): void {
    if (this.attentionLoading) return;
    this.attentionLoading = true;
    this.api.attention().subscribe({
      next: (a) => { this.attentionLoading = false; this.attentionDto.set(a); },
      error: () => { this.attentionLoading = false; },
    });
  }

  /** Signed out: forget everything (the next user loads their own). */
  clear(): void {
    this.overview.set(null);
    this.attentionDto.set(null);
  }
}
