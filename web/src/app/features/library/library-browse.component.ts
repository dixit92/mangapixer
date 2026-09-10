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
import { CatalogNodeDto, PageResponse, ReaderMode, LibraryViewMode, LibraryGridDensity, LibrarySortOrder, JumpIndexBucketDto } from '../../core/api/api-types';

/**
 * Library browse component. Shows the actual folder/archive tree with keyset
 * pagination, breadcrumbs, and item state indicators.
 *
 * Selection mode (1.2.0) is the single home for card actions (owner review
 * 2026-09-09): tapping cards selects them (mouse or touch — no hover-only
 * affordances), and a STICKY top bar carries every bulk action applied to the
 * selection — mark read / unread for everyone, plus set/clear reading direction
 * for admins (folders only). There is no competing per-card menu.
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
  ],
  template: `
    <!-- Sticky top bar: breadcrumbs + Select normally; the merged action set while
         selecting. Sticky so the controls stay reachable when scrolling a long
         folder (touch-friendly — requirement 3). -->
    <div class="browse-bar" [class.selecting]="selectMode()">
      @if (!selectMode()) {
        <div class="breadcrumbs">
          @if (breadcrumbs().length > 0) {
            <a routerLink="/libraries/{{ libraryId() }}/browse">{{ libraryName() || 'Library' }}</a>
            @for (crumb of breadcrumbs(); track crumb.id) {
              <span class="sep"> / </span>
              <a routerLink="/libraries/{{ libraryId() }}/browse/{{ crumb.id }}">{{ crumb.displayName }}</a>
            }
          } @else {
            <a routerLink="/libraries/{{ libraryId() }}/browse">{{ libraryName() || 'Library' }}</a>
          }
        </div>
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
          @if (viewMode() !== 'list') {
            <mat-divider></mat-divider>
            <button mat-menu-item (click)="setDensity('comfortable')">
              <mat-icon>{{ density() === 'comfortable' ? 'check' : 'density_medium' }}</mat-icon>
              Comfortable
            </button>
            <button mat-menu-item (click)="setDensity('compact')">
              <mat-icon>{{ density() === 'compact' ? 'check' : 'density_small' }}</mat-icon>
              Compact
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
        </mat-menu>
        <button mat-stroked-button class="select-toggle" (click)="toggleSelectMode()">
          <mat-icon>checklist</mat-icon> Select
        </button>
      } @else {
        <span class="count">{{ selected().size }} selected</span>
        <div class="actions">
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

    <div class="nodes" [class.grid]="viewMode() === 'grid'"
         [class.list]="viewMode() === 'list'" [class.poster]="viewMode() === 'poster'"
         [class.compact]="density() === 'compact'">
      @for (node of nodes(); track node.id) {
        <div class="node-wrap" [class.selected]="isSelected(node)">
          <a class="node-card" [routerLink]="selectMode() ? null : getNodeLink(node)"
             (click)="onCardClick($event, node)">
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
    .breadcrumbs.muted { color: #999; }
    .count { font-weight: 600; }
    .actions { flex: 1 1 auto; display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    .actions mat-icon { margin-right: 4px; }
    .select-toggle mat-icon, .done mat-icon { margin-right: 4px; }
    /* View modes (1.2.0). Grid/Poster are cover grids at different sizes; density
       tightens them; List is a compact row layout with a small thumbnail. */
    .nodes.grid {
      display: grid; gap: 16px;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
    }
    .nodes.grid.compact { gap: 10px; grid-template-columns: repeat(auto-fill, minmax(112px, 1fr)); }
    .nodes.poster {
      display: grid; gap: 20px;
      grid-template-columns: repeat(auto-fill, minmax(210px, 1fr));
    }
    .nodes.poster.compact { gap: 14px; grid-template-columns: repeat(auto-fill, minmax(168px, 1fr)); }
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

  // Per-user library view mode (1.2.0). Tolerant: unknown persisted values fall back.
  readonly viewMode = signal<LibraryViewMode>('grid');
  readonly density = signal<LibraryGridDensity>('comfortable');
  readonly viewOptions: { value: LibraryViewMode; label: string; icon: string }[] = [
    { value: 'grid', label: 'Grid', icon: 'grid_view' },
    { value: 'list', label: 'List', icon: 'view_list' },
    { value: 'poster', label: 'Poster', icon: 'view_module' },
  ];
  readonly viewIcon = computed(() =>
    this.viewOptions.find((o) => o.value === this.viewMode())?.icon ?? 'grid_view');

  // Per-user browse sort (post-1.2.0). Folders stay first in every mode; the sort
  // orders within kind. Omitted on the request → the server uses the stored pref;
  // we pass it explicitly so a change reorders immediately without a persist race.
  readonly sort = signal<LibrarySortOrder>('name');
  readonly sortOptions: { value: LibrarySortOrder; label: string; icon: string }[] = [
    { value: 'name', label: 'Name', icon: 'sort_by_alpha' },
    { value: 'recentlyAdded', label: 'Recently added', icon: 'schedule' },
    { value: 'recentlyRead', label: 'Recently read', icon: 'history' },
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
        const vm = p.viewMode as LibraryViewMode;
        this.viewMode.set(vm === 'list' || vm === 'poster' ? vm : 'grid');
        this.density.set(p.density === 'compact' ? 'compact' : 'comfortable');
        if (p.sort === 'recentlyAdded' || p.sort === 'recentlyRead') this.sort.set(p.sort);
        this.subscribeToRoute();
      },
      error: () => this.subscribeToRoute(), // keep defaults, still load
    });
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
      // only meaningful for the name sort — other sorts ignore the cursor.
      if (!parentId && this.sort() === 'name') this.loadJumpIndex(libId);
      else this.jumpBuckets.set([]);
      if (parentId) this.loadBreadcrumbs(parentId);
      else this.breadcrumbs.set([]);
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

  setDensity(d: LibraryGridDensity): void {
    if (this.density() === d) return;
    this.density.set(d);
    this.persistView();
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
    // The jump rail is only valid for the name sort (the cursor is a raw
    // SortKey that other sorts ignore). Load/clear it to match.
    if (s === 'name' && !this.parentId()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
  }

  private persistView(): void {
    this.api.setLibraryPreferences({
      viewMode: this.viewMode(),
      density: this.density(),
      sort: this.sort(),
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

  /** In select mode, a card tap toggles selection instead of navigating. */
  onCardClick(event: Event, node: CatalogNodeDto): void {
    if (!this.selectMode()) return;
    event.preventDefault();
    event.stopPropagation();
    this.selected.update((set) => {
      const next = new Set(set);
      if (next.has(node.id)) next.delete(node.id);
      else next.add(node.id);
      return next;
    });
  }

  clearSelection(): void {
    this.selected.set(new Set());
  }

  /**
   * Sticky read-mark over the selection: archives individually, folders recursively
   * over their descendant archives. Visible archive badges update; folder effects
   * (on items inside them) are reported by count. Selection is kept so more actions
   * can be applied to the same set.
   */
  bulkMarkRead(read: boolean): void {
    const ids = this.selected();
    const chosen = this.nodes().filter((n) => ids.has(n.id));
    const archives = chosen.filter((n) => n.kind === 'Archive');
    const folders = chosen.filter((n) => n.kind === 'Folder');

    const calls = [
      ...archives.map((a) => this.api.setItemRead(a.id, read)),
      ...folders.map((f) => this.api.setFolderRead(f.id, read)),
    ];
    if (calls.length === 0) return;

    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        const archiveIds = new Set(archives.map((a) => a.id));
        this.nodes.update((list) =>
          list.map((n) => (archiveIds.has(n.id) ? { ...n, isRead: read } : n)));

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
    this.api.browseLibrary(libId, this.parentId(), this.cursor, 50, this.sort()).subscribe({
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
}
