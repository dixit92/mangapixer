import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SeriesCommunityScoreDto } from '../../core/api/api-types';

/** "MangaUpdates community score 8.98 of 10, 1,200 ratings" - the line's accessible name and tooltip. */
export function communityScoreLabel(s: SeriesCommunityScoreDto): string {
  const votes = s.votes ? `, ${s.votes.toLocaleString('en-US')} ${s.votes === 1 ? 'rating' : 'ratings'}` : '';
  return `${s.sourceName} community score ${s.score} of 10${votes}`;
}

/**
 * 1.54.0 (community scores, owner q4 "Yes"): a series' community scores under the viewer's own stars and the server average - one short
 * line per source, "MangaUpdates 9.0", with a globe icon, not stars: the number stays the source's own 0-10 (never converted), so it cannot
 * be mistaken for the viewer's rating or this server's average. Nothing (and no space) when there is none.
 */
@Component({
  selector: 'app-community-scores',
  standalone: true,
  imports: [DecimalPipe, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (scores().length > 0) {
      <ul class="scores" data-testid="community-scores">
        @for (s of scores(); track s.source) {
          <li role="img" [attr.aria-label]="label(s)" [matTooltip]="label(s)" [attr.data-source]="s.source">
            <mat-icon aria-hidden="true">public</mat-icon>
            <span class="name" aria-hidden="true">{{ s.sourceName }}</span>
            <span class="value" aria-hidden="true">{{ s.score | number: '1.1-1' }}</span>
          </li>
        }
      </ul>
    }
  `,
  styles: [`
    :host { display: block; }
    .scores { list-style: none; margin: 4px 0 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
    li { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; color: var(--mp-text-muted); white-space: nowrap; width: fit-content; }
    mat-icon { font-size: 15px; width: 15px; height: 15px; }
    .value { font-variant-numeric: tabular-nums; color: var(--mp-text); }
  `],
})
export class CommunityScoresComponent {
  readonly scores = input<SeriesCommunityScoreDto[]>([]);
  readonly label = communityScoreLabel;
}
