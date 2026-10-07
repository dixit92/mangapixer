import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import {
  ApiError,
  LibraryDto,
  RefreshCadenceDto,
  RefreshPaceSource,
  ScheduledJobDto,
  ScheduledJobsDto,
  TrashOverviewDto,
  UpdateRefreshCadenceRequest,
} from '../../../core/api/api-types';
import { LibraryScanScheduleComponent, hourLabel } from '../library-scan-schedule/library-scan-schedule.component';
import { trashCountsText } from '../trash-card/trash-card.component';

/** Each job's name and one plain line on what it does. */
export const JOB_TEXT: Record<string, { name: string; what: string }> = {
  'library-scan': { name: 'Library scan', what: 'Looks for new, changed and removed files in the library.' },
  'metadata-refresh': {
    name: 'Series information refresh',
    what: 'Fetches the series you have linked again from MangaUpdates, by record number. Needs Automatic matching.',
  },
  backup: { name: 'Database backup', what: 'Takes a copy of the database and keeps the newest ones.' },
  trash: { name: 'Empty trash and clean bundles', what: 'Empties the trash and removes unused files once a day, when automatic cleaning is on. Libraries with a hold are skipped.' },
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

/** The groups the jobs are shown in (1.35.0), by job key; a key not listed goes to Maintenance. */
export const JOB_GROUPS: readonly { key: string; title: string; jobs: readonly string[] }[] = [
  { key: 'web', title: 'Web information', jobs: ['auto-match', 'metadata-refresh', 'volume-covers', 'update-check'] },
  { key: 'upkeep', title: 'Library upkeep', jobs: ['thumbnails', 'content-signatures', 'cover-decisions'] },
  { key: 'maintenance', title: 'Maintenance', jobs: ['backup', 'trash', 'cache-eviction', 'session-cleanup'] },
];

/** "Pace from" (1.35.0). */
export const PACE_SOURCES: readonly { value: RefreshPaceSource; label: string }[] = [
  { value: 'faster', label: 'Whichever is faster' },
  { value: 'chapters', label: 'New chapters (scanlations)' },
  { value: 'volumes', label: 'New volumes (original release)' },
];

/** The line under the refresh settings: what the ongoing choice does. */
export function cadenceHint(refresh: RefreshCadenceDto): string {
  if (!refresh.followPace) {
    return `Every ongoing series is checked ${cadenceLabel(refresh.ongoingDays).toLowerCase()}. Choose "Follow their pace" to check `
      + 'busy series more often and quiet ones less.';
  }
  const by = refresh.paceSource === 'chapters' ? 'new chapters (the latest chapter MangaUpdates lists, which follows scanlation releases)'
    : refresh.paceSource === 'volumes' ? 'new volumes of the original release'
    : 'new chapters or new volumes, whichever comes more often';
  return `Each ongoing series is checked every week, every 2 weeks or every month, by how often it gets ${by}. A series on hiatus `
    + 'or with nothing new for six months is checked like a finished one; one whose pace is not known yet, every month.';
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
 * next run in server time. 1.35.0 (owner: "not neat"): the library scans are one table, the other jobs are grouped (Web information,
 * Library upkeep, Maintenance) as cards of one shape - name, what it does, its settings or "When", last / next run at the bottom. The admin chooses the hour of the daily jobs (series information refresh, backups, trash, cache
 * clean-up; for the trash also the automatic-cleaning switch), each library's scan time (the scan schedule control of the Libraries card) and the refresh cadence. The other jobs
 * keep their own rhythm and are listed read-only.
 */
@Component({
  selector: 'app-scheduled-jobs',
  standalone: true,
  imports: [MatButtonModule, MatCardModule, MatSlideToggleModule, LibraryScanScheduleComponent],
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
          <div class="groups">
            @if (scans().length > 0) {
              <section class="group" aria-labelledby="jobs-scans-h" data-testid="jobs-group-scans">
                <h4 id="jobs-scans-h">Library scans</h4>
                <p class="what">{{ what('library-scan') }}</p>
                <div class="scans">
                  <div class="scan-head" aria-hidden="true">
                    <span>Library</span><span>Auto-scan</span><span>At</span><span>On</span><span>Last scan</span><span>Next scan (approx.)</span>
                  </div>
                  @for (job of scans(); track job.libraryId) {
                    <div class="scan-row" [attr.data-testid]="'job-library-scan-' + job.libraryId">
                      <span class="lib">
                        {{ job.libraryName }}
                        @if (job.running) { <span class="badge">Running</span> }
                      </span>
                      <app-library-scan-schedule class="cells" [library]="libraryOf(job)" [serverZone]="j.timeZone" />
                    </div>
                  }
                </div>
              </section>
            }
            @for (group of groups(); track group.key) {
              <section class="group" [attr.aria-labelledby]="'jobs-' + group.key + '-h'" [attr.data-testid]="'jobs-group-' + group.key">
                <h4 [attr.id]="'jobs-' + group.key + '-h'">{{ group.title }}</h4>
                <ul class="jobs">
                  @for (job of group.jobs; track job.key) {
                    <li [attr.data-testid]="'job-' + job.key" [attr.id]="'job-' + job.key" [class.wide]="job.key === 'metadata-refresh'">
                      <div class="head">
                        <span class="name">{{ name(job) }}</span>
                        @if (job.running) { <span class="badge">Running</span> }
                      </div>
                      <div class="what">{{ what(job.key) }}</div>

                      @switch (job.key) {
                        @case ('metadata-refresh') {
                          <div class="refresh">
                            <div class="settings">
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
                                        (change)="setOngoing($any($event.target).value)">
                                  <option value="pace" [selected]="j.refresh.followPace">Follow their pace</option>
                                  @for (d of j.refresh.allowedOngoingDays; track d) {
                                    <option [value]="d" [selected]="!j.refresh.followPace && d === j.refresh.ongoingDays">{{ cadence(d) }}</option>
                                  }
                                </select>
                              </div>
                              @if (j.refresh.followPace) {
                                <div class="row">
                                  <label for="job-refresh-pace">Pace from</label>
                                  <select id="job-refresh-pace" data-testid="job-cadence-pace-source" [disabled]="busy()"
                                          (change)="setCadence({ paceSource: $any($event.target).value })">
                                    @for (o of paceSources; track o.value) {
                                      <option [value]="o.value" [selected]="o.value === j.refresh.paceSource">{{ o.label }}</option>
                                    }
                                  </select>
                                </div>
                              }
                              <div class="row">
                                <label for="job-refresh-finished">Check finished series</label>
                                <select id="job-refresh-finished" data-testid="job-cadence-finished" [disabled]="busy()"
                                        (change)="setCadence({ finishedDays: +$any($event.target).value })">
                                  @for (d of j.refresh.allowedFinishedDays; track d) {
                                    <option [value]="d" [selected]="d === j.refresh.finishedDays">{{ cadence(d) }}</option>
                                  }
                                </select>
                              </div>
                            </div>
                            <div class="notes">
                              <p class="hint" data-testid="job-cadence-hint">{{ cadenceHint(j.refresh) }}</p>
                              <p class="status" data-testid="job-refresh-counts">
                                Due now: {{ j.refresh.overdue }} · checked today: {{ j.refresh.usedToday }}
                              </p>
                              @if (breakdown(j.refresh); as b) { <p class="status">{{ b }}</p> }
                            </div>
                          </div>
                        }
                        @case ('backup') {
                          <div class="row">
                            <label for="job-backup-hour">At</label>
                            <select id="job-backup-hour" data-testid="job-hour-backup"
                                    [disabled]="busy() || job.managedByConfig || !wholeDays(job)"
                                    (change)="setHour('backup', +$any($event.target).value)">
                              <option [value]="-1" [selected]="(job.hour ?? null) === null">Any time</option>
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
                          <mat-slide-toggle [checked]="job.enabled" [disabled]="busy() || trashAsk() !== null" data-testid="job-trash-auto"
                                            (change)="onTrashToggle($event.checked, $event.source)">
                            Turn automatic cleaning on
                          </mat-slide-toggle>
                          <div class="row">
                            <label for="job-trash-hour">Run at</label>
                            <select id="job-trash-hour" data-testid="job-hour-trash" [disabled]="busy()"
                                    (change)="setHour('trash', +$any($event.target).value)">
                              @for (h of hours; track h) { <option [value]="h" [selected]="h === job.hour">{{ hourLabel(h) }}</option> }
                            </select>
                          </div>
                          @if (!job.enabled) {
                            <p class="hint">Off: nothing is removed unless you choose "Empty trash now" or "Clean bundles now" in the Trash card.</p>
                          }
                          @if (trashAsk(); as ask) {
                            <div class="confirm" role="alertdialog" aria-labelledby="job-trash-confirm-h" data-testid="job-trash-confirm">
                              <h5 id="job-trash-confirm-h">Turn automatic cleaning on?</h5>
                              <p>{{ trashConfirmText(ask, job.hour ?? job.defaultHour ?? 4) }}</p>
                              <div class="actions">
                                <button mat-flat-button color="warn" type="button" data-testid="job-trash-confirm-yes" [disabled]="busy()"
                                        (click)="confirmTrashOn()">Turn on</button>
                                <button mat-button type="button" data-testid="job-trash-confirm-no" [disabled]="busy()"
                                        (click)="cancelTrashOn()">Cancel</button>
                              </div>
                            </div>
                          }
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
                          <div class="row rhythm"><span class="k">When</span><span>{{ rhythmOf(job) }}</span></div>
                        }
                      }

                      <div class="runs">
                        <span>Last run: {{ lastRun(job) }}</span>
                        <span>Next run: {{ nextRun(job) }}</span>
                      </div>
                    </li>
                  }
                </ul>
              </section>
            }
          </div>
        } @else if (!error()) {
          <p class="muted">Loading…</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .clock { font-size: 14px; margin: 4px 0 6px; }
    .groups { container-type: inline-size; }
    .group { margin: 14px 0 0; }
    .group h4 { margin: 0 0 6px; font-size: 14px; font-weight: 500; text-transform: uppercase; letter-spacing: 0.04em; color: #b0b0b0; }
    .group > .what { margin: 0 0 6px; }

    /* Library scans: one row per library, the columns lined up (1.35.0, owner: "not neat"). */
    .scans { border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 6px; padding: 2px 10px; }
    .scan-head, .scan-row {
      display: grid; align-items: center; gap: 4px 12px; padding: 6px 0;
      grid-template-columns: minmax(120px, 1.3fr) 130px 100px 120px minmax(130px, 1fr) minmax(130px, 1fr);
      font-size: 13px;
    }
    .scan-head { color: #999; font-size: 12px; border-bottom: 1px solid rgba(255, 255, 255, 0.08); }
    .scan-row + .scan-row { border-top: 1px solid rgba(255, 255, 255, 0.05); }
    .lib { font-weight: 500; overflow-wrap: anywhere; font-size: 14px; }

    /* The other jobs: cards of equal height per row, the runs line at the bottom of each. */
    .jobs { list-style: none; padding: 0; margin: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(min(100%, 300px), 1fr)); gap: 10px; align-items: stretch; }
    .jobs li { border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; display: flex; flex-direction: column; }
    /* The refresh card has a row of its own, so its neighbours keep their own height. */
    .jobs li.wide { grid-column: 1 / -1; }
    @container (min-width: 760px) {
      .refresh { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 0 20px; }
    }
    .head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .badge { font-size: 12px; color: #4caf50; }
    .what, .hint, .muted, .runs, .status { color: #999; font-size: 13px; margin: 4px 0; }
    /* Label and value side by side in every card (the value never wraps under its label); stacked on a phone. */
    .row { display: grid; grid-template-columns: 150px minmax(0, 1fr); align-items: center; gap: 4px 8px; margin: 6px 0; font-size: 14px; }
    .row > select { justify-self: start; }
    .rhythm { font-size: 13px; grid-template-columns: 56px minmax(0, 1fr); }
    .rhythm .k { color: #999; }
    select { font: inherit; font-size: 14px; padding: 4px 6px; max-width: 100%; }
    .runs { display: flex; flex-wrap: wrap; gap: 4px 16px; margin-top: auto; padding-top: 6px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
    .confirm { border: 1px solid rgba(244, 67, 54, 0.5); border-radius: 6px; padding: 10px 12px; margin: 10px 0; font-size: 14px; }
    .confirm h5 { margin: 0 0 6px; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
    /* The table needs about 800 px: narrower (by the section's own width, not the window's - 1.35.0, the Administration column
       is narrower than the window) each library is a wrapped block with labelled times. */
    @container (max-width: 860px) {
      .scan-head { display: none; }
      .scan-row { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; }
      .lib { flex-basis: 100%; }
    }
    @media (max-width: 760px) {
      .row { grid-template-columns: minmax(0, 1fr); }
    }
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

  /** Raised after a save that changed the trash's automatic cleaning (switch or hour), so the Trash card refreshes its status line. */
  readonly trashChanged = output<void>();

  /** The trash preview shown while the admin confirms turning automatic cleaning on (null when no confirm is open). */
  readonly trashAsk = signal<TrashOverviewDto | null>(null);
  /** The slide toggle that asked, put back if the admin cancels. */
  private trashToggle: { checked: boolean } | null = null;

  readonly offset = computed(() => offsetLabel(this.jobs()?.utcOffsetMinutes ?? 0));
  readonly paceSources = PACE_SOURCES;
  readonly cadenceHint = cadenceHint;

  /** The library scan rows (one table). */
  readonly scans = computed(() => (this.jobs()?.jobs ?? []).filter(j => j.key === 'library-scan'));

  /** The other jobs in their groups, server order inside a group; empty groups are left out. */
  readonly groups = computed(() => {
    const others = (this.jobs()?.jobs ?? []).filter(j => j.key !== 'library-scan');
    const known = new Set(JOB_GROUPS.flatMap(g => g.jobs));
    return JOB_GROUPS
      .map(g => ({
        key: g.key,
        title: g.title,
        jobs: others.filter(j => g.jobs.includes(j.key) || (g.key === 'maintenance' && !known.has(j.key))),
      }))
      .filter(g => g.jobs.length > 0);
  });

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
    this.save(this.api.setJobHour(key, hour < 0 ? null : hour), key === 'trash');
  }

  /**
   * Automatic trash cleaning is switched here (1.32.0; it was the Trash card's switch). Turning it on is the approval of automatic
   * purging, so it asks first with what the first run would remove; turning it off saves at once. Same endpoint and audit as before.
   */
  onTrashToggle(on: boolean, source: { checked: boolean }): void {
    if (!on) {
      this.saveTrash(false);
      return;
    }
    this.trashToggle = source;
    this.error.set(null);
    this.api.getTrash().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: o => this.trashAsk.set(o),
      error: (e: ApiError) => {
        this.resetTrashToggle();
        this.error.set(e?.message || 'The trash could not be read. Automatic cleaning stays off.');
      },
    });
  }

  trashConfirmText(o: TrashOverviewDto, hour: number): string {
    return `Every day at ${hourLabel(hour)} (server time) the trash is emptied and unused files are removed. `
      + `The first run removes what is ready now: ${trashCountsText(o.total)}, and ${o.bundles.files} unused `
      + `${o.bundles.files === 1 ? 'file' : 'files'}. Libraries with a hold are skipped. This cannot be undone.`;
  }

  confirmTrashOn(): void {
    if (this.busy()) return;
    this.trashToggle = null;
    this.saveTrash(true);
  }

  cancelTrashOn(): void {
    this.trashAsk.set(null);
    this.resetTrashToggle();
  }

  private resetTrashToggle(): void {
    if (this.trashToggle) {
      this.trashToggle.checked = this.jobs()?.jobs.find(j => j.key === 'trash')?.enabled ?? false;
      this.trashToggle = null;
    }
  }

  private saveTrash(automaticCleaning: boolean): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.updateTrashSettings({ automaticCleaning }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.trashAsk.set(null);
        // The jobs list carries the trash row's enabled flag and next run: read it again, then tell the Trash card.
        this.api.getScheduledJobs().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
          next: dto => {
            this.jobs.set(dto);
            this.busy.set(false);
            this.trashChanged.emit();
          },
          error: () => {
            this.busy.set(false);
            this.error.set('Saved, but the list could not be reloaded.');
            this.trashChanged.emit();
          },
        });
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.trashAsk.set(null);
        this.resetTrashToggle();
        this.error.set(e?.message || 'Could not save. The previous setting is kept.');
      },
    });
  }

  /** "Check ongoing series": "pace" follows each series' pace; a number of days is a fixed rhythm. */
  setOngoing(value: string): void {
    this.setCadence(value === 'pace' ? { followPace: true } : { followPace: false, ongoingDays: +value });
  }

  setCadence(request: UpdateRefreshCadenceRequest): void {
    this.save(this.api.setRefreshCadence(request));
  }

  private save(call: Observable<ScheduledJobsDto>, trash = false): void {
    this.busy.set(true);
    this.error.set(null);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: dto => {
        this.jobs.set(dto);
        this.busy.set(false);
        if (trash) this.trashChanged.emit();
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Could not save. The previous setting is kept.');
        this.ngOnInit();
      },
    });
  }
}
