import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { LibraryDto, RefreshCadenceDto, ScheduledJobDto, ScheduledJobsDto, UpdateRefreshCadenceRequest } from '../../../core/api/api-types';
import { LibraryScanScheduleComponent, hourLabel } from '../library-scan-schedule/library-scan-schedule.component';

/** Each job's name and one plain line on what it does. */
export const JOB_TEXT: Record<string, { name: string; what: string }> = {
  'library-scan': { name: 'Library scan', what: 'Looks for new, changed and removed files in the library.' },
  'metadata-refresh': {
    name: 'Series information refresh',
    what: 'Fetches the series you have linked again from MangaUpdates, by record number. Needs Automatic matching.',
  },
  backup: { name: 'Database backup', what: 'Takes a copy of the database and keeps the newest ones.' },
  trash: { name: 'Empty trash and clean bundles', what: 'Empties the trash and removes unused files, when automatic cleaning is on.' },
  'cache-eviction': { name: 'Cache clean-up', what: 'Removes the oldest cached pages when the cache is over its size limit.' },
  'auto-match': { name: 'Automatic matching', what: 'Matches new series folders to MangaUpdates records.' },
  'volume-covers': { name: 'Volume covers and volume lists', what: 'Reads volume lists and covers of linked series.' },
  'cover-decisions': { name: 'Cover choices', what: 'Picks the cover of each series and volume from what is stored.' },
  'content-signatures': { name: 'Content signatures', what: 'Fingerprints older archives so moves and renames are recognised.' },
  'session-cleanup': { name: 'Sign-in session clean-up', what: 'Removes expired sign-in sessions.' },
  thumbnails: { name: 'Thumbnails', what: 'Makes the thumbnails that are missing.' },
  'update-check': { name: 'Update check', what: 'Asks GitHub whether a newer MangaPixer is out, when the Update Checker is on.' },
};

/** Why a job waits, in the admin's words. */
export const WAITING_TEXT: Record<string, string> = {
  automatic_off: 'Automatic matching is off',
  metadata_disabled: 'Fetch from the web is off',
  metadata_network_disabled: 'web requests are switched off by server configuration',
  consent_required: 'the consent needs renewing',
  auto_consent_required: 'the automatic matching consent needs renewing',
  budget_exhausted: 'daily budget used until midnight (server time)',
  provider_backoff: 'MangaUpdates asked MangaPixer to wait',
  provider_busy: 'MangaUpdates is busy',
  provider_not_allowed: 'MangaUpdates is not on the allowed sites',
  volume_covers_off: '"Volume covers from the web" is off',
  volume_covers_disabled: 'volume covers are switched off by server configuration',
  refresh_cap: 'today\'s refresh limit is reached',
};

/** A cadence in days as the admin reads it. */
export function cadenceLabel(days: number): string {
  switch (days) {
    case 7: return 'Every week';
    case 14: return 'Every 2 weeks';
    case 30: return 'Every month';
    case 90: return 'Every 3 months';
    case 180: return 'Every 6 months';
    default: return `Every ${days} days`;
  }
}

/** "UTC-04:00" for an offset in minutes. */
export function offsetLabel(minutes: number): string {
  const sign = minutes < 0 ? '-' : '+';
  const abs = Math.abs(minutes);
  return `UTC${sign}${String(Math.floor(abs / 60)).padStart(2, '0')}:${String(abs % 60).padStart(2, '0')}`;
}

/** An instant in the SERVER's zone ("Fri 2 Oct, 03:00"), so it matches the hour selects wherever the browser is. */
export function serverTime(iso: string | null | undefined, zone: string, withDate = true): string {
  if (!iso) return '';
  const options: Intl.DateTimeFormatOptions = withDate
    ? { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }
    : { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' };
  try {
    return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: zone }).format(new Date(iso));
  } catch {
    return new Intl.DateTimeFormat('en-GB', { ...options, timeZone: 'UTC' }).format(new Date(iso));
  }
}

/** The fixed rhythm of a read-only job. */
function rhythm(job: ScheduledJobDto): string {
  switch (job.key) {
    case 'auto-match': return 'Continuous - new folders within about a minute';
    case 'volume-covers': return 'Continuous, with automatic matching; each series on its refresh cadence';
    case 'cover-decisions': return `Every ${Math.round((job.intervalHours ?? 1 / 6) * 60)} minutes (nothing is sent)`;
    case 'content-signatures': return 'Every 6 hours while archives lack one';
    case 'session-cleanup': return 'Hourly';
    case 'thumbnails': return 'Once after start-up';
    case 'update-check': return job.enabled ? 'When an admin opens Administration, at most once a day' : 'Off';
    default: return '';
  }
}

/**
 * Scheduled jobs (1.32.0), self-contained (`<app-scheduled-jobs />`): every job MangaPixer runs on its own, with its last and
 * next run in server time. The admin chooses the hour of the daily jobs (series information refresh, backups, trash, cache
 * clean-up), each library's scan time (the scan schedule control of the Libraries card) and the refresh cadence. The other jobs
 * keep their own rhythm and are listed read-only.
 */
@Component({
  selector: 'app-scheduled-jobs',
  standalone: true,
  imports: [MatCardModule, MatSlideToggleModule, LibraryScanScheduleComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="scheduled-jobs">
      <mat-card-header>
        <mat-card-title>Scheduled jobs</mat-card-title>
        <mat-card-subtitle>What MangaPixer does on its own, and when</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        @if (jobs(); as j) {
          <p class="clock" data-testid="jobs-clock">
            Times are server time: {{ j.timeZone }} ({{ offset() }}), now {{ time(j.serverTime, false) }}.
          </p>
          <ul class="jobs">
            @for (job of j.jobs; track job.key + (job.libraryId ?? '')) {
              <li [attr.data-testid]="'job-' + job.key + (job.libraryId ? '-' + job.libraryId : '')">
                <div class="head">
                  <span class="name">{{ name(job) }}</span>
                  @if (job.running) { <span class="badge">Running</span> }
                </div>
                <div class="what">{{ what(job.key) }}</div>

                @switch (job.key) {
                  @case ('library-scan') {
                    <app-library-scan-schedule class="scan" [library]="libraryOf(job)" />
                  }
                  @case ('metadata-refresh') {
                    <div class="row">
                      <label for="job-refresh-hour">Run at</label>
                      <select id="job-refresh-hour" data-testid="job-hour-metadata-refresh" [disabled]="busy()"
                              (change)="setHour('metadata-refresh', +$any($event.target).value)">
                        @for (h of hours; track h) { <option [value]="h" [selected]="h === job.hour">{{ hourLabel(h) }}</option> }
                      </select>
                    </div>
                    <div class="row">
                      <label for="job-refresh-ongoing">Check ongoing series</label>
                      <select id="job-refresh-ongoing" data-testid="job-cadence-ongoing" [disabled]="busy()"
                              (change)="setCadence({ ongoingDays: +$any($event.target).value })">
                        @for (d of j.refresh.allowedOngoingDays; track d) {
                          <option [value]="d" [selected]="d === j.refresh.ongoingDays">{{ cadence(d) }}</option>
                        }
                      </select>
                    </div>
                    <div class="row">
                      <label for="job-refresh-finished">Check finished series</label>
                      <select id="job-refresh-finished" data-testid="job-cadence-finished" [disabled]="busy()"
                              (change)="setCadence({ finishedDays: +$any($event.target).value })">
                        @for (d of j.refresh.allowedFinishedDays; track d) {
                          <option [value]="d" [selected]="d === j.refresh.finishedDays">{{ cadence(d) }}</option>
                        }
                      </select>
                    </div>
                    <mat-slide-toggle [checked]="j.refresh.followPace" [disabled]="busy()" data-testid="job-cadence-pace"
                                      (change)="setCadence({ followPace: $event.checked })">
                      Follow each series' publishing pace
                    </mat-slide-toggle>
                    <p class="hint">
                      Series that publish quickly are checked more often, up to once a week. A series on hiatus or with nothing
                      new for six months is checked like a finished one.
                    </p>
                    <p class="status" data-testid="job-refresh-counts">
                      Today: {{ j.refresh.usedToday }} of {{ j.refresh.maxPerDay }} checked.
                      @if (j.refresh.overdue > 0) { {{ j.refresh.overdue }} series are past their check date. }
                    </p>
                    @if (breakdown(j.refresh); as b) { <p class="status">{{ b }}</p> }
                  }
                  @case ('backup') {
                    <div class="row">
                      <label for="job-backup-hour">At</label>
                      <select id="job-backup-hour" data-testid="job-hour-backup"
                              [disabled]="busy() || job.managedByConfig || !wholeDays(job)"
                              (change)="setHour('backup', +$any($event.target).value)">
                        <option [value]="-1" [selected]="job.hour == null">Any time</option>
                        @for (h of hours; track h) { <option [value]="h" [selected]="h === job.hour">{{ hourLabel(h) }}</option> }
                      </select>
                    </div>
                    @if (job.managedByConfig) {
                      <p class="hint">Set by server configuration.</p>
                    } @else if (!wholeDays(job)) {
                      <p class="hint">Only for daily or longer intervals (Database backup card).</p>
                    }
                  }
                  @case ('trash') {
                    <div class="row">
                      <label for="job-trash-hour">Run automatic cleaning at</label>
                      <select id="job-trash-hour" data-testid="job-hour-trash" [disabled]="busy()"
                              (change)="setHour('trash', +$any($event.target).value)">
                        @for (h of hours; track h) { <option [value]="h" [selected]="h === job.hour">{{ hourLabel(h) }}</option> }
                      </select>
                    </div>
                    @if (!job.enabled) { <p class="hint">Off - turn automatic cleaning on in the Trash card.</p> }
                  }
                  @case ('cache-eviction') {
                    <div class="row">
                      <label for="job-cache-hour">Run at</label>
                      <select id="job-cache-hour" data-testid="job-hour-cache-eviction" [disabled]="busy()"
                              (change)="setHour('cache-eviction', +$any($event.target).value)">
                        @for (h of hours; track h) { <option [value]="h" [selected]="h === job.hour">{{ hourLabel(h) }}</option> }
                      </select>
                    </div>
                  }
                  @default {
                    <div class="rhythm">{{ rhythmOf(job) }}</div>
                  }
                }

                @if (job.key !== 'library-scan') {
                  <div class="runs">
                    <span>Last run: {{ lastRun(job) }}</span>
                    <span>Next run: {{ nextRun(job) }}</span>
                  </div>
                }
              </li>
            }
          </ul>
        } @else if (!error()) {
          <p class="muted">Loading…</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    .clock { font-size: 14px; margin: 4px 0 10px; }
    .jobs { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 10px; }
    .jobs li { border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; }
    .head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .badge { font-size: 12px; color: #4caf50; }
    .what, .hint, .muted, .runs, .rhythm, .status { color: #999; font-size: 13px; margin: 4px 0; }
    .rhythm { color: inherit; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0; font-size: 14px; }
    .row label { min-width: 110px; }
    select { font: inherit; padding: 4px 6px; max-width: 100%; }
    .runs { display: flex; flex-wrap: wrap; gap: 4px 16px; }
    .scan ::ng-deep .scan-schedule { padding: 4px 0 0; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
  `],
})
export class ScheduledJobsComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly jobs = signal<ScheduledJobsDto | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly hours = Array.from({ length: 24 }, (_, h) => h);
  readonly hourLabel = hourLabel;
  readonly cadence = cadenceLabel;

  readonly offset = computed(() => offsetLabel(this.jobs()?.utcOffsetMinutes ?? 0));

  ngOnInit(): void {
    this.api.getScheduledJobs()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => this.jobs.set(dto),
        error: () => this.error.set('Could not load the scheduled jobs.'),
      });
  }

  name(job: ScheduledJobDto): string {
    return job.key === 'library-scan' && job.libraryName ? `Library scan: ${job.libraryName}` : JOB_TEXT[job.key]?.name ?? job.key;
  }

  what(key: string): string {
    return JOB_TEXT[key]?.what ?? '';
  }

  rhythmOf(job: ScheduledJobDto): string {
    return rhythm(job);
  }

  time(iso: string | null | undefined, withDate = true): string {
    return serverTime(iso, this.jobs()?.timeZone ?? 'UTC', withDate);
  }

  /** The library row the scan schedule control expects (it reads the admin DTO itself). */
  libraryOf(job: ScheduledJobDto): LibraryDto {
    return {
      id: job.libraryId ?? '',
      name: job.libraryName ?? '',
      isScanning: false,
      itemCount: null,
      lastScanCompleted: job.lastFinishedAt ?? null,
      defaultReaderMode: null,
      icon: null,
    };
  }

  wholeDays(job: ScheduledJobDto): boolean {
    const h = job.intervalHours ?? 0;
    return h >= 24 && h <= 720 && h % 24 === 0;
  }

  lastRun(job: ScheduledJobDto): string {
    const at = job.lastStartedAt ?? job.lastFinishedAt;
    if (job.running) return 'running now';
    if (!at) return 'not yet';
    const outcome = job.lastOutcome === 'failed' ? ' - failed' : job.lastOutcome === 'waiting' ? ' - waiting' : '';
    return `${this.time(at)}${outcome}${job.lastDetail && job.lastOutcome !== 'failed' ? ` - ${job.lastDetail}` : ''}`;
  }

  nextRun(job: ScheduledJobDto): string {
    if (job.waitingCode) return `Waiting: ${WAITING_TEXT[job.waitingCode] ?? job.waitingCode}`;
    if (!job.enabled) return 'Off';
    if (job.kind === 'continuous') return 'continuous';
    if (job.kind === 'startup') return 'at the next start';
    if (job.kind === 'onDemand' || !job.nextRunAt) return '-';
    return Date.parse(job.nextRunAt) <= Date.parse(this.jobs()?.serverTime ?? '') ? 'shortly' : this.time(job.nextRunAt);
  }

  /** "Checked every week: 41, every 2 weeks: 120, ..." (only cadences in use). */
  breakdown(refresh: RefreshCadenceDto): string {
    if (refresh.byDays.length === 0) return '';
    return 'Checked ' + refresh.byDays.map(c => `${cadenceLabel(c.days).toLowerCase()}: ${c.count}`).join(', ') + '.';
  }

  setHour(key: string, hour: number): void {
    this.save(this.api.setJobHour(key, hour < 0 ? null : hour));
  }

  setCadence(request: UpdateRefreshCadenceRequest): void {
    this.save(this.api.setRefreshCadence(request));
  }

  private save(call: Observable<ScheduledJobsDto>): void {
    this.busy.set(true);
    this.error.set(null);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: dto => {
        this.jobs.set(dto);
        this.busy.set(false);
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Could not save. The previous setting is kept.');
        this.ngOnInit();
      },
    });
  }
}
