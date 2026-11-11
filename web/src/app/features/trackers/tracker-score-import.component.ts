import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';

import { TrackerScoreDifferenceDto, TrackerScoreImportResultDto } from '../../core/api/api-types';
import { starsText } from '../ratings/rating-scale';
import { TrackersApiService } from './trackers-api.service';
import { failureText, scoreImportNotice } from './tracker-texts';

/** One listed difference and whether the user took the tracker's score for it. */
interface DifferRow extends TrackerScoreDifferenceDto {
  used: boolean;
}

/**
 * "Take my scores from <tracker>" on the Tracking page's account card (1.53.0, lane T): one click reads the user's own scores on the
 * tracker; series without a MangaPixer rating get the tracker's score, the same ones stay, and the ones that differ are KEPT and listed
 * here - each with "Use <tracker>'s" (the user's choice; nothing is overwritten silently). Nothing is changed on the tracker.
 * `offer` (set right after the user turned "Keep my score in step" on) adds a one-line invitation above the button. `showButton` false:
 * the host renders the button itself (the account card puts it next to Disconnect) and calls `run()`.
 */
@Component({
  selector: 'app-tracker-score-import',
  standalone: true,
  imports: [MatButtonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  // Nothing to show (the host renders the button): no box, so the card keeps its height.
  host: { '[class.idle]': 'idle()' },
  template: `
    <div class="import" [attr.data-testid]="'tracker-score-import-' + slug()">
      @if (offer() && !result()) {
        <p class="muted small" [attr.data-testid]="'tracker-score-offer-' + slug()">
          Give the series you have not rated here your {{ name() }} scores too?
        </p>
      }
      @if (showButton()) {
        <div class="actions">
          <button mat-stroked-button type="button" [disabled]="busy() || disabled()" (click)="run()"
                  [attr.data-testid]="'tracker-import-scores-' + slug()">Take my scores from {{ name() }}</button>
        </div>
      }
      @if (notice(); as n) { <p class="ok" role="status" [attr.data-testid]="'tracker-score-summary-' + slug()">{{ n }}</p> }
      @if (rows().length > 0) {
        <ul class="differ" [attr.aria-label]="'Ratings that differ from ' + name()" [attr.data-testid]="'tracker-score-differ-' + slug()">
          @for (r of rows(); track r.seriesId) {
            <li class="row" [attr.data-testid]="'tracker-score-row-' + r.seriesId">
              <span class="title-cell">
                <a class="title" [routerLink]="['/series', r.nodeId]">{{ r.title }}</a>
                <span class="scores muted small">
                  @if (r.used) {
                    Now {{ stars(r.trackerScore) }} ★ (from {{ name() }})
                  } @else {
                    Yours {{ stars(r.myScore) }} ★ · {{ name() }} {{ stars(r.trackerScore) }} ★
                  }
                </span>
              </span>
              @if (!r.used) {
                <button mat-button type="button" class="use" [disabled]="busy()" (click)="use(r)"
                        [attr.data-testid]="'tracker-score-use-' + r.seriesId">Use {{ name() }}'s</button>
              }
            </li>
          }
        </ul>
      }
      @if (error(); as e) { <p class="error" role="alert" [attr.data-testid]="'tracker-score-error-' + slug()">{{ e }}</p> }
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .import { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
    :host(.idle) { display: none; }
    p { margin: 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; }
    .actions button { flex: none; }
    .muted { color: var(--mp-text-muted); }
    .small { font-size: 12px; }
    .ok { color: var(--mp-success); font-size: 13px; overflow-wrap: anywhere; }
    .error { color: var(--mp-error); font-size: 13px; }
    .differ { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px;
              border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.12); }
    .row { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 4px 12px; padding: 6px 0;
           border-bottom: 1px solid rgb(var(--mp-ink-rgb) / 0.08); min-width: 0; }
    .title-cell { display: flex; flex-direction: column; min-width: 0; flex: 1 1 180px; }
    .title { color: var(--mp-text); text-decoration: none; overflow-wrap: anywhere; font-size: 14px; }
    .title:hover { text-decoration: underline; }
    .use { flex: none; }
    @media (pointer: coarse) {
      .use, .actions button { min-height: 40px; }
      .title { display: inline-flex; align-items: center; min-height: 40px; }
    }
  `],
})
export class TrackerScoreImportComponent {
  private readonly api = inject(TrackersApiService);

  readonly slug = input.required<string>();
  readonly name = input.required<string>();
  /** Show the one-line invitation (the user just turned "Keep my score in step" on). */
  readonly offer = input(false);
  /** The account cannot reach the tracker right now. */
  readonly disabled = input(false);
  /** Render the "Take my scores" button here (false: the host renders it and calls `run()`). */
  readonly showButton = input(true);

  readonly busy = signal(false);
  readonly result = signal<TrackerScoreImportResultDto | null>(null);
  readonly rows = signal<DifferRow[]>([]);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly idle = computed(() => !this.showButton() && !(this.offer() && !this.result()) && !this.notice() && !this.error()
    && this.rows().length === 0);

  stars(score: number): string {
    return starsText(score);
  }

  run(): void {
    if (this.busy()) return;
    const slug = this.slug();
    this.busy.set(true);
    this.error.set(null);
    this.api.importScores(slug).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.result.set(r);
        this.rows.set(r.differ.map((d) => ({ ...d, used: false })));
        this.notice.set(scoreImportNotice(r, this.name()));
      },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, slug)); },
    });
  }

  use(row: DifferRow): void {
    if (this.busy() || row.used) return;
    const slug = this.slug();
    this.busy.set(true);
    this.error.set(null);
    this.api.useTrackerScore(slug, row.seriesId, row.trackerScore).subscribe({
      next: () => {
        this.busy.set(false);
        this.rows.update((rows) => rows.map((r) => (r.seriesId === row.seriesId ? { ...r, used: true } : r)));
      },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, slug)); },
    });
  }
}
