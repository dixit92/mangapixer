import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';

import { MissingSeriesDto } from '../../../core/api/api-types';
import { DUPLICATE_TIP, duplicateCountLabel, duplicateListText, duplicateTotals } from '../../../shared/duplicate-units';
import { MissingReportApiService } from './missing-report-api.service';
import { gapDetail, gapsOf, haveSentence } from './missing-labels';
import { folderLine, trackersLine } from '../progress/series-progress-labels';
import { CompletionMarkComponent } from '../official/completion-mark.component';
import { DeclaredFactsApiService } from '../declared/declared-facts-api.service';

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
          @if (dupText(r); as dup) { <br /><span class="dup" data-testid="series-line-duplicates" [title]="dupTip">{{ dup }}</span> }
          @if (showReportLink()) {
            <a class="more" routerLink="/admin/metadata" [queryParams]="{ tab: 'missing' }">Missing report</a>
          }
        </div>
      } @else if (gapsOf(r).length > 0 || dupText(r)) {
        <p class="missing-line" data-testid="series-missing-line">
          @for (gap of gapsOf(r); track gap.kind; let last = $last) {
            <span>{{ haveSentence(gap) }}@if (gapDetail(gap); as d) { <span class="detail"> · {{ d }}</span> }</span>@if (!last) { <br /> }
          }
          @if (dupText(r); as dup) { @if (gapsOf(r).length > 0) { <br /> }<span class="dup" data-testid="series-line-duplicates" [title]="dupTip">{{ dup }}</span> }
          @if (showReportLink()) {
            <a class="more" routerLink="/admin/metadata" [queryParams]="{ tab: 'missing' }">Missing report</a>
          }
        </p>
      }
    }
  `,
  styles: [`
    .missing-line { margin: 8px 0 0; font-size: 13px; color: var(--mp-text-secondary); }
    .detail { color: var(--mp-warn); }
    .dup { color: var(--mp-warn); font-size: 12px; overflow-wrap: anywhere; }
    .more { margin-left: 10px; font-size: 12px; color: var(--mp-accent); }
    .trackers { color: var(--mp-text-muted); font-size: 12px; }
    .mark { margin-left: 8px; }
  `],
})
export class SeriesMissingLineComponent {
  private readonly api = inject(MissingReportApiService);
  /** 1.39.0: a saved declaration (edition volumes, track completion) changes the answers - read the line again. */
  private readonly declared = inject(DeclaredFactsApiService);

  readonly nodeId = input.required<string>();

  /** The link to the admin Missing report (admins only); the line itself is for everyone (1.28.0, owner). */
  readonly showReportLink = input(false);
  readonly row = signal<MissingSeriesDto | null>(null);

  readonly haveSentence = haveSentence;
  readonly gapDetail = gapDetail;
  readonly gapsOf = gapsOf;
  readonly trackersLine = trackersLine;
  readonly dupTip = DUPLICATE_TIP;
  readonly folderLine = folderLine;

  /** 1.31.0: "2 duplicate chapters: Chapter 1: 2 files, Chapter 2: 2 files"; '' when the same number sits in only one file. */
  dupText(r: MissingSeriesDto): string {
    const list = r.duplicates ?? [];
    if (list.length === 0) return '';
    const t = duplicateTotals(list);
    return `${duplicateCountLabel(t.chapters, t.volumes)}: ${duplicateListText(list, r.duplicateCount)}`;
  }

  constructor() {
    effect(() => {
      const id = this.nodeId();
      this.declared.version();
      untracked(() => {
        this.row.set(null);
        this.api.forNodeViewer(id).subscribe({ next: (r) => this.row.set(r), error: () => this.row.set(null) });
      });
    });
  }
}
