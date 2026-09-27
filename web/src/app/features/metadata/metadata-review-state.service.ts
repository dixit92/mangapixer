import { Injectable, computed, inject, signal } from '@angular/core';

import { MetadataReviewSummaryDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';

/**
 * The admin's "what needs attention" counts (metadata stage 2): the review summary
 * behind the admin nav badge, the summary tile on the admin page and the tab counts
 * of `/admin/metadata`. One GET (local counts, no provider call), refreshed when a
 * surface opens and after every review / flag action, so all three agree.
 *
 * A failure (not an admin, or a server without stage 2 yet) clears the counts and
 * the badge simply does not show.
 */
@Injectable({ providedIn: 'root' })
export class MetadataReviewStateService {
  private readonly api = inject(MetadataApiService);

  readonly summary = signal<MetadataReviewSummaryDto | null>(null);

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

  clear(): void {
    this.summary.set(null);
  }
}
