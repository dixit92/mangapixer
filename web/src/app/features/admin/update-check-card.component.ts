import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ApiService } from '../../core/api/api.service';
import { ApiError, UpdateCheckStatusDto } from '../../core/api/api-types';

/**
 * Update Checker card (1.21.0): the admin opt-in for update notifications.
 *
 * The checker is OFF by default and is the single sanctioned outbound
 * third-party call in MangaPixer: only when the admin enables it does the
 * server compare the running version against the latest GitHub release
 * (`GET /api/v1/operations/update-check`; `PUT .../settings` toggles the flag).
 * The request carries no instance id, path, or telemetry — only version strings
 * come back, shown here and (when an update exists) in the app footer badge.
 *
 * Standalone and self-loading so it works both as a routed page and embedded as
 * `<app-update-check-card />` inside the admin screen — the same pattern as
 * `DebugLogCardComponent`.
 */
@Component({
  selector: 'app-update-check-card',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Update Checker</mat-card-title>
        <mat-card-subtitle>Notify me when a newer MangaPixer release exists</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (loadFailed()) {
          <div class="error" role="alert">{{ error() }}</div>
        } @else {
          <mat-checkbox
            [checked]="enabled()"
            [disabled]="saving()"
            (change)="setEnabled($event.checked)">
            Check for updates
          </mat-checkbox>
          <p class="scope-note">
            When on, the server makes one call to the GitHub Releases API to compare
            versions. No instance identifier, path, or telemetry is sent.
          </p>

          <div class="status" [attr.data-state]="statusState()">
            @if (!enabled()) {
              <span class="muted">Update checking is off.</span>
            } @else if (checking()) {
              <mat-spinner diameter="18" />
              <span>Checking…</span>
            } @else if (status()?.updateAvailable) {
              <mat-icon class="avail" inline>system_update_alt</mat-icon>
              <span class="avail">Update available: v{{ status()?.latestVersion }}</span>
            } @else if (status()?.latestVersion) {
              <mat-icon class="ok" inline>check_circle</mat-icon>
              <span>Up to date</span>
            } @else {
              <span class="muted">No check has run yet.</span>
            }
          </div>

          <p class="versions">
            Current: v{{ status()?.currentVersion }}
            @if (lastChecked()) {
              · Last checked {{ lastChecked() | date: 'short' }}
            }
          </p>

          <button
            mat-stroked-button
            type="button"
            [disabled]="!enabled() || checking() || saving()"
            (click)="checkNow()">
            Check now
          </button>

          @if (error()) {
            <div class="error" role="alert">{{ error() }}</div>
          }
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    .scope-note { color: #999; font-size: 13px; margin: 8px 0 12px; }
    .status { display: flex; align-items: center; gap: 8px; min-height: 24px; margin-bottom: 4px; }
    .status .avail { color: #ffb300; font-weight: 500; }
    .status .ok { color: #4caf50; }
    .versions { color: #999; font-size: 13px; margin: 4px 0 12px; }
    .muted { color: #999; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
  `],
})
export class UpdateCheckCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly error = signal<string | null>(null);
  readonly saving = signal(false);
  readonly checking = signal(false);
  readonly status = signal<UpdateCheckStatusDto | null>(null);

  readonly enabled = computed(() => this.status()?.enabled ?? false);
  readonly lastChecked = computed(() => this.status()?.lastChecked ?? null);
  readonly statusState = computed(() => {
    const s = this.status();
    if (!s?.enabled) return 'off';
    if (this.checking()) return 'checking';
    if (s.updateAvailable) return 'available';
    if (s.latestVersion) return 'up-to-date';
    return 'unknown';
  });

  ngOnInit(): void {
    this.api.getUpdateCheck().subscribe({
      next: (dto) => {
        this.status.set(dto);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load update-check status');
        this.loadFailed.set(true);
        this.loading.set(false);
      },
    });
  }

  setEnabled(enabled: boolean): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.api.setUpdateCheckEnabled(enabled).subscribe({
      next: (dto) => {
        this.status.set(dto);
        this.saving.set(false);
      },
      error: (err: ApiError) => {
        this.saving.set(false);
        this.error.set(err?.message || 'Failed to change the update-check setting');
      },
    });
  }

  checkNow(): void {
    if (this.checking() || !this.enabled()) return;
    this.checking.set(true);
    this.error.set(null);
    this.api.getUpdateCheck(true).subscribe({
      next: (dto) => {
        this.status.set(dto);
        this.checking.set(false);
      },
      error: (err: ApiError) => {
        this.checking.set(false);
        this.error.set(err?.message || 'Update check failed');
      },
    });
  }
}
