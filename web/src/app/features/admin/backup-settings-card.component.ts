import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatRadioModule } from '@angular/material/radio';

import { ApiService } from '../../core/api/api.service';
import {
  ApiError,
  BackupSettingsDto,
  BackupSettingsUpdateResultDto,
  BackupSnapshotMoveStatusDto,
  UpdateBackupSettingsRequest,
} from '../../core/api/api-types';
import { BackupSnapshotMoveStatusComponent, formatBytes } from './backup-snapshot-move-status.component';

/** Interval presets offered in the schedule select (hours). */
export const BACKUP_INTERVAL_PRESETS: readonly { hours: number; label: string }[] = [
  { hours: 6, label: 'Every 6 hours' },
  { hours: 12, label: 'Every 12 hours' },
  { hours: 24, label: 'Daily' },
  { hours: 48, label: 'Every 2 days' },
  { hours: 168, label: 'Weekly' },
];

type PendingAction = 'test' | 'save';

/** How often the card polls a running snapshot move (ms). */
export const SNAPSHOT_MOVE_POLL_MS = 1000;

/**
 * Backup settings card (1.22.0): scheduled backups on/off, interval, how many
 * are kept, and WHERE the rotating snapshots live (the default folder inside
 * the data folder, or a custom folder such as an archive disk or NAS share).
 *
 * Every field shows its source; a field pinned by server configuration renders
 * read-only. Changing the location (Test or Save) asks for the admin's current
 * password first, because it is the one setting that decides where a full copy
 * of the database is written. On a location change the existing rotating
 * snapshots can be moved along (1.23.0, checked by default): the server moves
 * them in the background and the card shows the progress and any snapshot
 * that stayed behind. Unchecked, they stay where they are, unmanaged.
 *
 * Standalone and self-loading, embedded as `<app-backup-settings-card />` in the
 * admin screen; `(changed)` lets the host refresh its Backups status and list.
 */
@Component({
  selector: 'app-backup-settings-card',
  standalone: true,
  imports: [MatButtonModule, MatCardModule, MatCheckboxModule, MatIconModule, MatRadioModule, BackupSnapshotMoveStatusComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Backup settings</mat-card-title>
        <mat-card-subtitle>Schedule, retention and where snapshots are kept</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (settings(); as s) {
          @if (locationBroken()) {
            <div class="banner" role="alert" data-testid="location-banner">
              <mat-icon inline>error</mat-icon>
              Backup location unavailable. Scheduled backups are paused until the
              folder is reachable again (is the disk or share mounted?).
            </div>
          }

          <mat-checkbox
            [checked]="draftEnabled()"
            [disabled]="managed(s.enabledSource) || busy()"
            (change)="draftEnabled.set($event.checked)">
            Scheduled backups
          </mat-checkbox>
          @if (managed(s.enabledSource)) { <span class="managed">Managed by server configuration</span> }

          <div class="row">
            <label for="bk-interval">Frequency</label>
            <select id="bk-interval"
                    [disabled]="managed(s.intervalHoursSource) || busy()"
                    (change)="onIntervalChoice($any($event.target).value)">
              @for (p of presets; track p.hours) {
                <option [value]="p.hours" [selected]="intervalChoice() === '' + p.hours">{{ p.label }}</option>
              }
              <option value="custom" [selected]="intervalChoice() === 'custom'">Custom…</option>
            </select>
            @if (intervalChoice() === 'custom') {
              <input id="bk-interval-hours" type="number" min="1" max="720" step="1"
                     aria-label="Hours between backups"
                     [value]="draftInterval()"
                     [disabled]="managed(s.intervalHoursSource) || busy()"
                     (input)="draftInterval.set(+$any($event.target).value)"> hours
            }
            @if (managed(s.intervalHoursSource)) { <span class="managed">Managed by server configuration</span> }
          </div>

          <div class="row">
            <label for="bk-retention">Keep the newest</label>
            <input id="bk-retention" type="number" min="1" max="100" step="1"
                   [value]="draftRetention()"
                   [disabled]="managed(s.retentionCountSource) || busy()"
                   (input)="draftRetention.set(+$any($event.target).value)"> snapshots
            @if (managed(s.retentionCountSource)) { <span class="managed">Managed by server configuration</span> }
          </div>
          @if (retentionWarning() > 0) {
            <p class="warn" data-testid="retention-warning">
              {{ retentionWarning() }} oldest snapshot(s) will be deleted after the next backup.
            </p>
          }

          <h4>Location</h4>
          <mat-radio-group
            [value]="draftMode()"
            [disabled]="!s.locationChangeAllowed || busy() || moveRunning()"
            (change)="draftMode.set($event.value)">
            <mat-radio-button value="default">Default (inside the data folder)</mat-radio-button>
            <mat-radio-button value="custom">Custom folder</mat-radio-button>
          </mat-radio-group>
          @if (!s.locationChangeAllowed) { <span class="managed">Managed by server configuration</span> }
          @if (draftMode() === 'custom') {
            <input class="location" type="text" aria-label="Custom backup folder"
                   [placeholder]="placeholder()"
                   [value]="draftLocation()"
                   [disabled]="!s.locationChangeAllowed || busy() || moveRunning()"
                   (input)="draftLocation.set($any($event.target).value)">
            <p class="hint">{{ locationHelp() }}</p>
          }
          @if (moveOffer()) {
            <mat-checkbox
              data-testid="move-snapshots"
              [checked]="moveSnapshots()"
              [disabled]="busy()"
              (change)="moveSnapshots.set($event.checked)">
              Move existing snapshots ({{ s.rotatingSnapshotCount }} {{ s.rotatingSnapshotCount === 1 ? 'file' : 'files' }}, {{ size(s.rotatingSnapshotBytes ?? 0) }})
            </mat-checkbox>
            <p class="hint">
              @if (moveSnapshots()) {
                They are copied to the new location, checked, and only then removed from the old one.
                Afterwards the newest {{ draftRetention() }} are kept.
              } @else {
                They stay in the current location and are no longer listed or pruned.
              }
            </p>
          }
          <p class="hint" data-testid="safety-note">
            Safety snapshots taken before a database upgrade (pre-migration) or a restore (pre-restore)
            always stay in the data folder, newest 3 of each: an upgrade or restore must not depend on a
            custom folder that might be unavailable at that moment (for example an unmounted share).
          </p>

          @if (pending(); as action) {
            <div class="reauth" data-testid="reauth">
              <label for="bk-password">Current password</label>
              <input id="bk-password" type="password" autocomplete="current-password"
                     [value]="password()"
                     (input)="password.set($any($event.target).value)"
                     (keydown.enter)="confirm()">
              <button mat-flat-button color="primary" type="button"
                      [disabled]="!password() || busy()" (click)="confirm()">
                {{ action === 'test' ? 'Test folder' : 'Save' }}
              </button>
              <button mat-button type="button" [disabled]="busy()" (click)="cancel()">Cancel</button>
              <p class="hint">Changing where backups are written requires your password.</p>
            </div>
          }

          <div class="actions">
            @if (draftMode() === 'custom' && s.locationChangeAllowed) {
              <button mat-stroked-button type="button" [disabled]="busy() || !draftLocation().trim()"
                      (click)="test()">Test</button>
            }
            <button mat-raised-button color="primary" type="button"
                    [disabled]="busy() || !dirty() || !valid()" (click)="save()">
              {{ busy() ? 'Saving…' : 'Save' }}
            </button>
          </div>

          @if (testResult(); as r) {
            <p class="ok" data-testid="test-result">
              <mat-icon inline>check_circle</mat-icon>
              The folder is usable.
              @if (r.willCreate) { The last folder will be created when you save. }
              @if (r.warnings.includes('low_free_space')) {
                <span class="warn">Warning: little free space for a database copy.</span>
              }
            </p>
          }
          @if (moveStatus(); as m) {
            <app-backup-snapshot-move-status [status]="m" />
          }
          @if (movedNotice()) {
            <p class="notice" data-testid="moved-notice">
              Saved. Existing snapshots stay in the previous location and are no longer managed.
              <button mat-stroked-button type="button" [disabled]="busy()" (click)="backUpNow()">Back up now</button>
            </p>
          }
          @if (error()) {
            <div class="error" role="alert">{{ error() }}</div>
          }
        } @else {
          <div class="error" role="alert">{{ error() }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 10px 0; font-size: 14px; }
    .row label { min-width: 110px; }
    input[type=number] { width: 72px; }
    select, input { font: inherit; padding: 4px 6px; }
    .location { width: 100%; max-width: 480px; margin-top: 8px; box-sizing: border-box; }
    mat-radio-group { display: flex; flex-wrap: wrap; gap: 12px; }
    .managed { color: #999; font-size: 12px; margin-left: 8px; }
    .hint, .muted { color: #999; font-size: 13px; margin: 6px 0; }
    .warn { color: #ffb300; font-size: 13px; margin: 4px 0; }
    .ok { color: #4caf50; font-size: 14px; }
    .notice { font-size: 14px; display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .banner { background: rgba(244, 67, 54, 0.15); color: #f44336; padding: 8px 12px; border-radius: 4px; margin-bottom: 12px; font-size: 14px; }
    .reauth { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 12px 0; }
    .actions { display: flex; gap: 8px; margin-top: 12px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
    h4 { margin: 16px 0 6px; }
  `],
})
export class BackupSettingsCardComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  /** Snapshots currently listed by the host (drives the retention-lowering warning). */
  readonly snapshotCount = input(0);
  /** Emitted after a save or a "Back up now", so the host refreshes its Backups card. */
  readonly changed = output<void>();

  readonly presets = BACKUP_INTERVAL_PRESETS;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly settings = signal<BackupSettingsDto | null>(null);

  readonly draftEnabled = signal(true);
  readonly draftInterval = signal(24);
  readonly customInterval = signal(false);
  readonly draftRetention = signal(7);
  readonly draftMode = signal<'default' | 'custom'>('default');
  readonly draftLocation = signal('');

  readonly pending = signal<PendingAction | null>(null);
  readonly password = signal('');
  readonly testResult = signal<BackupSettingsUpdateResultDto | null>(null);
  readonly movedNotice = signal(false);
  /** Move existing snapshots along with a location change (checked by default). */
  readonly moveSnapshots = signal(true);
  /** Last known snapshot move (null while none ran since the server started). */
  readonly moveStatus = signal<BackupSnapshotMoveStatusDto | null>(null);

  readonly moveRunning = computed(() => this.moveStatus()?.state === 'running');

  readonly intervalChoice = computed(() =>
    this.customInterval() || !this.presets.some((p) => p.hours === this.draftInterval())
      ? 'custom'
      : String(this.draftInterval()));

  readonly locationBroken = computed(() => {
    const status = this.settings()?.locationStatus;
    return status === 'unavailable' || status === 'invalid';
  });

  readonly locationChanged = computed(() => {
    const s = this.settings();
    if (!s || !s.locationChangeAllowed) return false;
    if (this.draftMode() !== s.locationKind) return true;
    return this.draftMode() === 'custom' && this.draftLocation().trim() !== (s.customLocation ?? '');
  });

  readonly dirty = computed(() => {
    const s = this.settings();
    if (!s) return false;
    return this.draftEnabled() !== s.enabled
      || this.draftInterval() !== s.intervalHours
      || this.draftRetention() !== s.retentionCount
      || this.locationChanged();
  });

  readonly valid = computed(() => {
    const hours = this.draftInterval();
    const keep = this.draftRetention();
    const locationOk = this.draftMode() === 'default' || this.draftLocation().trim().length > 0;
    return hours >= 1 && hours <= 720 && Number.isInteger(keep) && keep >= 1 && keep <= 100 && locationOk;
  });

  /** Offer the move when the location changes and the current one holds snapshots. */
  readonly moveOffer = computed(() =>
    this.locationChanged() && (this.settings()?.rotatingSnapshotCount ?? 0) > 0);

  /** How many existing snapshots a lowered retention would delete after the next run. */
  readonly retentionWarning = computed(() => {
    const s = this.settings();
    if (!s || this.draftRetention() >= s.retentionCount) return 0;
    return Math.max(0, this.snapshotCount() - this.draftRetention());
  });

  readonly placeholder = computed(() =>
    this.settings()?.platform === 'windows'
      ? 'D:\\MangaPixer-backups or \\\\nas\\archive\\mangapixer'
      : '/backups');

  readonly locationHelp = computed(() =>
    this.settings()?.platform === 'windows'
      ? 'Any local or network folder. Prefer a \\\\server\\share path over a mapped drive letter: mapped drives may not be connected when MangaPixer starts. Only the last folder is created automatically.'
      : 'In Docker or Unraid, first add a bind mount for the folder (for example /mnt/user/archive/mangapixer-backups:/backups); it must be writable by PUID/PGID. Only the last folder is created automatically.');

  ngOnInit(): void {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      if (this.pollTimer) clearTimeout(this.pollTimer);
    });
    this.api.getBackupSnapshotMove().subscribe({
      next: (status) => this.onMoveStatus(status, false),
      error: () => undefined,
    });
    this.api.getBackupSettings().subscribe({
      next: (dto) => {
        this.apply(dto);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load the backup settings');
        this.loading.set(false);
      },
    });
  }

  size(bytes: number): string {
    return formatBytes(bytes);
  }

  managed(source: string): boolean {
    return source === 'configuration';
  }

  onIntervalChoice(value: string): void {
    if (value === 'custom') {
      this.customInterval.set(true);
      return;
    }
    this.customInterval.set(false);
    this.draftInterval.set(Number(value));
  }

  test(): void {
    this.testResult.set(null);
    this.error.set(null);
    this.pending.set('test');
  }

  save(): void {
    if (!this.dirty() || !this.valid()) return;
    this.testResult.set(null);
    this.error.set(null);
    if (this.locationChanged()) {
      this.pending.set('save');
      return;
    }
    this.send(this.buildRequest(false), false);
  }

  confirm(): void {
    const action = this.pending();
    if (!action || !this.password()) return;
    this.send({ ...this.buildRequest(action === 'test'), currentPassword: this.password() }, action === 'test');
  }

  cancel(): void {
    this.pending.set(null);
    this.password.set('');
  }

  backUpNow(): void {
    this.busy.set(true);
    this.api.runRotatingBackupNow().subscribe({
      next: () => {
        this.busy.set(false);
        this.movedNotice.set(false);
        this.reload();
        this.changed.emit();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err?.message || 'Backup failed');
        this.reload();
      },
    });
  }

  private buildRequest(validateOnly: boolean): UpdateBackupSettingsRequest {
    const s = this.settings()!;
    const location = { mode: this.draftMode(), customLocation: this.draftLocation().trim() || undefined };
    if (validateOnly) {
      return { location: { mode: 'custom', customLocation: this.draftLocation().trim() }, validateOnly: true };
    }
    const request: UpdateBackupSettingsRequest = {};
    if (!this.managed(s.enabledSource) && this.draftEnabled() !== s.enabled) request.enabled = this.draftEnabled();
    if (!this.managed(s.intervalHoursSource) && this.draftInterval() !== s.intervalHours) request.intervalHours = this.draftInterval();
    if (!this.managed(s.retentionCountSource) && this.draftRetention() !== s.retentionCount) request.retentionCount = this.draftRetention();
    if (this.locationChanged()) request.location = location.mode === 'default' ? { mode: 'default' } : location;
    if (this.moveOffer()) request.moveExistingSnapshots = this.moveSnapshots();
    return request;
  }

  private send(request: UpdateBackupSettingsRequest, validateOnly: boolean): void {
    this.busy.set(true);
    this.api.updateBackupSettings(request).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.pending.set(null);
        this.password.set('');
        if (validateOnly) {
          this.testResult.set(result);
          return;
        }
        this.apply(result.settings);
        this.movedNotice.set(result.locationChanged && !result.snapshotMoveStarted);
        this.changed.emit();
        if (result.snapshotMoveStarted) this.pollMove();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.password.set('');
        this.error.set(err?.message || 'Failed to save the backup settings');
      },
    });
  }

  private pollMove(): void {
    if (this.destroyed) return;
    this.api.getBackupSnapshotMove().subscribe({
      next: (status) => this.onMoveStatus(status, true),
      error: () => this.schedulePoll(),
    });
  }

  private schedulePoll(): void {
    if (this.destroyed) return;
    this.pollTimer = setTimeout(() => this.pollMove(), SNAPSHOT_MOVE_POLL_MS);
  }

  private onMoveStatus(status: BackupSnapshotMoveStatusDto, polling: boolean): void {
    if (status.state === 'idle') {
      this.moveStatus.set(null);
      return;
    }
    this.moveStatus.set(status);
    if (status.state === 'running') {
      this.schedulePoll();
    } else if (polling) {
      // Finished: refresh the counts here and the host's snapshot list.
      this.reload();
      this.changed.emit();
    }
  }

  private reload(): void {
    this.api.getBackupSettings().subscribe({ next: (dto) => this.apply(dto), error: () => undefined });
  }

  private apply(dto: BackupSettingsDto): void {
    this.settings.set(dto);
    this.draftEnabled.set(dto.enabled);
    this.draftInterval.set(dto.intervalHours);
    this.customInterval.set(!this.presets.some((p) => p.hours === dto.intervalHours));
    this.draftRetention.set(dto.retentionCount);
    this.draftMode.set(dto.locationKind);
    this.draftLocation.set(dto.customLocation ?? '');
    this.moveSnapshots.set(true);
  }
}
