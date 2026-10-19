import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatRadioModule } from '@angular/material/radio';

import { AuthorSearchCandidateDto } from '../../../core/api/api-types';
import { MAX_AUTHOR_CANDIDATES, authorCandidateDetails, authorCandidateName, authorPageUrl } from './author-search-labels';

/**
 * Author search by name (1.45.0): the authors MangaUpdates found for one folder, as a choice - name (and the other name the search
 * matched), the number of series, up to three genres and a link to the author's MangaUpdates page (opened by the admin's own browser,
 * without a referrer; no images). Nothing is chosen automatically: "None of these" until the admin picks one.
 */
@Component({
  selector: 'app-author-candidates',
  standalone: true,
  imports: [MatRadioModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-radio-group class="list" [value]="pick()" (change)="picked.emit($event.value)" [disabled]="disabled()"
                     [attr.aria-label]="'Authors on MangaUpdates for ' + label()" [attr.data-testid]="testId()">
      @for (c of shown(); track c.externalId; let i = $index) {
        <div class="cand">
          <mat-radio-button [value]="i" [attr.data-testid]="testId() + '-' + i">
            <span class="name">{{ nameOf(c) }}</span>
            @if (detailsOf(c); as d) { <span class="details">{{ d }}</span> }
          </mat-radio-button>
          @if (urlOf(c); as url) {
            <a class="page" [href]="url" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer" [attr.data-testid]="testId() + '-link-' + i">MangaUpdates page</a>
          }
        </div>
      }
      <mat-radio-button [value]="-1" [attr.data-testid]="testId() + '-none'">None of these</mat-radio-button>
    </mat-radio-group>
  `,
  styles: [`
    .list { display: flex; flex-direction: column; gap: 2px; margin-top: 4px; min-width: 0; }
    .cand { display: flex; flex-wrap: wrap; align-items: center; gap: 0 8px; min-width: 0; }
    .cand mat-radio-button { min-width: 0; max-width: 100%; }
    .name { font-size: 14px; overflow-wrap: anywhere; }
    .details { display: block; font-size: 12px; color: var(--mp-text-muted); overflow-wrap: anywhere; }
    .page { font-size: 12px; color: var(--mp-accent); margin-left: 40px; }
  `],
})
export class AuthorCandidatesComponent {
  readonly candidates = input.required<AuthorSearchCandidateDto[]>();
  /** The chosen candidate (index), -1 = none. */
  readonly pick = input(-1);
  readonly disabled = input(false);
  /** What the list is for (its aria label). */
  readonly label = input('');
  readonly testId = input('author-candidates');
  readonly picked = output<number>();

  readonly shown = computed(() => this.candidates().slice(0, MAX_AUTHOR_CANDIDATES));

  nameOf = authorCandidateName;
  detailsOf = authorCandidateDetails;
  urlOf = authorPageUrl;
}
