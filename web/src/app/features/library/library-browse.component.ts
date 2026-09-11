import { Component, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar } from '@angular/material/snack-bar';
import { forkJoin } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { CatalogNodeDto, PageResponse, ReaderMode, LibraryViewMode, LibraryGridDensity, LibrarySortOrder, LibrarySortDirection, LibraryViewPreferencesDto, JumpIndexBucketDto } from '../../core/api/api-types';

/**
 * Library browse component. Shows the actual folder/archive tree with keyset
 * pagination, breadcrumbs, and item state indicators.
 *
 * Selection mode (1.2.0) is the single home for card actions (owner review
 * 2026-09-09): tapping cards selects them (mouse or touch — no hover-only
 * affordances), and a STICKY top bar carries every bulk action applied to the
 * selection — mark read / unread for everyone, plus set/clear reading direction
 * for admins (folders only). There is no competing per-card menu.
 *
 * Range selection (1.7.0, file-browser semantics): Shift-click fills the
 * contiguous range from the ANCHOR (the last individually-selected card) to
 * the clicked card, in the currently displayed sort order; Ctrl/Cmd-click and
 * a plain click both toggle one card and move the anchor. Touch has no shift
 * key, so long-press opens "Select to here" instead (entering select mode
 * first if needed) - tap = one, long-press = range fill. "Select all" /
 * "Select all unread" / "Select all read" act over the currently-listed nodes
 * for whole-folder selection.
 */
@Component({
  selector: 'app-library-browse',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatTooltipModule,
    MatDividerModule,
    CoverImageDirective,
    FolderRollupBadgeComponent,
  ],
  template: `
    <!-- Sticky top bar: breadcrumbs + Select normally; the merged action set while
         selecting. Sticky so the controls stay reachable when scrolling a long
         folder (touch-friendly — requirement 3). -->
    <div class="browse-bar" [class.selecting]="selectMode()">
      @if (!selectMode()) {
        <div class="breadcrumbs">
          <!-- Library root is always a clickable crumb. -->
          <a routerLink="/libraries/{{ libraryId() }}/browse">{{ libraryName() || 'Library' }}</a>
          <!-- Ancestors of the current folder (clickable). -->
          @for (crumb of breadcrumbs(); track crumb.id) {
            <span class="sep"> / </span>
            <a routerLink="/libraries/{{ libraryId() }}/browse/{{ crumb.id }}">{{ crumb.displayName }}</a>
          }
          <!-- The current folder itself: plain, non-clickable text (File Explorer
               behavior - you are already in it). Rendered whenever we are inside a
               folder, even at the first level where there are no ancestor crumbs. -->
          @if (currentFolderName()) {
            <span class="sep"> / </span>
            <span class="current" aria-current="page">{{ currentFolderName() }}</span>
          }
        </div>
        <!-- Card size slider (1.6.0): only meaningful for the Card view. Dragging
             resizes the grid live (input); releasing persists the preference
             (change). Subsumes the old comfortable/compact density toggle. -->
        @if (viewMode() === 'card') {
          <div class="size-control" matTooltip="Card size">
            <mat-icon class="size-icon">photo_size_select_small</mat-icon>
            <input type="range" class="size-slider" aria-label="Card size"
                   [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep"
                   [value]="cardSize()"
                   (input)="onCardSizeInput($event)"
                   (change)="onCardSizeChange($event)">
            <mat-icon class="size-icon">photo_size_select_large</mat-icon>
          </div>
        }
        <button mat-stroked-button class="view-toggle" [matMenuTriggerFor]="viewMenu"
                matTooltip="Change how the library is displayed" aria-label="View options">
          <mat-icon>{{ viewIcon() }}</mat-icon> View
        </button>
        <mat-menu #viewMenu="matMenu">
          @for (opt of viewOptions; track opt.value) {
            <button mat-menu-item (click)="setViewMode(opt.value)">
              <mat-icon>{{ viewMode() === opt.value ? 'check' : opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <mat-divider></mat-divider>
          <span class="menu-caption">Sort by</span>
          @for (opt of sortOptions; track opt.value) {
            <button mat-menu-item (click)="setSort(opt.value)">
              <mat-icon>{{ sort() === opt.value ? 'check' : opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <span class="menu-caption">Order</span>
          @for (opt of sortDirectionOptions; track opt.value) {
            <button mat-menu-item (click)="setSortDirection(opt.value)">
              <mat-icon>{{ sortDirection() === opt.value ? 'check' : opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
        </mat-menu>
        <button mat-stroked-button class="select-toggle" (click)="toggleSelectMode()">
          <mat-icon>checklist</mat-icon> Select
        </button>
      } @else {
        <span class="count">{{ selected().size }} selected</span>
        <div class="actions">
          <button mat-button [matMenuTriggerFor]="selectMenu" [disabled]="busy() || nodes().length === 0"
                  matTooltip="Select the whole folder">
            <mat-icon>playlist_add_check</mat-icon><span class="lbl">Select</span>
          </button>
          <mat-menu #selectMenu="matMenu">
            <button mat-menu-item (click)="selectAll()">
              <mat-icon>select_all</mat-icon> Select all
            </button>
            <button mat-menu-item (click)="selectAllUnread()">
              <mat-icon>radio_button_unchecked</mat-icon> Select all unread
            </button>
            <button mat-menu-item (click)="selectAllRead()">
              <mat-icon>check_circle</mat-icon> Select all read
            </button>
          </mat-menu>
          <button mat-button (click)="bulkMarkRead(true)" [disabled]="busy() || selected().size === 0">
            <mat-icon>check_circle</mat-icon><span class="lbl">Mark read</span>
          </button>
          <button mat-button (click)="bulkMarkRead(false)" [disabled]="busy() || selected().size === 0">
            <mat-icon>remove_done</mat-icon><span class="lbl">Mark unread</span>
          </button>
          @if (auth.isAdmin()) {
            <button mat-button [matMenuTriggerFor]="dirMenu"
                    [disabled]="busy() || selectedFolderCount() === 0"
                    matTooltip="Set reading direction for selected folders">
              <mat-icon>swap_horiz</mat-icon><span class="lbl">Direction</span>
            </button>
            <mat-menu #dirMenu="matMenu">
              @for (opt of directionOptions; track opt.label) {
                <button mat-menu-item (click)="bulkSetDirection(opt.value)">{{ opt.label }}</button>
              }
            </mat-menu>
          }
        </div>
        <button mat-stroked-button class="done" (click)="toggleSelectMode()">
          <mat-icon>close</mat-icon> Done
        </button>
      }
    </div>

    @if (jumpBuckets().length > 0) {
      <nav class="jump-rail" aria-label="Jump to letter">
        @for (bucket of jumpBuckets(); track bucket.label) {
          <button class="jump-chip" type="button"
                  (click)="jumpToBucket(bucket)"
                  [class.active]="activeJump() === bucket.label"
                  [matTooltip]="bucket.label + ' (' + bucket.count + ')'">
            {{ bucket.label }}
          </button>
        }
      </nav>
    }

    <div class="nodes" [class.card]="viewMode() === 'card'"
         [class.list]="viewMode() === 'list'"
         [style.--card-size]="cardSize() + 'px'">
      @for (node of nodes(); track node.id) {
        <div class="node-wrap" [class.selected]="isSelected(node)">
          <a class="node-card" [routerLink]="selectMode() ? null : getNodeLink(node)"
             (click)="onCardClick($event, node)"
             (pointerdown)="onCardPointerDown($event, node)"
             (pointerup)="onCardPointerUp()"
             (pointercancel)="onCardPointerCancel()"
             (pointerleave)="onCardPointerCancel()">
            <div class="cover">
              @if (node.coverUrl) {
                <img appCover [src]="node.coverUrl" alt="" loading="lazy">
              }
              <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>

              @if (node.isRead) {
                <span class="badge read" matTooltip="Read">✓ Read</span>
              } @else if (node.readingState === 'InProgress') {
                <span class="badge reading">Reading</span>
              }
              <app-folder-rollup-badge [rollup]="node.readRollup" />

              <!-- Admin folders show their current direction override as a small,
                   non-interactive chip (the control now lives in the action bar). -->
              @if (auth.isAdmin() && node.kind === 'Folder' && node.readerDefault) {
                <span class="badge dir" matTooltip="Reading direction override">
                  {{ directionShort(node.readerDefault) }}
                </span>
              }

              @if (selectMode()) {
                <span class="check" [class.on]="isSelected(node)">
                  <mat-icon>{{ isSelected(node) ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
                </span>
              }

              <!-- Touch range fill (long-press "Select to here"): shown only on the
                   long-pressed card, over its cover so it stays reachable without
                   scrolling away from the target item. -->
              @if (rangePromptNode()?.id === node.id) {
                <div class="range-prompt">
                  <button type="button" (click)="onSelectToHereClick($event)">Select to here</button>
                  <button type="button" class="cancel" (click)="onDismissRangePromptClick($event)">Cancel</button>
                </div>
              }
            </div>
            <div class="node-text">
              <div class="node-title" [title]="node.displayName">{{ node.displayName }}</div>
              <div class="node-sub">
                @if (node.pageCount !== null) { {{ node.pageCount }} pages }
                @else if (node.kind === 'Folder' && node.childArchiveCount !== null) { {{ node.childArchiveCount }} items }
                @if (node.availability !== 'Available') { · {{ node.availability }} }
              </div>
            </div>
          </a>
        </div>
      } @empty {
        <p class="empty">This folder is empty.</p>
      }
    </div>

    @if (hasMore()) {
      <div class="load-more">
        <button mat-raised-button (click)="loadMore()">Load More</button>
      </div>
    }
  `,
  styles: [`
    .menu-caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    .browse-bar {
      position: sticky; top: 0; z-index: 20;
      display: flex; align-items: center; gap: 12px;
      margin-bottom: 16px; padding: 10px 12px;
      background: #14141c; border: 1px solid rgba(255,255,255,0.08);
      border-radius: 10px;
    }
    .browse-bar.selecting { background: #1c1730; border-color: rgba(124,77,255,0.5); }
    .breadcrumbs {
      flex: 1 1 auto; min-width: 0;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .breadcrumbs a { text-decoration: none; color: #b39dff; }
    /* Current folder: plain text, not a link. Slightly brighter than the muted
       ancestors' link color to read as "you are here", but no pointer/underline. */
    .breadcrumbs .current { color: #e6e6ee; font-weight: 500; }
    .breadcrumbs.muted { color: #999; }
    .count { font-weight: 600; }
    .actions { flex: 1 1 auto; display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    .actions mat-icon { margin-right: 4px; }
    .select-toggle mat-icon, .done mat-icon { margin-right: 4px; }
    /* Card size slider (1.6.0). Sits inline in the browse bar between the
       breadcrumbs and the View menu; the small/large icons frame the range. */
    .size-control { display: flex; align-items: center; gap: 6px; flex: 0 0 auto; }
    .size-control .size-icon { font-size: 18px; width: 18px; height: 18px; color: #8a8a99; }
    .size-slider {
      width: 120px; max-width: 34vw; accent-color: #7c4dff; cursor: pointer;
      background: transparent;
    }
    @media (max-width: 560px) { .size-slider { width: 80px; } }
    /* View modes. Card (1.6.0) is a single cover grid whose card size is a
       continuous slider — the min column width comes from the --card-size custom
       property fed by the component, replacing the former Grid/Poster modes and the
       comfortable/compact density. The gap scales gently with the card size. List
       is a compact row layout with a small thumbnail (unchanged). */
    .nodes.card {
      display: grid;
      gap: clamp(10px, calc(var(--card-size, 150px) * 0.09), 20px);
      grid-template-columns: repeat(auto-fill, minmax(var(--card-size, 150px), 1fr));
    }
    .nodes.list { display: flex; flex-direction: column; gap: 8px; }
    .nodes.list .node-wrap { width: 100%; }
    .nodes.list .node-card {
      display: flex; align-items: center; gap: 12px;
      padding: 6px; border-radius: 8px; background: rgba(255,255,255,0.03);
    }
    .nodes.list .cover { width: 46px; height: 66px; flex: 0 0 auto; border-radius: 4px; }
    .nodes.list .cover-fallback { font-size: 24px; width: 24px; height: 24px; }
    .nodes.list .badge { font-size: 9px; padding: 1px 4px; top: 2px; right: 2px; }
    .nodes.list .badge.dir { bottom: 2px; top: auto; }
    .nodes.list .node-text { flex: 1 1 auto; min-width: 0; }
    .nodes.list .node-title { margin-top: 0; white-space: nowrap; }
    .node-wrap { position: relative; border-radius: 8px; }
    .node-wrap.selected { outline: 2px solid #7c4dff; outline-offset: 3px; }
    .node-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative;
      aspect-ratio: 2 / 3;
      border-radius: 8px;
      overflow: hidden;
      background: rgba(255,255,255,0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img {
      width: 100%; height: 100%; object-fit: cover;
      position: relative; z-index: 1;
    }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: #777; position: absolute; z-index: 0; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgba(124, 77, 255, 0.9); color: #fff;
    }
    .badge.read { background: rgba(76, 175, 80, 0.95); }
    .badge.dir { top: auto; bottom: 6px; right: 6px; background: rgba(0,0,0,0.65); }
    .check {
      position: absolute; top: 6px; left: 6px; z-index: 3;
      color: #fff; line-height: 0;
      border-radius: 50%; background: rgba(0, 0, 0, 0.45);
    }
    .check.on { color: #7c4dff; background: #fff; }
    .check mat-icon { font-size: 24px; width: 24px; height: 24px; }
    /* Touch range fill (long-press "Select to here"). A small floating action over
       the long-pressed card's cover so it works without a positioned menu overlay. */
    .range-prompt {
      position: absolute; inset: 0; z-index: 4;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 6px; padding: 8px; background: rgba(10, 8, 20, 0.85); border-radius: 8px;
    }
    .range-prompt button {
      border: none; border-radius: 6px; padding: 6px 10px; font-size: 12px; font-weight: 600;
      cursor: pointer; background: #7c4dff; color: #fff; width: 100%;
    }
    .range-prompt button.cancel { background: rgba(255,255,255,0.12); }
    .node-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .node-sub { font-size: 12px; color: #999; }
    .empty { color: #999; padding: 32px; text-align: center; }
    .load-more { text-align: center; margin-top: 16px; }
    /* A–Z/script jump rail (1.4.0 Lane E). Only shown at library root level. */
    .jump-rail {
      display: flex; flex-wrap: wrap; gap: 4px;
      margin-bottom: 12px; padding: 6px 8px;
      background: rgba(255,255,255,0.03); border-radius: 8px;
    }
    .jump-chip {
      min-width: 28px; padding: 4px 8px; border: none; cursor: pointer;
      background: transparent; color: #b39dff; border-radius: 6px;
      font-size: 12px; font-weight: 600; line-height: 1;
      transition: background 0.1s;
    }
    .jump-chip:hover { background: rgba(124,77,255,0.18); }
    .jump-chip.active { background: #7c4dff; color: #fff; }

    /* Touch / small screens: keep the action bar compact by dropping button labels
       (icons remain, so the controls stay usable) — requirement 1 (dual input). */
    @media (max-width: 560px) {
      .actions .lbl { display: none; }
      .actions mat-icon { margin-right: 0; }
    }
  `],
})
export class LibraryBrowseComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  // Per-folder direction override options (1.2.0). null = inherit (clear).
  readonly directionOptions: { value: ReaderMode | null; label: string }[] = [
    { value: null, label: 'Inherit (clear)' },
    { value: 'PagedLtr', label: 'Left-to-right' },
    { value: 'PagedRtl', label: 'Right-to-left' },
    { value: 'VerticalWebtoon', label: 'Vertical' },
  ];

  readonly libraryId = signal('');
  readonly libraryName = signal('');
  readonly parentId = signal<string | null>(null);
  readonly nodes = signal<CatalogNodeDto[]>([]);
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  /**
   * Name of the folder currently being viewed (Task B, 1.5.0). Rendered as the
   * last, non-clickable breadcrumb segment. The breadcrumbs endpoint only returns
   * the current folder's *ancestors* (never the node itself), so this is sourced
   * from the existing `GET /nodes/{id}` node lookup - a frontend-only addition,
   * no contract change. Empty at the library root (there is no current folder).
   */
  readonly currentFolderName = signal('');
  readonly hasMore = signal(false);
  private cursor: string | null = null;

  // Jump-index rail (1.4.0 Lane E). Only loaded at the library root (no parentId);
  // subfolders don't have a per-folder jump index. The rail is a name-sort
  // navigation aid, so it is hidden when the sort is not "name".
  readonly jumpBuckets = signal<JumpIndexBucketDto[]>([]);
  readonly activeJump = signal<string | null>(null);

  // Selection mode (1.2.0 read-marks + merged card actions).
  readonly selectMode = signal(false);
  readonly selected = signal<Set<string>>(new Set());
  readonly busy = signal(false);

  /**
   * Range selection (1.7.0). The ANCHOR is the index (within the currently
   * displayed `nodes()` order) of the last INDIVIDUALLY selected/deselected
   * item — a plain click, a ctrl/cmd-click, or the item long-press entered
   * select mode on. Shift-click and touch "Select to here" both fill the
   * inclusive range between the anchor and the target from this index, so the
   * range always follows the active sort/direction (requirement: range is
   * over the visible, currently-displayed order).
   */
  readonly anchorIndex = signal<number | null>(null);

  /**
   * The card currently showing the long-press "Select to here" action (touch
   * range-fill). Null when no prompt is open. Only one card shows the prompt
   * at a time.
   */
  readonly rangePromptNode = signal<CatalogNodeDto | null>(null);

  private longPressTimer: ReturnType<typeof setTimeout> | null = null;
  private longPressTriggered = false;
  private readonly longPressMs = 550;

  // Per-user library view mode. Tolerant: unknown/legacy persisted values fall back.
  // 1.6.0: the former Grid and Poster modes are merged into a single Card view whose
  // size is the continuous slider below; List stays a separate mode.
  readonly viewMode = signal<LibraryViewMode>('card');
  // Legacy density (1.2.0), subsumed by the card-size slider. Kept only so the stored
  // preference round-trips unchanged; there is no longer any density UI.
  readonly density = signal<LibraryGridDensity>('comfortable');
  readonly viewOptions: { value: LibraryViewMode; label: string; icon: string }[] = [
    { value: 'card', label: 'Card', icon: 'grid_view' },
    { value: 'list', label: 'List', icon: 'view_list' },
  ];
  readonly viewIcon = computed(() =>
    this.viewOptions.find((o) => o.value === this.viewMode())?.icon ?? 'grid_view');

  // Card size (1.6.0): the grid's min column width in px. The slider range spans the
  // legacy grid/poster x density sizes (112-210px today) with a little headroom on
  // each end. Persisted as a stringified px value in LibraryViewPreferencesDto.cardSize.
  readonly cardSizeMin = 110;
  readonly cardSizeMax = 260;
  readonly cardSizeStep = 10;
  readonly defaultCardSize = 150;
  readonly cardSize = signal<number>(this.defaultCardSize);

  // Per-user browse sort (post-1.2.0). Folders stay first in every mode; the sort
  // orders within kind. Omitted on the request → the server uses the stored pref;
  // we pass it explicitly so a change reorders immediately without a persist race.
  readonly sort = signal<LibrarySortOrder>('name');
  readonly sortOptions: { value: LibrarySortOrder; label: string; icon: string }[] = [
    { value: 'name', label: 'Name', icon: 'sort_by_alpha' },
    { value: 'recentlyAdded', label: 'Recently added', icon: 'schedule' },
    { value: 'recentlyRead', label: 'Recently read', icon: 'history' },
  ];

  // Ascending/descending toggle for the active sort (1.5.0). Named "sortDirection"
  // (not "direction") to stay distinct from the unrelated per-folder reading
  // direction (LTR/RTL/Vertical) already on this component. Default mirrors the
  // server's per-sort default (name -> asc; everything else -> desc) until the
  // stored preference loads.
  readonly sortDirection = signal<LibrarySortDirection>('asc');
  readonly sortDirectionOptions: { value: LibrarySortDirection; label: string; icon: string }[] = [
    { value: 'asc', label: 'Ascending', icon: 'arrow_upward' },
    { value: 'desc', label: 'Descending', icon: 'arrow_downward' },
  ];

  /** How many currently-selected nodes are folders (gates the Direction action). */
  readonly selectedFolderCount = computed(() => {
    const ids = this.selected();
    return this.nodes().filter((n) => ids.has(n.id) && n.kind === 'Folder').length;
  });

  ngOnInit(): void {
    // Load the per-user view preference FIRST (tolerate unknown values), then start
    // routing. Sequencing matters: the sort must be known before the first browse so
    // it issues a single, correctly-ordered request. (Loading nodes from both the
    // prefs handler and the route subscription would double-append — duplicate rows.)
    this.api.getLibraryPreferences().subscribe({
      next: (p) => {
        this.applyStoredView(p);
        if (p.sort === 'recentlyAdded' || p.sort === 'recentlyRead') this.sort.set(p.sort);
        // Direction is optional/tolerant like the other fields: an unset or
        // unrecognized value falls back to the sort-specific default (matches
        // CatalogController.ParseDirection) so pre-1.5.0 stored preferences
        // keep their existing ordering.
        this.sortDirection.set(
          p.direction === 'asc' || p.direction === 'desc' ? p.direction : this.defaultDirectionFor(this.sort()));
        this.subscribeToRoute();
      },
      error: () => this.subscribeToRoute(), // keep defaults, still load
    });
  }

  /** Sort-specific default direction, matching the server's fallback (1.5.0). */
  private defaultDirectionFor(sort: LibrarySortOrder): LibrarySortDirection {
    return sort === 'name' ? 'asc' : 'desc';
  }

  /**
   * Map a stored preference blob onto the Card/List view + card size (1.6.0),
   * tolerating pre-1.6.0 values. A stored 'list' stays List; everything else -
   * 'card', the legacy 'grid'/'poster', and any unknown value - becomes Card. The
   * card size uses the stored cardSize when present and valid, otherwise it is
   * DERIVED from the legacy viewMode + density so existing users keep the effective
   * card size they had before Grid/Poster were merged.
   */
  private applyStoredView(p: LibraryViewPreferencesDto): void {
    this.viewMode.set(p.viewMode === 'list' ? 'list' : 'card');
    this.density.set(p.density === 'compact' ? 'compact' : 'comfortable');
    this.cardSize.set(this.resolveCardSize(p));
  }

  /**
   * Resolve the initial card size: the stored numeric cardSize if usable, else the
   * px width the legacy viewMode+density combination used (poster 210/168, grid
   * 150/112), else the default. Keeps pre-1.6.0 stored prefs visually unchanged.
   */
  private resolveCardSize(p: LibraryViewPreferencesDto): number {
    const stored = Number(p.cardSize);
    if (p.cardSize != null && p.cardSize !== '' && Number.isFinite(stored)) {
      return this.clampCardSize(stored);
    }
    const compact = p.density === 'compact';
    if (p.viewMode === 'poster') return compact ? 168 : 210;
    if (p.viewMode === 'grid') return compact ? 112 : 150;
    return this.defaultCardSize;
  }

  /** Subscribe to the route params and load each folder as it is navigated. */
  private subscribeToRoute(): void {
    this.route.paramMap.subscribe((params) => {
      const libId = params.get('libraryId')!;
      const parentId = params.get('nodeId');
      this.libraryId.set(libId);
      this.parentId.set(parentId);
      this.cursor = null;
      this.nodes.set([]);
      this.activeJump.set(null);
      this.clearSelection();
      this.loadLibraryName(libId);
      this.loadNodes();
      // The jump rail is a library-root navigation aid (1.4.0 Lane E). It is
      // only meaningful for the name sort in ascending order — its bucket
      // cursors assume A→Z order, and other sorts ignore the cursor entirely.
      if (!parentId && this.sort() === 'name' && this.sortDirection() === 'asc') this.loadJumpIndex(libId);
      else this.jumpBuckets.set([]);
      if (parentId) {
        this.loadBreadcrumbs(parentId);
        this.loadCurrentFolder(parentId);
      } else {
        this.breadcrumbs.set([]);
        this.currentFolderName.set('');
      }
    });
  }

  /** Load the per-library A–Z/script jump index (1.4.0 Lane E). */
  private loadJumpIndex(libId: string): void {
    this.api.getJumpIndex(libId).subscribe({
      next: (res) => this.jumpBuckets.set(res.buckets),
      error: () => this.jumpBuckets.set([]),
    });
  }

  /**
   * Jump to a bucket: set the cursor to the bucket's firstCursor and reload
   * from the top. A null cursor means the start of the listing (first page).
   */
  jumpToBucket(bucket: JumpIndexBucketDto): void {
    this.cursor = bucket.firstCursor;
    this.nodes.set([]);
    this.clearSelection();
    this.activeJump.set(bucket.label);
    this.loadNodes();
  }

  /** Resolve the library's display name for the breadcrumb root (reader-accessible). */
  private loadLibraryName(libId: string): void {
    this.api.getLibraries().subscribe({
      next: (libs) => this.libraryName.set(libs.find((l) => l.id === libId)?.name ?? ''),
      error: () => { /* fall back to "Library" in the template */ },
    });
  }

  loadMore(): void {
    this.loadNodes();
  }

  getNodeLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') {
      return ['/libraries', this.libraryId(), 'browse', node.id];
    }
    return ['/reader', node.id];
  }

  /** Short label for a folder's direction override chip. */
  directionShort(mode: ReaderMode): string {
    switch (mode) {
      case 'PagedLtr': return 'LTR';
      case 'PagedRtl': return 'RTL';
      case 'VerticalWebtoon': return 'Vertical';
      default: return 'Spread';
    }
  }

  // --- View mode (1.2.0, per-user persisted) ---

  setViewMode(mode: LibraryViewMode): void {
    if (this.viewMode() === mode) return;
    this.viewMode.set(mode);
    this.persistView();
  }

  /**
   * Live card-size preview while dragging the slider (1.6.0): only updates the
   * signal (which drives the grid's --card-size), deliberately WITHOUT persisting,
   * so a drag doesn't fire a PUT per pixel. The commit happens on `change`.
   */
  onCardSizeInput(event: Event): void {
    this.cardSize.set(this.clampCardSize(Number((event.target as HTMLInputElement).value)));
  }

  /** Commit the card size when the slider is released (change): persists it. */
  onCardSizeChange(event: Event): void {
    this.setCardSize(Number((event.target as HTMLInputElement).value));
  }

  /** Set the card size (clamped to the slider range) and persist the preference. */
  setCardSize(px: number): void {
    const size = this.clampCardSize(px);
    this.cardSize.set(size);
    this.persistView();
  }

  private clampCardSize(px: number): number {
    if (!Number.isFinite(px)) return this.defaultCardSize;
    return Math.min(this.cardSizeMax, Math.max(this.cardSizeMin, Math.round(px)));
  }

  /** Change the browse sort: persist the preference and reorder from the top. */
  setSort(s: LibrarySortOrder): void {
    if (this.sort() === s) return;
    this.sort.set(s);
    this.persistView();
    this.cursor = null;
    this.nodes.set([]);
    this.activeJump.set(null);
    this.clearSelection();
    this.loadNodes();
    // The jump rail is only valid for the name sort in ascending order (the
    // cursor is a raw SortKey that assumes A→Z order; other sorts ignore it).
    if (s === 'name' && this.sortDirection() === 'asc' && !this.parentId()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
  }

  /** Toggle ascending/descending for the active sort: persist and reorder from the top. */
  setSortDirection(d: LibrarySortDirection): void {
    if (this.sortDirection() === d) return;
    this.sortDirection.set(d);
    this.persistView();
    this.cursor = null;
    this.nodes.set([]);
    this.activeJump.set(null);
    this.clearSelection();
    this.loadNodes();
    if (this.sort() === 'name' && d === 'asc' && !this.parentId()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
  }

  private persistView(): void {
    this.api.setLibraryPreferences({
      viewMode: this.viewMode(),
      density: this.density(),
      sort: this.sort(),
      direction: this.sortDirection(),
      cardSize: String(this.cardSize()),
    }).subscribe({ error: () => { /* non-fatal: the choice still applies this session */ } });
  }

  // --- Selection mode (1.2.0) ---

  toggleSelectMode(): void {
    this.selectMode.update((v) => !v);
    if (!this.selectMode()) this.clearSelection();
  }

  isSelected(node: CatalogNodeDto): boolean {
    return this.selected().has(node.id);
  }

  /**
   * Desktop card click, file-browser semantics (1.7.0):
   *  - a long-press already handled this pointer session (touch) - suppress
   *    the trailing click entirely.
   *  - Shift-click, with an anchor set: fills the inclusive range from the
   *    anchor to the clicked card (display order) into the selection.
   *  - Plain click or Ctrl/Cmd-click: toggles just this card and moves the
   *    anchor to it (Ctrl/Cmd is equivalent to a plain click here, since
   *    every click in select mode already toggles rather than replacing the
   *    selection - it is accepted so the file-browser modifier still "works").
   * Outside select mode, a click is a normal navigation and this is a no-op.
   */
  onCardClick(event: MouseEvent, node: CatalogNodeDto): void {
    if (this.longPressTriggered) {
      // The long-press action already ran (or its prompt is open); this is
      // the click that follows the touch release and must be swallowed.
      this.longPressTriggered = false;
      event.preventDefault();
      event.stopPropagation();
      return;
    }
    if (!this.selectMode()) return;
    event.preventDefault();
    event.stopPropagation();

    const index = this.nodes().findIndex((n) => n.id === node.id);
    if (index === -1) return;

    if (event.shiftKey && this.anchorIndex() !== null) {
      this.selectRange(this.anchorIndex()!, index);
      return;
    }
    this.toggleOne(node, index);
  }

  /** Toggle a single node's selection and move the range anchor to it. */
  private toggleOne(node: CatalogNodeDto, index: number): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (next.has(node.id)) next.delete(node.id);
      else next.add(node.id);
      return next;
    });
    this.anchorIndex.set(index);
  }

  /** Add the inclusive range [fromIndex, toIndex] (display order) to the selection. */
  private selectRange(fromIndex: number, toIndex: number): void {
    const lo = Math.min(fromIndex, toIndex);
    const hi = Math.max(fromIndex, toIndex);
    const rangeIds = this.nodes().slice(lo, hi + 1).map((n) => n.id);
    this.selected.update((set) => new Set([...set, ...rangeIds]));
    this.anchorIndex.set(toIndex);
  }

  // --- Touch range selection: long-press -> "Select to here" (1.7.0) ---

  /** Start the long-press timer for a touch/pen pointer only; mouse uses shift-click instead. */
  onCardPointerDown(event: PointerEvent, node: CatalogNodeDto): void {
    if (event.pointerType !== 'touch' && event.pointerType !== 'pen') return;
    this.clearLongPressTimer();
    this.longPressTimer = setTimeout(() => this.onLongPress(node), this.longPressMs);
  }

  /** A normal tap released before the long-press fired: just cancel the timer. */
  onCardPointerUp(): void {
    this.clearLongPressTimer();
  }

  /** Pointer left/cancelled (scroll, interruption): cancel the pending long-press. */
  onCardPointerCancel(): void {
    this.clearLongPressTimer();
  }

  private clearLongPressTimer(): void {
    if (this.longPressTimer !== null) {
      clearTimeout(this.longPressTimer);
      this.longPressTimer = null;
    }
  }

  /**
   * Long-press fired on `node`. Mental model: tap = one, long-press = range fill.
   *  - Not yet in select mode: enter it and select+anchor this card (nothing to
   *    fill a range from yet).
   *  - In select mode with no anchor yet (nothing individually selected since
   *    entering select mode / after Select all* reset it): same as above.
   *  - In select mode with an anchor: open "Select to here" on this card so the
   *    range is filled only on explicit confirmation.
   * `longPressTriggered` suppresses the click event that the browser fires on
   * touch release right after this.
   */
  private onLongPress(node: CatalogNodeDto): void {
    this.longPressTriggered = true;
    const index = this.nodes().findIndex((n) => n.id === node.id);
    if (index === -1) return;

    if (!this.selectMode()) {
      this.selectMode.set(true);
      this.toggleOne(node, index);
      return;
    }
    if (this.anchorIndex() === null) {
      this.toggleOne(node, index);
      return;
    }
    this.rangePromptNode.set(node);
  }

  /** Button click wrapper: stop the click from bubbling to the card's own click handler. */
  onSelectToHereClick(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.confirmSelectToHere();
  }

  /** Button click wrapper: stop the click from bubbling to the card's own click handler. */
  onDismissRangePromptClick(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.dismissRangePrompt();
  }

  /** Confirm the "Select to here" prompt: fill the range from the anchor to the prompted card. */
  confirmSelectToHere(): void {
    const target = this.rangePromptNode();
    const anchor = this.anchorIndex();
    this.rangePromptNode.set(null);
    if (target === null || anchor === null) return;
    const index = this.nodes().findIndex((n) => n.id === target.id);
    if (index !== -1) this.selectRange(anchor, index);
  }

  /** Dismiss the "Select to here" prompt without changing the selection. */
  dismissRangePrompt(): void {
    this.rangePromptNode.set(null);
  }

  // --- Whole-folder selection (1.7.0) ---

  /** Select every currently-listed node (respects the active sort/filter, not just the loaded page). */
  selectAll(): void {
    this.selected.set(new Set(this.nodes().map((n) => n.id)));
    const count = this.nodes().length;
    this.anchorIndex.set(count > 0 ? count - 1 : null);
  }

  /** Select every currently-listed node that is not yet marked read. */
  selectAllUnread(): void {
    this.selected.set(new Set(this.nodes().filter((n) => !n.isRead).map((n) => n.id)));
    this.anchorIndex.set(null);
  }

  /** Select every currently-listed node that is marked read. */
  selectAllRead(): void {
    this.selected.set(new Set(this.nodes().filter((n) => n.isRead).map((n) => n.id)));
    this.anchorIndex.set(null);
  }

  clearSelection(): void {
    this.selected.set(new Set());
    this.anchorIndex.set(null);
    this.rangePromptNode.set(null);
    this.clearLongPressTimer();
    this.longPressTriggered = false;
  }

  /**
   * Sticky read-mark over the selection: archives individually, folders recursively
   * over their descendant archives. Visible archive badges update; folder effects
   * (on items inside them) are reported by count. Selection is kept so more actions
   * can be applied to the same set.
   *
   * Mark-UNREAD (1.6.0 fix): clearing the sticky read-mark (DELETE .../read) is a
   * no-op for an item that was opened but never marked read - it is InProgress with
   * no read-mark, so it would stay "reading". So when unmarking, we ALSO reset the
   * reading progress of any InProgress archive (DELETE .../progress -> Unread). That
   * makes it leave both the browse "Reading" badge and the continue-reading strip.
   * resetProgress is idempotent server-side (no-op when already unread), so it is
   * safe to fire for the InProgress subset only. (Single-page archives auto-marking
   * read on OPEN is intended and untouched - this is only the mark-unread action.)
   */
  bulkMarkRead(read: boolean): void {
    const ids = this.selected();
    const chosen = this.nodes().filter((n) => ids.has(n.id));
    const archives = chosen.filter((n) => n.kind === 'Archive');
    const folders = chosen.filter((n) => n.kind === 'Folder');

    // Only when marking unread: the mid-read archives whose progress must also be reset.
    const resetArchives = read
      ? []
      : archives.filter((a) => a.readingState === 'InProgress');

    const calls = [
      ...archives.map((a) => this.api.setItemRead(a.id, read)),
      ...folders.map((f) => this.api.setFolderRead(f.id, read)),
      ...resetArchives.map((a) => this.api.resetProgress(a.id)),
    ];
    if (calls.length === 0) return;

    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        const archiveIds = new Set(archives.map((a) => a.id));
        const resetIds = new Set(resetArchives.map((a) => a.id));
        this.nodes.update((list) =>
          list.map((n) => {
            if (!archiveIds.has(n.id)) return n;
            const updated = { ...n, isRead: read };
            // Marking unread also cleared mid-read progress: drop the "Reading" state
            // (and the last-read page) so the badge disappears and the item is no
            // longer mid-read.
            if (resetIds.has(n.id)) {
              updated.readingState = 'Unread';
              updated.lastReadPage = null;
            }
            return updated;
          }));

        const folderNote = folders.length
          ? ` and ${folders.length} folder${folders.length > 1 ? 's' : ''}`
          : '';
        this.snackBar.open(
          `Marked ${read ? 'read' : 'unread'}: ${archives.length} item${archives.length === 1 ? '' : 's'}${folderNote}`,
          'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  /**
   * Admin-only: set (or clear, when mode is null) the global reading-direction
   * override on every selected folder. Archives in the selection are ignored.
   */
  bulkSetDirection(mode: ReaderMode | null): void {
    const ids = this.selected();
    const folders = this.nodes().filter((n) => ids.has(n.id) && n.kind === 'Folder');
    if (folders.length === 0) return;

    const calls = folders.map((f) =>
      mode ? this.api.setFolderReaderDefault(f.id, mode) : this.api.clearFolderReaderDefault(f.id));

    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        const fids = new Set(folders.map((f) => f.id));
        this.nodes.update((list) =>
          list.map((n) => (fids.has(n.id) ? { ...n, readerDefault: mode } : n)));
        const label = this.directionOptions.find((o) => o.value === mode)?.label ?? 'updated';
        this.snackBar.open(
          `Reading direction (${label}) on ${folders.length} folder${folders.length === 1 ? '' : 's'}`,
          'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  private loadNodes(): void {
    const libId = this.libraryId();
    if (!libId) return;

    // Initial page (no cursor) replaces; "Load more" (cursor set) appends. Replacing
    // on the initial load keeps a stray concurrent load from duplicating rows.
    const initial = this.cursor === null;
    this.api.browseLibrary(libId, this.parentId(), this.cursor, 50, this.sort(), this.sortDirection()).subscribe({
      next: (response: PageResponse<CatalogNodeDto>) => {
        this.nodes.update((current) => initial ? [...response.items] : [...current, ...response.items]);
        this.hasMore.set(response.hasMore);
        this.cursor = response.nextCursor;
      },
    });
  }

  private loadBreadcrumbs(nodeId: string): void {
    this.api.getBreadcrumbs(nodeId).subscribe({
      next: (response) => this.breadcrumbs.set(response.trail),
    });
  }

  /**
   * Resolve the current folder's display name for the trailing (non-clickable)
   * breadcrumb segment (Task B). Uses the existing node-lookup endpoint; on error
   * we clear the name so the breadcrumb simply omits the current segment rather
   * than showing a stale one.
   */
  private loadCurrentFolder(nodeId: string): void {
    this.api.getNode(nodeId).subscribe({
      next: (node) => this.currentFolderName.set(node.displayName),
      error: () => this.currentFolderName.set(''),
    });
  }
}
