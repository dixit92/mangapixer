import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable } from 'rxjs';

import {
  ApiError,
  MetadataLibrarySettingsDto,
  MetadataPrecedence,
  MetadataSettingsDto,
} from '../../../core/api/api-types';
import { MetadataApiService } from '../../metadata/metadata-api.service';

/** Consent text version the card shows; must equal the server's `currentConsentVersion`. */
export const CONSENT_TEXT_VERSION = 1;

/** Integer-only daily budget in 1..1,000,000 (the server validates the same range). */
export function parseDailyBudget(raw: string | number | null | undefined): number | null {
  const text = String(raw ?? '').trim();
  if (!/^\d{1,7}$/.test(text)) return null;
  const value = Number(text);
  return value >= 1 && value <= 1_000_000 ? value : null;
}

/**
 * "Series metadata" admin card (1.24.0, lane B2). Self-loading, embedded as
 * `<app-metadata-settings-card />` after the update-check card.
 *
 * - Global "Fetch series information from the web": OFF by default; it can only be
 *   turned on after the consent checkbox under the consent text (version 1). Turning
 *   it on makes NO network call - lookups happen only when an admin runs Identify.
 * - Status line: budget used today, backoff, last error code.
 * - Daily request budget (integer, default 5000).
 * - Per library: Fetch toggle, Show toggle, source precedence, Delete fetched data.
 * - ComicInfo coverage and "Delete all fetched web data".
 */
@Component({
  selector: 'app-metadata-settings-card',
  standalone: true,
  imports: [
    DatePipe, FormsModule, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatSelectModule, MatSlideToggleModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="metadata-settings-card">
      <mat-card-header>
        <mat-card-title>Series metadata</mat-card-title>
        <mat-card-subtitle>Series information from ComicInfo.xml and, if you allow it, MangaUpdates</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (settings(); as s) {
          <mat-slide-toggle [checked]="s.showSeriesInfo" [disabled]="saving()" (change)="setShow($event.checked)" data-testid="md-show">
            Show series information
          </mat-slide-toggle>
          <p class="note">Hides all series information (from files and from the web) for everyone when off. Stored data is kept.</p>

          <h4>Fetch series information from the web <span class="muted">(off by default)</span></h4>
          @if (s.networkDisabledByConfig) {
            <p class="banner" role="status" data-testid="md-config-kill">
              <mat-icon inline>block</mat-icon> Disabled by the server configuration (Metadata:NetworkDisabled).
            </p>
          }
          <div class="consent" data-testid="md-consent-text">
            <p>When on, MangaPixer can look up series details - description, authors, genres, publication status and
              cover art - on <strong>MangaUpdates</strong> for the libraries you enable below.</p>
            <p><strong>What is sent:</strong> the search text you confirm in the Identify dialog (usually a folder or
              file name) and MangaUpdates record numbers. MangaUpdates also sees your server's IP address, as with any
              web request.</p>
            <p><strong>What is never sent:</strong> file paths, your file list, user accounts, reading progress, or
              anything that identifies this server.</p>
            <p><strong>When:</strong> only when an admin runs Identify, Look up or Refresh in an enabled library.
              Nothing happens automatically.</p>
            <p>Fetched information is stored on this server and credited to MangaUpdates, which provides it as-is. You
              can switch this off at any time; stored information stays until you delete it.</p>
          </div>
          @if (consentCurrent()) {
            <p class="muted small" data-testid="md-consented">
              Consent given {{ s.consentAt | date: 'mediumDate' }} (version {{ s.acceptedConsentVersion }}).</p>
          } @else {
            <mat-checkbox [checked]="consentTicked()" (change)="consentTicked.set($event.checked)"
                          [disabled]="s.networkDisabledByConfig" data-testid="md-consent">
              I have read this - enable web metadata
            </mat-checkbox>
          }
          <div>
            <mat-slide-toggle [checked]="s.fetchEnabled" [disabled]="!canToggleFetch()" (change)="setFetch($event.checked)"
                              data-testid="md-fetch">
              Fetch from the web
            </mat-slide-toggle>
          </div>

          <p class="status" data-testid="md-status">
            Requests today: {{ s.budgetUsedToday }} / {{ s.dailyBudget }}
            @if (backoffActive()) { · <span class="warn">MangaUpdates asked us to wait until {{ s.backoffUntil | date: 'shortTime' }}</span> }
            @if (s.lastErrorCode) { · last error: <code>{{ s.lastErrorCode }}</code> {{ s.lastErrorAt | date: 'short' }} }
          </p>

          <form class="budget" (ngSubmit)="saveBudget()">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Daily request budget</mat-label>
              <input matInput name="budget" type="text" inputmode="numeric" [ngModel]="budgetText()"
                     (ngModelChange)="budgetText.set($event)" data-testid="md-budget">
            </mat-form-field>
            <button mat-stroked-button type="submit" [disabled]="saving() || parsedBudget() === null || parsedBudget() === s.dailyBudget">Save</button>
            <button mat-button type="button" [disabled]="saving() || s.dailyBudget === s.defaultDailyBudget" (click)="resetBudget()">
              Default ({{ s.defaultDailyBudget }})</button>
          </form>
          @if (budgetText() && parsedBudget() === null) { <p class="error small">Enter a whole number from 1 to 1,000,000.</p> }

          <h4>Libraries</h4>
          <div class="libs">
            @for (lib of s.libraries; track lib.libraryId) {
              <div class="lib" [attr.data-library]="lib.libraryId">
                <span class="name">{{ lib.name }}</span>
                <mat-slide-toggle [checked]="lib.fetchEnabled" [disabled]="saving()" (change)="setLibrary(lib, { fetchEnabled: $event.checked })">Fetch</mat-slide-toggle>
                <mat-slide-toggle [checked]="lib.showSeriesInfo" [disabled]="saving()" (change)="setLibrary(lib, { showSeriesInfo: $event.checked })">Show</mat-slide-toggle>
                <mat-form-field appearance="outline" subscriptSizing="dynamic" class="prec">
                  <mat-label>Precedence</mat-label>
                  <mat-select [value]="lib.precedence ?? 'default'" (selectionChange)="setPrecedence(lib, $event.value)" [disabled]="saving()">
                    <mat-option value="default">Default (web first)</mat-option>
                    <mat-option value="WebFirst">Web first</mat-option>
                    <mat-option value="ComicInfoFirst">ComicInfo first</mat-option>
                  </mat-select>
                </mat-form-field>
                @if (confirming() === lib.libraryId) {
                  <span class="confirm">Delete {{ lib.linkCount }} link{{ lib.linkCount === 1 ? '' : 's' }}?
                    <button mat-flat-button color="warn" type="button" (click)="purge(lib.libraryId)">Delete</button>
                    <button mat-button type="button" (click)="confirming.set(null)">Cancel</button></span>
                } @else {
                  <button mat-button type="button" [disabled]="saving() || lib.linkCount === 0" (click)="confirming.set(lib.libraryId)">
                    Delete fetched data</button>
                }
              </div>
            } @empty {
              <p class="muted">No libraries yet.</p>
            }
          </div>

          <p class="muted small" data-testid="md-comicinfo">
            ComicInfo: {{ s.comicInfo.archivesRead }} of {{ s.comicInfo.archivesTotal }} archives read,
            {{ s.comicInfo.archivesWithComicInfo }} with ComicInfo · {{ s.webRecordCount }} web record{{ s.webRecordCount === 1 ? '' : 's' }} stored
          </p>
          @if (confirming() === 'all') {
            <span class="confirm">Delete all fetched web data (links, records and cover images)? Don't-match marks and ComicInfo stay.
              <button mat-flat-button color="warn" type="button" (click)="purge(null)" data-testid="md-purge-confirm">Delete</button>
              <button mat-button type="button" (click)="confirming.set(null)">Cancel</button></span>
          } @else {
            <button mat-stroked-button type="button" [disabled]="saving() || s.webRecordCount === 0" (click)="confirming.set('all')"
                    data-testid="md-purge">Delete all fetched web data</button>
          }
        }
        @if (message()) { <p class="ok small" role="status">{{ message() }}</p> }
        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    h4 { margin: 16px 0 6px; }
    .note, .small { font-size: 12px; }
    .note { color: #999; margin: 4px 0 8px; }
    .muted { color: #999; }
    .consent { font-size: 13px; color: #c8c8d0; border-left: 3px solid #555; padding: 2px 12px; margin: 6px 0 10px; }
    .consent p { margin: 6px 0; }
    .banner { color: #ffb300; }
    .status { font-size: 13px; margin: 10px 0; }
    .warn { color: #ffb300; }
    .budget { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .libs { display: flex; flex-direction: column; gap: 6px; }
    .lib { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; padding: 4px 0; border-bottom: 1px solid rgba(255,255,255,0.06); }
    .lib .name { min-width: 140px; font-weight: 500; }
    .prec { width: 190px; }
    .confirm { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; font-size: 13px; }
    .error { color: #f44336; }
    .ok { color: #4caf50; }
  `],
})
export class MetadataSettingsCardComponent implements OnInit {
  private readonly api = inject(MetadataApiService);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly settings = signal<MetadataSettingsDto | null>(null);
  readonly consentTicked = signal(false);
  readonly budgetText = signal('');
  readonly confirming = signal<string | null>(null);

  /** Consent already given for the current text version. */
  readonly consentCurrent = computed(() => {
    const s = this.settings();
    return !!s && s.acceptedConsentVersion === s.currentConsentVersion && s.currentConsentVersion === CONSENT_TEXT_VERSION;
  });

  /** The Fetch switch: turning it ON needs the consent tick (or current consent); OFF is always allowed. */
  readonly canToggleFetch = computed(() => {
    const s = this.settings();
    if (!s || this.saving()) return false;
    if (s.fetchEnabled) return true;
    return !s.networkDisabledByConfig && (this.consentCurrent() || this.consentTicked());
  });

  readonly parsedBudget = computed(() => parseDailyBudget(this.budgetText()));

  readonly backoffActive = computed(() => {
    const until = this.settings()?.backoffUntil;
    return !!until && new Date(until).getTime() > Date.now();
  });

  ngOnInit(): void {
    this.api.getSettings().subscribe({
      next: (s) => {
        this.apply(s);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load the series metadata settings');
        this.loading.set(false);
      },
    });
  }

  setShow(show: boolean): void {
    this.save(this.api.updateSettings({ showSeriesInfo: show }));
  }

  setFetch(on: boolean): void {
    const s = this.settings();
    if (!s) return;
    this.save(this.api.updateSettings(on
      ? { fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }
      : { fetchEnabled: false }));
  }

  saveBudget(): void {
    const value = this.parsedBudget();
    if (value === null) return;
    this.save(this.api.updateSettings({ dailyBudget: value }), 'Daily budget saved');
  }

  resetBudget(): void {
    this.save(this.api.updateSettings({ resetDailyBudget: true }), 'Daily budget reset');
  }

  setLibrary(lib: MetadataLibrarySettingsDto, change: { fetchEnabled?: boolean; showSeriesInfo?: boolean }): void {
    this.save(this.api.updateLibrary(lib.libraryId, change));
  }

  setPrecedence(lib: MetadataLibrarySettingsDto, value: MetadataPrecedence | 'default'): void {
    this.saving.set(true);
    this.error.set(null);
    this.api.setLibraryPrecedence(lib.libraryId, value === 'default' ? null : value).subscribe({
      next: () => this.reload('Source precedence saved'),
      error: (err: ApiError) => this.fail(err),
    });
  }

  purge(libraryId: string | null): void {
    this.saving.set(true);
    this.error.set(null);
    this.confirming.set(null);
    this.api.purge(libraryId).subscribe({
      next: (r) => this.reload(`Deleted ${r.linksRemoved} link(s) and ${r.recordsRemoved} record(s)`),
      error: (err: ApiError) => this.fail(err),
    });
  }

  private save(call: Observable<MetadataSettingsDto>, message?: string): void {
    this.saving.set(true);
    this.error.set(null);
    this.message.set(null);
    call.subscribe({
      next: (s) => {
        this.apply(s);
        this.saving.set(false);
        if (message) this.message.set(message);
      },
      error: (err: ApiError) => this.fail(err),
    });
  }

  private reload(message: string): void {
    this.api.getSettings().subscribe({
      next: (s) => {
        this.apply(s);
        this.saving.set(false);
        this.message.set(message);
      },
      error: (err: ApiError) => this.fail(err),
    });
  }

  private apply(s: MetadataSettingsDto): void {
    this.settings.set(s);
    this.budgetText.set(String(s.dailyBudget));
  }

  private fail(err: ApiError): void {
    this.saving.set(false);
    this.error.set(err?.message || 'The change was not saved');
    // Re-sync toggles with the server state after a refused change.
    this.api.getSettings().subscribe({ next: (s) => this.apply(s), error: () => undefined });
  }
}
