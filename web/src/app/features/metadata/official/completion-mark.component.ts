import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SeriesProgressDto } from '../../../core/api/api-types';
import { answerOf, answerSentence, completionMarkLabel } from '../progress/series-progress-labels';

/**
 * The completion mark of a linked series (1.30.0; 1.32.0 wording, owner-approved): the two "Finished" answers of the Completion tab -
 * "Finished - you have it all (Chapter-based)" when the original run ended and the folder holds the whole edition, "Finished - missing
 * some (Official, 12 of 14)" when it does not. The tooltip says why. Renders nothing for the other answers.
 */
@Component({
  selector: 'app-completion-mark',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (label(); as l) {
      <span class="mark" [class.complete]="complete()" [class.prompt]="!complete()" data-testid="completion-mark" [matTooltip]="tip()">
        <mat-icon aria-hidden="true">{{ complete() ? 'workspace_premium' : 'flag' }}</mat-icon>
        <span>{{ l }}</span>
      </span>
    }
  `,
  styles: [`
    :host { display: inline-block; max-width: 100%; }
    .mark { display: inline-flex; align-items: center; gap: 4px; padding: 0 8px; border-radius: 10px; font-size: 12px; line-height: 20px;
      max-width: 100%; }
    .mark span { overflow-wrap: anywhere; }
    .complete { background: rgba(129, 199, 132, 0.16); color: #a5d6a7; }
    .prompt { background: rgba(255, 183, 77, 0.16); color: #ffcc80; }
    mat-icon { font-size: 16px; width: 16px; height: 16px; flex: none; }
  `],
})
export class CompletionMarkComponent {
  readonly progress = input<SeriesProgressDto | null | undefined>(null);

  readonly label = computed(() => {
    const p = this.progress();
    return p ? completionMarkLabel(p) : null;
  });
  readonly tip = computed(() => {
    const p = this.progress();
    return (p ? answerSentence(p) : null) ?? '';
  });
  readonly complete = computed(() => answerOf(this.progress()) === 'HaveItAll');
}
