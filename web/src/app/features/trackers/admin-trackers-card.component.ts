import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';

import { AdminTrackerAccountDto, AdminTrackerSettingsDto } from '../../core/api/api-types';
import { AdminTrackerPanelComponent } from './admin-tracker-panel.component';
import { TrackersApiService } from './trackers-api.service';
import { TRACKER_SETUP_DOCS_URL, failureText, trackerName } from './tracker-texts';

/**
 * Administration > Trackers (1.43.0, `<app-admin-trackers-card />`): per tracker (MyAnimeList, MangaUpdates) the allow switch
 * behind its admin consent text and, for MyAnimeList, this server's app registration; then every user's connected account (user,
 * tracker, state) with Disconnect after a confirm. The admin never sees a token or the user's account on the tracker.
 */
@Component({
  selector: 'app-admin-trackers-card',
  standalone: true,
  imports: [DatePipe, MatButtonModule, MatCardModule, MatDividerModule, MatIconModule, AdminTrackerPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="admin-trackers-card">
      <mat-card-header>
        <mat-card-title>Trackers</mat-card-title>
        <mat-card-subtitle>Let users sync their reading progress to their own lists</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">
          Each user connects their own account on the Tracking page, after you allow the tracker here. Nothing is sent for users who have
          not connected one.
          <a [href]="docsUrl" target="_blank" rel="noopener noreferrer" data-testid="admin-trackers-help">How to set this up</a>
        </p>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else {
          @for (s of trackers(); track s.slug; let last = $last) {
            <app-admin-tracker-panel [settings]="s" (saved)="onSaved($event)" />
            @if (!last) { <mat-divider /> }
          }
          <mat-divider />
          <h4>Connected accounts</h4>
          @if (accounts().length === 0) {
            <p class="muted" data-testid="admin-trackers-no-accounts">No user has connected an account.</p>
          } @else {
            <ul class="accounts" data-testid="admin-trackers-accounts">
              @for (a of accounts(); track a.userId + a.tracker) {
                <li [attr.data-testid]="'admin-tracker-account-' + a.tracker + '-' + a.userId">
                  <div class="head">
                    <span class="user">{{ a.username }}</span>
                    <span class="tracker">{{ name(a.tracker) }}</span>
                    <span class="status" [attr.data-state]="a.state">{{ stateText(a) }}</span>
                  </div>
                  <div class="meta">
                    <span>Connected {{ a.connectedAt | date: 'mediumDate' }}</span>
                    <span>Last sync: {{ a.lastSuccessAt ? (a.lastSuccessAt | date: 'medium') : 'none yet' }}</span>
                  </div>
                  @if (a.state !== 'DisconnectedByAdmin') {
                    @if (confirming() === a.tracker + ':' + a.userId) {
                      <div class="confirm" role="alertdialog" data-testid="admin-tracker-disconnect-confirm">
                        <p>Disconnect {{ a.username }}'s {{ name(a.tracker) }} account? Its sign-in is deleted and syncing stops.
                          The user sees that an admin disconnected it and can connect again.</p>
                        <div class="actions">
                          <button mat-flat-button color="warn" type="button" [disabled]="busy()" (click)="disconnect(a)"
                                  data-testid="admin-tracker-disconnect-yes">Disconnect</button>
                          <button mat-button type="button" [disabled]="busy()" (click)="confirming.set(null)">Cancel</button>
                        </div>
                      </div>
                    } @else {
                      <button mat-stroked-button type="button" [disabled]="busy()" (click)="confirming.set(a.tracker + ':' + a.userId)"
                              [attr.data-testid]="'admin-tracker-disconnect-' + a.tracker + '-' + a.userId">Disconnect</button>
                    }
                  }
                </li>
              }
            </ul>
          }
        }
        @if (error(); as e) { <p class="error" role="alert">{{ e }}</p> }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .hint, .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; overflow-wrap: anywhere; }
    .hint a { color: var(--mp-accent); }
    h4 { margin: 12px 0 6px; }
    mat-divider { margin: 8px 0; }
    .accounts { list-style: none; padding: 0; margin: 4px 0 0; display: flex; flex-direction: column; gap: 8px; }
    .accounts li { border: 1px solid rgb(var(--mp-ink-rgb) / 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; }
    .head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .user { font-weight: 500; overflow-wrap: anywhere; }
    .tracker { font-size: 13px; color: var(--mp-text-secondary); }
    .status { font-size: 12px; border-radius: 10px; padding: 1px 8px; background: rgb(var(--mp-success-strong-rgb) / 0.15); color: var(--mp-success); }
    .status:not([data-state="Ok"]) { background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-secondary); }
    .meta { display: flex; flex-wrap: wrap; gap: 2px 14px; font-size: 13px; color: var(--mp-text-secondary); margin: 4px 0; }
    .accounts button { margin-top: 4px; }
    .confirm { border: 1px solid rgb(var(--mp-error-strong-rgb) / 0.5); border-radius: 6px; padding: 8px 10px; margin: 8px 0 0; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
  `],
})
export class AdminTrackersCardComponent implements OnInit {
  private readonly api = inject(TrackersApiService);

  readonly docsUrl = TRACKER_SETUP_DOCS_URL;
  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly trackers = signal<AdminTrackerSettingsDto[]>([]);
  readonly accounts = signal<AdminTrackerAccountDto[]>([]);
  /** `<tracker>:<userId>` of the account whose Disconnect is being confirmed. */
  readonly confirming = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly name = trackerName;

  ngOnInit(): void {
    this.api.adminList().subscribe({
      next: (list) => { this.trackers.set(list); this.loading.set(false); },
      error: (err: unknown) => { this.loading.set(false); this.error.set(failureText(err)); },
    });
    this.loadAccounts();
  }

  loadAccounts(): void {
    this.api.adminAccounts().subscribe({
      next: (list) => this.accounts.set(list),
      error: (err: unknown) => this.error.set(failureText(err)),
    });
  }

  onSaved(updated: AdminTrackerSettingsDto): void {
    this.trackers.update((list) => list.map((s) => (s.slug === updated.slug ? updated : s)));
  }

  stateText(a: AdminTrackerAccountDto): string {
    switch (a.state) {
      case 'Ok': return 'Syncing';
      case 'NeedsReconnect': return 'Needs to connect again';
      case 'DisconnectedByAdmin': return 'Disconnected by an admin';
      default: return a.state;
    }
  }

  disconnect(a: AdminTrackerAccountDto): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.adminDisconnect(a.tracker, a.userId).subscribe({
      next: () => {
        this.busy.set(false);
        this.confirming.set(null);
        this.loadAccounts();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(failureText(err, a.tracker));
      },
    });
  }
}
