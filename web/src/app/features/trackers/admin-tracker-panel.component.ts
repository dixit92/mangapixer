import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';

import { AdminTrackerSettingsDto, UpdateTrackerSettingsRequest } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';
import { TrackerConsentTextComponent } from './tracker-consent-text.component';
import { TRACKER_ADMIN_CONSENT_VERSION, failureText, proposedRedirectUri } from './tracker-texts';

/**
 * One tracker in Administration > Trackers (1.43.0): the admin consent text (shown until accepted, then folded behind "What is
 * sent?"), the allow switch, and - for MyAnimeList - this server's own app registration: the Client ID, an optional client secret
 * (write-only: the server only says whether one is saved) and the redirect URL, proposed from the address this browser uses with a
 * Copy button. Emits the saved settings.
 */
@Component({
  selector: 'app-admin-tracker-panel',
  standalone: true,
  imports: [DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatSlideToggleModule, TrackerConsentTextComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let s = settings();
    <section class="tracker" [attr.data-testid]="'admin-tracker-' + s.slug">
      <h4>
        {{ s.name }}
        <span class="chip" [class.on]="allowed()" [attr.data-testid]="'admin-tracker-state-' + s.slug">{{ stateText() }}</span>
      </h4>
      @if (s.networkDisabledByConfig) {
        <p class="banner" role="status"><mat-icon inline>block</mat-icon> Disabled by the server configuration (Trackers:NetworkDisabled).</p>
      }
      @if (textOutdated()) {
        <p class="error" role="alert">This page shows an older text than the server. Reload the page.</p>
      }
      @if (!consentCurrent() || showConsent()) {
        <app-tracker-consent-text [slug]="s.slug" audience="admin" />
      }
      @if (consentCurrent()) {
        <p class="muted small">
          Allowed with this text {{ s.consentAt | date: 'mediumDate' }} ·
          <button type="button" class="link" (click)="showConsent.set(!showConsent())" [attr.aria-expanded]="showConsent()"
                  [attr.data-testid]="'admin-tracker-consent-toggle-' + s.slug">{{ showConsent() ? 'Hide' : 'What is sent?' }}</button>
        </p>
      } @else {
        <mat-checkbox [checked]="consentTicked()" (change)="consentTicked.set($event.checked)"
                      [disabled]="s.networkDisabledByConfig || textOutdated()" [attr.data-testid]="'admin-tracker-consent-' + s.slug">
          I have read this - allow {{ s.name }} on this server
        </mat-checkbox>
      }
      <div>
        <mat-slide-toggle [checked]="allowed()" [disabled]="!canToggle()" (change)="setAllowed($event)"
                          [attr.data-testid]="'admin-tracker-allow-' + s.slug">
          Allow users to sync to {{ s.name }}
        </mat-slide-toggle>
      </div>

      @if (s.needsAppRegistration) {
        <form class="registration" (ngSubmit)="saveRegistration()" [attr.data-testid]="'admin-tracker-registration-' + s.slug">
          <h5>This server's {{ s.name }} app</h5>
          <p class="hint">
            Register an app for this server at <a href="https://myanimelist.net/apiconfig/create" target="_blank" rel="noopener noreferrer">myanimelist.net/apiconfig/create</a> (choose non-commercial), then enter what it shows.
            Users cannot connect until the Client ID and the redirect URL are saved.
          </p>
          <label class="field">
            <span>Client ID</span>
            <input type="text" name="clientId" autocomplete="off" spellcheck="false" maxlength="256" [disabled]="saving()"
                   [ngModel]="clientId()" (ngModelChange)="clientId.set($event)" [attr.data-testid]="'admin-tracker-client-id-' + s.slug" />
          </label>
          <label class="field">
            <span>Client secret (optional)</span>
            <input type="password" name="clientSecret" autocomplete="new-password" spellcheck="false" maxlength="1024" [disabled]="saving()"
                   [placeholder]="s.clientSecretSet ? 'Saved - type a new one to replace it' : 'Only if MyAnimeList gave you one'"
                   [ngModel]="clientSecret()" (ngModelChange)="clientSecret.set($event)"
                   [attr.data-testid]="'admin-tracker-client-secret-' + s.slug" />
          </label>
          @if (s.clientSecretSet) {
            <p class="muted small">A client secret is saved. It is never shown again.
              <button type="button" class="link" [disabled]="saving()" (click)="removeSecret()"
                      [attr.data-testid]="'admin-tracker-remove-secret-' + s.slug">Remove it</button></p>
          }
          <label class="field">
            <span>Redirect URL</span>
            <input type="url" name="redirectUri" autocomplete="off" spellcheck="false" maxlength="1024" [disabled]="saving()"
                   [ngModel]="redirectUri()" (ngModelChange)="redirectUri.set($event)" [attr.data-testid]="'admin-tracker-redirect-' + s.slug" />
          </label>
          <p class="hint">
            Register exactly this URL as the App Redirect URL at MyAnimeList - the same http or https, name and port. It must be
            the address your users open MangaPixer at.
            @if (redirectUri() !== proposed()) {
              This browser opened the server as <code class="url">{{ proposed() }}</code>.
              <button type="button" class="link" [disabled]="saving()" (click)="redirectUri.set(proposed())"
                      [attr.data-testid]="'admin-tracker-use-address-' + s.slug">Use this address</button>
            }
          </p>
          <div class="actions">
            <button mat-stroked-button type="button" (click)="copy(redirectUri())" [disabled]="!redirectUri()"
                    [attr.data-testid]="'admin-tracker-copy-redirect-' + s.slug">
              <mat-icon>content_copy</mat-icon> Copy redirect URL
            </button>
            <button mat-raised-button color="primary" type="submit" [disabled]="saving() || !dirty()"
                    [attr.data-testid]="'admin-tracker-save-' + s.slug">Save</button>
          </div>
          @if (copied()) { <p class="ok" role="status">Copied.</p> }
        </form>
      }
      <!-- 1.43.0 (owner E5): the per-user daily request limit for this tracker. -->
      <form class="cap" (ngSubmit)="saveCap()" [attr.data-testid]="'admin-tracker-cap-form-' + s.slug">
        <label class="field">
          <span>Requests per user per day ({{ capMin }} - {{ capMax }})</span>
          <input type="number" name="dailyRequestCap" [min]="capMin" [max]="capMax" step="1" [disabled]="saving()"
                 [ngModel]="cap()" (ngModelChange)="cap.set($event)" [attr.data-testid]="'admin-tracker-cap-' + s.slug" />
        </label>
        <button mat-stroked-button type="submit" [disabled]="saving() || !capValid() || cap() === (s.dailyRequestCap ?? 2000)"
                [attr.data-testid]="'admin-tracker-cap-save-' + s.slug">Save limit</button>
      </form>
      <p class="muted small">Reads, writes and sign-in renewals for one user count; syncing waits for the next day (UTC) when the limit
        is reached. 2000 by default.</p>
      <p class="muted small" [attr.data-testid]="'admin-tracker-connected-' + s.slug">
        {{ s.connectedUsers === 1 ? '1 user has' : s.connectedUsers + ' users have' }} connected an account.
      </p>
      @if (notice(); as n) { <p class="ok" role="status" [attr.data-testid]="'admin-tracker-notice-' + s.slug">{{ n }}</p> }
      @if (error(); as e) { <p class="error" role="alert" [attr.data-testid]="'admin-tracker-error-' + s.slug">{{ e }}</p> }
    </section>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .tracker { padding: 4px 0 8px; min-width: 0; }
    h4 { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0 4px; font-size: 16px; }
    h5 { margin: 12px 0 4px; font-size: 14px; }
    .chip { font-size: 12px; font-weight: 400; border-radius: 10px; padding: 1px 8px; background: rgb(var(--mp-ink-rgb) / 0.08);
            color: var(--mp-text-secondary); }
    .chip.on { background: rgb(var(--mp-success-strong-rgb) / 0.15); color: var(--mp-success); }
    .hint, .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; overflow-wrap: anywhere; }
    .small { font-size: 12px; }
    .banner { color: var(--mp-warn-strong); font-size: 13px; margin: 6px 0; }
    .field { display: flex; flex-direction: column; gap: 2px; margin: 8px 0; font-size: 13px; color: var(--mp-text-secondary); min-width: 0; }
    .field input { font: inherit; font-size: 14px; padding: 6px 8px; max-width: 100%; min-width: 0; box-sizing: border-box;
                   color: var(--mp-text); background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    .url { font-size: 12px; overflow-wrap: anywhere; word-break: break-all; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .cap { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 4px 12px; margin-top: 8px; }
    .cap .field { margin: 0; }
    .cap .field input { width: 120px; }
    .actions mat-icon { margin-right: 4px; }
    .link { background: none; border: 0; padding: 0; font: inherit; color: var(--mp-accent); cursor: pointer; text-decoration: underline; }
    .link:disabled { color: var(--mp-text-faint); cursor: default; }
    .ok { color: var(--mp-success-strong); font-size: 13px; margin: 6px 0; }
    .error { color: var(--mp-error-strong); font-size: 13px; margin: 6px 0; }
  `],
})
export class AdminTrackerPanelComponent {
  private readonly api = inject(TrackersApiService);

  readonly settings = input.required<AdminTrackerSettingsDto>();
  readonly saved = output<AdminTrackerSettingsDto>();

  readonly consentTicked = signal(false);
  readonly showConsent = signal(false);
  readonly saving = signal(false);
  readonly copied = signal(false);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  /** The form fields start from what the server has (the proposed address when no redirect URL is saved yet). */
  readonly clientId = linkedSignal(() => this.settings().clientId ?? '');
  readonly clientSecret = linkedSignal(() => { this.settings(); return ''; });
  readonly redirectUri = linkedSignal(() => this.settings().redirectUri || this.proposed());
  readonly proposed = computed(() => proposedRedirectUri(this.settings().slug));

  /** The per-user daily request limit (owner E5; the server takes 100 - 20000). */
  readonly capMin = 100;
  readonly capMax = 20000;
  readonly cap = linkedSignal<number | null>(() => this.settings().dailyRequestCap ?? 2000);
  readonly capValid = computed(() => {
    const v = this.cap();
    return v != null && Number.isInteger(v) && v >= this.capMin && v <= this.capMax;
  });

  readonly dirty = computed(() => {
    const s = this.settings();
    return this.clientId().trim() !== (s.clientId ?? '') || this.clientSecret() !== '' || this.redirectUri().trim() !== (s.redirectUri ?? '');
  });

  /** The client shows the text the server currently asks for. */
  readonly textOutdated = computed(() => this.settings().currentConsentVersion !== TRACKER_ADMIN_CONSENT_VERSION[this.settings().slug]);

  readonly consentCurrent = computed(() => {
    const s = this.settings();
    return !this.textOutdated() && s.acceptedConsentVersion === s.currentConsentVersion;
  });

  readonly allowed = computed(() => this.settings().enabled && this.consentCurrent() && !this.settings().networkDisabledByConfig);

  readonly canToggle = computed(() => {
    const s = this.settings();
    if (this.saving() || s.networkDisabledByConfig) return false;
    if (this.allowed()) return true;
    return !this.textOutdated() && (this.consentCurrent() || this.consentTicked());
  });

  readonly stateText = computed(() => {
    const s = this.settings();
    if (s.networkDisabledByConfig) return 'Disabled by configuration';
    if (!this.allowed()) return s.enabled && s.consentRenewalNeeded ? 'Paused - accept the new text' : 'Off';
    if (s.needsAppRegistration && (!s.clientId || !s.redirectUri)) return 'Allowed - app not set up';
    return 'Allowed';
  });

  setAllowed(event: Pick<MatSlideToggleChange, 'checked' | 'source'>): void {
    const on = event.checked;
    const slug = this.settings().slug;
    const request: UpdateTrackerSettingsRequest = on
      ? { enabled: true, acceptedConsentVersion: TRACKER_ADMIN_CONSENT_VERSION[slug] }
      : { enabled: false };
    // A refused change puts the switch back (its [checked] binding did not change, so Angular would not).
    this.save(request, on ? `${this.settings().name} is allowed.` : `${this.settings().name} is off - nothing is synced to it.`,
      () => { event.source.checked = this.allowed(); });
  }

  saveRegistration(): void {
    const s = this.settings();
    const request: UpdateTrackerSettingsRequest = {
      clientId: this.clientId().trim(),
      redirectUri: this.redirectUri().trim(),
    };
    if (this.clientSecret() !== '') request.clientSecret = this.clientSecret();
    this.save(request, `${s.name} app settings saved.`);
  }

  saveCap(): void {
    const v = this.cap();
    if (!this.capValid() || v == null) return;
    this.save({ dailyRequestCap: v }, `Up to ${v} requests per user per day to ${this.settings().name}.`);
  }

  removeSecret(): void {
    this.save({ clientSecret: '' }, 'Client secret removed.');
  }

  copy(text: string): void {
    this.copied.set(false);
    navigator.clipboard?.writeText(text).then(
      () => this.copied.set(true),
      () => this.error.set('Copying failed - select the URL and copy it by hand.'),
    );
  }

  private save(request: UpdateTrackerSettingsRequest, done: string, onError?: () => void): void {
    if (this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.notice.set(null);
    this.api.adminUpdate(this.settings().slug, request).subscribe({
      next: (updated) => {
        this.saving.set(false);
        this.consentTicked.set(false);
        this.notice.set(done);
        this.saved.emit(updated);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        // The secret field is cleared either way: it is never kept in the page longer than one attempt.
        this.clientSecret.set('');
        this.error.set(failureText(err, this.settings().slug));
        onError?.();
      },
    });
  }
}
