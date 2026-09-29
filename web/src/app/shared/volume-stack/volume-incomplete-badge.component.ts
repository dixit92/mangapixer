import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { VolumeStackSummaryDto } from '../../core/api/api-types';

/**
 * The incomplete mark of a virtual volume stack (1.29.0): a small amber corner badge "8/10" - whole chapters present of
 * whole chapters the volume holds - shown only while chapters are missing. Extras (45.5) are never counted, so they never
 * make a volume look incomplete. Its own styles keep the near-budget browse CSS untouched; the badge sits in the cover's
 * top-left corner (a stack has no direction chip and no select check there).
 */
@Component({
  selector: 'app-volume-incomplete-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shown()) {
      <span class="incomplete" [attr.aria-label]="label()" [title]="label()" data-testid="stack-incomplete">{{ have() }}/{{ total() }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .incomplete {
      position: absolute; top: 6px; left: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 6px; border-radius: 10px;
      background: rgba(255, 179, 0, 0.95); color: #1a1200;
    }
  `],
})
export class VolumeIncompleteBadgeComponent {
  readonly summary = input.required<VolumeStackSummaryDto>();

  readonly shown = computed(() => this.summary().missingCount > 0);
  /** Whole chapters the volume holds (the provider's list or the estimated range). */
  readonly total = computed(() => this.summary().chapterCount ?? this.have() + this.summary().missingCount);
  /** Whole chapters on disk: the members minus the extras (a volume archive never has missing chapters). */
  readonly have = computed(() => Math.max(0, this.summary().presentCount - this.summary().extraCount));
  readonly label = computed(() => `${this.have()} of ${this.total()} chapters`);
}
