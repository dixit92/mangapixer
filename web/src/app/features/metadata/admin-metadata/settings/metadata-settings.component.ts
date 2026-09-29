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
  CoverPassStatusDto,
  MetadataLibrarySettingsDto,
  MetadataMatchRunDto,
  MetadataMatchThresholdBoundsDto,
  MetadataMatchThresholdsDto,
  MetadataPrecedence,
  MetadataSettingsDto,
} from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { MetadataReviewStateService } from '../../metadata-review-state.service';
import { scorePercent } from '../metadata-admin-labels';
import { LibraryMatchPanelComponent } from './library-match-panel.component';
import { MetadataProvidersComponent } from './metadata-providers.component';
import { coverLanguageOptions, volumeCoversWaitingLabel } from './volume-covers';
import { CoverSettingsCardComponent } from './cover-settings-card.component';

/**
 * Consent text version the page shows; must equal the server's `currentConsentVersion`. 2 (1.28.0): the text
 * describes the provider allowlist (MangaUpdates + AniList); an instance that accepted 1 re-accepts.
 * 3 (1.29.0): MangaDex (volume covers and volume lists) joins the allowed sites; an earlier consent is not carried over.
 */
export const CONSENT_TEXT_VERSION = 3;

/**
 * Automatic-lookups consent text version (stage 2, owner decisions 2 + 3); must equal the
 * server's `currentAutoConsentVersion`. Bump it whenever the text below changes.
 * v2 (1.28.0): the cover comparison downloads; an earlier consent is not carried over (owner).
 * v3 (1.29.0): volume covers and volume lists from MangaDex (AniList totals as the fallback).
 */
export const AUTO_CONSENT_TEXT_VERSION = 3;

/** Integer-only daily budget in 1..1,000,000 (the server validates the same range). */
export function parseDailyBudget(raw: string | number | null | undefined): number | null {
  const text = String(raw ?? '').trim();
  if (!/^\d{1,7}$/.test(text)) return null;
  const value = Number(text);
  return value >= 1 && value <= 1_000_000 ? value : null;
}

/** The threshold form as typed: WHOLE PERCENTS (owner decision, 1.27.0), e.g. "92" for 0.92. */
export interface ThresholdText {
  autoTitle: string;
  margin: string;
  reviewFloor: string;
}

/** A 0-1 bound as the whole percent shown in a hint or an error message. */
function boundPercent(value: number): number {
  return scorePercent(value) ?? 0;
}

/**
 * Validates the three thresholds against their bounds (decision 13), the review floor
 * strictly below the auto-link score; the server validates the same. The UI edge is
 * whole percents (owner decision, 1.27.0) - the API and DB keep 0-1 values, so a percent
 * is parsed and divided by 100 before it is compared with the (0-1) bounds or sent, and
 * round-trips exactly back to the same whole percent on the next load (`apply()` below).
 * Returns the parsed 0-1 values or the first problem in words.
 */
export function validateThresholds(
  text: ThresholdText,
  bounds: MetadataMatchThresholdBoundsDto,
): { value: MetadataMatchThresholdsDto } | { error: string } {
  const parse = (raw: string) => {
    const t = raw.trim();
    return /^\d{1,3}$/.test(t) ? Number(t) / 100 : NaN;
  };
  const autoTitle = parse(text.autoTitle);
  const margin = parse(text.margin);
  const reviewFloor = parse(text.reviewFloor);
  const within = (v: number, min: number, max: number) => Number.isFinite(v) && v >= min - 1e-9 && v <= max + 1e-9;
  if (!within(autoTitle, bounds.autoTitleMin, bounds.autoTitleMax)) {
    return { error: `Auto-link title score must be from ${boundPercent(bounds.autoTitleMin)}% to ${boundPercent(bounds.autoTitleMax)}%.` };
  }
  if (!within(margin, bounds.marginMin, bounds.marginMax)) {
    return { error: `Lead over the runner-up must be from ${boundPercent(bounds.marginMin)}% to ${boundPercent(bounds.marginMax)}%.` };
  }
  if (!within(reviewFloor, bounds.reviewFloorMin, bounds.reviewFloorMax)) {
    return { error: `Review floor must be from ${boundPercent(bounds.reviewFloorMin)}% to ${boundPercent(bounds.reviewFloorMax)}%.` };
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
 * - The budget is ONE global budget (decision 5): usage shown, no hidden reserve.
 * - Consent texts show until accepted, then fold behind "What is sent?" (owner, 2026-09-26).
 * - Per library "Match now" with the local estimate (decision 1).
 * - **Advanced**: the three thresholds with their bounds and Reset (decision 13).
 * - **Volume covers** (1.29.0): "Volume covers from the web" (MangaDex, for series linked to MangaUpdates), the
 *   preferred cover language, the pass's progress (loaded on demand), "Delete stored volume covers", and the ONE
 *   MangaDex credit in the UI (owner: Metadata Manager + the docs only).
 */
@Component({
  selector: 'app-metadata-settings',
  standalone: true,
  imports: [
    DatePipe, FormsModule, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatFormFieldModule, MatIconModule,
    MatInputModule, MatProgressBarModule, MatSelectModule, MatSlideToggleModule, LibraryMatchPanelComponent, MetadataProvidersComponent,
    CoverSettingsCardComponent,
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
            @if (!consentCurrent() || showConsent()) {
            <div class="consent" data-testid="md-consent-text">
              <p>When on, MangaPixer can look up series information for the libraries you enable below, on the
                <strong>allowed sites</strong> listed here - and nowhere else.</p>
              <p><strong>What is sent:</strong></p>
              <ul>
                <li><strong>MangaUpdates</strong> (description, authors, genres, publication status, English release totals,
                  cover art): the search text you confirm in the Identify dialog (usually a folder or file name) and
                  MangaUpdates record numbers.</li>
                <li><strong>MangaDex</strong> (volume covers, and which chapters make up each volume, for series already linked
                  to MangaUpdates): the MangaUpdates title of the linked series - never a folder or file name - MangaDex record
                  numbers and your cover languages. Cover images are downloaded from MangaDex's image server
                  (uploads.mangadex.org).</li>
                <li><strong>AniList</strong> (volume and chapter totals, to convert chapters to volumes): the AniList record
                  number when it is known, otherwise the MangaUpdates title of a series that is already linked - never a folder
                  or file name.</li>
              </ul>
              <p>You can remove a site from the list at any time; nothing is ever sent to a removed site. Each site also
                sees your server's IP address, as with any web request.</p>
              <p><strong>What is never sent:</strong> file paths, your file list, user accounts, reading progress, or
                anything that identifies this server.</p>
              <p><strong>When:</strong> only when an admin runs Identify, Look up, Refresh, Choose cover or a Missing-report
                lookup in an enabled library. Nothing happens automatically unless you also turn on Automatic matching.</p>
              <p>Fetched information and covers are stored on this server as-is. You can switch this off at any time; stored
                information stays until you delete it.</p>
            </div>
            }
            <app-metadata-providers [settings]="s" [disabled]="saving()" (changed)="apply($event)" />
            @if (consentCurrent()) {
              <p class="muted small" data-testid="md-consented">
                Consent given {{ s.consentAt | date: 'mediumDate' }} ·
                <button type="button" class="link" (click)="showConsent.set(!showConsent())" [attr.aria-expanded]="showConsent()"
                        data-testid="md-consent-toggle">{{ showConsent() ? 'Hide' : 'What is sent?' }}</button></p>
            } @else {
              <mat-checkbox [checked]="consentTicked()" (change)="consentTicked.set($event.checked)"
                            [disabled]="s.networkDisabledByConfig" data-testid="md-consent">
                I have read this - enable web metadata
              </mat-checkbox>
            }
            <div>
              <mat-slide-toggle [checked]="s.fetchEnabled && consentCurrent()" [disabled]="!canToggleFetch()" (change)="setFetch($event.checked)"
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
              <span class="pill" [class.on]="s.autoMatchEnabled && !autoWaiting(s)" data-testid="md-auto-state">{{ autoStateLabel(s) }}</span></h3>
            @if ((s.fetchEnabled && !autoConsentCurrent()) || showAutoConsent()) {
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
                series. For a folder declared manga, manhwa or manhua, automatic searches leave the other two types out.
                Each site also sees your server's IP address.</p>
              <p><strong>Cover comparison:</strong> when two series tie on the title for a folder of volumes or a one-shot,
                MangaPixer may also download the cover images of those two series from MangaUpdates' image server
                (cdn.mangaupdates.com), by the address MangaUpdates gave, to compare them with the folder's own cover. These
                downloads carry nothing from your library. The comparison runs on your server and the downloaded covers are
                deleted right after.</p>
              <p><strong>Volume covers and volume lists:</strong> for every series that gets linked - by Automatic matching or
                by you - MangaPixer also finds the series on MangaDex by its MangaUpdates title (never a folder or file name),
                reads which chapters make up each volume and which volume covers exist in your preferred cover language and the
                original language, and downloads the covers of volume 1 and of the volumes you have from MangaDex's image
                server (uploads.mangadex.org). It checks again on the refresh schedule until a cover in your preferred language
                appears. When MangaDex has no volume list for a series, it asks AniList for the series' totals - by AniList
                record number when known, otherwise by the MangaUpdates title. You can switch volume covers off below.</p>
              <p><strong>What is never sent:</strong> file paths, your file list, user accounts, reading progress, or anything
                that identifies this server. Folders marked "Don't match", and everything inside them, are never looked up.</p>
              <p><strong>Budget:</strong> automatic requests come out of the same daily budget as Identify. When it is spent,
                automatic work stops until the next day.</p>
              <p>You can switch this off at any time; links it made stay until you remove them.</p>
            </div>
            }
            @if (autoConsentCurrent()) {
              <p class="muted small" data-testid="md-auto-consented">
                Consent given {{ s.autoConsentAt | date: 'mediumDate' }} ·
                <button type="button" class="link" (click)="showAutoConsent.set(!showAutoConsent())" [attr.aria-expanded]="showAutoConsent()"
                        data-testid="md-auto-consent-toggle">{{ showAutoConsent() ? 'Hide' : 'What is sent?' }}</button></p>
            } @else if (s.fetchEnabled) {
              <mat-checkbox [checked]="autoConsentTicked()" (change)="autoConsentTicked.set($event.checked)"
                            [disabled]="s.networkDisabledByConfig || !s.fetchEnabled" data-testid="md-auto-consent">
                I understand that folder names will be sent automatically - enable automatic matching
              </mat-checkbox>
            }
            <div>
              <mat-slide-toggle [checked]="!!s.autoMatchEnabled && autoConsentCurrent()" [disabled]="!canToggleAuto()" (change)="setAuto($event.checked)"
                                data-testid="md-auto-switch">
                Automatic matching
              </mat-slide-toggle>
            </div>
            <div class="sub-toggle" data-testid="md-compare-covers-row">
              <mat-checkbox [checked]="s.compareCoversEnabled !== false"
                            [disabled]="saving() || !s.autoMatchEnabled || !!s.compareCoversDisabledByConfig"
                            (change)="setCompareCovers($event.checked)" data-testid="md-compare-covers">
                Compare covers
              </mat-checkbox>
              <p class="muted small">When two series tie on the title for a folder of volumes or a one-shot, download their
                two covers and prefer the one that is the same picture as the folder's own cover.</p>
              @if (s.compareCoversDisabledByConfig) {
                <p class="note" data-testid="md-compare-covers-config">Switched off in the server configuration.</p>
              }
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

          <!-- 3b. Volume covers and volume lists (1.29.0, MangaDex) -->
          <section class="card" aria-labelledby="md-vc-h" data-testid="md-volume-covers">
            <h3 id="md-vc-h"><mat-icon aria-hidden="true">photo_library</mat-icon> Volume covers</h3>
            <mat-slide-toggle [checked]="s.volumeCoversEnabled !== false && !s.volumeCoversDisabledByConfig"
                              [disabled]="saving() || !!s.volumeCoversDisabledByConfig" (change)="setVolumeCovers($event.checked)"
                              data-testid="md-volume-covers-switch">
              Volume covers from the web
            </mat-slide-toggle>
            <p class="note">For series linked to MangaUpdates: the covers of volume 1 and of the volumes you have, and which
              chapters make up each volume, from MangaDex. In the background with Automatic matching on; otherwise only when
              you use Refresh or Change MangaDex match. Covers are stored on this server; the browser never contacts MangaDex.</p>
            @if (s.volumeCoversDisabledByConfig) {
              <p class="note" data-testid="md-volume-covers-config">Switched off in the server configuration (Metadata:AutoMatch:VolumeCovers).</p>
            }
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lang">
              <mat-label>Preferred cover language</mat-label>
              <mat-select [value]="s.preferredCoverLanguage ?? 'en'" (selectionChange)="setCoverLanguage($event.value)"
                          [disabled]="saving()" data-testid="md-cover-language">
                @for (o of coverLanguages(); track o.code) {
                  <mat-option [value]="o.code">{{ o.label }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <p class="note">When a volume has no cover in this language yet, the cover in the series' original language is used,
              and MangaPixer checks again on the refresh schedule.</p>
            <p class="status" data-testid="md-volume-covers-status">
              @if (coverStatus(); as cs) {
                {{ cs.coversStored }} cover{{ cs.coversStored === 1 ? '' : 's' }} stored · {{ cs.coversListed }} known, not
                downloaded · {{ cs.seriesPending }} series to check
                @if (waitingLabel(); as w) { <br><span class="warn">{{ w }}</span> }
              }
              <button type="button" class="link" (click)="loadCoverStatus()" data-testid="md-volume-covers-progress">
                {{ coverStatus() ? 'Refresh' : 'Show progress' }}</button>
            </p>
            @if (confirming() === 'covers') {
              <span class="confirm">Delete every stored volume cover? The volume lists stay; nothing is sent.
                <button mat-flat-button color="warn" type="button" (click)="deleteCovers()" data-testid="md-volume-covers-delete-confirm">Delete</button>
                <button mat-button type="button" (click)="confirming.set(null)">Cancel</button></span>
            } @else {
              <button mat-stroked-button type="button" [disabled]="saving()" (click)="confirming.set('covers')"
                      data-testid="md-volume-covers-delete">Delete stored volume covers</button>
            }
            <p class="credit" data-testid="md-mangadex-credit">Cover images and volume data from MangaDex (mangadex.org) -
              thanks to MangaDex and its community.</p>
          </section>

          <!-- Volumes view (1.29.0): the global default of the virtual volume stacks. Reads stored data only; a library, a
               folder and each person's own Volumes | Folders switch can override it. -->
          <section class="card" aria-labelledby="md-volumes-h">
            <h3 id="md-volumes-h"><mat-icon aria-hidden="true">collections_bookmark</mat-icon> Volumes view</h3>
            <mat-slide-toggle [checked]="s.virtualVolumesEnabled !== false" [disabled]="saving()" (change)="setVirtualVolumes($event.checked)"
                              data-testid="md-volumes-default">
              Group chapters into volumes by default
            </mat-slide-toggle>
            <p class="note">A series' chapters show as volume stacks, ordered by volume, wherever the file names or a stored volume list say which volume they belong to. Each library and folder can override this, and everyone has a Volumes | Folders switch of their own.</p>
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
                  <mat-form-field appearance="outline" subscriptSizing="dynamic" class="prec">
                    <mat-label>Volumes view</mat-label>
                    <mat-select [value]="lib.virtualVolumes ?? 'default'" (selectionChange)="setVolumesView(lib, $event.value)" [disabled]="saving()"
                                data-testid="md-lib-volumes">
                      <mat-option value="default">Default</mat-option>
                      <mat-option value="On">On</mat-option>
                      <mat-option value="Off">Off</mat-option>
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
                    <input matInput name="autoTitle" inputmode="numeric" [ngModel]="thresholdText().autoTitle"
                           (ngModelChange)="setThreshold('autoTitle', $event)" data-testid="md-th-auto">
                    <span matSuffix>%</span>
                    <mat-hint>{{ pct(b.autoTitleMin) }}–{{ pct(b.autoTitleMax) }}%, default {{ pct(s.defaultThresholds?.autoTitle) }}%</mat-hint>
                  </mat-form-field>
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>Lead over the runner-up</mat-label>
                    <input matInput name="margin" inputmode="numeric" [ngModel]="thresholdText().margin"
                           (ngModelChange)="setThreshold('margin', $event)" data-testid="md-th-margin">
                    <span matSuffix>%</span>
                    <mat-hint>{{ pct(b.marginMin) }}–{{ pct(b.marginMax) }}%, default {{ pct(s.defaultThresholds?.margin) }}%</mat-hint>
                  </mat-form-field>
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>Review floor</mat-label>
                    <input matInput name="reviewFloor" inputmode="numeric" [ngModel]="thresholdText().reviewFloor"
                           (ngModelChange)="setThreshold('reviewFloor', $event)" data-testid="md-th-floor">
                    <span matSuffix>%</span>
                    <mat-hint>{{ pct(b.reviewFloorMin) }}–{{ pct(b.reviewFloorMax) }}%, default {{ pct(s.defaultThresholds?.reviewFloor) }}%</mat-hint>
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

          <!-- 7. Covers (1.29.0 cover layer: crop switch, "Show saved web covers" per library, delete stored covers) -->
          <app-cover-settings-card [initial]="s" />
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
    .consent ul { margin: 6px 0; padding-left: 18px; }
    .consent li { margin: 4px 0; }
    .link { background: none; border: none; padding: 0; font: inherit; color: #b39dff; cursor: pointer; text-decoration: underline; }
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
    .sub-toggle { margin: 4px 0 0 8px; }
    .sub-toggle p { margin: 0 0 4px 40px; }
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
    .lang { width: 260px; margin-top: 8px; }
    .credit { font-size: 12px; color: #9a9aa8; margin: 12px 0 0; padding-top: 8px; border-top: 1px solid rgba(255, 255, 255, 0.06); }
    @media (max-width: 599.98px) {
      .card { padding: 12px; }
      .lib .name { min-width: 100%; }
      .thresholds mat-form-field { width: 100%; }
    }
  `],
})
export class MetadataSettingsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly reviewState = inject(MetadataReviewStateService);

  /** A run was queued from "Match now" (the page switches to Runs). */
  readonly runStarted = output<MetadataMatchRunDto>();

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly settings = signal<MetadataSettingsDto | null>(null);
  readonly consentTicked = signal(false);
  readonly autoConsentTicked = signal(false);
  /** After consent the texts fold away behind "What is sent?" so the controls come first. */
  readonly showConsent = signal(false);
  readonly showAutoConsent = signal(false);
  readonly budgetText = signal('');
  readonly confirming = signal<string | null>(null);
  /** The library whose "Match now" panel is open. */
  readonly matching = signal<string | null>(null);
  readonly thresholdText = signal<ThresholdText>({ autoTitle: '', margin: '', reviewFloor: '' });
  /** The volume-cover pass's progress (1.29.0), loaded on demand. */
  readonly coverStatus = signal<CoverPassStatusDto | null>(null);
  readonly coverLanguages = computed(() => coverLanguageOptions(this.settings()?.preferredCoverLanguage));
  readonly waitingLabel = computed(() => volumeCoversWaitingLabel(this.coverStatus()?.waiting));

  /** Consent already given for the current text version. */
  readonly consentCurrent = computed(() => {
    const s = this.settings();
    return !!s && s.acceptedConsentVersion === s.currentConsentVersion && s.currentConsentVersion === CONSENT_TEXT_VERSION;
  });

  /** The Fetch switch: turning it ON needs the consent tick (or current consent); OFF is always allowed. */
  readonly canToggleFetch = computed(() => {
    const s = this.settings();
    if (!s || this.saving()) return false;
    // 1.28.0: on under an older consent reads as off (the server stops it); turning it on again re-accepts.
    if (s.fetchEnabled && this.consentCurrent()) return true;
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
    if (s.autoMatchEnabled && this.autoConsentCurrent()) return true;
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

  /**
   * 1.28.0 (owner): after an update that renewed a consent, the server keeps Automatic matching off until an admin
   * accepts again - the card says so instead of "On".
   */
  autoWaiting(s: MetadataSettingsDto): boolean {
    return !!s.autoMatchEnabled && (!!s.autoConsentRenewalNeeded || !!s.consentRenewalNeeded);
  }

  autoStateLabel(s: MetadataSettingsDto): string {
    return this.autoWaiting(s) ? 'Waiting for consent' : s.autoMatchEnabled ? 'On' : 'Off';
  }

  /** "Compare covers" (1.28.0): one settings PUT, no consent of its own (the automatic consent covers it). */
  setCompareCovers(on: boolean): void {
    if (!this.settings()) return;
    this.save(this.api.updateSettings({ compareCoversEnabled: on }), on ? 'Covers will be compared' : 'Covers will not be compared');
  }

  /** "Volume covers from the web" (1.29.0): one settings PUT; no consent of its own (both consents cover it). */
  setVolumeCovers(on: boolean): void {
    if (!this.settings()) return;
    this.save(this.api.updateSettings({ volumeCoversEnabled: on }), on ? 'Volume covers are on' : 'Volume covers are off');
  }

  /** The preferred cover language: every cover list is read again once (budgeted) to find covers in it. */
  setCoverLanguage(code: string): void {
    if (!this.settings() || code === this.settings()!.preferredCoverLanguage) return;
    this.save(this.api.updateSettings({ preferredCoverLanguage: code }), 'Preferred cover language saved');
  }

  loadCoverStatus(): void {
    this.api.getVolumeCoverStatus().subscribe({
      next: (st) => this.coverStatus.set(st),
      error: (err: ApiError) => this.error.set(err?.message || 'Failed to load the volume cover progress'),
    });
  }

  /** "Delete stored volume covers": local only. */
  deleteCovers(): void {
    this.saving.set(true);
    this.error.set(null);
    this.confirming.set(null);
    this.api.deleteStoredVolumeCovers().subscribe({
      next: (st) => {
        this.saving.set(false);
        this.coverStatus.set(st);
        this.message.set('Stored volume covers deleted');
      },
      error: (err: ApiError) => this.fail(err),
    });
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

  /** A 0-1 threshold value (bound or default) as the whole percent the inputs show. */
  pct(value: number | null | undefined): number | '' {
    return scorePercent(value) ?? '';
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

  /** The global default of the Volumes view (1.29.0). */
  setVirtualVolumes(on: boolean): void {
    this.save(this.api.updateSettings({ virtualVolumesEnabled: on }), on ? 'Volumes view is on by default' : 'Volumes view is off by default');
  }

  /** One library's Volumes view override (1.29.0): On, Off, or back to the global default. */
  setVolumesView(lib: MetadataLibrarySettingsDto, value: 'default' | 'On' | 'Off'): void {
    this.save(this.api.updateLibrary(lib.libraryId, value === 'default' ? { resetVirtualVolumes: true } : { virtualVolumes: value }));
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

  /** Applies a saved settings state (also from the allowed-sites chips). */
  apply(s: MetadataSettingsDto): void {
    this.settings.set(s);
    // Share the saved state with the summary card at the top of the page (1.27.0).
    this.reviewState.setSettings(s);
    this.budgetText.set(String(s.dailyBudget));
    const t = s.thresholds;
    if (t) {
      this.thresholdText.set({
        autoTitle: String(this.pct(t.autoTitle)), margin: String(this.pct(t.margin)), reviewFloor: String(this.pct(t.reviewFloor)),
      });
    }
  }

  private fail(err: ApiError): void {
    this.saving.set(false);
    this.error.set(err?.message || 'The change was not saved');
    // Re-sync toggles with the server state after a refused change.
    this.api.getSettings().subscribe({ next: (s) => this.apply(s), error: () => undefined });
  }
}
