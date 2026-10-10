import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SeriesProgressDto } from '../../../core/api/api-types';
import {
  ANSWER_ICONS, ANSWER_LABELS, NOT_TRACKED_LABEL, answerOf, answerSentence, editionLabel, upgradeSentence,
} from '../progress/series-progress-labels';

/**
 * The chips of one series on the Completion tab (1.32.0, owner-approved wording): its answer ("Finished - you have it all",
 * "Finished - missing some", "Everything released so far", "Missing some", "Can't tell"), the edition of the two Finished answers
 * (Official, Official chapters, Chapter-based, Original run, One-shot) and "Upgrade available" - a separate flag, never an answer.
 */
@Component({
  selector: 'app-series-answer-chip',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (answer(); as a) {
      <span [class]="'chip answer ' + a" data-testid="series-answer" [matTooltip]="sentence()">
        <mat-icon aria-hidden="true">{{ icon() }}</mat-icon><span>{{ label() }}</span>
      </span>
    }
    @if (edition(); as e) {
      <span class="chip edition" data-testid="series-edition">{{ e }}</span>
    }
    @if (upgrade(); as u) {
      <span class="chip upgrade" data-testid="series-upgrade" [matTooltip]="u">
        <mat-icon aria-hidden="true">new_releases</mat-icon><span>Upgrade available</span>
      </span>
    }
  `,
  styles: [`
    :host { display: inline-flex; flex-wrap: wrap; gap: 4px 6px; align-items: center; max-width: 100%; }
    .chip { display: inline-flex; align-items: center; gap: 4px; padding: 0 8px; border-radius: 10px; font-size: 12px; line-height: 20px;
      max-width: 100%; }
    .chip span { overflow-wrap: anywhere; }
    mat-icon { font-size: 16px; width: 16px; height: 16px; flex: none; }
    .HaveItAll { background: rgb(var(--mp-success-rgb) / 0.16); color: var(--mp-success-soft); }
    .FinishedMissing { background: rgb(var(--mp-caution-rgb) / 0.16); color: var(--mp-warn); }
    .UpToDate { background: rgb(var(--mp-info-rgb) / 0.16); color: var(--mp-info); }
    .MissingSome { background: rgb(var(--mp-error-strong-rgb) / 0.16); color: var(--mp-error); }
    .CantTell { background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-muted); }
    .edition { border: 1px solid rgb(var(--mp-ink-rgb) / 0.18); color: var(--mp-text-secondary); line-height: 18px; }
    .upgrade { background: rgb(var(--mp-accent-rgb) / 0.16); color: var(--mp-accent); }
  `],
})
export class SeriesAnswerChipComponent {
  readonly progress = input<SeriesProgressDto | null | undefined>(null);

  readonly answer = computed(() => answerOf(this.progress()));
  readonly label = computed(() => {
    const a = this.answer();
    // 1.39.0: "Track completion" off for the folder.
    if (this.progress()?.trackingOff) return NOT_TRACKED_LABEL;
    return a ? ANSWER_LABELS[a] : '';
  });
  readonly icon = computed(() => { const a = this.answer(); return a ? ANSWER_ICONS[a] : ''; });
  readonly sentence = computed(() => { const p = this.progress(); return (p ? answerSentence(p) : null) ?? ''; });
  readonly edition = computed(() => { const p = this.progress(); return p ? editionLabel(p) : null; });
  readonly upgrade = computed(() => { const p = this.progress(); return p ? upgradeSentence(p) : null; });
}
