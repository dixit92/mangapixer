import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';

import { TrackerAccountPanelComponent } from './tracker-account-panel.component';
import { TrackerStateService } from './tracker-state.service';
import { TrackersApiService } from './trackers-api.service';
import { TRACKER_SYNC_DOCS_URL, failureText } from './tracker-texts';

/**
 * Settings > Connected accounts (1.43.0, `<app-connected-accounts-card />`): the trackers an admin allows on this server, each
 * with the user's own connection (`app-tracker-account-panel`), plus any account the user still has on a tracker that was turned
 * off. Reloads the shared overview after every change so the browse selection's "Sync to trackers" follows.
 */
@Component({
  selector: 'app-connected-accounts-card',
  standalone: true,
  imports: [MatCardModule, MatDividerModule, TrackerAccountPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card class="connected-accounts-card" data-testid="connected-accounts-card">
      <mat-card-header>
        <mat-card-title>Connected accounts</mat-card-title>
        <mat-card-subtitle>Sync your reading progress to your own tracker lists</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">
          When you connect an account, MangaPixer updates your list there as you read. Private libraries and doujinshi are not
          synced unless you turn sync on for a folder or series (Select in a library, then "Sync to trackers").
          <a [href]="docsUrl" target="_blank" rel="noopener noreferrer" data-testid="connected-accounts-help">How tracker sync works</a>
        </p>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (shown().length === 0) {
          <p class="muted" data-testid="connected-accounts-none">
            No tracker is allowed on this server yet. An admin can allow one in Administration > Trackers.
          </p>
        } @else {
          @for (t of shown(); track t.slug; let last = $last) {
            <app-tracker-account-panel [tracker]="t" (changed)="reload()" />
            @if (!last) { <mat-divider /> }
          }
        }
        @if (error(); as e) { <p class="error" role="alert">{{ e }}</p> }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .hint, .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; overflow-wrap: anywhere; }
    .hint a { color: var(--mp-accent); }
    mat-divider { margin: 10px 0; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
  `],
})
export class ConnectedAccountsCardComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly state = inject(TrackerStateService);

  readonly docsUrl = TRACKER_SYNC_DOCS_URL;
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  /** Allowed trackers, and trackers the user still has an account on. */
  readonly shown = computed(() => (this.state.overview()?.trackers ?? []).filter((t) => t.allowed || !!t.account));

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.api.overview().subscribe({
      next: (o) => { this.state.set(o); this.loading.set(false); },
      error: (err: unknown) => { this.loading.set(false); this.error.set(failureText(err)); },
    });
  }
}
