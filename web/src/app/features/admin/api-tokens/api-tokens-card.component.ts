import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { ApiService } from '../../../core/api/api.service';
import { ApiError, ApiTokenDto, CreateApiTokenResponse } from '../../../core/api/api-types';

/** The expiry choices in days; null = never. 365 is the default. */
export const EXPIRY_CHOICES: { days: number | null; label: string }[] = [
  { days: 30, label: '30 days' },
  { days: 90, label: '90 days' },
  { days: 365, label: '1 year' },
  { days: null, label: 'Never' },
];

const STATUS_TEXT: Record<ApiTokenDto['status'], string> = {
  active: 'Active',
  expired: 'Expired',
  revoked: 'Revoked',
  ownerInactive: 'Paused - its admin is no longer an active admin',
};

/**
 * API tokens card (1.33.0, `<app-api-tokens-card />`): personal access tokens that let another app (MangaList) read the
 * metadata export and nothing else.
 * - The list: name, the token's first characters, owner, created / expires / last used, status; revoke after a confirm.
 * - Create: a name and an expiry (30 / 90 days, 1 year - the default - or never). The token is shown ONCE with a copy button;
 *   the server keeps only its hash.
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
          A token lets an app such as MangaList read the metadata export. It cannot change anything and cannot open
          any other page. It stops working when you revoke it, when it expires, or when the admin who created it is no
          longer an admin.
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
        }

        <form class="create" (ngSubmit)="create()" data-testid="api-token-create-form">
          <h4>New token</h4>
          <div class="row">
            <label for="api-token-name">Name</label>
            <input id="api-token-name" name="name" type="text" maxlength="64" placeholder="e.g. MangaList"
                   data-testid="api-token-name" [ngModel]="name()" (ngModelChange)="name.set($event)" [disabled]="busy()" />
          </div>
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
                  [disabled]="busy() || name().trim().length === 0">Create token</button>
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
    .hint, .muted { color: #999; font-size: 13px; margin: 6px 0; }
    h4 { margin: 16px 0 6px; }
    h5 { margin: 0 0 6px; font-size: 14px; overflow-wrap: anywhere; }
    .tokens { list-style: none; padding: 0; margin: 8px 0 0; display: flex; flex-direction: column; gap: 10px; }
    .tokens li { border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; }
    .tokens li.off .name { color: #999; }
    .head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .prefix { font-size: 12px; color: #bbb; }
    .status { font-size: 12px; border-radius: 10px; padding: 1px 8px; background: rgba(76, 175, 80, 0.15); color: #81c784; }
    .status:not([data-status="active"]) { background: rgba(255, 255, 255, 0.08); color: #bbb; }
    .meta { display: flex; flex-wrap: wrap; gap: 2px 14px; font-size: 13px; color: #bbb; margin: 4px 0; }
    .tokens button { margin-top: 4px; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0; font-size: 14px; }
    .row label { min-width: 110px; }
    input, select { font: inherit; padding: 4px 6px; max-width: 100%; min-width: 0; box-sizing: border-box; }
    input { flex: 1 1 160px; }
    .secret { border: 1px solid rgba(255, 179, 0, 0.6); border-radius: 6px; padding: 10px 12px; margin: 10px 0; font-size: 14px; }
    .secret p { margin: 4px 0; }
    .value { display: block; margin: 8px 0; padding: 8px; background: rgba(255, 255, 255, 0.06); border-radius: 4px;
             font-size: 13px; overflow-wrap: anywhere; word-break: break-all; user-select: all; }
    .confirm { border: 1px solid rgba(244, 67, 54, 0.5); border-radius: 6px; padding: 8px 10px; margin: 8px 0 0; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 8px; }
    .warn { color: #ffb300; }
    .ok { color: #4caf50; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
  `],
})
export class ApiTokensCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly expiryChoices = EXPIRY_CHOICES;

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly tokens = signal<ApiTokenDto[]>([]);
  readonly name = signal('');
  readonly expiresInDays = signal<number | null>(365);
  readonly created = signal<CreateApiTokenResponse | null>(null);
  readonly copied = signal(false);
  readonly revoking = signal<string | null>(null);
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

  setExpiry(value: string): void {
    this.expiresInDays.set(value === 'never' ? null : Number(value));
  }

  create(): void {
    const name = this.name().trim();
    if (!name || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.copied.set(false);
    this.api.createApiToken({ name, expiresInDays: this.expiresInDays() }).subscribe({
      next: (created) => {
        this.busy.set(false);
        this.created.set(created);
        this.name.set('');
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
}
