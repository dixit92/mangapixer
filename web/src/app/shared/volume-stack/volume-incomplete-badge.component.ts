import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { VolumeStackSummaryDto } from '../../core/api/api-types';
import { DUPLICATE_TIP, duplicateListText, duplicatesLabel } from '../duplicate-units';

/**
 * The incomplete mark of a virtual volume stack (1.29.0): a small amber corner badge "8/10" - whole chapters present of
 * whole chapters the volume holds - shown only while chapters are missing. Extras (45.5) are never counted, so they never
 * make a volume look incomplete. Its own styles keep the near-budget browse CSS untouched; the badge sits in the cover's
 * top-left corner, and moves to the bottom-left while its card is being selected (`moved`, 1.30.0: the select check owns
 * top-left then; a stack has no (i) there). A split chapter (4.1 + 4.2) counts once.
 * 1.31.0: the same chapter in more than one file of the volume (uploaded twice) gets a second chip, "2 duplicates" - below the
 * mark, or alone when nothing is missing; the "8/10" itself counts each chapter once. The chip is in this component so every
 * place a stack card is drawn (browse card, list row) shows it without a template change.
 */
@Component({
  selector: 'app-volume-incomplete-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shown()) {
      <span class="incomplete" [class.moved]="moved()" [attr.aria-label]="label()" [title]="label()" data-testid="stack-incomplete">{{ have() }}/{{ total() }}</span>
    }
    @if (dupCount() > 0) {
      <span class="dup" [class.below]="shown()" [class.moved]="moved()" [attr.aria-label]="dupTitle()" [title]="dupTitle()"
            data-testid="stack-duplicates">{{ dupCount() }} duplicate{{ dupCount() === 1 ? '' : 's' }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .incomplete {
      position: absolute; top: 6px; left: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 6px; border-radius: 10px;
      background: rgba(255, 179, 0, 0.95); color: #1a1200;
    }
    .incomplete.moved { top: auto; bottom: 6px; }
    .dup {
      position: absolute; top: 6px; left: 6px; z-index: 2; max-width: calc(100% - 12px); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      font-size: 11px; font-weight: 600; padding: 1px 6px; border-radius: 10px; background: rgba(38, 50, 56, 0.92); color: #ffcc80;
      border: 1px solid rgba(255, 204, 128, 0.4);
    }
    .dup.below { top: 30px; }
    .dup.moved { top: auto; bottom: 6px; }
    .dup.moved.below { bottom: 30px; }
  `],
})
export class VolumeIncompleteBadgeComponent {
  readonly summary = input.required<VolumeStackSummaryDto>();

  /** Sit in the bottom-left corner: the card is in select mode and the select check takes the top-left one. */
  readonly moved = input(false);

  readonly shown = computed(() => this.summary().missingCount > 0);
  /** Whole chapters the volume holds (the provider's list or the estimated range). */
  readonly total = computed(() => this.summary().chapterCount ?? this.have() + this.summary().missingCount);
  /**
   * Complete chapters here: the server's count (a split chapter counts once, when all its listed parts are here); else the members
   * minus the extras.
   */
  readonly have = computed(() => this.summary().chaptersPresent ?? Math.max(0, this.summary().presentCount - this.summary().extraCount));
  readonly label = computed(() => `${this.have()} of ${this.total()} chapters`);
  readonly dupCount = computed(() => this.summary().duplicates?.length ?? 0);
  readonly dupTitle = computed(() => `${duplicatesLabel(this.summary().duplicates)}: ${duplicateListText(this.summary().duplicates)}. ${DUPLICATE_TIP}`);
}
