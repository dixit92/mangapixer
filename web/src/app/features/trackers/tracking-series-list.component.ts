import {
  ChangeDetectionStrategy, Component, ElementRef, OnChanges, OnDestroy, OnInit, SimpleChanges, computed, inject, input, output, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, firstValueFrom } from 'rxjs';

import { TrackerBatchAction, TrackerBatchResultDto, TrackerEntryDto, TrackerListCountsDto, TrackerSeriesRowDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { TrackersApiService } from './trackers-api.service';
import {
  BatchTotals, actionBlockText, batchActionLabel, batchActionsFor, batchConfirmLabel, batchQuestion, batchSummary, bucketText, bucketTone,
  dateText, emptyBatchTotals, entryStateText, failureText, fromTrackerQuestion, movedCounts, numbersText, ofText, previewNotice, statusText,
  volumesFromChaptersLabel, volumesFromChaptersText,
  trackerName, trackerSeriesUrl, trackingChips,
} from './tracker-texts';
import { SelectFlags, applies, flagsOfRow, flagsOfSelectable, noneReason, reachesTracker } from './tracking-selection';
import { provideTrackerTooltips } from './tracker-tooltips';
import { TrackingListMemory } from './tracking-list-memory.service';

/** Rows per page (the server's page size). */
export const TRACKING_PAGE_SIZE = 50;

/**
 * Series per batch request: small for the actions that reach the tracker (paced at about one request a second, so a request stays
 * short and the progress moves), the server's maximum for the switches (no request to the tracker).
 */
export const BATCH_CHUNK_TRACKER = 10;
export const BATCH_CHUNK_SWITCH = 100;

/** How long the search waits after the last key before it asks the server. */
const SEARCH_DELAY_MS = 300;

/**
 * The Tracking page's series list for ONE tracker (1.43.0 owner redesign), built for hundreds of series: server paging (50 a
 * page, "Load more"), filter chips by state with counts, a title search, and compact rows - cover, title (to the series), state
 * chip, MangaPixer's chapters / volumes (of how many), the tracker's (and its status), the dates (MyAnimeList; automatic or the
 * user's, "Set dates…"), and the actions:
 * - "Set progress from <tracker> to MangaPixer" marks chapters / volumes read HERE up to the tracker's numbers, after a confirm
 *   ("Mark chapters 1-51 read in MangaPixer?"), and says what it marked; it never unmarks;
 * - "Set progress from MangaPixer to <tracker>" sends MangaPixer's numbers (a held jump too; never lowers the tracker);
 * - the one that cannot move anything is disabled, with the reason as a muted line under the buttons (a touch screen has no hover;
 *   a mouse also gets it as the tooltip - UX review UX1);
 * - "⋯": Sync now, Set dates on <tracker>…, "Set <tracker> to a number… (can lower it)" (the warning form, in the warn colour),
 *   Don't sync this series / Sync again, Open the series; a disabled item says why in a muted line (UX7).
 * The tracker column's numbers open the series on the tracker's site (a new tab, no referrer; also "Open on <tracker>" in "⋯").
 * The toolbar keeps "Check my list on <tracker>" (writes nothing: MyAnimeList reads the whole list, others 50 series a click) and
 * "Select" (1.43.2, replaces "Send all"): a tick per row (Shift-click fills a range), "Select all N in this chip" (every matching
 * series on the server, not only the loaded pages), and a bar at the bottom with the actions - each with how many of the selected it
 * applies to, the chip's natural one first, a line saying why one applies to none -, ONE confirm with the counts, then the series go to
 * the server in chunks with "37 of 51…" and Cancel between chunks, and one summary. The list itself is stored state: showing, paging,
 * filtering and searching contact no tracker. A list reload (chip, search, Load more) closes an open confirm or "Set to" form and clears the error line once a page
 * arrives; a row action moves its series between the chip counts at once; Back from "Open the series" finds the same chip, search
 * and pages (UX review UX2-UX4, UX11).
 */
@Component({
  selector: 'app-tracking-series-list',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, CoverImageDirective],
  providers: [provideTrackerTooltips()],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="list" [attr.data-testid]="'tracking-list-' + slug()">
      <div class="toolbar">
        <label class="search">
          <mat-icon aria-hidden="true">search</mat-icon>
          <input type="search" placeholder="Find a series" aria-label="Find a series by title" maxlength="200" autocomplete="off"
                 [ngModel]="search()" (ngModelChange)="onSearch($event)" (keydown.enter)="searchNow()" data-testid="tracking-search" />
        </label>
        <button mat-stroked-button type="button" [disabled]="busy() || !canSync()" (click)="preview()"
                [attr.data-testid]="'tracker-preview-' + slug()">
          <mat-icon>fact_check</mat-icon>
          {{ previewRemaining() > 0 ? 'Check ' + Math.min(previewRemaining(), 50) + ' more (' + previewRemaining() + ' left)' : 'Check my list on ' + name() }}
        </button>
        <button mat-stroked-button type="button" class="select-toggle" [class.on]="selecting()" [attr.aria-pressed]="selecting()"
                [disabled]="applying()" (click)="toggleSelect()" [attr.data-testid]="'tracking-select-' + slug()">
          <mat-icon>checklist</mat-icon> {{ selecting() ? 'Done selecting' : 'Select' }}
        </button>
      </div>

      <div class="chips" role="group" aria-label="Show series by state">
        @for (chip of chips(); track chip.filter) {
          <button type="button" class="chip-btn" [class.on]="filter() === chip.filter" [attr.aria-pressed]="filter() === chip.filter"
                  (click)="setFilter(chip.filter)" [attr.data-testid]="'tracking-chip-' + chip.filter">
            {{ chip.label }} · {{ chip.count }}
          </button>
        }
      </div>

      @if (notice(); as n) { <p class="ok" role="status" [attr.data-testid]="'tracking-notice-' + slug()">{{ n }}</p> }
      @if (error(); as e) { <p class="error" role="alert" [attr.data-testid]="'tracking-error-' + slug()">{{ e }}</p> }

      @if (loading()) {
        <p class="muted pad">Loading…</p>
      } @else if (items().length === 0) {
        <p class="muted pad" [attr.data-testid]="'tracking-empty-' + slug()">{{ emptyText() }}</p>
      } @else {
        <div class="rows" [class.with-dates]="datesColumn()">
          <div class="row header" aria-hidden="true">
            <span>Series</span><span>MangaPixer</span><span>{{ name() }}</span>
            @if (datesColumn()) { <span>Dates on {{ name() }}</span> }
            <span>Bring in line</span>
          </div>
          @for (r of items(); track r.seriesId; let i = $index) {
            <div class="row" [class.focused]="focused() === r.seriesId" [class.picked]="selecting() && isSelected(r)"
                 [attr.data-testid]="'tracking-row-' + r.seriesId" [attr.data-bucket]="r.bucket">
              <div class="series">
                @if (selecting()) {
                  <label class="pick" [attr.data-testid]="'tracking-pick-' + r.seriesId">
                    <input type="checkbox" [checked]="isSelected(r)" [disabled]="applying()" (click)="pick($event, r, i)"
                           [attr.aria-label]="'Select ' + r.title" [attr.data-testid]="'tracking-pick-box-' + r.seriesId" />
                  </label>
                }
                <div class="thumb" aria-hidden="true">
                  @if (r.nodeId) { <img appCover [src]="coverOf(r.nodeId)" alt="" loading="lazy"> }
                  <mat-icon>menu_book</mat-icon>
                </div>
                <div class="names">
                  @if (r.nodeId) {
                    <a class="title" [routerLink]="['/series', r.nodeId]">{{ r.title }}</a>
                  } @else {
                    <span class="title">{{ r.title }}</span>
                  }
                  <span class="state" [attr.data-tone]="tone(r)" [attr.data-testid]="'tracking-state-' + r.seriesId">{{ chipText(r) }}</span>
                </div>
              </div>
              <div class="cell">
                <span class="cell-label">MangaPixer</span>
                <span [attr.data-testid]="'tracking-here-' + r.seriesId">{{ here(r) }}</span>
                @if (numbers(r.heldChapters, r.heldVolumes); as h) { <span class="held">Waiting for you: {{ h }}</span> }
              </div>
              <div class="cell remote">
                <span class="cell-label">{{ name() }}</span>
                @if (seriesUrl(r); as url) {
                  <!-- 1.43.2: the series on the tracker's site - a new tab, no referrer (this server's address never reaches it). -->
                  <a class="remote-num ext" [href]="url" target="_blank" rel="noopener noreferrer"
                     [attr.aria-label]="'Open on ' + name() + ': ' + remote(r)" [attr.data-testid]="'tracking-remote-link-' + r.seriesId">
                    <span [attr.data-testid]="'tracking-remote-' + r.seriesId">{{ remote(r) }}</span>
                    <mat-icon aria-hidden="true">open_in_new</mat-icon>
                  </a>
                } @else {
                  <span class="remote-num" [attr.data-testid]="'tracking-remote-' + r.seriesId">{{ remote(r) }}</span>
                }
                @if (!r.remoteMissing && status(r.remoteStatus); as s) { <span class="sub">{{ s }}</span> }
              </div>
              @if (datesColumn()) {
                <div class="cell dates" [attr.data-testid]="'tracking-dates-' + r.seriesId">
                  <span class="cell-label">Dates on {{ name() }}</span>
                  @if (r.dates.supported) {
                    <span>Start {{ date(r.dates.startedOn) }} <span class="sub">{{ dateNote(r.dates.startedOn, r.dates.startedOnOverridden, r.dates.remoteStartedOn) }}</span></span>
                    <span>Finish {{ date(r.dates.finishedOn) }} <span class="sub">{{ dateNote(r.dates.finishedOn, r.dates.finishedOnOverridden, r.dates.remoteFinishedOn) }}</span></span>
                    @if (r.dates.pending) { <span class="sub">Goes with the next update</span> }
                    <button type="button" class="link" [disabled]="busy()" (click)="openDates(r)"
                            [attr.data-testid]="'tracking-set-dates-' + r.seriesId">Set dates…</button>
                  } @else {
                    <span class="sub">None</span>
                  }
                </div>
              }
              <div class="cell actions-cell">
                @if (confirmFrom() === r.seriesId) {
                  <div class="confirm" role="alertdialog" aria-label="Set progress from the tracker" [attr.data-testid]="'tracking-from-confirm-' + r.seriesId">
                    <p>{{ question(r) }} Nothing is unmarked.</p>
                    <div class="actions">
                      <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="fromTracker(r)"
                              [attr.data-testid]="'tracking-from-yes-' + r.seriesId">Mark read</button>
                      <button mat-button type="button" [disabled]="busy()" (click)="confirmFrom.set(null)">Cancel</button>
                    </div>
                  </div>
                } @else if (setting() === r.seriesId) {
                  <form class="set" (ngSubmit)="setProgress(r)" [attr.data-testid]="'tracker-set-form-' + r.seriesId">
                    <p class="warn">This replaces what {{ name() }} has for this series - also when {{ name() }} has MORE. Leave a field
                      empty to keep that number.</p>
                    <label>Chapters <input type="number" name="chapters" min="0" max="100000" step="1" [ngModel]="setChapters()"
                           (ngModelChange)="setChapters.set($event)" [attr.data-testid]="'tracker-set-chapters-' + r.seriesId" /></label>
                    <label>Volumes <input type="number" name="volumes" min="0" max="100000" step="1" [ngModel]="setVolumes()"
                           (ngModelChange)="setVolumes.set($event)" [attr.data-testid]="'tracker-set-volumes-' + r.seriesId" /></label>
                    <div class="actions">
                      <button mat-flat-button color="warn" type="submit" [disabled]="busy() || !setValid()"
                              [attr.data-testid]="'tracker-set-yes-' + r.seriesId">Set {{ name() }} to {{ setSummary() }}</button>
                      <button mat-button type="button" [disabled]="busy()" (click)="setting.set(null)">Cancel</button>
                    </div>
                  </form>
                } @else {
                  <div class="actions row-actions">
                    <button mat-flat-button color="primary" type="button" disabledInteractive
                            [disabled]="busy() || !canSync() || !r.fromTracker.possible" [matTooltip]="fromReason(r)"
                            (click)="askFrom(r)" [attr.data-testid]="'tracking-from-' + r.seriesId">
                      Set progress from {{ name() }} to MangaPixer
                    </button>
                    <button mat-flat-button color="primary" type="button" disabledInteractive
                            [disabled]="busy() || !canSync() || !r.toTracker.possible" [matTooltip]="toReason(r)"
                            (click)="toTracker(r)" [attr.data-testid]="'tracking-to-' + r.seriesId">
                      Set progress from MangaPixer to {{ name() }}
                    </button>
                    <button mat-icon-button type="button" [matMenuTriggerFor]="more" [matMenuTriggerData]="{ r: r }"
                            aria-label="More actions" matTooltip="More actions" [attr.data-testid]="'tracking-more-' + r.seriesId">
                      <mat-icon>more_horiz</mat-icon>
                    </button>
                    @let why = whyBlocked(r);
                    @if (why.length) {
                      <p class="why" [attr.data-testid]="'tracking-why-' + r.seriesId">
                        @for (line of why; track $index) { <span>{{ line }}</span> }
                      </p>
                    }
                    <!-- 1.44.0 (owner): the tracker's own chapters finish more volumes than it lists - raise them on the user's click. -->
                    @if (!r.dontSync && volumesOffer(r); as offer) {
                      <div class="vol-offer" [attr.data-testid]="'tracking-volumes-' + r.seriesId">
                        <span>{{ offer }}</span>
                        <button mat-stroked-button type="button" [disabled]="busy() || !canSync() || r.dontSync" (click)="raiseVolumes(r)"
                                [attr.data-testid]="'tracking-volumes-set-' + r.seriesId">{{ volumesLabel(r) }}</button>
                      </div>
                    }
                  </div>
                }
              </div>
            </div>
          }
        </div>
      }

      <mat-menu #more="matMenu" class="tracking-more-menu">
        <ng-template matMenuContent let-r="r">
          <button mat-menu-item type="button" [disabled]="!canAct(r)" (click)="syncNow(r)" data-testid="tracking-menu-sync">
            <mat-icon>sync</mat-icon><span class="item">Sync now
              @if (syncBlock(r); as why) { <span class="item-why" data-testid="tracking-menu-sync-why">{{ why }}</span> }</span>
          </button>
          @if (r.dates.supported) {
            <button mat-menu-item type="button" (click)="openDates(r)" data-testid="tracking-menu-dates">
              <mat-icon>event</mat-icon><span>Set dates on {{ name() }}…</span>
            </button>
          }
          <button mat-menu-item type="button" class="warn-item" [disabled]="!canSetTo(r)" (click)="openSet(r)" data-testid="tracking-menu-set">
            <mat-icon>warning_amber</mat-icon><span class="item">Set {{ name() }} to a number… (can lower it)
              @if (setBlock(r); as why) { <span class="item-why" data-testid="tracking-menu-set-why">{{ why }}</span> }</span>
          </button>
          <button mat-menu-item type="button" (click)="toggleDontSync(r)" data-testid="tracking-menu-dont-sync">
            <mat-icon>{{ r.dontSync ? 'sync' : 'sync_disabled' }}</mat-icon><span>{{ r.dontSync ? 'Sync this series again' : "Don't sync this series" }}</span>
          </button>
          @if (r.nodeId) {
            <a mat-menu-item [routerLink]="['/series', r.nodeId]" data-testid="tracking-menu-open">
              <mat-icon>menu_book</mat-icon><span>Open the series</span>
            </a>
          }
          @if (seriesUrl(r); as url) {
            <a mat-menu-item [href]="url" target="_blank" rel="noopener noreferrer" data-testid="tracking-menu-open-tracker">
              <mat-icon>open_in_new</mat-icon><span>Open on {{ name() }}</span>
            </a>
          }
        </ng-template>
      </mat-menu>

      @if (selecting()) {
        <!-- 1.43.2 Select mode: one bar for every chip - the count, Select all in this chip, Clear, and the actions with how many of the
             selected each applies to; an action that applies to none says why in a line (no hover needed). -->
        <div class="select-bar" role="region" aria-label="Selected series" [attr.data-testid]="'tracking-select-bar-' + slug()">
          <div class="sel-head">
            <span class="sel-count" role="status" data-testid="tracking-selected-count">{{ selectedCount() }} selected</span>
            @if (selectAllLabel(); as label) {
              <button mat-button type="button" [disabled]="busy() || selectingAll()" (click)="selectAllInChip()"
                      data-testid="tracking-select-all">{{ selectingAll() ? 'Selecting…' : label }}</button>
            }
            <button mat-button type="button" [disabled]="applying() || selectedCount() === 0" (click)="clearSelection()"
                    data-testid="tracking-select-clear">Clear</button>
            <button mat-stroked-button type="button" class="sel-done" [disabled]="applying()" (click)="toggleSelect()"
                    data-testid="tracking-select-done"><mat-icon>close</mat-icon> Done</button>
          </div>
          @if (progress(); as p) {
            <div class="progress" role="status" data-testid="tracking-batch-progress">
              <span>{{ p.done }} of {{ p.total }}…</span>
              <button mat-button type="button" [disabled]="cancelRequested()" (click)="cancelBatch()" data-testid="tracking-batch-cancel">
                {{ cancelRequested() ? 'Stopping after this part…' : 'Cancel' }}</button>
            </div>
          } @else if (batchConfirm(); as action) {
            <div class="confirm" role="alertdialog" aria-label="Apply to the selected series" data-testid="tracking-batch-confirm">
              <p>{{ batchQuestionText(action) }}</p>
              <div class="actions">
                <button mat-flat-button color="primary" type="button" (click)="applyBatch(action)" data-testid="tracking-batch-yes">
                  {{ confirmLabel(action) }}</button>
                <button mat-button type="button" (click)="batchConfirm.set(null)" data-testid="tracking-batch-no">Cancel</button>
              </div>
            </div>
          } @else {
            <div class="sel-actions">
              @for (a of batchActions(); track a; let first = $first) {
                <button type="button" [class]="first ? 'sel-act primary' : 'sel-act'" [disabled]="busy() || actionCount(a) === 0"
                        [attr.aria-label]="actionLabel(a) + ' (' + actionCount(a) + ')'" (click)="askBatch(a)"
                        [attr.data-testid]="'tracking-batch-' + a">
                  <span class="lbl-long">{{ actionLabel(a) }}</span><span class="lbl-short">{{ actionLabel(a, true) }}</span>
                  <span class="n">{{ actionCount(a) }}</span>
                </button>
              }
            </div>
            @if (selectedCount() === 0) {
              <p class="why" data-testid="tracking-batch-why">Tick series with the boxes{{ selectAllLabel() ? ', or select all in this chip' : '' }}.</p>
            } @else if (whyLines().length) {
              <!-- 1.44.0 (owner, lane P question): on a phone the reason lines fold behind one line; a tap opens them in place. -->
              <button type="button" class="why-toggle" [attr.aria-expanded]="whyOpen()" aria-controls="tracking-batch-why-lines"
                      (click)="whyOpen.set(!whyOpen())" data-testid="tracking-batch-why-toggle">
                <mat-icon>{{ whyOpen() ? 'expand_less' : 'info_outline' }}</mat-icon>{{ whyToggleLabel() }}
              </button>
              <p class="why why-lines" id="tracking-batch-why-lines" [class.open]="whyOpen()" data-testid="tracking-batch-why">
                @for (line of whyLines(); track line.action) {
                  <span [attr.data-testid]="'tracking-batch-why-' + line.action"><b>{{ actionLabel(line.action, true) }}:</b> {{ line.text }}</span>
                }
              </p>
            }
          }
        </div>
      }

      <div class="footer">
        <span [attr.data-testid]="'tracking-shown-' + slug()">{{ shownText() }}</span>
        <button mat-stroked-button type="button" [disabled]="loadingMore() || items().length >= total()" (click)="loadMore()"
                [attr.data-testid]="'tracking-load-more-' + slug()">{{ loadingMore() ? 'Loading…' : 'Load more' }}</button>
      </div>
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .list { min-width: 0; container-type: inline-size; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; padding: 12px 16px; }
    .toolbar mat-icon { margin-right: 4px; }
    .search { display: flex; align-items: center; gap: 6px; min-height: 36px; padding: 0 12px; border-radius: 18px; flex: 1 1 200px;
              max-width: 320px; min-width: 0; background: var(--mp-surface-raised); border: 1px solid rgb(var(--mp-ink-rgb) / 0.14);
              color: var(--mp-text-muted); }
    .search mat-icon { font-size: 18px; width: 18px; height: 18px; margin: 0; }
    .search input { flex: 1 1 auto; min-width: 0; border: 0; background: transparent; color: var(--mp-text); font: inherit; outline: none; }
    .chips { display: flex; flex-wrap: wrap; gap: 8px; padding: 4px 16px 12px; border-bottom: 1px solid rgb(var(--mp-ink-rgb) / 0.1); }
    .chip-btn { min-height: 32px; padding: 0 12px; border-radius: 16px; font: inherit; font-size: 13px; cursor: pointer;
                border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); background: transparent; color: var(--mp-text-secondary); }
    .chip-btn:hover { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .chip-btn.on { background: var(--mp-accent-strong); border-color: var(--mp-accent-strong); color: var(--mp-on-accent); }
    .pad { padding: 8px 16px; }
    .muted { color: var(--mp-text-muted); font-size: 13px; margin: 6px 0; overflow-wrap: anywhere; }
    .ok { color: var(--mp-success); font-size: 13px; margin: 8px 16px; }
    .error { color: var(--mp-error); font-size: 13px; margin: 8px 16px; }
    .confirm { border: 1px solid rgb(var(--mp-warn-strong-rgb) / 0.6); border-radius: 6px; padding: 8px 10px; font-size: 14px; }
    .list > .confirm { margin: 10px 16px; }
    .confirm p { margin: 2px 0 8px; overflow-wrap: anywhere; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }

    /* Rows: a five-column grid on a wide list, one card per series below 900 px of list width (container query). */
    .rows { display: flex; flex-direction: column; }
    .row { display: grid; gap: 8px 16px; align-items: center; padding: 12px 16px; border-bottom: 1px solid rgb(var(--mp-ink-rgb) / 0.08);
           grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.1fr) minmax(0, 1.1fr) minmax(0, 2.6fr); }
    .rows.with-dates .row { grid-template-columns: minmax(0, 2.2fr) minmax(0, 1.1fr) minmax(0, 1.1fr) minmax(0, 1.2fr) minmax(0, 2.6fr); }
    .row.header { padding-top: 8px; padding-bottom: 8px; font-size: 12px; font-weight: 500; letter-spacing: 0.04em; text-transform: uppercase;
                  color: var(--mp-text-muted); }
    .row.focused { background: rgb(var(--mp-warn-strong-rgb) / 0.1); box-shadow: inset 3px 0 0 var(--mp-tracker-mark); }
    .series { display: flex; align-items: center; gap: 12px; min-width: 0; }
    .thumb { flex: none; position: relative; width: 40px; height: 56px; border-radius: 4px; overflow: hidden;
             background: rgb(var(--mp-ink-rgb) / 0.08); display: flex; align-items: center; justify-content: center; }
    .thumb img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .thumb mat-icon { position: absolute; z-index: 0; color: var(--mp-text-faint); font-size: 20px; width: 20px; height: 20px; }
    .names { min-width: 0; display: flex; flex-direction: column; align-items: flex-start; gap: 4px; }
    .title { font-weight: 500; color: var(--mp-text-strong); overflow-wrap: anywhere; text-decoration: none; }
    a.title:hover { text-decoration: underline; }
    .state { font-size: 12px; padding: 2px 8px; border-radius: 10px; overflow-wrap: anywhere;
             background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-secondary); }
    .state[data-tone='ok'] { background: rgb(var(--mp-success-strong-rgb) / 0.16); color: var(--mp-success); }
    .state[data-tone='ahead'] { background: rgb(var(--mp-accent-strong-rgb) / 0.16); color: var(--mp-accent); }
    .state[data-tone='tracker'], .state[data-tone='warn'] { background: rgb(var(--mp-warn-strong-rgb) / 0.16); color: var(--mp-warn); }
    .cell { display: flex; flex-direction: column; gap: 2px; min-width: 0; font-size: 13px; overflow-wrap: anywhere; color: var(--mp-text); }
    .cell-label { display: none; font-size: 12px; color: var(--mp-text-muted); }
    .remote .remote-num { color: var(--mp-warn); }
    a.ext { display: inline-flex; align-items: center; gap: 4px; align-self: flex-start; text-decoration: none; min-height: 24px; }
    a.ext:hover span, a.ext:focus-visible span { text-decoration: underline; }
    a.ext mat-icon { font-size: 15px; width: 15px; height: 15px; flex: none; }
    /* Select mode (1.43.2): a 44 px tick target before the cover; the picked row is tinted. */
    .select-toggle.on { background: var(--mp-surface-accent); border-color: rgb(var(--mp-accent-strong-rgb) / 0.5); }
    .pick { flex: none; width: 44px; height: 44px; margin: -6px -4px -6px -8px; display: flex; align-items: center; justify-content: center;
            cursor: pointer; border-radius: 8px; }
    .pick:hover { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .pick input { width: 20px; height: 20px; margin: 0; cursor: pointer; accent-color: var(--mp-accent-strong); }
    .row.picked { background: rgb(var(--mp-accent-strong-rgb) / 0.08); }
    /* The bar stays at the bottom of the screen while the list scrolls under it. */
    .select-bar { position: sticky; bottom: 0; z-index: 5; display: flex; flex-direction: column; gap: 8px; padding: 10px 16px 12px;
                  background: var(--mp-surface-raised); border-top: 1px solid rgb(var(--mp-accent-strong-rgb) / 0.5);
                  box-shadow: 0 -6px 16px rgb(var(--mp-shade-rgb) / 0.25); }
    .sel-head { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
    .sel-count { font-weight: 600; margin-right: auto; color: var(--mp-text-strong); }
    .sel-done mat-icon { margin-right: 4px; }
    .sel-actions { display: flex; flex-wrap: wrap; gap: 8px; }
    .sel-act { display: inline-flex; align-items: center; gap: 8px; min-height: 40px; padding: 4px 12px; border-radius: 20px; font: inherit;
               font-size: 13px; line-height: 1.25; text-align: left; cursor: pointer; color: var(--mp-accent);
               background: transparent; border: 1px solid rgb(var(--mp-accent-strong-rgb) / 0.6); max-width: 100%; }
    .sel-act.primary { background: var(--mp-accent-strong); border-color: var(--mp-accent-strong); color: var(--mp-on-accent); }
    .sel-act:disabled { cursor: default; color: var(--mp-text-faint); background: transparent; border-color: rgb(var(--mp-ink-rgb) / 0.14); }
    .sel-act .n { font-weight: 600; font-size: 12px; padding: 1px 7px; border-radius: 9px; background: rgb(var(--mp-ink-rgb) / 0.12); }
    .sel-act .lbl-short { display: none; }
    .select-bar .why { margin: 0; display: flex; flex-direction: column; gap: 2px; font-size: 12px; line-height: 1.3; color: var(--mp-text-muted);
                       overflow-wrap: anywhere; }
    .select-bar .why b { font-weight: 500; color: var(--mp-text-secondary); }
    .vol-offer { grid-column: 1 / -1; display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; font-size: 13px; color: var(--mp-text-secondary); }
    .why-toggle { display: none; align-self: flex-start; align-items: center; gap: 4px; min-height: 40px; padding: 0 4px; border: 0;
                  background: none; font: inherit; font-size: 12px; color: var(--mp-text-muted); cursor: pointer; }
    .why-toggle mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .progress { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 14px; color: var(--mp-text); }
    .sub { font-size: 12px; color: var(--mp-text-muted); }
    .held { font-size: 12px; color: var(--mp-warn); }
    .link { align-self: flex-start; padding: 0; margin-top: 2px; border: 0; background: none; font: inherit; font-size: 12px;
            color: var(--mp-accent); text-decoration: underline; cursor: pointer; }
    .link:disabled { color: var(--mp-text-faint); cursor: default; }
    /* The two Set-progress buttons stacked, the "⋯" beside them: two short lines a row, not three. */
    .row-actions { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 6px 4px; align-items: center; }
    .row-actions button[mat-flat-button] { grid-column: 1; white-space: normal; height: auto; min-height: 32px; padding: 4px 14px;
                                           font-size: 13px; line-height: 1.25; text-align: left; justify-content: flex-start; }
    .row-actions button[mat-icon-button] { grid-column: 2; grid-row: 1 / span 2; }
    /* Why a Set-progress button is off, readable without a hover (UX1): one short muted line per reason, under both buttons. */
    .row-actions .why { grid-column: 1 / -1; margin: 0; display: flex; flex-direction: column; gap: 2px; font-size: 12px; line-height: 1.3;
                        color: var(--mp-text-muted); overflow-wrap: anywhere; }
    /* A disabled "⋯" item keeps its reason readable: the item is not faded as a whole, only its icon and label (UX7). */
    ::ng-deep .tracking-more-menu .item { display: flex; flex-direction: column; gap: 2px; padding: 4px 0; white-space: normal; }
    ::ng-deep .tracking-more-menu .item-why { font-size: 12px; line-height: 1.3; color: var(--mp-text-muted); }
    ::ng-deep .tracking-more-menu .mat-mdc-menu-item[disabled] { opacity: 1; }
    ::ng-deep .tracking-more-menu .mat-mdc-menu-item[disabled] > .mat-icon,
    ::ng-deep .tracking-more-menu .mat-mdc-menu-item[disabled] .item { color: var(--mp-text-faint); }
    .set { display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: center; font-size: 13px; }
    .set p { flex: 1 1 100%; margin: 2px 0; }
    .set input { font: inherit; width: 90px; padding: 4px 6px; box-sizing: border-box; color: var(--mp-text); background: var(--mp-surface);
                 border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    .set .actions { flex: 1 1 100%; }
    .warn { color: var(--mp-warn); }
    ::ng-deep .tracking-more-menu .warn-item:not([disabled]), ::ng-deep .tracking-more-menu .warn-item:not([disabled]) .mat-icon { color: var(--mp-warn); }
    .footer { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 16px;
              color: var(--mp-text-muted); font-size: 13px; }
    @container (max-width: 600px) {
      .sel-act .lbl-long { display: none; }
      .sel-act .lbl-short { display: inline; }
      .why-toggle { display: inline-flex; }
      .select-bar .why-lines:not(.open) { display: none; }
    }
    @container (max-width: 900px) {
      .row, .rows.with-dates .row { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); }
      .row.header { display: none; }
      .series, .actions-cell { grid-column: 1 / -1; }
      .rows.with-dates .dates { grid-column: 1 / -1; }
      .cell-label { display: block; }
    }
  `],
})
export class TrackingSeriesListComponent implements OnInit, OnChanges, OnDestroy {
  private readonly api = inject(TrackersApiService);
  private readonly dialog = inject(MatDialog);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly memory = inject(TrackingListMemory);

  readonly slug = input.required<string>();
  readonly name = input.required<string>();
  /** The account can sync right now (allowed, connected, current consent): the actions that reach the tracker are on. */
  readonly canSync = input(true);
  /** A series to show highlighted (from the series header's "Open in Tracking"). */
  readonly focusSeries = input<string | null>(null);
  /** The search to start with (the focused series' title). */
  readonly initialSearch = input('');
  /** The account's "Add series I start reading" (words the confirm of "Set progress from MangaPixer"). */
  readonly addStartedSeries = input(true);
  /** Something changed that the account menu's badge counts (a send, a from-tracker, a preview). */
  readonly changed = output<void>();

  protected readonly Math = Math;

  readonly filter = signal<string>('All');
  readonly search = signal('');
  readonly items = signal<TrackerSeriesRowDto[]>([]);
  readonly counts = signal<TrackerListCountsDto | null>(null);
  readonly total = signal(0);
  readonly page = signal(0);
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly busy = signal(false);
  readonly notice = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly previewRemaining = signal(0);
  /** Select mode (1.43.2): the selected series (in the order they were picked) with what applies to each. */
  readonly selecting = signal(false);
  readonly selected = signal<Map<string, SelectFlags>>(new Map());
  readonly selectingAll = signal(false);
  /** The action whose confirm is open, the progress of a running apply, and a Cancel waiting for the current chunk. */
  readonly batchConfirm = signal<TrackerBatchAction | null>(null);
  readonly progress = signal<{ done: number; total: number } | null>(null);
  readonly cancelRequested = signal(false);
  readonly applying = computed(() => this.progress() !== null);
  readonly focused = signal<string | null>(null);

  /** The series whose from-tracker confirm is open. */
  readonly confirmFrom = signal<string | null>(null);
  /** The series whose "Set to" form is open, and its numbers. */
  readonly setting = signal<string | null>(null);
  readonly setChapters = signal<number | null>(null);
  readonly setVolumes = signal<number | null>(null);

  readonly chips = computed(() => trackingChips(this.counts(), this.slug()));
  readonly selectedCount = computed(() => this.selected().size);
  /** The selected series' flags - a loaded row's current ones (they follow a row action), else what "Select all" brought. */
  private readonly selectedFlags = computed(() => {
    const rows = new Map(this.items().map((r) => [r.seriesId, r]));
    return [...this.selected()].map(([id, f]) => { const r = rows.get(id); return r ? flagsOfRow(r) : f; });
  });
  /**
   * The bar's actions for the chip. 1.44.0: "Set <tracker>'s volumes from its chapters" shows only while a selected series has that offer - it
   * rarely applies, and a greyed button with its own reason line for every selection made the bar tall enough to cover rows.
   */
  readonly batchActions = computed(() => {
    const offers = this.selectedFlags().some((f) => f.volumesFromChapters);
    return batchActionsFor(this.filter()).filter((a) => a !== 'VolumesFromChapters' || offers);
  });
  private readonly actionCounts = computed(() => {
    const flags = this.selectedFlags();
    const sync = this.canSync();
    return new Map(this.batchActions().map((a) => [a, !sync && reachesTracker(a) ? 0 : flags.filter((f) => applies(a, f)).length]));
  });
  /** 1.44.0: the folded reason lines are open (a phone only; on a wider screen they always show). */
  readonly whyOpen = signal(false);
  /** "Why 2 actions are off" / "Why one action is off". */
  readonly whyToggleLabel = computed(() => {
    const n = this.whyLines().length;
    return n === 1 ? 'Why one action is off' : `Why ${n} actions are off`;
  });
  readonly whyLines = computed(() => {
    const flags = this.selectedFlags();
    return this.batchActions()
      .map((action) => ({ action, text: noneReason(action, flags, this.canSync(), this.slug()) }))
      .filter((l): l is { action: TrackerBatchAction; text: string } => !!l.text);
  });
  /** "Select all 51 in this chip" while more match than are selected; null when every match is selected. */
  readonly selectAllLabel = computed(() => {
    const total = this.total();
    if (total === 0 || this.selected().size >= total) return null;
    if (this.search().trim()) return `Select all ${total} matching`;
    return this.filter() === 'All' ? `Select all ${total}` : `Select all ${total} in this chip`;
  });
  readonly datesColumn = computed(() => this.items().some((r) => r.dates.supported));
  readonly shownText = computed(() => {
    const n = this.items().length;
    return n === 0 ? `Showing 0 of ${this.total()}` : `Showing 1–${n} of ${this.total()} · ${TRACKING_PAGE_SIZE} per page`;
  });
  readonly emptyText = computed(() => {
    if (this.search().trim()) return 'No series matches this search.';
    if (this.filter() !== 'All') return 'No series in this group.';
    return `No series yet. Series you read here that ${this.name()} knows show up as you read them; "Check my list on ${this.name()}" ` +
      'looks at the ones you read before you connected.';
  });
  readonly setValid = computed(() => {
    const ok = (v: number | null) => v == null || (Number.isInteger(v) && v >= 0 && v <= 100000);
    return (this.setChapters() != null || this.setVolumes() != null) && ok(this.setChapters()) && ok(this.setVolumes());
  });
  readonly setSummary = computed(() => numbersText(this.setChapters(), this.setVolumes()) ?? '…');

  readonly numbers = numbersText;
  readonly status = statusText;
  readonly date = dateText;

  /**
   * The note beside a date of the "Dates on <tracker>" cell: "(yours)" for the user's own, nothing for what the tracker already keeps,
   * "(auto)" only for the automatic date MangaPixer would send because the tracker keeps none.
   */
  dateNote(shown: string | null | undefined, overridden: boolean, remote: string | null | undefined): string {
    if (!shown) return '';
    if (overridden) return '(yours)';
    return shown === remote ? '' : '(auto)';
  }
  readonly question = fromTrackerQuestion;

  private generation = 0;
  /** The index (in the loaded rows) of the last row ticked or unticked: a Shift-click fills from here. */
  private anchor: number | null = null;
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  /** The focused series was highlighted, or the user moved on (a search or a chip): it is not highlighted again. */
  private focusDone = false;
  /** Back from "Open the series": the pages still to load and the scroll position to return to. */
  private restoring: { pages: number; scrollY: number } | null = null;

  ngOnInit(): void {
    const q = this.initialSearch().trim();
    const back = this.focusSeries() ? null : this.memory.take(this.slug());
    if (q) {
      this.search.set(q.slice(0, 200));
    } else if (back) {
      this.filter.set(back.filter);
      this.search.set(back.search);
      this.restoring = { pages: back.pages, scrollY: back.scrollY };
    }
    this.reload();
  }

  /** Another `?series=` while the list is shown (UX8): that series is searched and highlighted; none = the highlight ends. */
  ngOnChanges(changes: SimpleChanges): void {
    const moved = [changes['focusSeries'], changes['initialSearch']].some((c) => c && !c.firstChange);
    if (!moved) return;
    if (!this.focusSeries()) {
      this.endFocus();
      return;
    }
    this.focusDone = false;
    this.focused.set(null);
    this.clearSelection();
    this.filter.set('All');
    this.search.set(this.initialSearch().trim().slice(0, 200));
    if (this.searchTimer) { clearTimeout(this.searchTimer); this.searchTimer = null; }
    this.reload();
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.memory.save(this.slug(), { filter: this.filter(), search: this.search(), pages: Math.max(1, this.page()), scrollY: globalThis.scrollY ?? 0 });
  }

  coverOf(nodeId: string): string {
    return `/api/v1/nodes/${encodeURIComponent(nodeId)}/cover`;
  }

  chipText(r: TrackerSeriesRowDto): string {
    return bucketText(r);
  }

  tone(r: TrackerSeriesRowDto): string {
    return bucketTone(r.bucket);
  }

  /** "45 of 51 ch · 4 of 10 vol" with every mark here. */
  here(r: TrackerSeriesRowDto): string {
    const parts = [ofText(r.allChapters ?? r.chapters, r.totalChapters, 'ch'), ofText(r.allVolumes ?? r.volumes, r.totalVolumes, 'vol')]
      .filter((p): p is string => !!p);
    return parts.length ? parts.join(' · ') : 'Nothing read';
  }

  remote(r: TrackerSeriesRowDto): string {
    if (r.remoteMissing) return 'Not on your list';
    const n = numbersText(r.remoteChapters, r.remoteVolumes);
    if (n) return n;
    return r.lastSyncAt ? 'Nothing read' : 'Not checked yet';
  }

  fromReason(r: TrackerSeriesRowDto): string {
    if (!this.canSync()) return 'Your account cannot sync right now';
    return actionBlockText(r.fromTracker.block, 'from', this.slug()) ?? `Mark read here what ${this.name()} has`;
  }

  toReason(r: TrackerSeriesRowDto): string {
    if (!this.canSync()) return 'Your account cannot sync right now';
    return actionBlockText(r.toTracker.block, 'to', this.slug()) ?? `Send MangaPixer's numbers to ${this.name()} (never lowers it)`;
  }

  /**
   * Why the disabled Set-progress buttons are off, as the muted lines under them (UX1): none while both can act; one line when both
   * are off for the same reason ("neither side is ahead" for a series in sync). A busy moment is not a reason.
   */
  whyBlocked(r: TrackerSeriesRowDto): string[] {
    if (!this.canSync()) return ['Your account cannot sync right now'];
    const from = r.fromTracker.possible ? null : actionBlockText(r.fromTracker.block, 'from', this.slug());
    const to = r.toTracker.possible ? null : actionBlockText(r.toTracker.block, 'to', this.slug());
    if (from && to && r.fromTracker.block === 'NotAhead' && r.toTracker.block === 'NotAhead') {
      return ['Neither side is ahead - nothing to bring in or send'];
    }
    if (from && to && from === to) return [from];
    return [from, to].filter((t): t is string => !!t);
  }

  /** Why "Sync now" is off (the menu item's muted line, UX7); null when it is on. */
  syncBlock(r: TrackerSeriesRowDto): string | null {
    if (this.canAct(r)) return null;
    if (!this.canSync()) return 'Your account cannot sync right now';
    if (r.dontSync) return 'Not synced (your choice)';
    return `No ${trackerName(this.slug())} id - an admin can set it`;
  }

  /** Why "Set <tracker> to a number…" is off; null when it is on. */
  setBlock(r: TrackerSeriesRowDto): string | null {
    if (this.canSetTo(r)) return null;
    return this.syncBlock(r) ?? `Left as is on ${trackerName(this.slug())} - MangaPixer does not change it`;
  }

  /** Sync now reaches the tracker: only for a series with an id that syncs. */
  canAct(r: TrackerSeriesRowDto): boolean {
    return this.canSync() && !r.dontSync && r.bucket !== 'NoId' && !!r.remoteId;
  }

  /** "Set <tracker> to N" - never for a series left as is (the server leaves it alone even then). */
  canSetTo(r: TrackerSeriesRowDto): boolean {
    return this.canAct(r) && r.bucket !== 'LeftAsIs';
  }

  setFilter(filter: string): void {
    if (this.filter() === filter || this.applying()) return;
    this.endFocus();
    this.clearSelection();
    this.filter.set(filter);
    this.reload();
  }

  onSearch(text: string): void {
    this.endFocus();
    this.clearSelection();
    this.search.set(text ?? '');
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.searchTimer = null; this.reload(); }, SEARCH_DELAY_MS);
  }

  searchNow(): void {
    if (this.searchTimer) { clearTimeout(this.searchTimer); this.searchTimer = null; }
    this.endFocus();
    this.reload();
  }

  /** Page 1 again (a new filter or search, or after a change that may move rows between chips); `then` runs once it arrived. */
  reload(then?: () => void): void {
    const gen = ++this.generation;
    this.closeRowForms();
    this.loading.set(this.items().length === 0);
    this.api.seriesPage(this.slug(), 1, this.filter(), this.search()).subscribe({
      next: (p) => {
        if (gen !== this.generation) return;
        this.loading.set(false);
        this.error.set(null);
        this.items.set(p.items);
        this.counts.set(p.counts);
        this.total.set(p.totalCount);
        this.page.set(p.page);
        this.focusOnce();
        this.continueRestore();
        then?.();
      },
      error: (err: unknown) => {
        if (gen !== this.generation) return;
        this.loading.set(false);
        this.restoring = null;
        this.error.set(failureText(err, this.slug()));
      },
    });
  }

  loadMore(): void {
    if (this.loadingMore() || this.items().length >= this.total()) return;
    const gen = this.generation;
    this.closeRowForms();
    this.loadingMore.set(true);
    this.api.seriesPage(this.slug(), this.page() + 1, this.filter(), this.search()).subscribe({
      next: (p) => {
        this.loadingMore.set(false);
        if (gen !== this.generation) return;
        this.error.set(null);
        const seen = new Set(this.items().map((r) => r.seriesId));
        this.items.update((list) => [...list, ...p.items.filter((r) => !seen.has(r.seriesId))]);
        this.counts.set(p.counts);
        this.total.set(p.totalCount);
        this.page.set(p.page);
        this.continueRestore();
      },
      error: (err: unknown) => {
        this.loadingMore.set(false);
        this.restoring = null;
        this.error.set(failureText(err, this.slug()));
      },
    });
  }

  /** A reload or another page drops the transient row UI: the from-tracker confirm, the "Set to" form, the Select-mode confirm (UX3). */
  private closeRowForms(): void {
    this.confirmFrom.set(null);
    this.setting.set(null);
    this.batchConfirm.set(null);
  }

  /** Back from "Open the series": the next saved page, or - all there - the saved scroll position. */
  private continueRestore(): void {
    const r = this.restoring;
    if (!r) return;
    if (this.page() < r.pages && this.items().length < this.total()) {
      this.loadMore();
      return;
    }
    this.restoring = null;
    setTimeout(() => globalThis.scrollTo?.({ top: r.scrollY }));
  }

  /** The `?series=` highlight ends for good: the user searches or picks a chip (UX11). */
  private endFocus(): void {
    this.focusDone = true;
    this.focused.set(null);
  }

  askFrom(r: TrackerSeriesRowDto): void {
    if (this.busy() || !this.canSync() || !r.fromTracker.possible) return;
    this.setting.set(null);
    this.confirmFrom.set(r.seriesId);
  }

  /** "Set progress from <tracker> to MangaPixer" after the confirm: marks read here, never unmarks, and says what it did. */
  fromTracker(r: TrackerSeriesRowDto): void {
    this.run(this.api.fromTracker(this.slug(), r.seriesId), (res) => {
      this.confirmFrom.set(null);
      this.replace(res.entry);
      const files = (n: number) => (n === 1 ? '1 file' : `${n} files`);
      this.notice.set(res.marked > 0
        ? `${r.title}: marked ${files(res.marked)} read in MangaPixer${res.alreadyRead > 0 ? ` (${files(res.alreadyRead)} already read)` : ''}.`
        : `${r.title}: nothing new to mark - ${files(res.alreadyRead)} already read.`);
      this.changed.emit();
    });
  }

  /** "Set progress from MangaPixer to <tracker>" (the held jump too; never lowers the tracker). */
  toTracker(r: TrackerSeriesRowDto): void {
    if (this.busy() || !this.canSync() || !r.toTracker.possible) return;
    this.runEntry(r, this.api.send(this.slug(), r.seriesId));
  }

  readonly volumesOffer = volumesFromChaptersText;
  readonly volumesLabel = volumesFromChaptersLabel;

  /** 1.44.0: "Set <tracker>'s volumes to N" (the server reads the entry again first; only the volume count is written). */
  raiseVolumes(r: TrackerSeriesRowDto): void {
    if (this.busy() || !this.canSync() || r.volumesFromChapters == null) return;
    const volumes = r.volumesFromChapters;
    this.runEntry(r, this.api.volumesFromChapters(this.slug(), r.seriesId), undefined,
      () => `${r.title}: ${this.name()}'s volumes set to ${volumes}.`);
  }

  syncNow(r: TrackerSeriesRowDto): void {
    if (!this.canAct(r)) return;
    this.runEntry(r, this.api.syncNow(this.slug(), r.seriesId));
  }

  openSet(r: TrackerSeriesRowDto): void {
    if (!this.canSetTo(r)) return;
    this.confirmFrom.set(null);
    this.setChapters.set(r.allChapters ?? r.chapters ?? null);
    this.setVolumes.set(r.allVolumes ?? r.volumes ?? null);
    this.setting.set(r.seriesId);
  }

  setProgress(r: TrackerSeriesRowDto): void {
    if (!this.setValid()) return;
    this.runEntry(r, this.api.setProgress(this.slug(), r.seriesId, this.setChapters(), this.setVolumes()), () => this.setting.set(null));
  }

  toggleDontSync(r: TrackerSeriesRowDto): void {
    this.run(this.api.setDontSync(this.slug(), r.seriesId, !r.dontSync), (updated) => {
      this.notice.set(updated.dontSync ? `${r.title} is not synced any more.` : `${r.title} syncs again.`);
      this.refreshRow(r);
      // A series waiting for the user leaves the badge's count when it stops syncing.
      this.changed.emit();
    });
  }

  async openDates(r: TrackerSeriesRowDto): Promise<void> {
    if (this.busy()) return;
    const { TrackerDatesDialogComponent } = await import('./tracker-dates-dialog.component');
    const phone = globalThis.matchMedia?.('(max-width: 599.98px)').matches ?? false;
    const ref = this.dialog.open(TrackerDatesDialogComponent, {
      data: { row: r }, width: phone ? '100vw' : '440px', maxWidth: '100vw', ariaLabel: `Dates on ${this.name()}`,
      autoFocus: 'first-tabbable', restoreFocus: true,
    });
    const updated = await firstValueFrom(ref.afterClosed());
    if (updated) {
      this.replace(updated);
      this.notice.set(`${r.title}: dates saved. They go to ${this.name()} with the next update.`);
    }
  }

  preview(): void {
    this.run(this.api.preview(this.slug()), (p) => {
      this.previewRemaining.set(p.remaining);
      this.notice.set(previewNotice(p, this.name()));
      this.reload();
      this.changed.emit();
    });
  }

  /** The series' page on the tracker's site, or null without a usable id. */
  seriesUrl(r: TrackerSeriesRowDto): string | null {
    return trackerSeriesUrl(this.slug(), r.remoteId);
  }

  // ---- Select mode (1.43.2) ----

  isSelected(r: TrackerSeriesRowDto): boolean {
    return this.selected().has(r.seriesId);
  }

  toggleSelect(): void {
    if (this.applying()) return;
    this.selecting.update((v) => !v);
    this.clearSelection();
  }

  clearSelection(): void {
    this.selected.set(new Map());
    this.anchor = null;
    this.batchConfirm.set(null);
  }

  /** A row's tick: toggles it; Shift-click (desktop) also ticks every loaded row between the last tick and this one. */
  pick(event: MouseEvent, r: TrackerSeriesRowDto, index: number): void {
    // The tick shows the selection (a Shift-click on a ticked row keeps it ticked): the box never toggles on its own.
    event.preventDefault();
    if (this.applying()) return;
    this.batchConfirm.set(null);
    const next = new Map(this.selected());
    const anchor = this.anchor;
    if (event.shiftKey && anchor !== null && anchor < this.items().length) {
      const [lo, hi] = anchor < index ? [anchor, index] : [index, anchor];
      for (const row of this.items().slice(lo, hi + 1)) next.set(row.seriesId, flagsOfRow(row));
    } else if (next.has(r.seriesId)) {
      next.delete(r.seriesId);
    } else {
      next.set(r.seriesId, flagsOfRow(r));
    }
    this.anchor = index;
    this.selected.set(next);
  }

  /** "Select all N in this chip": every series matching the chip and the search - from the loaded rows when all are loaded, else the server's ids. */
  selectAllInChip(): void {
    if (this.busy() || this.selectingAll()) return;
    this.batchConfirm.set(null);
    if (this.items().length >= this.total()) {
      this.selected.set(new Map(this.items().map((r) => [r.seriesId, flagsOfRow(r)])));
      return;
    }
    const gen = this.generation;
    this.selectingAll.set(true);
    this.error.set(null);
    this.api.selectableSeries(this.slug(), this.filter(), this.search()).subscribe({
      next: (list) => {
        this.selectingAll.set(false);
        if (gen !== this.generation || !this.selecting()) return;
        this.selected.set(new Map(list.items.map((x) => [x.seriesId, flagsOfSelectable(x)])));
        this.notice.set(list.truncated ? `Selected the first ${list.items.length} of ${list.totalCount} series - apply, then select the rest.` : null);
      },
      error: (err: unknown) => { this.selectingAll.set(false); this.error.set(failureText(err, this.slug())); },
    });
  }

  actionLabel(action: TrackerBatchAction, short = false): string {
    return batchActionLabel(action, this.slug(), short);
  }

  confirmLabel(action: TrackerBatchAction): string {
    return batchConfirmLabel(action);
  }

  actionCount(action: TrackerBatchAction): number {
    return this.actionCounts().get(action) ?? 0;
  }

  batchQuestionText(action: TrackerBatchAction): string {
    const applying = this.actionCount(action);
    return batchQuestion(action, this.slug(), applying, this.selectedCount() - applying, this.addStartedSeries());
  }

  askBatch(action: TrackerBatchAction): void {
    if (this.busy() || this.actionCount(action) === 0) return;
    this.batchConfirm.set(action);
  }

  cancelBatch(): void {
    if (this.applying()) this.cancelRequested.set(true);
  }

  /**
   * After the confirm: every selected series goes to the server, a chunk at a time (the server skips the ones the action does not
   * apply to, with a reason), "37 of 51…" between chunks; it stops at the daily cap, when the account can no longer sync, on an error
   * or on Cancel. Then one summary, the list again (chip counts follow), the badge, and the selection cleared.
   */
  async applyBatch(action: TrackerBatchAction): Promise<void> {
    if (this.busy()) return;
    const ids = [...this.selected().keys()];
    if (ids.length === 0) return;
    const size = reachesTracker(action) ? BATCH_CHUNK_TRACKER : BATCH_CHUNK_SWITCH;
    const totals: BatchTotals = emptyBatchTotals();
    this.batchConfirm.set(null);
    this.cancelRequested.set(false);
    this.notice.set(null);
    this.error.set(null);
    this.busy.set(true);
    this.progress.set({ done: 0, total: ids.length });
    let sent = 0;
    let failure: string | null = null;
    try {
      while (sent < ids.length) {
        if (this.cancelRequested()) { totals.cancelled = true; break; }
        const chunk = ids.slice(sent, sent + size);
        let r: TrackerBatchResultDto;
        try {
          r = await firstValueFrom(this.api.batch(this.slug(), action, chunk));
        } catch (err: unknown) {
          failure = failureText(err, this.slug());
          break;
        }
        sent += chunk.length;
        totals.done += r.done;
        totals.skipped += r.skipped;
        totals.failed += r.failed;
        totals.marked += r.marked;
        totals.alreadyRead += r.alreadyRead;
        totals.notReached += r.notReached;
        for (const s of r.skips) totals.skips[s.reason] = (totals.skips[s.reason] ?? 0) + s.count;
        for (const row of r.rows) this.replace(row);
        this.progress.set({ done: sent, total: ids.length });
        if (r.capReached) { totals.capReached = true; break; }
        if (r.stoppedCode) { totals.stoppedCode = r.stoppedCode; break; }
      }
    } finally {
      totals.unsent = ids.length - sent;
      this.progress.set(null);
      this.cancelRequested.set(false);
      this.busy.set(false);
    }
    if (totals.done + totals.skipped + totals.failed > 0) this.notice.set(batchSummary(action, this.slug(), totals));
    if (failure) this.error.set(failure);
    this.clearSelection();
    // The list again (its arrival clears the error line - a refused chunk is said again after it).
    this.reload(() => { if (failure) this.error.set(failure); });
    this.changed.emit();
  }

  /** An action answering the old per-series entry: say what happened, then re-read the row in the list's shape. */
  private runEntry(r: TrackerSeriesRowDto, call: Observable<TrackerEntryDto>, done?: () => void, noticeText?: () => string): void {
    this.run(call, (updated) => {
      done?.();
      // 1.44.0 (UX review): an action may word its own notice; the state text keeps the tracker's name as written (no lowercasing).
      this.notice.set(noticeText ? noticeText() : `${r.title}: ${entryStateText(updated)}.`);
      this.refreshRow(r);
      this.changed.emit();
    });
  }

  /** Re-reads one row: the series header's summary when the series has a folder, else page 1 of the list. */
  private refreshRow(r: TrackerSeriesRowDto): void {
    if (!r.nodeId) {
      this.reload();
      return;
    }
    this.api.summary(r.nodeId).subscribe({
      next: (s) => {
        const row = s.rows.find((x) => x.tracker === this.slug() && x.seriesId === r.seriesId);
        if (row) this.replace(row);
        else this.reload();
      },
      error: () => this.reload(),
    });
  }

  /** Puts a row's new state in place; when its chip changed, the chip counts follow without a reload (UX2). */
  private replace(updated: TrackerSeriesRowDto): void {
    const before = this.items().find((x) => x.seriesId === updated.seriesId);
    this.items.update((list) => list.map((x) => (x.seriesId === updated.seriesId ? updated : x)));
    const counts = this.counts();
    if (before && counts && before.bucket !== updated.bucket) this.counts.set(movedCounts(counts, before.bucket, updated.bucket));
  }

  /** Highlights and scrolls to the focused series once, after the first page that holds it. */
  private focusOnce(): void {
    const id = this.focusSeries();
    if (!id || this.focusDone || !this.items().some((r) => r.seriesId === id)) return;
    this.focusDone = true;
    this.focused.set(id);
    setTimeout(() => {
      const el = this.host.nativeElement.querySelector(`[data-testid="tracking-row-${id}"]`);
      el?.scrollIntoView?.({ block: 'center' });
    });
  }

  private run<T>(call: Observable<T>, done: (result: T) => void): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    call.subscribe({
      next: (result) => { this.busy.set(false); done(result); },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, this.slug())); },
    });
  }
}
