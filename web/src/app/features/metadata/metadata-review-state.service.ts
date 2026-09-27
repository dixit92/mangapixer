import { Injectable, computed, inject, signal } from '@angular/core';

import { MetadataReviewSummaryDto, MetadataSettingsDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';

/**
 * The admin's "what needs attention" counts (metadata stage 2): the review summary
 * behind the admin nav badge, the summary tile on the admin page and the tab counts
 * of `/admin/metadata`. One GET (local counts, no provider call), refreshed when a
 * surface opens and after every review / flag action, so all three agree.
 *
 * A failure (not an admin, or a server without stage 2 yet) clears the counts and
 * the badge simply does not show.
 *
 * It also holds the metadata SETTINGS snapshot the Metadata Manager summary card shows
 * (automatic matching on/off, requests today) - 1.27.0: the card used to fetch its own
 * copy once, so turning automatic matching on in the Settings tab did not reach it. The
 * Settings tab publishes every saved state here with `setSettings`.
 */
@Injectable({ providedIn: 'root' })
export class MetadataReviewStateService {
  private readonly api = inject(MetadataApiService);

  readonly summary = signal<MetadataReviewSummaryDto | null>(null);

  /** The last known metadata settings (null until loaded, or when the read failed). */
  readonly settings = signal<MetadataSettingsDto | null>(null);

  /** Needs review + open flags: the nav badge (0 hides it). */
  readonly attention = computed(() => {
    const s = this.summary();
    return s ? s.needsReview + s.openFlags : 0;
  });

  refresh(): void {
    this.api.getReviewSummary().subscribe({
      next: (s) => this.summary.set(s),
      error: () => this.summary.set(null),
    });
  }

  /** Re-reads the settings snapshot (local GET, no provider call). */
  refreshSettings(): void {
    this.api.getSettings().subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.settings.set(null),
    });
  }

  /** A surface that just saved or read the settings shares them with every other surface. */
  setSettings(s: MetadataSettingsDto): void {
    this.settings.set(s);
  }

  clear(): void {
    this.summary.set(null);
    this.settings.set(null);
  }
}
