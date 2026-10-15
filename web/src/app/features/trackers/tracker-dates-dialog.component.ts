import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

import { TrackerSeriesRowDto } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';
import { dateText, failureText, trackerName } from './tracker-texts';

export interface TrackerDatesDialogData {
  row: TrackerSeriesRowDto;
}

/** Today's date as `yyyy-MM-dd` in the browser's own calendar (the server allows up to tomorrow, for time zones). */
export function todayIso(now: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/**
 * "Set dates…" on the Tracking page (1.43.0, owner A1): the start / finish dates MangaPixer sends to MyAnimeList for one series.
 * Each date is the user's own or automatic (the first counted read, the day MangaPixer set Completed); "Automatic" clears the
 * user's date. Saved with `PUT .../dates` (both dates replaced; nothing is sent to the tracker until the next write) and closes
 * with the updated row.
 */
@Component({
  selector: 'app-tracker-dates-dialog',
  standalone: true,
  imports: [FormsModule, MatDialogModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Dates on {{ name }}</h2>
    <mat-dialog-content>
      <form id="tracker-dates-form" (ngSubmit)="save()" data-testid="tracker-dates-dialog">
        <p class="series">{{ row.title }}</p>
        <p class="muted small">MangaPixer sends these with the next update to {{ name }}. Leave a date on Automatic to use the day you
          started reading here and the day the series was completed.</p>
        <div class="field">
          <label for="tracker-started-on">Started</label>
          <div class="line">
            <input id="tracker-started-on" type="date" name="startedOn" min="1900-01-01" [max]="maxDate" [disabled]="busy()"
                   [ngModel]="started()" (ngModelChange)="started.set($event || null)" data-testid="tracker-dates-start" />
            <button mat-button type="button" [disabled]="busy() || started() === null" (click)="started.set(null)"
                    data-testid="tracker-dates-start-auto">Automatic</button>
          </div>
          <span class="muted small">{{ started() === null ? 'Automatic: ' + autoStart : 'Your date' }}</span>
        </div>
        <div class="field">
          <label for="tracker-finished-on">Finished</label>
          <div class="line">
            <input id="tracker-finished-on" type="date" name="finishedOn" min="1900-01-01" [max]="maxDate" [disabled]="busy()"
                   [ngModel]="finished()" (ngModelChange)="finished.set($event || null)" data-testid="tracker-dates-finish" />
            <button mat-button type="button" [disabled]="busy() || finished() === null" (click)="finished.set(null)"
                    data-testid="tracker-dates-finish-auto">Automatic</button>
          </div>
          <span class="muted small">{{ finished() === null ? 'Automatic: ' + autoFinish : 'Your date' }}</span>
        </div>
        @if (row.dates.remoteStartedOn || row.dates.remoteFinishedOn) {
          <p class="muted small">On {{ name }} now: start {{ date(row.dates.remoteStartedOn) }} · finish {{ date(row.dates.remoteFinishedOn) }}</p>
        }
        @if (invalid(); as why) { <p class="error" role="alert" data-testid="tracker-dates-invalid">{{ why }}</p> }
        @if (error(); as e) { <p class="error" role="alert" data-testid="tracker-dates-error">{{ e }}</p> }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-flat-button color="primary" type="submit" form="tracker-dates-form" [disabled]="busy() || !!invalid()"
              data-testid="tracker-dates-save">Save</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .series { margin: 0 0 6px; font-weight: 500; overflow-wrap: anywhere; color: var(--mp-text); }
    .field { display: flex; flex-direction: column; gap: 4px; margin: 12px 0; font-size: 13px; color: var(--mp-text-secondary); min-width: 0; }
    .line { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .field input { font: inherit; font-size: 14px; padding: 6px 8px; min-width: 0; box-sizing: border-box; color: var(--mp-text);
                   background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    .muted { color: var(--mp-text-muted); margin: 4px 0; overflow-wrap: anywhere; }
    .small { font-size: 12px; }
    .error { color: var(--mp-error); font-size: 13px; margin: 6px 0; }
  `],
})
export class TrackerDatesDialogComponent {
  private readonly api = inject(TrackersApiService);
  private readonly ref = inject<MatDialogRef<TrackerDatesDialogComponent, TrackerSeriesRowDto>>(MatDialogRef);
  private readonly data = inject<TrackerDatesDialogData>(MAT_DIALOG_DATA);

  readonly row = this.data.row;
  readonly name = trackerName(this.row.tracker);
  readonly maxDate = todayIso();
  readonly date = dateText;

  /** The user's own dates (null = automatic). */
  readonly started = signal<string | null>(this.row.dates.startedOnOverridden ? this.row.dates.startedOn ?? null : null);
  readonly finished = signal<string | null>(this.row.dates.finishedOnOverridden ? this.row.dates.finishedOn ?? null : null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  /** What "Automatic" means for this series right now. */
  readonly autoStart = this.row.dates.startedOnOverridden ? 'the day you started reading here' : dateText(this.row.dates.startedOn);
  readonly autoFinish = this.row.dates.finishedOnOverridden ? 'the day the series was completed' : dateText(this.row.dates.finishedOn);

  /** Checked here as the server checks (1900 to tomorrow, the finish not before the start); the server has the last word. */
  readonly invalid = computed(() => {
    const s = this.started();
    const f = this.finished();
    for (const d of [s, f]) {
      if (d !== null && (!/^\d{4}-\d{2}-\d{2}$/.test(d) || d < '1900-01-01')) return 'Give a full date from 1900 on.';
    }
    if (s !== null && f !== null && f < s) return 'The finish date is before the start date.';
    return null;
  });

  save(): void {
    if (this.busy() || this.invalid()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.setDates(this.row.tracker, this.row.seriesId, { startedOn: this.started(), finishedOn: this.finished() }).subscribe({
      next: (row) => { this.busy.set(false); this.ref.close(row); },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, this.row.tracker)); },
    });
  }
}
