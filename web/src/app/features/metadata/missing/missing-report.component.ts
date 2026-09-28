import { ChangeDetectionStrategy, Component, OnInit, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

import { MissingReportSummaryDto, MissingSeriesDto } from '../../../core/api/api-types';
import { ReviewLibraryOption } from '../review/review-dashboard.component';
import { MissingReportApiService } from './missing-report-api.service';
import {
  MISSING_CONFIDENCE_LABELS, MISSING_VERDICT_LABELS, gapDetail, gapsOf, haveSentence, noVerdictReason,
} from './missing-labels';

type Filter = 'missing' | 'all';

/**
 * Missing tab of `/admin/metadata` (1.28.0): every folder linked to a series, its highest volume / chapter
 * number on disk against the total its stored record states (English publisher first, then the country of
 * origin, then the latest chapter), holes in the local numbering, and where the total came from. Built from
 * stored data only - opening the tab never contacts a provider. Default filter: behind or with gaps.
 */
@Component({
  selector: 'app-missing-report',
  standalone: true,
  imports: [
    RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressSpinnerModule, MatSelectModule,
    MatTooltipModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="missing" data-testid="missing-report">
      <p class="intro">Compares the volume and chapter numbers in your archive names with the totals of each linked series'
        stored record. Nothing is fetched to build this list; totals update when records refresh.</p>
      <div class="toolbar">
        <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" aria-label="Which series" hideSingleSelectionIndicator>
          <mat-button-toggle value="missing" data-testid="missing-filter-missing">Behind or with gaps</mat-button-toggle>
          <mat-button-toggle value="all" data-testid="missing-filter-all">All linked series</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>

      @if (summary(); as s) {
        <p class="summary" data-testid="missing-summary">
          {{ s.series }} linked series · <span class="behind">{{ s.behind }} behind</span> · {{ s.holes }} with gaps ·
          {{ s.upToDate }} up to date @if (s.noTotal) { · {{ s.noTotal }} without a total } @if (s.noVerdict) { · {{ s.noVerdict }} not comparable }
        </p>
      }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (row of items(); track row.nodeId) {
            <article class="row" [class]="'v-' + row.verdict" data-testid="missing-row">
              @if (row.coverUrl) {
                <img class="cover" [src]="row.coverUrl" alt="" loading="lazy" />
              } @else {
                <div class="cover none"><mat-icon>folder</mat-icon></div>
              }
              <div class="body">
                <header>
                  <a class="name" [routerLink]="['/series', row.nodeId]" data-testid="missing-series-link">{{ row.displayName }}</a>
                  <span class="verdict" data-testid="missing-verdict">{{ verdictLabels[row.verdict] }}</span>
                </header>
                <p class="meta">{{ row.recordTitle }} · {{ row.libraryName }}@if (row.linkState === 'Auto') { · <span class="auto">automatic link</span> }</p>
                @for (gap of gapsOf(row); track gap.kind) {
                  <p class="gap" data-testid="missing-gap">
                    <span class="have">{{ haveSentence(gap) }}</span>
                    @if (gapDetail(gap); as d) { <span class="detail">{{ d }}</span> }
                    @if (gap.confidence; as c) {
                      <mat-icon class="conf" [class]="'c-' + c" [matTooltip]="'Total from the ' + confidenceLabels[c]"
                                [attr.aria-label]="'Total from the ' + confidenceLabels[c]">info</mat-icon>
                    }
                  </p>
                }
                @if (noVerdictReason(row); as reason) { <p class="note">{{ reason }}</p> }
                @if (row.englishTotalUnknown) {
                  <p class="note" data-testid="missing-english-unknown">An English edition is listed; its total is read on the record's next refresh.</p>
                }
              </div>
              <a mat-icon-button class="open" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]" aria-label="Browse folder">
                <mat-icon>folder_open</mat-icon>
              </a>
            </article>
          } @empty {
            <div class="empty" data-testid="missing-empty">
              <mat-icon>library_add_check</mat-icon>
              <p>{{ filter() === 'missing' ? 'Nothing behind or with gaps among the linked series.' : 'No linked series folders yet.' }}</p>
            </div>
          }
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)" data-testid="missing-more">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    .intro { margin: 0 0 12px; color: #9a9aa8; font-size: 13px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; margin-bottom: 8px; }
    .lib-filter { width: 220px; }
    .summary { margin: 4px 0 12px; font-size: 13px; color: #c8c8d4; }
    .summary .behind { color: #ffb74d; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.06); border-left: 3px solid #555; }
    .row.v-Behind { border-left-color: #ffb74d; }
    .row.v-Holes { border-left-color: #ff8a80; }
    .row.v-UpToDate { border-left-color: #81c784; }
    .cover { width: 48px; height: 68px; object-fit: cover; border-radius: 4px; flex: none; background: #2a2a36; }
    .cover.none { display: flex; align-items: center; justify-content: center; color: #6a6a78; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    .name:hover { text-decoration: underline; }
    .verdict { padding: 0 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.08); font-size: 12px; line-height: 20px; }
    .v-Behind .verdict { background: rgba(255, 183, 77, 0.18); color: #ffcc80; }
    .v-Holes .verdict { background: rgba(244, 67, 54, 0.18); color: #ff8a80; }
    .v-UpToDate .verdict { background: rgba(129, 199, 132, 0.16); color: #a5d6a7; }
    .meta { margin: 2px 0 4px; font-size: 12px; color: #9a9aa8; overflow-wrap: anywhere; }
    .auto { color: #ffcc80; }
    .gap { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; margin: 2px 0; font-size: 13px; }
    .detail { color: #ffcc80; }
    .conf { font-size: 16px; width: 16px; height: 16px; color: #8a8a99; cursor: help; }
    .conf.c-Low { color: #ff8a80; }
    .note { margin: 2px 0; font-size: 12px; color: #9a9aa8; }
    .open { flex: none; color: #b39dff; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: #8a8a99; text-align: center; }
    .more { display: flex; justify-content: center; margin-top: 12px; }
    @media (max-width: 599.98px) {
      .lib-filter { width: 100%; }
      .cover { width: 40px; height: 56px; }
      .open { display: none; }
    }
  `],
})
export class MissingReportComponent implements OnInit {
  private readonly api = inject(MissingReportApiService);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialLibrary = input<string | null>(null);

  readonly filter = signal<Filter>('missing');
  readonly library = signal<string | null>(null);
  readonly items = signal<MissingSeriesDto[]>([]);
  readonly summary = signal<MissingReportSummaryDto | null>(null);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly verdictLabels = MISSING_VERDICT_LABELS;
  readonly confidenceLabels = MISSING_CONFIDENCE_LABELS;
  readonly haveSentence = haveSentence;
  readonly gapDetail = gapDetail;
  readonly gapsOf = gapsOf;
  readonly noVerdictReason = noVerdictReason;

  ngOnInit(): void {
    this.library.set(this.initialLibrary());
    this.load(false);
  }

  setFilter(filter: Filter): void {
    this.filter.set(filter);
    this.load(false);
  }

  setLibrary(id: string | null): void {
    this.library.set(id);
    this.load(false);
  }

  load(more: boolean): void {
    if (!more) {
      this.loading.set(true);
      this.cursor.set(null);
    }
    this.error.set(null);
    this.api.list(this.library(), this.filter() === 'missing', more ? this.cursor() : null).subscribe({
      next: (page) => {
        this.items.set(more ? [...this.items(), ...page.items] : page.items);
        this.summary.set(page.summary);
        this.cursor.set(page.nextCursor ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The report could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}
