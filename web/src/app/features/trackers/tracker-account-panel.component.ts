import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';

import { TrackerSummaryDto, UpdateTrackerUserSettingsRequest } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';
import { TrackerConsentTextComponent } from './tracker-consent-text.component';
import { TrackerNavigationService } from './tracker-navigation.service';
import { TRACKER_USER_CONSENT_VERSION, accountProblem, failureText, revokeHint } from './tracker-texts';

/**
 * One tracker's account card on the Tracking page (1.43.0; Settings > Connected accounts until the owner's redesign), for the
 * signed-in user's own account:
 * - not connected (or connect again): a one-line pitch and "Connect <tracker>…", which shows the user consent text, a tick, then
 *   Connect - MyAnimeList: the server answers with its
 *   sign-in address and the browser goes there (the callback page finishes); MangaUpdates: a username + password form, the
 *   password field never echoed and cleared after every submit, an empty password refused here without a request;
 * - connected: "Connected as ...", Disconnect (after a confirm that says where else access ends), the user's settings (add
 *   series I start; private entries only where the tracker supports them). The series list is the page's own section.
 */
@Component({
  selector: 'app-tracker-account-panel',
  standalone: true,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatSlideToggleModule,
    TrackerConsentTextComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let t = tracker();
    <section class="tracker" [attr.data-testid]="'tracker-account-' + t.slug" [attr.aria-label]="t.name + ' account'">
      <div class="title">
        <h2>{{ t.name }}</h2>
        <span class="status" [attr.data-tone]="statusTone()" [attr.data-testid]="'tracker-status-' + t.slug">{{ statusLabel() }}</span>
      </div>
      @if (t.account; as a) {
        @if (a.displayName) {
          <p class="connected" [attr.data-testid]="'tracker-connected-as-' + t.slug">
            Connected as <strong>{{ a.displayName }}</strong> since {{ a.connectedAt | date: 'mediumDate' }}
          </p>
        }
        @if (!t.allowed) {
          <p class="warn" role="status">An admin has turned {{ t.name }} off for this server. Nothing is synced until it is allowed again.</p>
        }
        @if (problem(); as p) { <p class="warn" role="status" [attr.data-testid]="'tracker-problem-' + t.slug">{{ p }}</p> }
        @if (a.lastSuccessAt) {
          <p class="muted small">Last sync {{ a.lastSuccessAt | date: 'medium' }} · {{ a.requestsToday }} of {{ a.dailyRequestCap }} requests today</p>
        }
      } @else {
        <p class="muted">{{ pitch() }}</p>
      }

      @if (t.allowed && needsConnect()) {
        @if (!connectOpen()) {
          <div class="actions">
            <button mat-flat-button color="primary" type="button" (click)="connectOpen.set(true)"
                    [attr.data-testid]="'tracker-connect-open-' + t.slug">{{ t.account ? 'Connect again…' : 'Connect ' + t.name + '…' }}</button>
          </div>
        } @else {
          @if (textOutdated()) {
            <p class="error" role="alert">This page shows an older text than the server. Reload the page.</p>
          }
          <app-tracker-consent-text [slug]="t.slug" audience="user" />
          <mat-checkbox [checked]="consentTicked()" (change)="consentTicked.set($event.checked)" [disabled]="busy() || textOutdated()"
                        [attr.data-testid]="'tracker-consent-' + t.slug">
            I understand - connect my {{ t.name }} account
          </mat-checkbox>
          @if (t.connectKind === 'Password') {
            <form class="password" (ngSubmit)="connectWithPassword()" autocomplete="off" [attr.data-testid]="'tracker-password-form-' + t.slug">
              <label class="field">
                <span>{{ t.name }} username</span>
                <input type="text" name="trackerUsername" autocomplete="off" spellcheck="false" maxlength="256" [disabled]="busy()"
                       [ngModel]="username()" (ngModelChange)="username.set($event)" [attr.data-testid]="'tracker-username-' + t.slug" />
              </label>
              <label class="field">
                <span>{{ t.name }} password</span>
                <input type="password" name="trackerPassword" autocomplete="new-password" maxlength="1024" [disabled]="busy()"
                       [ngModel]="password()" (ngModelChange)="password.set($event)" [attr.data-testid]="'tracker-password-' + t.slug" />
              </label>
              <p class="muted small">Sent once to {{ t.name }} through this server to get a sign-in token; never stored.</p>
              <div class="actions">
                <button mat-flat-button color="primary" type="submit" [disabled]="busy() || !consentTicked() || textOutdated()"
                        [attr.data-testid]="'tracker-connect-' + t.slug">Connect {{ t.name }}</button>
                <button mat-button type="button" [disabled]="busy()" (click)="closeConnect()">Cancel</button>
              </div>
            </form>
          } @else {
            <div class="actions">
              <button mat-flat-button color="primary" type="button" [disabled]="busy() || !consentTicked() || textOutdated()"
                      (click)="connectWithSignIn()" [attr.data-testid]="'tracker-connect-' + t.slug">
                <mat-icon>open_in_new</mat-icon> Connect {{ t.name }}
              </button>
              <button mat-button type="button" [disabled]="busy()" (click)="closeConnect()">Cancel</button>
            </div>
            <p class="muted small">You sign in on {{ t.name }}'s own site and come back here. MangaPixer never sees your {{ t.name }} password.</p>
          }
        }
      }

      @if (t.account; as a) {
        @if (t.allowed && !needsConnect()) {
          <div class="settings">
            <mat-slide-toggle [checked]="a.addStartedSeries" [disabled]="busy()" (change)="saveSetting({ addStartedSeries: $event.checked }, $event)"
                              [attr.data-testid]="'tracker-add-started-' + t.slug">
              Add series I start reading to my {{ t.name }} list
            </mat-slide-toggle>
            @if (t.supportsPrivateEntries) {
              <mat-slide-toggle [checked]="a.privateEntries" [disabled]="busy()" (change)="saveSetting({ privateEntries: $event.checked }, $event)"
                                [attr.data-testid]="'tracker-private-entries-' + t.slug">
                Add them as private entries
              </mat-slide-toggle>
            }
          </div>
        }
        @if (confirmDisconnect()) {
          <div class="confirm" role="alertdialog" [attr.aria-label]="'Disconnect ' + t.name" [attr.data-testid]="'tracker-disconnect-confirm-' + t.slug">
            <p>Disconnect your {{ t.name }} account? MangaPixer deletes its sign-in and your series list for {{ t.name }} here;
              nothing on {{ t.name }} is changed. {{ revokeText() }}</p>
            <div class="actions">
              <button mat-flat-button color="warn" type="button" [disabled]="busy()" (click)="disconnect()"
                      [attr.data-testid]="'tracker-disconnect-yes-' + t.slug">Disconnect</button>
              <button mat-button type="button" [disabled]="busy()" (click)="confirmDisconnect.set(false)">Cancel</button>
            </div>
          </div>
        } @else {
          <div class="actions bottom">
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="confirmDisconnect.set(true)"
                    [attr.data-testid]="'tracker-disconnect-' + t.slug">Disconnect</button>
          </div>
        }
      }
      @if (notice(); as n) { <p class="ok" role="status" [attr.data-testid]="'tracker-notice-' + t.slug">{{ n }}</p> }
      @if (error(); as e) { <p class="error" role="alert" [attr.data-testid]="'tracker-error-' + t.slug">{{ e }}</p> }
    </section>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .tracker {
      min-width: 0; height: 100%; box-sizing: border-box; display: flex; flex-direction: column; gap: 8px;
      background: var(--mp-surface-raised); border: 1px solid rgb(var(--mp-ink-rgb) / 0.12); border-radius: 12px; padding: 16px 18px;
    }
    .title { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px 12px; }
    h2 { margin: 0; font-size: 17px; font-weight: 500; color: var(--mp-text-strong); }
    .status { font-size: 12px; font-weight: 600; padding: 3px 10px; border-radius: 10px;
              background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-secondary); }
    .status[data-tone='ok'] { background: rgb(var(--mp-success-strong-rgb) / 0.18); color: var(--mp-success); }
    .status[data-tone='warn'] { background: rgb(var(--mp-warn-strong-rgb) / 0.18); color: var(--mp-warn); }
    p { margin: 0; }
    .connected { overflow-wrap: anywhere; }
    .muted { color: var(--mp-text-muted); font-size: 13px; overflow-wrap: anywhere; }
    .small { font-size: 12px; }
    .warn { color: var(--mp-warn); font-size: 13px; }
    .password { max-width: 420px; }
    .field { display: flex; flex-direction: column; gap: 2px; margin: 8px 0; font-size: 13px; color: var(--mp-text-secondary); min-width: 0; }
    .field input { font: inherit; font-size: 14px; padding: 6px 8px; max-width: 100%; min-width: 0; box-sizing: border-box;
                   color: var(--mp-text); background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    .settings { display: flex; flex-direction: column; gap: 6px; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; }
    .actions.bottom { margin-top: auto; padding-top: 4px; }
    .actions mat-icon { margin-right: 4px; }
    .confirm { border: 1px solid rgb(var(--mp-error-strong-rgb) / 0.5); border-radius: 6px; padding: 8px 10px; font-size: 14px; }
    .confirm p { margin: 4px 0 8px; }
    .ok { color: var(--mp-success); font-size: 13px; }
    .error { color: var(--mp-error); font-size: 13px; }
  `],
})
export class TrackerAccountPanelComponent {
  private readonly api = inject(TrackersApiService);
  private readonly navigation = inject(TrackerNavigationService);

  readonly tracker = input.required<TrackerSummaryDto>();
  /** The account changed (connected, disconnected, settings): the host reloads the overview. */
  readonly changed = output<void>();

  readonly busy = signal(false);
  readonly consentTicked = signal(false);
  readonly confirmDisconnect = signal(false);
  /** The consent text and the Connect controls are shown (after "Connect <tracker>…"). */
  readonly connectOpen = signal(false);
  readonly username = signal('');
  readonly password = signal('');
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly textOutdated = computed(() => this.tracker().currentConsentVersion !== TRACKER_USER_CONSENT_VERSION[this.tracker().slug]);
  readonly problem = computed(() => {
    const t = this.tracker();
    return t.account && t.allowed ? accountProblem(t.account, t.slug) : null;
  });
  /** Not connected, or the account must connect again (needs reconnect, disconnected by an admin, a newer consent text). */
  readonly needsConnect = computed(() => {
    const a = this.tracker().account;
    return !a || a.state !== 'Ok' || a.consentRenewalNeeded;
  });
  readonly revokeText = computed(() => revokeHint(this.tracker().slug));

  /** The card's status chip. */
  readonly statusLabel = computed(() => {
    const t = this.tracker();
    if (!t.account) return t.allowed ? 'Not connected' : 'Off on this server';
    if (!t.allowed) return 'Off on this server';
    return this.needsConnect() ? 'Connect again' : 'Connected';
  });
  readonly statusTone = computed(() => {
    const t = this.tracker();
    if (!t.account) return 'muted';
    return !t.allowed || this.needsConnect() ? 'warn' : 'ok';
  });
  /** One line on what connecting does, before the user opens the consent text. */
  readonly pitch = computed(() => {
    const t = this.tracker();
    if (!t.allowed) return `An admin has not allowed ${t.name} on this server.`;
    return t.connectKind === 'Password'
      ? `Sync your reading progress to your ${t.name} reading list. Your username and password are sent once to ${t.name} and never stored.`
      : `Sync your reading progress to your ${t.name} list. You sign in on ${t.name}'s own site; MangaPixer never sees your password.`;
  });

  closeConnect(): void {
    this.connectOpen.set(false);
    this.consentTicked.set(false);
    this.password.set('');
    this.error.set(null);
  }

  connectWithSignIn(): void {
    const t = this.tracker();
    if (!this.consentTicked() || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.connectStart(t.slug, TRACKER_USER_CONSENT_VERSION[t.slug]).subscribe({
      next: (r) => {
        if (this.navigation.openSignIn(r.authorizeUrl)) {
          this.notice.set(`Opening ${t.name}…`);
        } else {
          this.busy.set(false);
          this.error.set(`The server answered with a sign-in address that is not https. Ask an admin to check the ${t.name} setup.`);
        }
      },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, t.slug)); },
    });
  }

  connectWithPassword(): void {
    const t = this.tracker();
    if (this.busy()) return;
    const username = this.username().trim();
    const password = this.password();
    // The password leaves the field at once, whatever happens next.
    this.password.set('');
    if (!this.consentTicked()) {
      this.error.set('Tick "I understand" first.');
      return;
    }
    if (!username || !password) {
      this.error.set(!username ? `Enter your ${t.name} username.` : `Enter your ${t.name} password.`);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.connectPassword(t.slug, username, password, TRACKER_USER_CONSENT_VERSION[t.slug]).subscribe({
      next: () => {
        this.busy.set(false);
        this.consentTicked.set(false);
        this.connectOpen.set(false);
        this.username.set('');
        this.notice.set(`Connected to ${t.name}.`);
        this.changed.emit();
      },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, t.slug)); },
    });
  }

  disconnect(): void {
    const t = this.tracker();
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.disconnect(t.slug).subscribe({
      next: () => {
        this.busy.set(false);
        this.confirmDisconnect.set(false);
        this.notice.set(`Disconnected from ${t.name}.`);
        this.changed.emit();
      },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, t.slug)); },
    });
  }

  saveSetting(request: UpdateTrackerUserSettingsRequest, event?: Pick<MatSlideToggleChange, 'source'>): void {
    const t = this.tracker();
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.updateSettings(t.slug, request).subscribe({
      next: () => { this.busy.set(false); this.changed.emit(); },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(failureText(err, t.slug));
        // Put the switch back: its [checked] binding did not change.
        if (event) event.source.checked = !event.source.checked;
      },
    });
  }
}
