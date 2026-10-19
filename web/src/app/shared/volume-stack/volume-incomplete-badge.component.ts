import { ChangeDetectionStrategy, Component, computed, input, viewChild } from '@angular/core';
import { MatTooltip, MatTooltipModule } from '@angular/material/tooltip';

import { VolumeStackSummaryDto } from '../../core/api/api-types';
import { DUPLICATE_TIP, duplicateListText, duplicatesLabel } from '../duplicate-units';

/**
 * The incomplete mark of a virtual volume stack (1.29.0): a small amber corner badge "8 of 10 ch" (1.44.0; was "8/10") - whole chapters present of
 * whole chapters the volume holds - shown only while chapters are missing. Extras (45.5) are never counted, so they never
 * make a volume look incomplete. Its own styles keep the near-budget browse CSS untouched; the badge sits in the cover's
 * top-left corner, and moves to the bottom-left while its card is being selected (`moved`, 1.30.0: the select check owns
 * top-left then; a stack has no (i) there). A split chapter (4.1 + 4.2) counts once.
 * 1.31.0: the same chapter in more than one file of the volume (uploaded twice) gets a second chip, "2 duplicates" - below the
 * mark, or alone when nothing is missing; the "8/10" itself counts each chapter once. The chip is in this component so every
 * place a stack card is drawn (browse card, list row) shows it without a template change.
 * 1.45.0 (UX review #12): the explanation is a tooltip (hover / focus) instead of a `title` only, and on a touch screen a tap on the
 * badge itself shows it for a few seconds instead of opening the volume (outside select mode; a tap anywhere else opens it as before).
 */
@Component({
  selector: 'app-volume-incomplete-badge',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatTooltipModule],
  // The tap is caught on the host (the badges sit inside the card's link, so they are not focusable controls of their own).
  host: { '(pointerdown)': 'pressed($event)', '(click)': 'tapped($event)' },
  template: `
    @if (shown()) {
      <span class="incomplete" [class.moved]="moved()" [attr.aria-label]="label()" [matTooltip]="label()" matTooltipTouchGestures="off"
            #tip="matTooltip"
            data-testid="stack-incomplete">{{ compact() ? have() + '/' + total() : have() + ' of ' + total() + ' ch' }}</span>
    }
    @if (dupCount() > 0) {
      <span class="dup" [class.below]="shown()" [class.moved]="moved()" [attr.aria-label]="dupTitle()" [matTooltip]="dupTitle()"
            matTooltipTouchGestures="off" #dupTip="matTooltip"
            data-testid="stack-duplicates">{{ dupCount() }} duplicate{{ dupCount() === 1 ? '' : 's' }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .incomplete {
      position: absolute; top: 6px; left: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 6px; border-radius: 10px;
      background: rgb(var(--mp-warn-strong-rgb) / 0.95); color: var(--mp-on-warn);
    }
    .incomplete.moved { top: auto; bottom: 6px; }
    .dup {
      position: absolute; top: 6px; left: 6px; z-index: 2; max-width: calc(100% - 12px); white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      font-size: 11px; font-weight: 600; padding: 1px 6px; border-radius: 10px; background: rgb(var(--mp-shade-rgb) / 0.92); color: #ffcc80; /* theme-exempt: amber text on the always-dark pill over cover art (--mp-warn turns dark in light themes) */
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

  /** 1.44.0 (UX review): the list row's small thumbnail shows "8/9" (its subtitle spells it out); cards show "8 of 9 ch". */
  readonly compact = input(false);

  readonly shown = computed(() => this.summary().missingCount > 0);
  /** Whole chapters the volume holds (the provider's list or the estimated range). */
  readonly total = computed(() => this.summary().chapterCount ?? this.have() + this.summary().missingCount);
  /**
   * Complete chapters here: the server's count (a split chapter counts once, when all its listed parts are here); else the members
   * minus the extras.
   */
  readonly have = computed(() => this.summary().chaptersPresent ?? Math.max(0, this.summary().presentCount - this.summary().extraCount));
  /** 1.44.0 (owner): what the two numbers are - "8 of 9 ch" alone read like a file count. */
  readonly label = computed(() => {
    const missing = this.summary().missingCount;
    return `${this.have()} of the ${this.total()} chapters of this volume are here; ${missing} ${missing === 1 ? 'is' : 'are'} missing`;
  });
  readonly dupCount = computed(() => this.summary().duplicates?.length ?? 0);
  readonly dupTitle = computed(() => `${duplicatesLabel(this.summary().duplicates)}: ${duplicateListText(this.summary().duplicates)}. ${DUPLICATE_TIP}`);

  /** How long a tapped badge's explanation stays up on a touch screen. */
  static readonly TAP_TIP_MS = 4000;
  private lastPointer = '';
  private readonly tip = viewChild('tip', { read: MatTooltip });
  private readonly dupTip = viewChild('dupTip', { read: MatTooltip });

  pressed(event: PointerEvent): void {
    this.lastPointer = event.pointerType ?? '';
  }

  /**
   * A touch tap on the badge explains it (the tooltip, for a few seconds) instead of opening the volume. A mouse click, and any tap
   * while selecting (the card toggles then), go on to the card as before.
   */
  tapped(event: Event): void {
    const touch = this.lastPointer === 'touch' || this.lastPointer === 'pen';
    this.lastPointer = '';
    const badge = (event.target as Element | null)?.closest?.('.incomplete, .dup');
    const tip = badge?.classList.contains('dup') ? this.dupTip() : badge ? this.tip() : undefined;
    if (!touch || this.moved() || !tip) return;
    event.preventDefault();
    event.stopPropagation();
    tip.show(0);
    tip.hide(VolumeIncompleteBadgeComponent.TAP_TIP_MS);
  }
}
