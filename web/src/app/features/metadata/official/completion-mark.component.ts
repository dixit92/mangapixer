import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { SeriesProgressDto } from '../../../core/api/api-types';
import { completeCollectionLabel, completionSentence } from '../progress/series-progress-labels';

/**
 * The completion mark of a linked series (1.30.0; owner, like Manga-list's Completed column cross-checked with the library):
 * "Complete collection" when the folder holds a finished series whole, or the prompt "Finished - Official, English (14 volumes) -
 * you have 12" (or "Finished - Chapter-based, English ...") when a series finished in the preferred language is not held whole. Renders nothing otherwise.
 */
@Component({
  selector: 'app-completion-mark',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (text(); as t) {
      <span class="mark" [class.complete]="complete()" [class.prompt]="!complete()" data-testid="completion-mark" [matTooltip]="t">
        <mat-icon aria-hidden="true">{{ complete() ? 'workspace_premium' : 'flag' }}</mat-icon>
        <span>{{ complete() ? completeLabel() : t }}</span>
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

  readonly text = computed(() => {
    const p = this.progress();
    return p ? completionSentence(p) : null;
  });
  readonly complete = computed(() => this.progress()?.completion === 'CompleteCollection');
  readonly completeLabel = computed(() => { const p = this.progress(); return p ? completeCollectionLabel(p) : 'Complete collection'; });
}
