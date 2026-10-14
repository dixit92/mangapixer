import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { TrackerStateService } from './tracker-state.service';
import { TrackersApiService } from './trackers-api.service';
import { takeTrackerCallback } from './tracker-callback';
import { failureText, trackerName } from './tracker-texts';

type CallbackView =
  | { kind: 'working' }
  | { kind: 'connected'; displayName: string | null }
  | { kind: 'failed'; message: string };

/**
 * The page a tracker's sign-in returns to (1.43.0, `/trackers/callback/<slug>`). The code and state were already taken out of
 * the address bar (`tracker-callback.ts`); this page posts them to the server once, then says in plain words whether the account
 * is connected, and links back to Settings. It never logs or shows the code or the state.
 */
@Component({
  selector: 'app-tracker-callback',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatCardModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <mat-card data-testid="tracker-callback">
        <mat-card-header>
          <mat-card-title>{{ name }}</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @switch (view().kind) {
            @case ('working') {
              <p class="row" role="status"><mat-spinner diameter="20" /> Connecting your {{ name }} account…</p>
            }
            @case ('connected') {
              <p class="ok" role="status" data-testid="tracker-callback-connected">
                <mat-icon inline>check_circle</mat-icon>
                Your {{ name }} account is connected{{ connectedAs() }}. MangaPixer now updates your list while you read.
              </p>
            }
            @case ('failed') {
              <p class="error" role="alert" data-testid="tracker-callback-error">{{ failure() }}</p>
            }
          }
          <a mat-raised-button color="primary" routerLink="/settings" data-testid="tracker-callback-settings">Back to Settings</a>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    :host { display: block; background: var(--mp-page); min-height: 100vh; }
    .page { max-width: 560px; margin: 0 auto; padding: 24px 16px; box-sizing: border-box; }
    .row { display: flex; align-items: center; gap: 10px; }
    .ok { color: var(--mp-success-strong); overflow-wrap: anywhere; }
    .error { color: var(--mp-error-strong); overflow-wrap: anywhere; }
    p { margin: 8px 0 16px; }
  `],
})
export class TrackerCallbackComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly state = inject(TrackerStateService);
  private readonly slug = inject(ActivatedRoute).snapshot.paramMap.get('slug') ?? '';

  readonly name = trackerName(this.slug);
  readonly view = signal<CallbackView>({ kind: 'working' });

  ngOnInit(): void {
    const params = takeTrackerCallback(this.slug);
    if (!params) {
      this.view.set({ kind: 'failed', message: `There is no ${this.name} sign-in to finish here. Start from Settings > Connected accounts.` });
      return;
    }
    if (params.error) {
      this.view.set({
        kind: 'failed',
        message: params.error === 'access_denied'
          ? `You did not allow MangaPixer on ${this.name}. Nothing was connected.`
          : `${this.name} did not finish the sign-in. Nothing was connected. Start again from Settings.`,
      });
      return;
    }
    if (!params.code || !params.state) {
      this.view.set({ kind: 'failed', message: failureText({ error: { error: 'invalid_state' } }, this.slug) });
      return;
    }
    this.api.connectFinish(this.slug, params.code, params.state).subscribe({
      next: (account) => {
        this.view.set({ kind: 'connected', displayName: account.displayName ?? null });
        this.state.refresh();
      },
      error: (err: unknown) => this.view.set({ kind: 'failed', message: failureText(err, this.slug) }),
    });
  }

  connectedAs(): string {
    const v = this.view();
    return v.kind === 'connected' && v.displayName ? ` as ${v.displayName}` : '';
  }

  failure(): string {
    const v = this.view();
    return v.kind === 'failed' ? v.message : '';
  }
}
