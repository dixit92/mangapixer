import { BreakpointObserver } from '@angular/cdk/layout';
import {
  ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnDestroy, OnInit, computed, inject, input, output, signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Observable, map } from 'rxjs';

import {
  ApiError,
  MetadataReviewAuthorDto,
  MetadataReviewBulkAction,
  MetadataReviewBulkResultDto,
  MetadataReviewItemDto,
  MetadataReviewSummaryDto,
  MetadataReviewTab,
} from '../../../core/api/api-types';
import { REVIEW_TABS, plural, reviewTabDef } from '../admin-metadata/metadata-admin-labels';
import { IdentifyDialogService, IdentifyMode } from '../identify-dialog/identify-dialog.service';
import { collectionLabel } from '../collection-labels';
import { MetadataApiService, ReviewListFilter } from '../metadata-api.service';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { MetadataStateService } from '../metadata-state.service';
import { PHONE_QUERY } from '../series-info-overlay.service';
import { DeferredCommitQueue } from './deferred-commit';
import { ReattachDialogData, ReattachDialogResult } from './reattach-dialog.component';
import { ReviewAuthorsSheetComponent } from './review-authors-sheet.component';
import {
  ReviewActionDef, ReviewGroup, ReviewRowAction, ReviewRowActionEvent, ReviewRowComponent, rowActions,
} from './review-row.component';

export interface ReviewLibraryOption {
  id: string;
  name: string;
}

interface BulkDef {
  action: MetadataReviewBulkAction;
  label: string;
  icon: string;
  /** A shorter label for the phone bottom bar. */
  short?: string;
}

/** Bulk actions per tab (the contract's `review/bulk`, max 200 nodes). */
export function bulkActions(tab: MetadataReviewTab): BulkDef[] {
  switch (tab) {
    case 'NeedsReview':
      return [
        { action: 'AcceptTop', label: 'Accept top candidates', short: 'Accept', icon: 'done_all' },
        // 1.34.0: the rows that look like a collection about a series (the others answer "no suggestion").
        { action: 'AcceptCollection', label: 'Accept as collections', short: 'Collections', icon: 'collections_bookmark' },
        { action: 'DontMatch', label: 'Don\'t match', icon: 'block' },
        { action: 'RerunMatching', label: 'Re-run matching', short: 'Re-run', icon: 'refresh' },
        { action: 'Later', label: 'Later', icon: 'schedule' },
      ];
    case 'AutoLinked':
      return [
        { action: 'Confirm', label: 'Confirm', icon: 'verified' },
        { action: 'Unlink', label: 'Unlink', icon: 'link_off' },
        { action: 'DontMatch', label: 'Don\'t match', icon: 'block' },
      ];
    case 'Unmatched':
      return [
        { action: 'DontMatch', label: 'Don\'t match', icon: 'block' },
        { action: 'RerunMatching', label: 'Re-run matching', icon: 'refresh' },
      ];
    case 'Confirmed':
      return [{ action: 'Unlink', label: 'Unlink', icon: 'link_off' }];
    default:
      return [];
  }
}

const BULK_DONE: Record<MetadataReviewBulkAction, string> = {
  AcceptTop: 'Accepted',
  DontMatch: 'Marked Don\'t match:',
  RerunMatching: 'Queued again:',
  Confirm: 'Confirmed',
  Unlink: 'Unlinked',
  Later: 'Set aside for later:',
  ClearLater: 'Back in the list:',
  AcceptCollection: 'Accepted as collections:',
};

/** The review action a single-row action sends through `review/bulk`. */
const ROW_BULK: Partial<Record<ReviewRowAction, MetadataReviewBulkAction>> = {
  dontMatch: 'DontMatch',
  confirm: 'Confirm',
  unlink: 'Unlink',
  rerun: 'RerunMatching',
};

/**
 * The review dashboard (metadata stage 2, design section 5): tabs with counts, a
 * library filter, the rows, bulk actions, Undo and keyboard triage (j/k move, a accept,
 * d Don't match, i identify, l later / not later, c confirm, u unlink, x select, e expand;
 * a key works when the row offers its action, see `rowActions`).
 *
 * "Later" (1.33.0) is remembered on the server for every admin: a row set aside is listed
 * after the others (oldest set aside first) until the work is decided or checked again;
 * Needs review can show all rows, only those set aside, or only the others.
 * Reading a tab never contacts a provider. Every change goes through a deferred commit
 * (see `DeferredCommitQueue`), so Undo is exact.
 *
 * Phone: one card per row, candidates as a vertical radio list; tapping a card focuses it
 * and its actions sit in a bottom bar; long-press starts selection (the browse pattern)
 * and the bottom bar then carries the bulk actions.
 */
@Component({
  selector: 'app-metadata-review',
  standalone: true,
  imports: [
    MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatMenuModule, MatProgressSpinnerModule, MatSelectModule,
    MatTooltipModule, ReviewRowComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'onKey($event)' },
  template: `
    <div class="review" [class.select-mode]="selectMode()" [class.phone]="phone()" data-testid="review-dashboard">
      <div class="toolbar">
        <div class="tabs" role="tablist" aria-label="Review lists">
          @for (t of tabs; track t.tab) {
            <button type="button" role="tab" class="tab" [class.active]="tab() === t.tab" [attr.aria-selected]="tab() === t.tab"
                    (click)="setTab(t.tab)" [attr.data-testid]="'review-tab-' + t.tab">
              {{ t.label }}
              @if (count(t.count); as n) { <span class="badge" [class.hot]="t.tab === 'NeedsReview' || t.tab === 'Flags'">{{ n }}</span> }
            </button>
          }
        </div>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)" data-testid="review-library">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      <p class="hint">{{ hint() }}</p>
      @if (tab() === 'NeedsReview' || tab() === 'Unmatched') {
        <div class="filters">
      @if (tab() === 'NeedsReview' && (count('later') > 0 || laterFilter() !== null)) {
        <div class="later-filter" role="group" aria-label="Show items set aside for later" data-testid="review-later-filter">
          <button type="button" class="tab" [class.active]="laterFilter() === null" [attr.aria-pressed]="laterFilter() === null"
                  (click)="setLaterFilter(null)" data-testid="review-later-all">All</button>
          <button type="button" class="tab" [class.active]="laterFilter() === false" [attr.aria-pressed]="laterFilter() === false"
                  (click)="setLaterFilter(false)" data-testid="review-later-now">To review</button>
          <button type="button" class="tab" [class.active]="laterFilter() === true" [attr.aria-pressed]="laterFilter() === true"
                  (click)="setLaterFilter(true)" data-testid="review-later-only">
            <mat-icon inline>schedule</mat-icon> Later <span class="badge">{{ count('later') }}</span></button>
        </div>
      }
          <!-- 1.33.0: works by one circle / artist, or from one folder, listed together (from local names only). -->
          @if (group(); as g) {
            <span class="group-chip" data-testid="review-group-chip">
              <mat-icon inline>{{ g.kind === 'author' ? 'groups' : 'folder' }}</mat-icon>
              <span class="glabel">{{ g.kind === 'author' ? 'By' : 'In' }} {{ g.label }}</span> ({{ total() }})
              <button type="button" class="gclear" (click)="setGroup(null)" aria-label="Show every item again" data-testid="review-group-clear">
                <mat-icon inline>close</mat-icon></button>
            </span>
          }
          @if (phone()) {
            <button type="button" class="tab authors" (click)="openAuthorsSheet()" data-testid="review-authors">
              <mat-icon inline>groups</mat-icon> Authors</button>
          } @else {
            <button type="button" class="tab authors" [matMenuTriggerFor]="authorsMenu" (menuOpened)="loadAuthors()" data-testid="review-authors">
              <mat-icon inline>groups</mat-icon> Authors <mat-icon inline>arrow_drop_down</mat-icon></button>
            <mat-menu #authorsMenu="matMenu" class="review-authors-menu">
              @for (a of authors() ?? []; track a.key) {
                <button mat-menu-item type="button" (click)="setGroup({ kind: 'author', key: a.key, label: a.label })" data-testid="review-author">
                  <span>{{ a.label }}</span> <span class="menu-count">{{ a.count }}</span></button>
              } @empty {
                <p class="menu-empty">{{ authors() === null ? 'Loading…' : (tab() === 'Unmatched' ? 'No author has two or more works here.' : 'No author has two or more works waiting.') }}</p>
              }
            </mat-menu>
          }
        </div>
      }
      @if (tab() === 'NeedsReview' && recheckPending() > 0) {
        <!-- 1.31.0: after an update changed how matches are scored, the items waiting here are scored once more (in the background). -->
        <p class="recheck" role="status" data-testid="review-rechecking">
          <mat-icon inline>autorenew</mat-icon>
          {{ recheckPending() }} {{ recheckPending() === 1 ? 'item is' : 'items are' }} being checked again under the current rules.
        </p>
      }

      @if (visible().length > 0 && bulk().length > 0) {
        <div class="select-line">
          <mat-checkbox [checked]="allSelected()" [indeterminate]="selected().size > 0 && !allSelected()"
                        (change)="selectAll($event.checked)" data-testid="review-select-all">
            Select all {{ visible().length }} shown
          </mat-checkbox>
          @if (!phone()) {
            <span class="keys">j/k move · a accept · f collection · d don't match · i identify · l later · g same author · x select · e covers</span>
          }
        </div>
      }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert" data-testid="review-error">{{ error() }}</p>
      } @else {
        <div class="rows" role="list">
          @for (it of visible(); track it.nodeId; let i = $index) {
            <div class="wrap" role="listitem" (pointerdown)="pressStart($event, it)" (pointerup)="pressEnd()"
                 (pointerleave)="pressEnd()" (pointercancel)="pressEnd()">
              <app-review-row [item]="it" [tab]="tab()" [focused]="focusIndex() === i" [selected]="selected().has(it.nodeId)"
                              [expanded]="expanded().has(it.nodeId)" [compact]="phone()"
                              [rank]="rankOf(it)"
                              (action)="onRowAction($event)" (toggleSelect)="toggle(it.nodeId)" (toggleExpand)="toggleExpanded(it.nodeId)"
                              (choose)="choose(it.nodeId, $event)" (focusRow)="onRowTap(i, it)" (group)="setGroup($event)" />
            </div>
          } @empty {
            <div class="empty" data-testid="review-empty">
              <mat-icon>task_alt</mat-icon>
              <p>{{ emptyText() }}</p>
            </div>
          }
        </div>
        @if (hasMore()) {
          <div class="more">
            <button mat-stroked-button type="button" [disabled]="loadingMore()" (click)="loadMore()" data-testid="review-more">
              Load more ({{ total() - items().length }} left)</button>
          </div>
        }
      }

      <!-- Desktop bulk bar -->
      @if (!phone() && selected().size > 0) {
        <div class="bulkbar" data-testid="review-bulkbar">
          <span>Selected {{ selected().size }}</span>
          @for (b of bulk(); track b.action) {
            <button mat-stroked-button type="button" (click)="runBulk(b.action)" [attr.data-testid]="'bulk-' + b.action">
              <mat-icon>{{ b.icon }}</mat-icon> {{ b.label }}</button>
          }
          <button mat-button type="button" (click)="clearSelection()">Clear</button>
        </div>
      }

      <!-- Phone bottom bar: bulk actions while selecting, else the focused row's actions -->
      @if (phone()) {
        @if (selectMode()) {
          <div class="bottombar" data-testid="review-bottombar">
            <button mat-icon-button type="button" (click)="clearSelection()" aria-label="Stop selecting"><mat-icon>close</mat-icon></button>
            <span class="sel-count">{{ selected().size }}</span>
            @for (b of bulk(); track b.action) {
              <button mat-button type="button" [disabled]="selected().size === 0" (click)="runBulk(b.action)"
                      [attr.data-testid]="'bulk-' + b.action">
                <mat-icon>{{ b.icon }}</mat-icon><span class="lbl">{{ b.short ?? b.label }}</span></button>
            }
          </div>
        } @else if (focusedItem(); as f) {
          <div class="bottombar" data-testid="review-bottombar">
            <span class="bar-name" data-testid="bar-name">{{ f.displayName }}</span>
            @for (a of focusedActions(); track a.action) {
              <button mat-button type="button" (click)="onRowAction({ action: a.action, item: f, rank: rankOf(f) })"
                      [disabled]="(a.action === 'accept' || a.action === 'acceptCollection') && !(f.candidates ?? []).length"
                      [attr.data-testid]="'bar-' + a.action">
                <mat-icon>{{ a.icon }}</mat-icon><span class="lbl">{{ a.short ?? a.label }}</span></button>
            }
          </div>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; justify-content: space-between; }
    .tabs { display: flex; flex-wrap: wrap; gap: 6px; }
    .tab { border: 1px solid rgba(255, 255, 255, 0.12); background: transparent; color: #d0d0dc; border-radius: 18px;
      padding: 6px 12px; font: inherit; font-size: 13px; cursor: pointer; display: inline-flex; align-items: center; gap: 6px; }
    .tab:hover { background: rgba(255, 255, 255, 0.05); }
    .tab.active { background: rgba(179, 157, 255, 0.2); border-color: #b39dff; color: #fff; }
    .tab:focus-visible { outline: 2px solid #b39dff; }
    .badge { font-size: 11px; font-weight: 600; padding: 0 6px; border-radius: 9px; background: rgba(255, 255, 255, 0.12); line-height: 18px; }
    .badge.hot { background: #7c4dff; color: #fff; }
    .lib-filter { width: 220px; }
    .hint { font-size: 13px; color: #9a9aa8; margin: 10px 0; }
    .filters { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; margin: 0 0 10px; }
    .later-filter { display: flex; flex-wrap: wrap; gap: 6px; }
    .later-filter .tab, .filters .authors { padding: 3px 10px; font-size: 12px; }
    .group-chip { display: inline-flex; align-items: center; gap: 4px; max-width: 100%; min-width: 0; font-size: 12px; padding: 2px 4px 2px 10px;
      border-radius: 16px; background: rgba(255, 183, 77, 0.16); border: 1px solid rgba(255, 204, 128, 0.45); color: #ffe0b2; }
    .group-chip .glabel { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
    .gclear { border: 0; background: transparent; color: inherit; cursor: pointer; padding: 2px; border-radius: 50%; display: inline-flex; }
    .gclear:hover, .gclear:focus-visible { background: rgba(255, 255, 255, 0.12); }
    .menu-count { margin-left: 8px; font-size: 12px; font-weight: 600; color: #ffcc80; }
    .menu-empty { margin: 8px 16px; color: #9a9aa8; font-size: 13px; }
    .recheck { font-size: 13px; color: #90caf9; margin: 0 0 10px; display: flex; align-items: center; gap: 6px; }
    .select-line { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: 8px; margin-bottom: 6px; }
    .keys { font-size: 12px; color: #8a8a99; }
    .rows { display: flex; flex-direction: column; gap: 8px; padding-bottom: 72px; }
    .wrap { touch-action: manipulation; }
    .phone .wrap { -webkit-touch-callout: none; user-select: none; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 40px 0; color: #9a9aa8; }
    .empty mat-icon { font-size: 40px; width: 40px; height: 40px; color: #81c784; }
    .more { display: flex; justify-content: center; margin: 8px 0 80px; }
    .bulkbar { position: sticky; bottom: 12px; display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 10px 14px;
      margin-top: 8px; border-radius: 12px; background: #2a2540; border: 1px solid #5e4b9c; box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45); }
    .bulkbar mat-icon { margin-right: 2px; }
    .bottombar { position: fixed; left: 0; right: 0; bottom: 0; z-index: 20; display: flex; align-items: center; justify-content: space-around;
      gap: 2px; padding: 4px 4px calc(4px + env(safe-area-inset-bottom)); background: #211d33; border-top: 1px solid #5e4b9c; }
    .bottombar button { display: inline-flex; flex-direction: column; align-items: center; min-width: 0; padding: 0 6px; height: 52px; }
    .bottombar .lbl { font-size: 11px; line-height: 14px; white-space: nowrap; }
    .sel-count { font-weight: 600; }
    .bottombar:has(.bar-name) { flex-wrap: wrap; }
    .bar-name { flex: 1 0 100%; font-size: 12px; color: #b0b0c0; text-align: center; padding: 2px 8px 0;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    @media (max-width: 599.98px) {
      .lib-filter { width: 100%; }
      .tabs { flex-wrap: nowrap; overflow-x: auto; scrollbar-width: none; padding-bottom: 2px; }
      .tab { flex: none; }
    }
  `],
})
export class ReviewDashboardComponent implements OnInit, OnDestroy {
  private readonly api = inject(MetadataApiService);
  private readonly reviewState = inject(MetadataReviewStateService);
  private readonly metadataState = inject(MetadataStateService);
  private readonly identifyDialog = inject(IdentifyDialogService);
  private readonly dialog = inject(MatDialog);
  private readonly bottomSheet = inject(MatBottomSheet);
  private readonly snackBar = inject(MatSnackBar);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly breakpoints = inject(BreakpointObserver);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialTab = input<MetadataReviewTab>('NeedsReview');
  readonly initialLibrary = input<string | null>(null);
  /** The Flags tab lives on the page's own Flags tab (one place to resolve flags). */
  readonly openFlags = output<void>();
  /** Tab / library changes, for the page URL. */
  readonly stateChange = output<{ tab: MetadataReviewTab; library: string | null }>();

  readonly tabs = REVIEW_TABS;
  readonly tab = signal<MetadataReviewTab>('NeedsReview');
  readonly library = signal<string | null>(null);
  readonly summary = signal<MetadataReviewSummaryDto | null>(null);
  readonly items = signal<MetadataReviewItemDto[]>([]);
  readonly total = signal(0);
  readonly cursor = signal<string | null>(null);
  readonly hasMore = signal(false);
  readonly loading = signal(true);
  readonly loadingMore = signal(false);
  readonly error = signal<string | null>(null);
  /** Needs review: null all rows (Later last), true only the rows set aside, false only the others. */
  readonly laterFilter = signal<boolean | null>(null);
  /** Needs review (1.33.0): only the works of one author group or one folder; null all. */
  readonly group = signal<ReviewGroup | null>(null);
  /** The Authors list (loaded when opened); null until then. */
  readonly authors = signal<MetadataReviewAuthorDto[] | null>(null);

  readonly selected = signal<ReadonlySet<string>>(new Set());
  readonly selectMode = signal(false);
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly ranks = signal<Readonly<Record<string, number>>>({});
  readonly focusIndex = signal(0);
  /** Rows hidden while their action waits in the Undo window (or is being sent). */
  readonly hidden = signal<ReadonlySet<string>>(new Set());

  readonly phone = signal(false);
  private readonly queue = new DeferredCommitQueue(this.snackBar);
  private pressTimer: ReturnType<typeof setTimeout> | null = null;
  private pressFired = false;

  readonly visible = computed(() => {
    const hidden = this.hidden();
    return this.items().filter((i) => !hidden.has(i.nodeId));
  });
  readonly focusedItem = computed(() => this.visible()[this.focusIndex()] ?? null);
  readonly focusedActions = computed<ReviewActionDef[]>(() => {
    const f = this.focusedItem();
    return f ? rowActions(this.tab(), f) : [];
  });
  readonly bulk = computed(() => bulkActions(this.tab()));
  readonly allSelected = computed(() => {
    const v = this.visible();
    return v.length > 0 && v.every((i) => this.selected().has(i.nodeId));
  });
  readonly hint = computed(() => reviewTabDef(this.tab()).hint);
  readonly recheckPending = computed(() => this.summary()?.recheckPending ?? 0);
  readonly emptyText = computed(() => {
    switch (this.tab()) {
      case 'NeedsReview': return 'Nothing waits for review.';
      case 'MissingFolders': return 'No links are left on missing folders.';
      case 'Collections': return 'No folder is marked as a collection about a series.';
      default: return `Nothing in ${reviewTabDef(this.tab()).label}.`;
    }
  });

  ngOnInit(): void {
    this.tab.set(this.initialTab() === 'Flags' ? 'NeedsReview' : this.initialTab());
    this.library.set(this.initialLibrary());
    this.breakpoints.observe(PHONE_QUERY).pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((s) => this.phone.set(s.matches));
    this.reload();
  }

  ngOnDestroy(): void {
    this.queue.flush();
    if (this.pressTimer) clearTimeout(this.pressTimer);
  }

  count(key: keyof MetadataReviewSummaryDto): number {
    return this.summary()?.[key] ?? 0;
  }

  setTab(tab: MetadataReviewTab): void {
    if (tab === 'Flags') {
      this.openFlags.emit();
      return;
    }
    if (tab === this.tab()) return;
    this.tab.set(tab);
    this.laterFilter.set(null);
    this.group.set(null);
    this.stateChange.emit({ tab, library: this.library() });
    this.reloadAfterFlush();
  }

  setLaterFilter(later: boolean | null): void {
    if (later === this.laterFilter()) return;
    this.laterFilter.set(later);
    this.reloadAfterFlush();
  }

  /** Lists only one author's or one folder's waiting works (a second pick of the same group shows every item again). */
  setGroup(group: ReviewGroup | null): void {
    const current = this.group();
    const same = !!group && !!current && current.kind === group.kind && current.key === group.key;
    if (!group && !current) return;
    this.group.set(same ? null : group);
    this.reloadAfterFlush();
  }

  loadAuthors(): void {
    this.authors.set(null);
    this.api.getReviewAuthors(this.library(), this.groupTab()).subscribe({
      next: (list) => this.authors.set(list.items),
      error: () => this.authors.set([]),
    });
  }

  /** Phone: the Authors list as a bottom sheet. */
  openAuthorsSheet(): void {
    this.api.getReviewAuthors(this.library(), this.groupTab()).subscribe({
      next: (list) => this.bottomSheet.open<ReviewAuthorsSheetComponent, MetadataReviewAuthorDto[], MetadataReviewAuthorDto>(
        ReviewAuthorsSheetComponent, { data: list.items }).afterDismissed().subscribe((a) => {
        if (a) this.setGroup({ kind: 'author', key: a.key, label: a.label });
      }),
      error: (err: ApiError) => this.snackBar.open(err?.message || 'The authors could not be loaded', 'Close', { duration: 4000 }),
    });
  }

  /** The tab the Authors list and the group filters belong to: Needs review, or (1.34.0) Unmatched. */
  private groupTab(): 'NeedsReview' | 'Unmatched' {
    return this.tab() === 'Unmatched' ? 'Unmatched' : 'NeedsReview';
  }

  /** The filters of the current request (Needs review: Later + group; Unmatched, 1.34.0: group). */
  private listFilter(): ReviewListFilter {
    if (this.tab() !== 'NeedsReview' && this.tab() !== 'Unmatched') return {};
    const filter: ReviewListFilter = {};
    const later = this.tab() === 'NeedsReview' ? this.laterFilter() : null;
    if (later !== null) filter.later = later;
    const group = this.group();
    if (group) filter[group.kind] = group.key;
    return filter;
  }

  setLibrary(library: string | null): void {
    this.library.set(library);
    this.group.set(null);
    this.stateChange.emit({ tab: this.tab(), library });
    this.reloadAfterFlush();
  }

  /**
   * Waits for a still-pending row action's deferred commit to settle before reloading
   * the list and summary - otherwise a just-confirmed (etc.) row could be missing from
   * the freshly-loaded tab until a manual refresh (owner bug report, 1.27.0). The view
   * clears and shows its spinner right away for instant feedback; the actual fetch
   * (`reload()`) waits for the flush to settle, on both success and failure, so it
   * always reflects the commit's outcome.
   */
  private reloadAfterFlush(): void {
    this.loading.set(true);
    this.error.set(null);
    this.resetView();
    this.queue.flush().subscribe(() => this.reload());
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.resetView();
    this.loadSummary();
    this.api.getReview(this.tab(), this.library(), null, 50, this.listFilter()).subscribe({
      next: (page) => {
        this.items.set(page.items);
        this.total.set(page.total);
        this.cursor.set(page.nextCursor ?? null);
        this.hasMore.set(!!page.hasMore || !!page.nextCursor);
        this.loading.set(false);
      },
      error: (err: ApiError & { status?: number }) => {
        this.items.set([]);
        this.loading.set(false);
        this.error.set(err?.status === 501
          ? 'The review list is not available on this server yet.'
          : err?.message || 'The review list could not be loaded.');
      },
    });
  }

  loadMore(): void {
    const cursor = this.cursor();
    if (!cursor) return;
    this.loadingMore.set(true);
    this.api.getReview(this.tab(), this.library(), cursor, 50, this.listFilter()).subscribe({
      next: (page) => {
        const known = new Set(this.items().map((i) => i.nodeId));
        this.items.update((prev) => [...prev, ...page.items.filter((i) => !known.has(i.nodeId))]);
        this.cursor.set(page.nextCursor ?? null);
        this.hasMore.set(!!page.hasMore || !!page.nextCursor);
        this.loadingMore.set(false);
      },
      error: (err: ApiError) => {
        this.loadingMore.set(false);
        this.snackBar.open(err?.message || 'Could not load more', 'Close', { duration: 4000 });
      },
    });
  }

  rankOf(item: MetadataReviewItemDto): number {
    // 1.34.0: a row that looks like a collection preselects the suggested series.
    return this.ranks()[item.nodeId] ?? item.collection?.rank ?? item.candidates?.[0]?.rank ?? 1;
  }

  choose(nodeId: string, rank: number): void {
    this.ranks.update((r) => ({ ...r, [nodeId]: rank }));
  }

  toggle(nodeId: string): void {
    this.selected.update((s) => {
      const next = new Set(s);
      if (next.has(nodeId)) next.delete(nodeId); else next.add(nodeId);
      return next;
    });
    if (this.selected().size > 0) this.selectMode.set(true);
    else if (this.phone()) this.selectMode.set(false);
  }

  selectAll(on: boolean): void {
    this.selected.set(on ? new Set(this.visible().map((i) => i.nodeId)) : new Set());
    this.selectMode.set(on);
  }

  clearSelection(): void {
    this.selected.set(new Set());
    this.selectMode.set(false);
  }

  toggleExpanded(nodeId: string): void {
    this.expanded.update((s) => {
      const next = new Set(s);
      if (next.has(nodeId)) next.delete(nodeId); else next.add(nodeId);
      return next;
    });
  }

  /** A tap on a row: select while selecting on phone, else focus it. */
  onRowTap(index: number, item: MetadataReviewItemDto): void {
    if (this.pressFired) {
      this.pressFired = false;
      return;
    }
    if (this.phone() && this.selectMode()) {
      this.toggle(item.nodeId);
      return;
    }
    this.focusIndex.set(index);
  }

  // --- Long-press (touch): start selecting, like browse ---

  pressStart(event: PointerEvent, item: MetadataReviewItemDto): void {
    // A new touch: a long-press whose release produced no click must not eat this tap.
    this.pressFired = false;
    if (event.pointerType !== 'touch' || this.selectMode()) return;
    this.pressEnd();
    this.pressTimer = setTimeout(() => {
      this.pressTimer = null;
      this.pressFired = true;
      this.selectMode.set(true);
      if (!this.selected().has(item.nodeId)) this.toggle(item.nodeId);
    }, 550);
  }

  pressEnd(): void {
    if (this.pressTimer) {
      clearTimeout(this.pressTimer);
      this.pressTimer = null;
    }
  }

  // --- Keyboard triage ---

  onKey(event: KeyboardEvent): void {
    if (event.ctrlKey || event.metaKey || event.altKey || this.dialog.openDialogs.length > 0) return;
    const target = event.target as HTMLElement | null;
    if (target && (target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)
      || target.closest('mat-select, .mat-mdc-select-panel, [role="listbox"]'))) return;
    const key = event.key.toLowerCase();
    const rows = this.visible();
    if (key === 'j' || key === 'k') {
      if (rows.length === 0) return;
      const next = Math.max(0, Math.min(rows.length - 1, this.focusIndex() + (key === 'j' ? 1 : -1)));
      this.focusIndex.set(next);
      this.scrollToFocused();
      event.preventDefault();
      return;
    }
    const item = this.focusedItem();
    if (!item) return;
    if (key === 'x') {
      this.toggle(item.nodeId);
      event.preventDefault();
      return;
    }
    if (key === 'e') {
      if ((item.candidates ?? []).length) this.toggleExpanded(item.nodeId);
      event.preventDefault();
      return;
    }
    if (key === 'g' && (this.tab() === 'NeedsReview' || this.tab() === 'Unmatched')) {
      const g = this.group();
      if (g?.kind === 'author') this.setGroup(null);
      else if (item.sameAuthor) this.setGroup({ kind: 'author', key: item.sameAuthor.key, label: item.sameAuthor.label });
      event.preventDefault();
      return;
    }
    const action = rowActions(this.tab(), item).find((a) => a.key === key)?.action;
    const ranked = action === 'accept' || action === 'acceptCollection';
    if (!action || (ranked && !(item.candidates ?? []).length)) return;
    event.preventDefault();
    this.onRowAction({ action, item, rank: ranked ? this.rankOf(item) : undefined });
  }

  // --- Actions ---

  onRowAction(e: ReviewRowActionEvent): void {
    const { item } = e;
    switch (e.action) {
      case 'accept': {
        const rank = e.rank ?? this.rankOf(item);
        const title = item.candidates?.find((c) => c.rank === rank)?.title ?? 'the candidate';
        this.defer([item], `Accepted "${title}" for ${item.displayName}`, () => none(this.api.acceptCandidate(item.nodeId, rank)));
        return;
      }
      case 'acceptCollection': {
        const rank = e.rank ?? this.rankOf(item);
        const title = item.candidates?.find((c) => c.rank === rank)?.title ?? item.collection?.title;
        this.defer([item], `${collectionLabel(title)}: ${item.displayName}`, () => none(this.api.acceptCollection(item.nodeId, rank)));
        return;
      }
      case 'clearCollection':
        this.defer([item], `Collection cleared on ${item.displayName}`, () => none(this.api.clearCollection(item.nodeId)));
        return;
      case 'changeCollection':
        this.identify(item, 'collection');
        return;
      case 'identify':
        this.identify(item);
        return;
      case 'later':
      case 'notLater':
        this.setLater([item], e.action === 'later');
        return;
      case 'clearDontMatch':
        this.defer([item], `Don't match cleared on ${item.displayName}`, () => none(this.api.clearDontMatch(item.nodeId)));
        return;
      case 'reattach':
        void this.reattach(item);
        return;
      case 'deleteMissing':
        this.defer([item], `Deleted what was left on ${item.displayName}`, () => none(this.api.deleteMissing(item.nodeId)));
        return;
      default: {
        const bulk = ROW_BULK[e.action];
        if (!bulk) return;
        this.defer([item], `${BULK_DONE[bulk]} ${item.displayName}`, () => this.bulkCall(bulk, [item]));
      }
    }
  }

  runBulk(action: MetadataReviewBulkAction): void {
    const chosen = this.visible().filter((i) => this.selected().has(i.nodeId)).slice(0, 200);
    if (chosen.length === 0) return;
    this.clearSelection();
    if (action === 'Later' || action === 'ClearLater') {
      this.setLater(chosen, action === 'Later');
      return;
    }
    this.defer(chosen, `${BULK_DONE[action]} ${plural(chosen.length, 'item')}`, () => this.bulkCall(action, chosen));
  }

  /** `review/bulk`; emits the ids whose result is not ok (they come back with the error code). */
  private bulkCall(action: MetadataReviewBulkAction, rows: MetadataReviewItemDto[]): Observable<string[]> {
    return this.api.reviewBulk(action, rows.map((r) => r.nodeId)).pipe(map((result: MetadataReviewBulkResultDto) => {
      const failed = result.results.filter((r) => r.code !== 'ok');
      if (failed.length > 0) {
        this.snackBar.open(`${plural(failed.length, 'item')} could not be changed (${failed[0].code})`, 'Close', { duration: 5000 });
      }
      return failed.map((f) => f.nodeId);
    }));
  }

  /**
   * Hides the rows now and sends `commit` when the Undo window closes; Undo shows them
   * again without sending anything, a failure shows them again with the error.
   */
  private defer(rows: MetadataReviewItemDto[], label: string, commit: () => Observable<string[]>): void {
    const ids = rows.map((r) => r.nodeId);
    this.hide(ids);
    this.queue.run<string[]>({
      label,
      commit,
      undone: () => this.unhide(ids),
      committed: (failedIds) => {
        const failed = new Set(failedIds);
        const done = ids.filter((id) => !failed.has(id));
        this.unhide(failedIds);
        this.forget(done);
        done.forEach((id) => this.metadataState.refresh(id));
        this.loadSummary();
      },
      failed: (err) => {
        this.unhide(ids);
        this.snackBar.open(`Failed: ${(err as ApiError)?.message ?? 'error'}`, 'Close', { duration: 5000 });
      },
    });
  }

  /**
   * "Later" / "Not later" (1.33.0): sent at once (it decides nothing; Undo sends the opposite). A row set aside moves to the
   * end of the list, as the server lists it: appended when the whole list is loaded, else dropped until paging reaches it.
   */
  private setLater(rows: MetadataReviewItemDto[], on: boolean): void {
    const call: Observable<string[]> = rows.length === 1
      ? none(this.api.setReviewLater(rows[0].nodeId, on))
      : this.bulkCall(on ? 'Later' : 'ClearLater', rows);
    call.subscribe({
      next: (failedIds) => {
        const failed = new Set(failedIds);
        const done = rows.filter((r) => !failed.has(r.nodeId));
        if (done.length === 0) return;
        const moved = this.applyLater(done, on);
        this.loadSummary();
        const what = done.length === 1 ? done[0].displayName : plural(done.length, 'item');
        this.snackBar.open(`${on ? BULK_DONE.Later : BULK_DONE.ClearLater} ${what}`, 'Undo', { duration: 5000 })
          .onAction().subscribe(() => this.setLater(moved, !on));
      },
      error: (err: ApiError) => this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 5000 }),
    });
  }

  /** Updates the loaded rows after a Later change; returns the rows as they are now. */
  private applyLater(rows: MetadataReviewItemDto[], on: boolean): MetadataReviewItemDto[] {
    const laterAt = on ? new Date().toISOString() : null;
    const changed = rows.map((r) => ({ ...r, laterAt }));
    const ids = new Set(rows.map((r) => r.nodeId));
    const filter = this.laterFilter();
    if (filter !== null && filter !== on) {
      this.forget([...ids]); // no longer part of the filtered list
    } else if (on) {
      const rest = this.items().filter((i) => !ids.has(i.nodeId));
      this.items.set(this.hasMore() ? rest : [...rest, ...changed]);
    } else {
      const byId = new Map(changed.map((r) => [r.nodeId, r]));
      this.items.update((list) => list.map((i) => byId.get(i.nodeId) ?? i));
    }
    this.clampFocus();
    return changed;
  }

  private identify(item: MetadataReviewItemDto, mode: IdentifyMode = 'link'): void {
    this.queue.flush();
    void this.identifyDialog.open(item.nodeId, mode).then((linked) => {
      if (!linked) return;
      this.hide([item.nodeId]);
      this.forget([item.nodeId]);
      this.loadSummary();
    });
  }

  private async reattach(item: MetadataReviewItemDto): Promise<void> {
    this.queue.flush();
    const { ReattachDialogComponent } = await import('./reattach-dialog.component');
    const ref = this.dialog.open<unknown, ReattachDialogData, ReattachDialogResult>(ReattachDialogComponent, {
      data: { libraryId: item.libraryId, libraryName: item.libraryName, displayName: item.displayName },
      width: this.phone() ? '100vw' : '520px',
      maxWidth: '100vw',
    });
    ref.afterClosed().subscribe((result) => {
      if (!result) return;
      this.api.reattachMissing(item.nodeId, result.targetNodeId).subscribe({
        next: () => {
          this.hide([item.nodeId]);
          this.forget([item.nodeId]);
          this.metadataState.refresh(result.targetNodeId);
          this.loadSummary();
          this.snackBar.open(`Re-attached to ${result.targetName}`, 'Close', { duration: 3000 });
        },
        error: (err: ApiError) => this.snackBar.open(`Re-attach failed: ${err?.message ?? 'error'}`, 'Close', { duration: 5000 }),
      });
    });
  }

  private hide(ids: string[]): void {
    this.hidden.update((s) => {
      const next = new Set(s);
      ids.forEach((id) => next.add(id));
      return next;
    });
    this.selected.update((s) => {
      const next = new Set(s);
      ids.forEach((id) => next.delete(id));
      return next;
    });
    this.adjustCount(-ids.length);
    this.clampFocus();
  }

  private unhide(ids: string[]): void {
    const present = ids.filter((id) => this.hidden().has(id));
    if (present.length === 0) return;
    this.hidden.update((s) => {
      const next = new Set(s);
      present.forEach((id) => next.delete(id));
      return next;
    });
    this.adjustCount(present.length);
  }

  /** The action was sent: drop the rows for good. */
  private forget(ids: string[]): void {
    const gone = new Set(ids);
    this.items.update((list) => list.filter((i) => !gone.has(i.nodeId)));
    this.total.update((t) => Math.max(0, t - ids.length));
    this.hidden.update((s) => {
      const next = new Set(s);
      ids.forEach((id) => next.delete(id));
      return next;
    });
    this.clampFocus();
  }

  private adjustCount(delta: number): void {
    const def = reviewTabDef(this.tab());
    this.summary.update((s) => (s ? { ...s, [def.count]: Math.max(0, s[def.count] + delta) } : s));
  }

  private clampFocus(): void {
    const n = this.visible().length;
    if (this.focusIndex() > n - 1) this.focusIndex.set(Math.max(0, n - 1));
  }

  private loadSummary(): void {
    this.api.getReviewSummary(this.library()).subscribe({
      next: (s) => this.summary.set(s),
      error: () => this.summary.set(null),
    });
    this.reviewState.refresh();
  }

  private resetView(): void {
    this.items.set([]);
    this.hidden.set(new Set());
    this.expanded.set(new Set());
    this.ranks.set({});
    this.focusIndex.set(0);
    this.clearSelection();
  }

  private scrollToFocused(): void {
    const node = this.focusedItem()?.nodeId;
    if (!node) return;
    queueMicrotask(() => {
      const el = this.host.nativeElement.querySelector(`[data-node="${node}"]`);
      el?.scrollIntoView?.({ block: 'nearest' });
    });
  }
}

/** A single-node call that has no per-node failures. */
function none(call: Observable<unknown>): Observable<string[]> {
  return call.pipe(map(() => []));
}
