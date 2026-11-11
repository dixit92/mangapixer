import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { RatingSheetService } from './rating-sheet.service';
import { RatingsStateService } from './ratings-state.service';
import { isScore, ratedLabel, starsText } from './rating-scale';

/**
 * Browse bar "Rate" (1.53.0, ratings follow-up 5b; audit N5): for the folder being viewed when it has no Info button (the Info sheet
 * holds the rating otherwise), in exactly that slot - the same stroked pill, the app's one Rate icon (`stars`, audit V8), the label
 * "Rate", or the folder's own rating ("4.5") once rated. Opens the Rate sheet for the folder. Phone: icon only, 40 x 40.
 */
@Component({
  selector: 'app-folder-rate-button',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-stroked-button type="button" class="rate-btn" data-testid="folder-rate-button"
            [matTooltip]="tooltip()" [attr.aria-label]="ariaLabel()" (click)="open()">
      <mat-icon>stars</mat-icon><span class="lbl">{{ label() }}</span>
    </button>
  `,
  styles: [`
    :host { display: contents; }
    .rate-btn mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) {
      .rate-btn .lbl { display: none; }
      .rate-btn mat-icon { margin-inline: 0; }
      .rate-btn { flex: none; min-width: 40px; width: 40px; padding: 0; }
    }
  `],
})
export class FolderRateButtonComponent {
  private readonly sheet = inject(RatingSheetService);
  private readonly ratings = inject(RatingsStateService);

  /** The folder being viewed. */
  readonly nodeId = input.required<string>();
  /** Its rating as the host read it (null = not rated). */
  readonly rating = input<number | null | undefined>(null);

  readonly value = signal<number | null>(null);
  readonly label = computed(() => (isScore(this.value()) ? starsText(this.value()!) : 'Rate'));
  readonly tooltip = computed(() => (isScore(this.value()) ? `${ratedLabel(this.value()!)} - change` : 'Rate this folder'));
  readonly ariaLabel = computed(() => (isScore(this.value()) ? `Your rating ${starsText(this.value()!)} of 5 - rate this folder` : 'Rate this folder'));

  constructor() {
    effect(() => {
      const r = this.rating();
      untracked(() => this.value.set(isScore(r) ? r : null));
    });
    this.ratings.changed$.pipe(takeUntilDestroyed()).subscribe((c) => {
      if (c.nodeId === this.nodeId()) this.value.set(c.score);
    });
  }

  open(): void {
    void this.sheet.openNode(this.nodeId());
  }
}
