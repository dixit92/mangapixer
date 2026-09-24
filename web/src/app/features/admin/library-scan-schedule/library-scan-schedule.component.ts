import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { ApiService } from '../../../core/api/api.service';
import { LibraryDto, LibraryScanSchedule } from '../../../core/api/api-types';

/** Preset labels in the order the server lists `LibraryScanSchedules.Allowed`. */
export const SCAN_SCHEDULE_OPTIONS: readonly { value: LibraryScanSchedule; label: string }[] = [
  { value: 'off', label: 'Off' },
  { value: '1h', label: 'Hourly' },
  { value: '6h', label: 'Every 6 hours' },
  { value: '1d', label: 'Daily' },
  { value: '7d', label: 'Weekly' },
];

/**
 * Automatic scan schedule for one library (1.23.0), shown under each row of
 * the admin Libraries card (a one-line wiring edit in `admin.component.ts`).
 * Unlike the icon picker it owns its API calls: it reads the admin library
 * DTO (`scanSchedule`, `nextScheduledScanAt`, which the catalog listing the
 * card is built from does not carry) and saves the preset itself. It re-reads
 * whenever the host's row changes scan state, so "Last scan" / "Next scan"
 * follow scans started from the card or by the scheduler.
 */
@Component({
  selector: 'app-library-scan-schedule',
  standalone: true,
  imports: [DatePipe, MatFormFieldModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="scan-schedule">
      <mat-form-field appearance="fill" class="schedule-select" floatLabel="always" subscriptSizing="dynamic">
        <mat-label>Auto-scan</mat-label>
        <mat-select [value]="schedule()" [disabled]="schedule() === null || saving()"
                    (selectionChange)="save($event.value)" aria-label="Automatic scan schedule">
          @for (opt of options; track opt.value) {
            <mat-option [value]="opt.value">{{ opt.label }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <span class="schedule-info">
        <span class="last">Last scan:
          @if (lastScan(); as last) { {{ last | date:'short' }} } @else { never }
        </span>
        <span class="next">Next scan (approx.):
          @if (schedule() === 'off') { off }
          @else if (nextScan(); as next) {
            @if (isDue()) { shortly } @else { {{ next | date:'short' }} }
          } @else { — }
        </span>
      </span>
      @if (error()) {
        <span class="schedule-error" role="alert">{{ error() }}</span>
      }
    </div>
  `,
  styles: [`
    .scan-schedule {
      display: flex; flex-wrap: wrap; align-items: center; gap: 4px 16px;
      padding: 0 16px 8px 72px;
      font-size: 13px;
    }
    .schedule-select { width: 150px; }
    .schedule-info { display: inline-flex; flex-wrap: wrap; gap: 4px 16px; opacity: 0.75; }
    .schedule-error { color: var(--mp-warn, #ff8a80); }
    @media (max-width: 600px) {
      .scan-schedule { padding-left: 16px; }
    }
  `],
})
export class LibraryScanScheduleComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** The library row from the admin card (catalog DTO). */
  readonly library = input.required<LibraryDto>();

  readonly options = SCAN_SCHEDULE_OPTIONS;

  readonly schedule = signal<LibraryScanSchedule | null>(null);
  readonly lastScan = signal<string | null>(null);
  readonly nextScan = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  /** A past (or present) next-scan time: the next scheduler pass picks it up. */
  readonly isDue = computed(() => {
    const next = this.nextScan();
    return next !== null && Date.parse(next) <= Date.now();
  });

  /** Key of the row state that should trigger a re-read. */
  private readonly refreshKey = computed(() => {
    const lib = this.library();
    return `${lib.id}|${lib.lastScanCompleted ?? ''}|${lib.isScanning}`;
  });

  constructor() {
    effect(() => {
      this.refreshKey();
      untracked(() => this.load(this.library().id));
    });
  }

  save(value: LibraryScanSchedule): void {
    const id = this.library().id;
    const previous = this.schedule();
    this.schedule.set(value);
    this.saving.set(true);
    this.error.set(null);
    this.api.setLibraryScanSchedule(id, value)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => {
          this.apply(dto);
          this.saving.set(false);
        },
        error: () => {
          this.schedule.set(previous);
          this.saving.set(false);
          this.error.set('Could not save the scan schedule.');
        },
      });
  }

  private load(id: string): void {
    this.api.getLibrary(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => this.apply(dto),
        error: () => this.error.set('Could not load the scan schedule.'),
      });
  }

  private apply(dto: LibraryDto): void {
    this.schedule.set(dto.scanSchedule ?? '1d');
    this.lastScan.set(dto.lastScanCompleted);
    this.nextScan.set(dto.nextScheduledScanAt ?? null);
  }
}
