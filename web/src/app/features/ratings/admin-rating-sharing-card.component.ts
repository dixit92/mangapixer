import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxChange, MatCheckboxModule } from '@angular/material/checkbox';

import { ApiService } from '../../core/api/api.service';
import { ApiError } from '../../core/api/api-types';
import { EscapeCancelsDirective } from '../../shared/escape-cancels.directive';
import { RatingSharingService } from './rating-sharing.service';

/** The confirmation shown before shared ratings are turned on (owner decision 3b, 2026-11-10 - the exact words). */
export const SHARED_RATINGS_CONFIRMATION =
  "Every user's ratings will count in an average and a count on each series and volume - never who rated what. Ratings in a user's " +
  'Private libraries never count. Each user can turn sharing off for themselves in Settings.';

/**
 * Administration > "Shared ratings" (1.53.0, owner decision 3b): the server-wide switch, off until an admin turns it on. Turning it on
 * asks first (an inline confirmation with the owner's text, Cancel focused); turning it off needs no question - every average is hidden at
 * once and no rating is deleted. The change is written to the audit trail by the server. Sits with the Update Checker (the server-wide
 * switches column). Admins never see who rated what: this switch is their only control.
 */
@Component({
  selector: 'app-admin-rating-sharing-card',
  standalone: true,
  imports: [MatButtonModule, MatCardModule, MatCheckboxModule, EscapeCancelsDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="admin-rating-sharing-card">
      <mat-card-header>
        <mat-card-title>Shared ratings</mat-card-title>
        <mat-card-subtitle>Show other users an average of everyone's ratings</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else {
          <mat-checkbox [checked]="enabled() || confirming()" [disabled]="saving() || loadFailed()" data-testid="admin-rating-sharing"
                        (change)="toggle($event)">
            Share ratings between users
          </mat-checkbox>
          <p class="scope-note">
            When on, the Info sheet and the series page show the average rating and how many people rated, and the library can be
            sorted by it. Users who turn sharing off in Settings are not counted. Nothing leaves this server.
          </p>
          @if (confirming()) {
            <div class="confirm" role="alertdialog" aria-labelledby="rating-sharing-confirm-text" data-testid="admin-rating-sharing-confirm"
                 appEscapeCancels [escapeDisabled]="saving()" (escapeCancel)="cancel()">
              <p id="rating-sharing-confirm-text">{{ confirmation }}</p>
              <div class="confirm-actions">
                <button mat-button type="button" [disabled]="saving()" (click)="cancel()" data-testid="admin-rating-sharing-cancel">Cancel</button>
                <button mat-flat-button type="button" [disabled]="saving()" (click)="save(true)"
                        data-testid="admin-rating-sharing-confirm-yes">Turn on shared ratings</button>
              </div>
            </div>
          }
          @if (error()) {
            <div class="error" role="alert">{{ error() }}</div>
          }
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .scope-note { color: var(--mp-text-muted); font-size: 13px; margin: 8px 0 12px; }
    .muted { color: var(--mp-text-muted); font-size: 14px; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
    .confirm { margin: 8px 0; padding: 10px 12px; border-radius: 10px; background: var(--mp-surface-accent);
      border: 1px solid rgb(var(--mp-accent-rgb) / 0.4); }
    .confirm p { margin: 0 0 8px; font-size: 13px; color: var(--mp-text-secondary); }
    .confirm-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
    @media (pointer: coarse) {
      .confirm-actions button { min-height: 40px; }
    }
  `],
})
export class AdminRatingSharingCardComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly sharing = inject(RatingSharingService);

  readonly confirmation = SHARED_RATINGS_CONFIRMATION;
  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly saving = signal(false);
  readonly enabled = signal(false);
  readonly confirming = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.api.getAdminRatingSharing().subscribe({
      next: (dto) => {
        this.enabled.set(dto.enabled ?? false);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Could not load the shared ratings setting');
        this.loadFailed.set(true);
        this.loading.set(false);
      },
    });
  }

  /** Ticking asks first; unticking turns it off at once. */
  toggle(change: MatCheckboxChange): void {
    this.error.set(null);
    if (change.checked) {
      this.confirming.set(true);
      return;
    }
    if (this.confirming()) {
      this.confirming.set(false);
      return;
    }
    this.save(false);
  }

  cancel(): void {
    this.confirming.set(false);
  }

  save(enabled: boolean): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.api.setAdminRatingSharing(enabled).subscribe({
      next: (dto) => {
        const now = dto.enabled ?? enabled;
        this.enabled.set(now);
        this.sharing.serverChanged(now);
        this.confirming.set(false);
        this.saving.set(false);
      },
      error: (err: ApiError) => {
        this.saving.set(false);
        this.error.set(err?.message || 'Could not change the shared ratings setting');
      },
    });
  }
}
