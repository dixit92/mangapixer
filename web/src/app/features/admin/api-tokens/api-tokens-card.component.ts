import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { ApiService } from '../../../core/api/api.service';
import { ApiError, ApiTokenDto, ApiTokenScope, CreateApiTokenResponse } from '../../../core/api/api-types';

/** The expiry choices in days; null = never. 365 is the default. */
export const EXPIRY_CHOICES: { days: number | null; label: string }[] = [
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 365, label: '1 year' },
  { days: null, label: 'Never' },
];

/**
 * The scopes an admin may tick when creating a token (1.36.0), in the server's canonical order. "Read" is ticked by default,
 * "scan" is not; at least one is required. `key` names the checkbox's test id.
 */
export const SCOPE_CHOICES: { scope: ApiTokenScope; key: string; label: string; hint: string; listText: string }[] = [
  {
    scope: 'metadata:read',
    key: 'read',
    label: 'Read the metadata export',
    hint: 'Series information for another app such as MangaList.',
    listText: 'read the metadata export',
  },
  {
    scope: 'library:scan',
    key: 'scan',
    label: 'Request library scans',
    hint: 'The app may ask for a full scan of a library, at most once every few minutes per library. It never changes your files.',
    listText: 'request library scans',
  },
];

const STATUS_TEXT: Record<ApiTokenDto['status'], string> = {
  active: 'Active',
  expired: 'Expired',
  revoked: 'Revoked',
  ownerInactive: 'Paused - its admin is no longer an active admin',
};

/**
 * API tokens card (1.33.0, `<app-api-tokens-card />`): personal access tokens that let another app (MangaList) read the
 * metadata export and - with the scan scope (1.36.0) - request a full library scan, and nothing else.
 * - The list: name, the token's first characters, what it may do (its scopes), owner, created / expires / last used, status;
 *   revoke after a confirm; "Clear revoked" (1.37.0) removes every revoked or expired token from the list after a confirm.
 * - Create: a name, what it may do ("Read the metadata export" ticked by default, "Request library scans" not; at least one) and an
 *   expiry (30 / 90 days, 1 year - the default - or never). The token is shown ONCE with a copy button; the server keeps only
 *   its hash. Scopes cannot be changed later: create a new token instead.
 */
@Component({
  selector: 'app-api-tokens-card',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="api-tokens-card">
      <mat-card-header>
        <mat-card-title>API tokens</mat-card-title>
        <mat-card-subtitle>Let another app read your series information</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">
          A token lets an app such as MangaList read the metadata export and, if you allow it, ask for a library scan.
          It cannot change anything else and cannot open any other page. It stops working when you revoke it, when it
          expires, or when the admin who created it is no longer an admin.
        </p>

        @if (created(); as c) {
          <div class="secret" role="alertdialog" aria-labelledby="api-token-secret-h" data-testid="api-token-secret">
            <h5 id="api-token-secret-h">Copy your new token "{{ c.token.name }}" now</h5>
            <p class="warn">You will not see it again. If you lose it, revoke it and create a new one.</p>
            <code class="value" data-testid="api-token-secret-value">{{ c.secret }}</code>
            <div class="actions">
              <button mat-flat-button color="primary" type="button" data-testid="api-token-copy" (click)="copy(c.secret)">
                <mat-icon>content_copy</mat-icon> Copy
              </button>
              <button mat-button type="button" data-testid="api-token-done" (click)="created.set(null)">Done</button>
            </div>
            @if (copied()) { <p class="ok" role="status">Copied.</p> }
          </div>
        }

        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (tokens().length === 0) {
          <p class="muted" data-testid="api-tokens-empty">No tokens yet.</p>
        } @else {
          <ul class="tokens">
            @for (t of tokens(); track t.id) {
              <li [attr.data-testid]="'api-token-' + t.id" [class.off]="t.status !== 'active'">
                <div class="head">
                  <span class="name">{{ t.name }}</span>
                  <code class="prefix">{{ t.prefix }}…</code>
                  <span class="status" [attr.data-status]="t.status">{{ statusText(t) }}</span>
                </div>
                <div class="meta">
                  <span>Created {{ t.createdAt | date: 'mediumDate' }} by {{ t.ownerUserName }}</span>
                  <span>
                    @if (t.revokedAt) { Revoked {{ t.revokedAt | date: 'mediumDate' }} }
                    @else if (t.expiresAt) { {{ t.status === 'expired' ? 'Expired' : 'Expires' }} {{ t.expiresAt | date: 'mediumDate' }} }
                    @else { Never expires }
                  </span>
                  <span>Last used: {{ t.lastUsedAt ? (t.lastUsedAt | date: 'medium') : 'never' }}</span>
                </div>
                <div class="can" [attr.data-testid]="'api-token-scopes-' + t.id">May: {{ scopesText(t) }}</div>
                @if (t.status !== 'revoked') {
                  @if (revoking() === t.id) {
                    <div class="confirm" role="alertdialog" [attr.data-testid]="'api-token-confirm-' + t.id">
                      <p>Revoke "{{ t.name }}"? Apps using it lose access at once. This cannot be undone.</p>
                      <div class="actions">
                        <button mat-flat-button color="warn" type="button" data-testid="api-token-revoke-yes"
                                [disabled]="busy()" (click)="revoke(t)">Revoke</button>
                        <button mat-button type="button" [disabled]="busy()" (click)="revoking.set(null)">Cancel</button>
                      </div>
                    </div>
                  } @else {
                    <button mat-stroked-button type="button" [disabled]="busy()"
                            [attr.data-testid]="'api-token-revoke-' + t.id" (click)="revoking.set(t.id)">Revoke</button>
                  }
                }
              </li>
            }
          </ul>
          @if (clearable() > 0) {
            @if (clearing()) {
              <div class="confirm" role="alertdialog" data-testid="api-tokens-clear-confirm">
                <p>
                  Remove {{ clearable() === 1 ? 'the revoked or expired token' : 'the ' + clearable() + ' revoked or expired tokens' }}
                  from this list? They no longer work. The audit trail keeps their history.
                </p>
                <div class="actions">
                  <button mat-flat-button color="warn" type="button" data-testid="api-tokens-clear-yes"
                          [disabled]="busy()" (click)="clearRevoked()">Remove</button>
                  <button mat-button type="button" [disabled]="busy()" (click)="clearing.set(false)">Cancel</button>
                </div>
              </div>
            } @else {
              <button mat-stroked-button type="button" class="clear" data-testid="api-tokens-clear" [disabled]="busy()"
                      (click)="clearing.set(true)">Clear revoked</button>
            }
          }
        }

        <form class="create" (ngSubmit)="create()" data-testid="api-token-create-form">
          <h4>New token</h4>
          <div class="row">
            <label for="api-token-name">Name</label>
            <input id="api-token-name" name="name" type="text" maxlength="64" placeholder="e.g. MangaList"
                   data-testid="api-token-name" [ngModel]="name()" (ngModelChange)="name.set($event)" [disabled]="busy()" />
          </div>
          <fieldset class="scopes" data-testid="api-token-scopes">
            <legend>The token may</legend>
            @for (choice of scopeChoices; track choice.scope) {
              <label class="check">
                <input type="checkbox" [attr.data-testid]="'api-token-scope-' + choice.key" [checked]="hasScope(choice.scope)"
                       [disabled]="busy()" (change)="toggleScope(choice.scope, $any($event.target).checked)" />
                <span>
                  <span class="label">{{ choice.label }}</span>
                  <span class="sub">{{ choice.hint }}</span>
                </span>
              </label>
            }
            @if (scopes().length === 0) {
              <p class="error" role="alert" data-testid="api-token-scope-required">Tick at least one.</p>
            }
          </fieldset>
          <div class="row">
            <label for="api-token-expiry">Expires after</label>
            <select id="api-token-expiry" name="expiry" data-testid="api-token-expiry" [disabled]="busy()"
                    (change)="setExpiry($any($event.target).value)">
              @for (choice of expiryChoices; track choice.label) {
                <option [value]="choice.days ?? 'never'" [selected]="choice.days === expiresInDays()">{{ choice.label }}</option>
              }
            </select>
          </div>
          <button mat-raised-button color="primary" type="submit" data-testid="api-token-create"
                  [disabled]="busy() || name().trim().length === 0 || scopes().length === 0">Create token</button>
          <p class="hint">
            Send the token only over HTTPS when the app reaches MangaPixer from outside your home network.
          </p>
        </form>

        @if (error()) { <div class="error" role="alert">{{ error() }}</div> }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .hint, .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; }
    h4 { margin: 16px 0 6px; }
    h5 { margin: 0 0 6px; font-size: 14px; overflow-wrap: anywhere; }
    .tokens { list-style: none; padding: 0; margin: 8px 0 0; display: flex; flex-direction: column; gap: 10px; }
    .tokens li { border: 1px solid rgb(var(--mp-ink-rgb) / 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; }
    .tokens li.off .name { color: var(--mp-text-muted); }
    .head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .prefix { font-size: 12px; color: var(--mp-text-secondary); }
    .status { font-size: 12px; border-radius: 10px; padding: 1px 8px; background: rgb(var(--mp-success-strong-rgb) / 0.15); color: var(--mp-success); }
    .status:not([data-status="active"]) { background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-secondary); }
    .meta { display: flex; flex-wrap: wrap; gap: 2px 14px; font-size: 13px; color: var(--mp-text-secondary); margin: 4px 0; }
    .can { font-size: 13px; color: var(--mp-text-secondary); margin: 2px 0 4px; overflow-wrap: anywhere; }
    .tokens button { margin-top: 4px; }
    .clear { margin-top: 10px; }
    .scopes { border: 0; padding: 0; margin: 8px 0; min-width: 0; }
    .scopes legend { font-size: 14px; padding: 0; margin: 0 0 4px; }
    .check { display: flex; align-items: flex-start; gap: 8px; margin: 6px 0; font-size: 14px; cursor: pointer; }
    .check input { margin: 3px 0 0; flex: 0 0 auto; }
    .check .label { display: block; }
    .check .sub { display: block; color: var(--mp-text-muted); font-size: 12px; overflow-wrap: anywhere; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0; font-size: 14px; }
    .row label { min-width: 110px; }
    input, select { font: inherit; padding: 4px 6px; max-width: 100%; min-width: 0; box-sizing: border-box; }
    input { flex: 1 1 160px; }
    .secret { border: 1px solid rgb(var(--mp-warn-strong-rgb) / 0.6); border-radius: 6px; padding: 10px 12px; margin: 10px 0; font-size: 14px; }
    .secret p { margin: 4px 0; }
    .value { display: block; margin: 8px 0; padding: 8px; background: rgb(var(--mp-ink-rgb) / 0.06); border-radius: 4px;
             font-size: 13px; overflow-wrap: anywhere; word-break: break-all; user-select: all; }
    .confirm { border: 1px solid rgb(var(--mp-error-strong-rgb) / 0.5); border-radius: 6px; padding: 8px 10px; margin: 8px 0 0; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .warn { color: var(--mp-warn-strong); }
    .ok { color: var(--mp-success-strong); font-size: 14px; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
  `],
})
export class ApiTokensCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly expiryChoices = EXPIRY_CHOICES;
  readonly scopeChoices = SCOPE_CHOICES;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly tokens = signal<ApiTokenDto[]>([]);
  readonly name = signal('');
  readonly expiresInDays = signal<number | null>(365);
  /** The ticked scopes, in canonical order; "read" by default. */
  readonly scopes = signal<ApiTokenScope[]>(['metadata:read']);
  readonly created = signal<CreateApiTokenResponse | null>(null);
  readonly copied = signal(false);
  readonly revoking = signal<string | null>(null);
  /** The "Clear revoked" confirm is open (1.37.0). */
  readonly clearing = signal(false);
  /** How many listed tokens "Clear revoked" would remove: the revoked and the expired ones. */
  readonly clearable = computed(() => this.tokens().filter((t) => t.status === 'revoked' || t.status === 'expired').length);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.listApiTokens().subscribe({
      next: (tokens) => {
        this.tokens.set(tokens);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.loading.set(false);
        this.error.set(e?.message || 'The tokens could not be loaded.');
      },
    });
  }

  statusText(t: ApiTokenDto): string {
    return STATUS_TEXT[t.status] ?? t.status;
  }

  /** What a listed token may do, e.g. "read the metadata export, request library scans" (unknown scopes as sent). */
  scopesText(t: ApiTokenDto): string {
    return t.scopes.map((s) => SCOPE_CHOICES.find((c) => c.scope === s)?.listText ?? s).join(', ') || 'nothing';
  }

  hasScope(scope: ApiTokenScope): boolean {
    return this.scopes().includes(scope);
  }

  toggleScope(scope: ApiTokenScope, on: boolean): void {
    const next = new Set(this.scopes());
    if (on) next.add(scope);
    else next.delete(scope);
    this.scopes.set(SCOPE_CHOICES.map((c) => c.scope).filter((s) => next.has(s)));
  }

  setExpiry(value: string): void {
    this.expiresInDays.set(value === 'never' ? null : Number(value));
  }

  create(): void {
    const name = this.name().trim();
    const scopes = this.scopes();
    if (!name || scopes.length === 0 || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.copied.set(false);
    this.api.createApiToken({ name, expiresInDays: this.expiresInDays(), scopes }).subscribe({
      next: (created) => {
        this.busy.set(false);
        this.created.set(created);
        this.name.set('');
        // The next token starts from the safe default again (read only).
        this.scopes.set(['metadata:read']);
        this.load();
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.error.set(e?.message || 'The token could not be created.');
      },
    });
  }

  copy(secret: string): void {
    navigator.clipboard?.writeText(secret).then(
      () => this.copied.set(true),
      () => this.error.set('Copying failed - select the token and copy it by hand.'),
    );
  }

  revoke(t: ApiTokenDto): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.revokeApiToken(t.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.revoking.set(null);
        // A revoked token's secret is useless; drop it from the screen if it is the one just created.
        if (this.created()?.token.id === t.id) this.created.set(null);
        this.load();
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.revoking.set(null);
        this.error.set(e?.message || 'The token could not be revoked.');
        this.load();
      },
    });
  }

  /** Removes the revoked and expired tokens from the list, after the confirm, then reloads. */
  clearRevoked(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.clearRevokedApiTokens().subscribe({
      next: () => {
        this.busy.set(false);
        this.clearing.set(false);
        this.load();
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.clearing.set(false);
        this.error.set(e?.message || 'The revoked tokens could not be removed.');
        this.load();
      },
    });
  }
}
