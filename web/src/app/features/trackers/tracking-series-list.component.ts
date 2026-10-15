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

import { TrackerEntryDto, TrackerListCountsDto, TrackerSeriesRowDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { TrackersApiService } from './trackers-api.service';
import {
  actionBlockText, bucketText, bucketTone, dateText, entryStateText, failureText, fromTrackerQuestion, movedCounts, numbersText, ofText,
  statusText, trackerName, trackingChips,
} from './tracker-texts';
import { provideTrackerTooltips } from './tracker-tooltips';
import { TrackingListMemory } from './tracking-list-memory.service';

/** Rows per page (the server's page size). */
export const TRACKING_PAGE_SIZE = 50;

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
 * The toolbar keeps "Check my list on <tracker>" (the first-connect preview: reads 50 series a click, writes nothing) and "Send
 * all" for the series waiting for the user. The list itself is stored state: showing, paging, filtering and searching contact no
 * tracker. A list reload (chip, search, Load more) closes an open confirm or "Set to" form and clears the error line once a page
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
        @if ((counts()?.held ?? 0) > 0) {
          <button mat-stroked-button type="button" [disabled]="busy() || !canSync()" (click)="confirmSendAll.set(true)"
                  [attr.data-testid]="'tracker-send-all-' + slug()">
            <mat-icon>send</mat-icon> Send all ({{ counts()?.held }})
          </button>
        }
      </div>

      <div class="chips" role="group" aria-label="Show series by state">
        @for (chip of chips(); track chip.filter) {
          <button type="button" class="chip-btn" [class.on]="filter() === chip.filter" [attr.aria-pressed]="filter() === chip.filter"
                  (click)="setFilter(chip.filter)" [attr.data-testid]="'tracking-chip-' + chip.filter">
            {{ chip.label }} · {{ chip.count }}
          </button>
        }
      </div>

      @if (confirmSendAll()) {
        <div class="confirm" role="alertdialog" aria-label="Send all" [attr.data-testid]="'tracker-send-all-confirm-' + slug()">
          <p>Send the waiting progress of {{ counts()?.held === 1 ? '1 series' : counts()?.held + ' series' }} to {{ name() }}? This
            includes folders you marked read at once and imported reading. A number on {{ name() }} is never lowered.</p>
          <div class="actions">
            <button mat-flat-button color="primary" type="button" [disabled]="busy()" (click)="sendAll()"
                    [attr.data-testid]="'tracker-send-all-yes-' + slug()">Send all</button>
            <button mat-button type="button" [disabled]="busy()" (click)="confirmSendAll.set(false)">Cancel</button>
          </div>
        </div>
      }
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
          @for (r of items(); track r.seriesId) {
            <div class="row" [class.focused]="focused() === r.seriesId" [attr.data-testid]="'tracking-row-' + r.seriesId"
                 [attr.data-bucket]="r.bucket">
              <div class="series">
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
                <span [attr.data-testid]="'tracking-remote-' + r.seriesId">{{ remote(r) }}</span>
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
                        @for (line of why; track line) { <span>{{ line }}</span> }
                      </p>
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
              <mat-icon>open_in_new</mat-icon><span>Open the series</span>
            </a>
          }
        </ng-template>
      </mat-menu>

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
    .remote > span:nth-child(2) { color: var(--mp-warn); }
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
  readonly confirmSendAll = signal(false);
  readonly focused = signal<string | null>(null);

  /** The series whose from-tracker confirm is open. */
  readonly confirmFrom = signal<string | null>(null);
  /** The series whose "Set to" form is open, and its numbers. */
  readonly setting = signal<string | null>(null);
  readonly setChapters = signal<number | null>(null);
  readonly setVolumes = signal<number | null>(null);

  readonly chips = computed(() => trackingChips(this.counts(), this.slug()));
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
    if (this.filter() === filter) return;
    this.endFocus();
    this.filter.set(filter);
    this.reload();
  }

  onSearch(text: string): void {
    this.endFocus();
    this.search.set(text ?? '');
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.searchTimer = null; this.reload(); }, SEARCH_DELAY_MS);
  }

  searchNow(): void {
    if (this.searchTimer) { clearTimeout(this.searchTimer); this.searchTimer = null; }
    this.endFocus();
    this.reload();
  }

  /** Page 1 again (a new filter or search, or after a change that may move rows between chips). */
  reload(): void {
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

  /** A reload or another page drops the transient row UI: the from-tracker confirm, the "Set to" form, the Send all confirm (UX3). */
  private closeRowForms(): void {
    this.confirmFrom.set(null);
    this.setting.set(null);
    this.confirmSendAll.set(false);
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
      this.notice.set(p.capReached
        ? `Checked ${p.entries.length}. Today's request limit for ${this.name()} is reached - try again tomorrow.`
        : `Checked ${p.entries.length} series${p.remaining > 0 ? `, ${p.remaining} left` : ''}. Nothing was changed on ${this.name()}.`);
      this.reload();
      this.changed.emit();
    });
  }

  sendAll(): void {
    this.run(this.api.sendAll(this.slug()), (r) => {
      this.confirmSendAll.set(false);
      const parts = [`Sent ${r.sent}`];
      if (r.skipped) parts.push(`${r.skipped} unchanged`);
      if (r.failed) parts.push(`${r.failed} failed`);
      this.notice.set(parts.join(', ') + (r.capReached ? `. Today's request limit for ${this.name()} is reached.` : '.'));
      this.reload();
      this.changed.emit();
    });
  }

  /** An action answering the old per-series entry: say what happened, then re-read the row in the list's shape. */
  private runEntry(r: TrackerSeriesRowDto, call: Observable<TrackerEntryDto>, done?: () => void): void {
    this.run(call, (updated) => {
      done?.();
      this.notice.set(`${r.title}: ${entryStateText(updated).toLowerCase()}.`);
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
