import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { ApiService } from '../../../core/api/api.service';
import { LibraryDto, LibraryScanSchedule } from '../../../core/api/api-types';

/** The 24 hours of the "At" select (server time) and the weekdays of "On" (0 = Sunday, as the server counts). */
export const SCAN_HOURS: readonly number[] = Array.from({ length: 24 }, (_, h) => h);
export const WEEKDAYS: readonly string[] = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

/** "03:00" for an hour. */
export function hourLabel(h: number): string {
  return `${String(h).padStart(2, '0')}:00`;
}

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
 * follow scans started from the card or by the scheduler. 1.32.0: Daily and Weekly take
 * a time of day ("At", server time; "Any time" = one interval after the last scan) and
 * Weekly a weekday ("On"); the request always carries the whole schedule.
 */
@Component({
  selector: 'app-library-scan-schedule',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="scan-schedule">
      @if (summary()) {
        <span class="summary" data-testid="scan-summary">
          Auto-scan: {{ summaryText() }}
          <button type="button" class="link" data-testid="scan-schedule-link" (click)="showInJobs()">Change in Scheduled jobs</button>
        </span>
      } @else {
      <!-- 1.35.0: compact native selects, one cell each, so the Scheduled jobs table lines its rows up (owner: "not neat"). -->
      <select class="cell schedule" [value]="schedule() ?? ''" [disabled]="schedule() === null || saving()"
              (change)="save($any($event.target).value)" aria-label="Automatic scan schedule" data-testid="scan-schedule">
        @for (opt of options; track opt.value) {
          <option [value]="opt.value" [selected]="opt.value === schedule()">{{ opt.label }}</option>
        }
      </select>
      @if (takesHour()) {
        <select class="cell" [disabled]="saving()" (change)="saveHour(+$any($event.target).value)"
                aria-label="Scan time of day (server time)" data-testid="scan-hour">
          <option [value]="-1" [selected]="hour() === null">Any time</option>
          @for (h of hours; track h) { <option [value]="h" [selected]="h === hour()">{{ hourLabel(h) }}</option> }
        </select>
      } @else {
        <span class="cell"></span>
      }
      @if (schedule() === '7d' && hour() !== null) {
        <select class="cell" [disabled]="saving()" (change)="saveWeekday(+$any($event.target).value)"
                aria-label="Scan weekday" data-testid="scan-weekday">
          @for (d of weekdays; track $index) { <option [value]="$index" [selected]="$index === (weekday() ?? 0)">{{ d }}</option> }
        </select>
      } @else {
        <span class="cell"></span>
      }
      }
      <span class="schedule-info">
        <span class="last cell"><span class="lbl">Last scan: </span>
          @if (lastScan(); as last) { {{ serverZone() ? inZone(last) : (last | date:'short') }} } @else { never }
        </span>
        <span class="next cell"><span class="lbl">Next scan (approx.): </span>
          @if (schedule() === 'off') { off }
          @else if (nextScan(); as next) {
            @if (isDue()) { shortly } @else { {{ serverZone() ? inZone(next) : (next | date:'short') }} }
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
    .schedule-info { display: inline-flex; flex-wrap: wrap; gap: 4px 16px; opacity: 0.75; }
    select { font: inherit; padding: 4px 6px; max-width: 100%; }
    .schedule-error { color: var(--mp-warn, #ff8a80); }
    .link { background: none; border: none; padding: 0; margin-left: 4px; font: inherit; color: #b39ddb; text-decoration: underline; cursor: pointer; }
    @media (max-width: 600px) {
      .scan-schedule { padding-left: 16px; }
    }
    /* In the Scheduled jobs table (1.35.0) every control and time is a cell of the host row's grid. */
    :host(.cells), :host(.cells) .scan-schedule, :host(.cells) .schedule-info { display: contents; }
    :host(.cells) .lbl { display: none; }
    :host(.cells) .schedule-info .cell { opacity: 0.75; }
    :host(.cells) .schedule-error { grid-column: 1 / -1; }
    /* Same width as the Scheduled jobs table's own switch (its section is the container). */
    @container (max-width: 860px) {
      :host(.cells) .lbl { display: inline; }
    }
  `],
})
export class LibraryScanScheduleComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** The library row from the admin card (catalog DTO). */
  readonly library = input.required<LibraryDto>();

  /** 1.32.0: an IANA zone (the Scheduled jobs section passes the server's) - last / next scan are then shown in that zone. */
  readonly serverZone = input<string | null>(null);

  /**
   * 1.33.0: read-only - the schedule as one line with a link to its Scheduled jobs row, where it is changed (the
   * Libraries card shows it this way, so the schedule is edited in one place).
   */
  readonly summary = input(false);

  readonly options = SCAN_SCHEDULE_OPTIONS;
  readonly hours = SCAN_HOURS;
  readonly weekdays = WEEKDAYS;
  readonly hourLabel = hourLabel;

  readonly schedule = signal<LibraryScanSchedule | null>(null);
  /** Server-local hour of a Daily / Weekly scan, or null ("Any time"). */
  readonly hour = signal<number | null>(null);
  /** Weekday of a Weekly scan with an hour (0 = Sunday), or null (Sunday). */
  readonly weekday = signal<number | null>(null);
  readonly takesHour = computed(() => this.schedule() === '1d' || this.schedule() === '7d');
  readonly lastScan = signal<string | null>(null);
  readonly nextScan = signal<string | null>(null);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  /** An instant in {@link serverZone} ("Fri 2 Oct, 03:00"). */
  inZone(iso: string): string {
    const zone = this.serverZone() ?? 'UTC';
    const options: Intl.DateTimeFormatOptions = { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
    try {
      return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: zone }).format(new Date(iso));
    } catch {
      return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(new Date(iso));
    }
  }

  /** "Daily at 03:00 (server time)", "Weekly on Monday at 03:00 (server time)", "Off" - the read-only line of {@link summary}. */
  readonly summaryText = computed(() => {
    const schedule = this.schedule();
    if (schedule === null) return '…';
    const label = this.options.find(o => o.value === schedule)?.label ?? schedule;
    if (!this.takesHour()) return label;
    const hour = this.hour();
    const day = schedule === '7d' && hour !== null ? ` on ${this.weekdays[this.weekday() ?? 0]}` : '';
    return hour === null ? `${label}${day}` : `${label}${day} at ${hourLabel(hour)} (server time)`;
  });

  /** Moves the page to this library's row in Scheduled jobs and focuses its schedule select. */
  showInJobs(): void {
    const row = document.querySelector<HTMLElement>(`[data-testid="job-library-scan-${this.library().id}"]`);
    if (!row) return;
    row.scrollIntoView({ behavior: 'smooth', block: 'center' });
    row.querySelector<HTMLElement>('select')?.focus({ preventScroll: true });
  }

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

  /** A new preset: the time of day stays for Daily / Weekly, the weekday for Weekly only. */
  save(value: LibraryScanSchedule): void {
    const takesHour = value === '1d' || value === '7d';
    const hour = takesHour ? this.hour() : null;
    this.persist(value, hour, value === '7d' && hour !== null ? this.weekday() : null);
  }

  /** "At": an hour, or -1 for "Any time". */
  saveHour(value: number): void {
    const schedule = this.schedule() ?? '1d';
    const hour = value < 0 ? null : value;
    this.persist(schedule, hour, schedule === '7d' && hour !== null ? this.weekday() : null);
  }

  /** "On": a weekday (0 = Sunday). */
  saveWeekday(value: number): void {
    this.persist(this.schedule() ?? '7d', this.hour(), value);
  }

  private persist(value: LibraryScanSchedule, hour: number | null, weekday: number | null): void {
    const id = this.library().id;
    const previous = [this.schedule(), this.hour(), this.weekday()] as const;
    this.schedule.set(value);
    this.hour.set(hour);
    this.weekday.set(weekday);
    this.saving.set(true);
    this.error.set(null);
    this.api.setLibraryScanSchedule(id, value, hour, weekday)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => {
          this.apply(dto);
          this.saving.set(false);
        },
        error: () => {
          this.schedule.set(previous[0]);
          this.hour.set(previous[1]);
          this.weekday.set(previous[2]);
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
    this.hour.set(dto.scanHour ?? null);
    this.weekday.set(dto.scanWeekday ?? null);
    this.lastScan.set(dto.lastScanCompleted);
    this.nextScan.set(dto.nextScheduledScanAt ?? null);
  }
}
