import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { MissingSeriesDto } from '../../../core/api/api-types';
import { MissingReportApiService } from './missing-report-api.service';
import { gapDetail, gapsOf, haveSentence } from './missing-labels';
import { folderLine, trackersLine } from '../progress/series-progress-labels';
import { CompletionMarkComponent } from '../official/completion-mark.component';

/**
 * One line on the series page for admins (1.28.0): "You have volumes 1-7 of 10 (English) · 3 behind", from the
 * missing volumes / chapters report (stored data only). Renders nothing when the folder has no own link, no
 * numbered archives, or the request fails. 1.30.0 (reach): the progress lines of the Volumes view instead - the trackers and
 * "You have volumes 1-14 + chapters 47-65 · up to date" - with the completion mark or the "finished" prompt.
 */
@Component({
  selector: 'app-series-missing-line',
  standalone: true,
  imports: [RouterLink, CompletionMarkComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (row(); as r) {
      @if (r.progress && folderLine(r.progress); as folder) {
        <div class="missing-line" data-testid="series-missing-line">
          @if (trackersLine(r.progress); as t) { <span class="trackers" data-testid="series-line-trackers">{{ t }}</span><br /> }
          <span data-testid="series-line-folder">{{ folder }}</span>
          <app-completion-mark class="mark" [progress]="r.progress" />
          @if (showReportLink()) {
            <a class="more" routerLink="/admin/metadata" [queryParams]="{ tab: 'missing' }">Missing report</a>
          }
        </div>
      } @else if (gapsOf(r).length > 0) {
        <p class="missing-line" data-testid="series-missing-line">
          @for (gap of gapsOf(r); track gap.kind; let last = $last) {
            <span>{{ haveSentence(gap) }}@if (gapDetail(gap); as d) { <span class="detail"> · {{ d }}</span> }</span>@if (!last) { <br /> }
          }
          @if (showReportLink()) {
            <a class="more" routerLink="/admin/metadata" [queryParams]="{ tab: 'missing' }">Missing report</a>
          }
        </p>
      }
    }
  `,
  styles: [`
    .missing-line { margin: 8px 0 0; font-size: 13px; color: #c8c8d4; }
    .detail { color: #ffcc80; }
    .more { margin-left: 10px; font-size: 12px; color: #b39dff; }
    .trackers { color: #9a9aa8; font-size: 12px; }
    .mark { margin-left: 8px; }
  `],
})
export class SeriesMissingLineComponent {
  private readonly api = inject(MissingReportApiService);

  readonly nodeId = input.required<string>();

  /** The link to the admin Missing report (admins only); the line itself is for everyone (1.28.0, owner). */
  readonly showReportLink = input(false);
  readonly row = signal<MissingSeriesDto | null>(null);

  readonly haveSentence = haveSentence;
  readonly gapDetail = gapDetail;
  readonly gapsOf = gapsOf;
  readonly trackersLine = trackersLine;
  readonly folderLine = folderLine;

  constructor() {
    effect(() => {
      const id = this.nodeId();
      this.row.set(null);
      this.api.forNodeViewer(id).subscribe({ next: (r) => this.row.set(r), error: () => this.row.set(null) });
    });
  }
}
