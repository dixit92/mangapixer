import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { MissingSeriesDto } from '../../../core/api/api-types';
import { MissingReportApiService } from './missing-report-api.service';
import { gapDetail, gapsOf, haveSentence } from './missing-labels';

/**
 * One line on the series page for admins (1.28.0): "You have volumes 1-7 of 10 (English) · 3 behind", from the
 * missing volumes / chapters report (stored data only). Renders nothing when the folder has no own link, no
 * numbered archives, or the request fails.
 */
@Component({
  selector: 'app-series-missing-line',
  standalone: true,
  imports: [RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (row(); as r) {
      @if (gapsOf(r).length > 0) {
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

  constructor() {
    effect(() => {
      const id = this.nodeId();
      this.row.set(null);
      this.api.forNodeViewer(id).subscribe({ next: (r) => this.row.set(r), error: () => this.row.set(null) });
    });
  }
}
