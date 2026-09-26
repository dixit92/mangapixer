import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { Observable } from 'rxjs';

import {
  ApiError,
  MetadataLibrarySettingsDto,
  MetadataMatchRunDto,
  MetadataMatchThresholdBoundsDto,
  MetadataMatchThresholdsDto,
  MetadataPrecedence,
  MetadataSettingsDto,
} from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { LibraryMatchPanelComponent } from './library-match-panel.component';

/** Consent text version the page shows; must equal the server's `currentConsentVersion`. */
export const CONSENT_TEXT_VERSION = 1;

/**
 * Automatic-lookups consent text version (stage 2, owner decisions 2 + 3); must equal the
 * server's `currentAutoConsentVersion`. Bump it whenever the text below changes.
 */
export const AUTO_CONSENT_TEXT_VERSION = 1;

/** Integer-only daily budget in 1..1,000,000 (the server validates the same range). */
export function parseDailyBudget(raw: string | number | null | undefined): number | null {
  const text = String(raw ?? '').trim();
  if (!/^\d{1,7}$/.test(text)) return null;
  const value = Number(text);
  return value >= 1 && value <= 1_000_000 ? value : null;
}

/** The threshold form as typed. */
export interface ThresholdText {
  autoTitle: string;
  margin: string;
  reviewFloor: string;
}

/**
 * Validates the three thresholds against their bounds (decision 13), the review floor
 * strictly below the auto-link score; the server validates the same. Returns the parsed
 * values or the first problem in words.
 */
export function validateThresholds(
  text: ThresholdText,
  bounds: MetadataMatchThresholdBoundsDto,
): { value: MetadataMatchThresholdsDto } | { error: string } {
  const parse = (raw: string) => {
    const t = raw.trim();
    return /^\d*\.?\d+$/.test(t) ? Number(t) : NaN;
  };
  const autoTitle = parse(text.autoTitle);
  const margin = parse(text.margin);
  const reviewFloor = parse(text.reviewFloor);
  const within = (v: number, min: number, max: number) => Number.isFinite(v) && v >= min - 1e-9 && v <= max + 1e-9;
  if (!within(autoTitle, bounds.autoTitleMin, bounds.autoTitleMax)) {
    return { error: `Auto-link title score must be from ${bounds.autoTitleMin} to ${bounds.autoTitleMax}.` };
  }
  if (!within(margin, bounds.marginMin, bounds.marginMax)) {
    return { error: `Lead over the runner-up must be from ${bounds.marginMin} to ${bounds.marginMax}.` };
  }
  if (!within(reviewFloor, bounds.reviewFloorMin, bounds.reviewFloorMax)) {
    return { error: `Review floor must be from ${bounds.reviewFloorMin} to ${bounds.reviewFloorMax}.` };
  }
  if (reviewFloor >= autoTitle) return { error: 'The review floor must be below the auto-link title score.' };
  return { value: { autoTitle, margin, reviewFloor } };
}

/**
 * Settings tab of `/admin/metadata` (stage 2). The stage-1 "Series metadata" card moved
 * here unchanged in behaviour (show switch, consent v1 + Fetch, budget, per-library
 * Fetch / Show / precedence / delete, ComicInfo progress, delete all), plus:
 * - **Automatic matching**: ONE global switch (decision 3), gated by the separate
 *   automatic-lookups consent (v2, decision 2); it applies to every library with Fetch
 *   on, and background refresh follows it. Turning it on is ONE settings PUT - nothing
 *   is looked up by the switch itself.
 * - The budget is ONE global budget (decision 5): usage shown honestly, no hidden reserve.
 * - Per library "Match now" with the local estimate (decision 1).
 * - **Advanced**: the three thresholds with their bounds and Reset (decision 13).
 */
@Component({
  selector: 'app-metadata-settings',
  standalone: true,
  imports: [
    DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatProgressBarModule, MatSelectModule, MatSlideToggleModule, LibraryMatchPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="settings" data-testid="metadata-settings-card">
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (settings(); as s) {
        <div class="grid">
          <div class="col">
          <!-- 1. Web lookups (stage 1) -->
          <section class="card" aria-labelledby="md-web-h">
            <h3 id="md-web-h"><mat-icon aria-hidden="true">public</mat-icon> Web lookups</h3>
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
                Nothing happens automatically unless you also turn on Automatic matching.</p>
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
          </section>

          </div>
          <div class="col">
          <!-- 2. The ONE daily budget (decision 5) -->
          <section class="card" aria-labelledby="md-budget-h">
            <h3 id="md-budget-h"><mat-icon aria-hidden="true">speed</mat-icon> Daily request budget</h3>
            <p class="status" data-testid="md-status">
              Requests today: {{ s.budgetUsedToday }} / {{ s.dailyBudget }}
              @if (backoffActive()) { · <span class="warn">MangaUpdates asked us to wait until {{ s.backoffUntil | date: 'shortTime' }}</span> }
              @if (s.lastErrorCode) { · last error: <code>{{ s.lastErrorCode }}</code> {{ s.lastErrorAt | date: 'short' }} }
            </p>
            <mat-progress-bar mode="determinate" [value]="budgetPercent()" [class.spent]="budgetPercent() >= 100"
                              aria-label="Requests used today" />
            <p class="note" data-testid="md-budget-explain">
              One budget for everything: Identify, automatic matching and background refresh. Every search, series fetch and
              cover counts one. When it is spent, automatic work stops and Identify waits until 00:00 UTC - raise the
              budget any time. There is no hidden reserve.
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
          </section>

          <!-- 3. Automatic matching (decisions 2 + 3) -->
          <section class="card" aria-labelledby="md-auto-h" data-testid="md-auto">
            <h3 id="md-auto-h"><mat-icon aria-hidden="true">auto_awesome</mat-icon> Automatic matching
              <span class="pill" [class.on]="s.autoMatchEnabled" data-testid="md-auto-state">{{ s.autoMatchEnabled ? 'On' : 'Off' }}</span></h3>
            <div class="consent" data-testid="md-auto-consent-text">
              <p>When on, MangaPixer matches new series folders on its own, in the background, in <strong>every library whose
                Fetch switch is on</strong>. Links it is sure about go live at once and are listed under Review › Auto-linked;
                close calls wait for you under Needs review. It also refreshes the series you have linked, by record number:
                every 30 days while a series is ongoing, every 90 days once it is complete, at most 100 a day.</p>
              <p><strong>What is sent automatically:</strong> the cleaned name of each new series-like folder, or of an
                archive that is its own work (in a collection folder, or loose next to other folders) - for example
                "Series Title" from "Series Title [English Title]" - which <strong>nobody reviews before it is sent</strong>,
                with a fixed list of types to leave out (doujinshi, novels, artbooks, drama CDs; doujinshi are searched below a
                folder whose Content is "Doujinshi &amp; adult one-shots"), and MangaUpdates record numbers to refresh linked
                series. MangaUpdates also sees your server's IP address.</p>
              <p><strong>What is never sent:</strong> file paths, your file list, user accounts, reading progress, or anything
                that identifies this server. Folders marked "Don't match", and everything inside them, are never looked up.</p>
              <p><strong>Budget:</strong> automatic requests come out of the same daily budget as Identify. When it is spent,
                automatic work stops until the next day.</p>
              <p>You can switch this off at any time; links it made stay until you remove them.</p>
            </div>
            @if (autoConsentCurrent()) {
              <p class="muted small" data-testid="md-auto-consented">
                Consent given {{ s.autoConsentAt | date: 'mediumDate' }} (version {{ s.acceptedAutoConsentVersion }}).</p>
            } @else {
              <mat-checkbox [checked]="autoConsentTicked()" (change)="autoConsentTicked.set($event.checked)"
                            [disabled]="s.networkDisabledByConfig || !s.fetchEnabled" data-testid="md-auto-consent">
                I understand that folder names will be sent automatically - enable automatic matching
              </mat-checkbox>
            }
            <div>
              <mat-slide-toggle [checked]="!!s.autoMatchEnabled" [disabled]="!canToggleAuto()" (change)="setAuto($event.checked)"
                                data-testid="md-auto-switch">
                Automatic matching
              </mat-slide-toggle>
            </div>
            @if (!s.fetchEnabled) {
              <p class="note" data-testid="md-auto-needs-fetch">Turn on "Fetch from the web" first.</p>
            }
            <p class="coverage" data-testid="md-auto-coverage">
              @if (fetchLibraries().length === 0) {
                No library has Fetch on, so automatic matching has nothing to do.
              } @else {
                {{ s.autoMatchEnabled ? 'Applies to' : 'Would apply to' }}:
                @for (lib of fetchLibraries(); track lib.libraryId; let last = $last) {
                  <span class="lib-chip" [class.active]="lib.autoMatchActive">{{ lib.name }}</span>{{ last ? '' : ' ' }}
                }
                @if (otherLibraries().length > 0) {
                  <span class="muted"> · not {{ otherLibraries().length === 1 ? otherLibraries()[0].name : otherLibraries().length + ' libraries' }} (Fetch off)</span>
                }
              }
            </p>
          </section>

          </div>

          <!-- 4. Libraries (stage 1 rows + "Match now") -->
          <section class="card wide" aria-labelledby="md-libs-h">
            <h3 id="md-libs-h"><mat-icon aria-hidden="true">collections_bookmark</mat-icon> Libraries</h3>
            <div class="libs">
              @for (lib of s.libraries; track lib.libraryId) {
                <div class="lib" [attr.data-library]="lib.libraryId">
                  <span class="name">{{ lib.name }}
                    <span class="muted small">{{ lib.linkCount }} link{{ lib.linkCount === 1 ? '' : 's' }}</span></span>
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
                  <span class="spacer"></span>
                  <button mat-stroked-button type="button" [disabled]="saving() || !lib.fetchEnabled || matching() === lib.libraryId"
                          (click)="matching.set(lib.libraryId)" data-testid="md-match-now">
                    <mat-icon>auto_awesome</mat-icon> Match now</button>
                  @if (confirming() === lib.libraryId) {
                    <span class="confirm">Delete {{ lib.linkCount }} link{{ lib.linkCount === 1 ? '' : 's' }}?
                      <button mat-flat-button color="warn" type="button" (click)="purge(lib.libraryId)">Delete</button>
                      <button mat-button type="button" (click)="confirming.set(null)">Cancel</button></span>
                  } @else {
                    <button mat-button type="button" [disabled]="saving() || lib.linkCount === 0" (click)="confirming.set(lib.libraryId)">
                      Delete fetched data</button>
                  }
                </div>
                @if (matching() === lib.libraryId) {
                  <app-library-match-panel [libraryId]="lib.libraryId" (started)="onRunStarted($event)" (closed)="matching.set(null)" />
                }
              } @empty {
                <p class="muted">No libraries yet.</p>
              }
            </div>
          </section>

          <!-- 5. Advanced: thresholds (decision 13) -->
          <section class="card wide advanced">
            <mat-expansion-panel data-testid="md-advanced">
              <mat-expansion-panel-header>
                <mat-panel-title>Advanced: matching thresholds</mat-panel-title>
                <mat-panel-description>{{ s.thresholdsAreDefault !== false ? 'Defaults' : 'Changed' }}</mat-panel-description>
              </mat-expansion-panel-header>
              @if (s.thresholdBounds; as b) {
                <p class="note">How sure the matcher must be. Higher numbers link less on their own and send more to Needs review.
                  They apply to the next matching; existing links stay.</p>
                <form class="thresholds" (ngSubmit)="saveThresholds()">
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>Auto-link title score</mat-label>
                    <input matInput name="autoTitle" inputmode="decimal" [ngModel]="thresholdText().autoTitle"
                           (ngModelChange)="setThreshold('autoTitle', $event)" data-testid="md-th-auto">
                    <mat-hint>{{ b.autoTitleMin }}–{{ b.autoTitleMax }}, default {{ s.defaultThresholds?.autoTitle }}</mat-hint>
                  </mat-form-field>
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>Lead over the runner-up</mat-label>
                    <input matInput name="margin" inputmode="decimal" [ngModel]="thresholdText().margin"
                           (ngModelChange)="setThreshold('margin', $event)" data-testid="md-th-margin">
                    <mat-hint>{{ b.marginMin }}–{{ b.marginMax }}, default {{ s.defaultThresholds?.margin }}</mat-hint>
                  </mat-form-field>
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>Review floor</mat-label>
                    <input matInput name="reviewFloor" inputmode="decimal" [ngModel]="thresholdText().reviewFloor"
                           (ngModelChange)="setThreshold('reviewFloor', $event)" data-testid="md-th-floor">
                    <mat-hint>{{ b.reviewFloorMin }}–{{ b.reviewFloorMax }}, default {{ s.defaultThresholds?.reviewFloor }}</mat-hint>
                  </mat-form-field>
                  <div class="th-actions">
                    <button mat-stroked-button type="submit" [disabled]="saving() || !thresholdsDirty() || !!thresholdError()"
                            data-testid="md-th-save">Save</button>
                    <button mat-button type="button" [disabled]="saving() || s.thresholdsAreDefault !== false" (click)="resetThresholds()"
                            data-testid="md-th-reset">Reset to defaults</button>
                  </div>
                </form>
                <ul class="note th-explain">
                  <li><strong>Auto-link title score</strong>: the top candidate's title must match at least this well to link on its own.</li>
                  <li><strong>Lead over the runner-up</strong>: and beat the second candidate by at least this much.</li>
                  <li><strong>Review floor</strong>: below this, a folder counts as unmatched instead of waiting in Needs review.</li>
                </ul>
                @if (thresholdError(); as te) { <p class="error small" data-testid="md-th-error">{{ te }}</p> }
              } @else {
                <p class="muted">Thresholds are not available from this server.</p>
              }
            </mat-expansion-panel>
          </section>

          <!-- 6. Stored data -->
          <section class="card wide" aria-labelledby="md-data-h">
            <h3 id="md-data-h"><mat-icon aria-hidden="true">storage</mat-icon> Stored data</h3>
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
          </section>
        </div>
      }
      @if (message()) { <p class="ok small" role="status">{{ message() }}</p> }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: [`
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr)); gap: 16px; align-items: start; }
    .col { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
    .card { background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.07); border-radius: 12px; padding: 14px 18px; min-width: 0; }
    .card.wide { grid-column: 1 / -1; }
    .card.advanced { padding: 0; background: transparent; border: none; }
    h3 { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; font-size: 16px; font-weight: 500; }
    h3 mat-icon { font-size: 20px; width: 20px; height: 20px; color: #b39dff; }
    h4 { margin: 16px 0 6px; font-size: 14px; }
    .note, .small { font-size: 12px; }
    .note { color: #9a9aa8; margin: 4px 0 8px; }
    .muted { color: #999; }
    .consent { font-size: 13px; color: #c8c8d0; border-left: 3px solid #555; padding: 2px 12px; margin: 6px 0 10px; }
    .consent p { margin: 6px 0; }
    .banner { color: #ffb300; }
    .status { font-size: 13px; margin: 0 0 8px; }
    .warn { color: #ffb300; }
    mat-progress-bar { margin: 4px 0 8px; border-radius: 4px; }
    mat-progress-bar.spent { --mdc-linear-progress-active-indicator-color: #ff7043; }
    .budget, .thresholds { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .thresholds mat-form-field { width: 200px; }
    .th-actions { display: flex; gap: 8px; }
    .th-explain { padding-left: 18px; }
    .pill { font-size: 11px; font-weight: 600; padding: 2px 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.08); color: #aaa; }
    .pill.on { background: rgba(76, 175, 80, 0.2); color: #81c784; }
    .coverage { font-size: 13px; margin: 8px 0 0; line-height: 1.9; }
    .lib-chip { display: inline-block; padding: 0 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.06); font-size: 12px; line-height: 20px; }
    .lib-chip.active { background: rgba(179, 157, 255, 0.18); color: #d8ccff; }
    .libs { display: flex; flex-direction: column; gap: 2px; }
    .lib { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; padding: 6px 0; border-bottom: 1px solid rgba(255,255,255,0.06); }
    .lib .name { min-width: 160px; font-weight: 500; display: flex; flex-direction: column; }
    .spacer { flex: 1 1 auto; }
    .prec { width: 190px; }
    .confirm { display: inline-flex; align-items: center; gap: 6px; flex-wrap: wrap; font-size: 13px; }
    .error { color: #f44336; }
    .ok { color: #4caf50; }
    @media (max-width: 599.98px) {
      .card { padding: 12px; }
      .lib .name { min-width: 100%; }
      .thresholds mat-form-field { width: 100%; }
    }
  `],
})
export class MetadataSettingsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);

  /** A run was queued from "Match now" (the page switches to Runs). */
  readonly runStarted = output<MetadataMatchRunDto>();

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly settings = signal<MetadataSettingsDto | null>(null);
  readonly consentTicked = signal(false);
  readonly autoConsentTicked = signal(false);
  readonly budgetText = signal('');
  readonly confirming = signal<string | null>(null);
  /** The library whose "Match now" panel is open. */
  readonly matching = signal<string | null>(null);
  readonly thresholdText = signal<ThresholdText>({ autoTitle: '', margin: '', reviewFloor: '' });

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

  /** Automatic-lookups consent already given for the text version this page shows. */
  readonly autoConsentCurrent = computed(() => {
    const s = this.settings();
    return !!s && s.acceptedAutoConsentVersion != null && s.acceptedAutoConsentVersion === s.currentAutoConsentVersion
      && s.currentAutoConsentVersion === AUTO_CONSENT_TEXT_VERSION;
  });

  /** The Automatic matching switch: ON needs Fetch on and the automatic consent; OFF is always allowed. */
  readonly canToggleAuto = computed(() => {
    const s = this.settings();
    if (!s || this.saving()) return false;
    if (s.autoMatchEnabled) return true;
    return !s.networkDisabledByConfig && s.fetchEnabled && (this.autoConsentCurrent() || this.autoConsentTicked());
  });

  readonly fetchLibraries = computed(() => (this.settings()?.libraries ?? []).filter((l) => l.fetchEnabled));
  readonly otherLibraries = computed(() => (this.settings()?.libraries ?? []).filter((l) => !l.fetchEnabled));

  readonly parsedBudget = computed(() => parseDailyBudget(this.budgetText()));

  readonly budgetPercent = computed(() => {
    const s = this.settings();
    if (!s || s.dailyBudget <= 0) return 0;
    return Math.min(100, Math.round((s.budgetUsedToday / s.dailyBudget) * 100));
  });

  readonly backoffActive = computed(() => {
    const until = this.settings()?.backoffUntil;
    return !!until && new Date(until).getTime() > Date.now();
  });

  private readonly thresholdCheck = computed(() => {
    const b = this.settings()?.thresholdBounds;
    return b ? validateThresholds(this.thresholdText(), b) : null;
  });

  readonly thresholdError = computed(() => {
    const r = this.thresholdCheck();
    return r && 'error' in r ? r.error : null;
  });

  readonly thresholdsDirty = computed(() => {
    const t = this.settings()?.thresholds;
    const r = this.thresholdCheck();
    if (!t) return false;
    if (!r || 'error' in r) return true;
    return r.value.autoTitle !== t.autoTitle || r.value.margin !== t.margin || r.value.reviewFloor !== t.reviewFloor;
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
    if (!this.settings()) return;
    this.save(this.api.updateSettings(on
      ? { fetchEnabled: true, acceptedConsentVersion: CONSENT_TEXT_VERSION }
      : { fetchEnabled: false }));
  }

  /** ONE settings PUT; the switch itself looks nothing up. */
  setAuto(on: boolean): void {
    if (!this.settings()) return;
    this.save(this.api.updateSettings(on
      ? { autoMatchEnabled: true, acceptedAutoConsentVersion: AUTO_CONSENT_TEXT_VERSION }
      : { autoMatchEnabled: false }), on ? 'Automatic matching is on' : 'Automatic matching is off');
  }

  saveBudget(): void {
    const value = this.parsedBudget();
    if (value === null) return;
    this.save(this.api.updateSettings({ dailyBudget: value }), 'Daily budget saved');
  }

  resetBudget(): void {
    this.save(this.api.updateSettings({ resetDailyBudget: true }), 'Daily budget reset');
  }

  setThreshold(key: keyof ThresholdText, value: string): void {
    this.thresholdText.update((t) => ({ ...t, [key]: value }));
  }

  saveThresholds(): void {
    const r = this.thresholdCheck();
    if (!r || 'error' in r) return;
    this.save(this.api.updateSettings({ thresholds: r.value }), 'Thresholds saved');
  }

  resetThresholds(): void {
    this.save(this.api.updateSettings({ resetThresholds: true }), 'Thresholds reset to the defaults');
  }

  setLibrary(lib: MetadataLibrarySettingsDto, change: { fetchEnabled?: boolean; showSeriesInfo?: boolean }): void {
    if (change.fetchEnabled === false && this.matching() === lib.libraryId) this.matching.set(null);
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

  onRunStarted(run: MetadataMatchRunDto): void {
    this.matching.set(null);
    this.message.set(`Matching queued for ${run.libraryName}`);
    this.runStarted.emit(run);
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
    const t = s.thresholds;
    if (t) this.thresholdText.set({ autoTitle: String(t.autoTitle), margin: String(t.margin), reviewFloor: String(t.reviewFloor) });
  }

  private fail(err: ApiError): void {
    this.saving.set(false);
    this.error.set(err?.message || 'The change was not saved');
    // Re-sync toggles with the server state after a refused change.
    this.api.getSettings().subscribe({ next: (s) => this.apply(s), error: () => undefined });
  }
}
