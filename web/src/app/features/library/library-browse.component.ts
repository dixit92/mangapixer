import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar } from '@angular/material/snack-bar';
import { forkJoin } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, PageResponse, ReaderMode } from '../../core/api/api-types';

/**
 * Library browse component. Shows the actual folder/archive tree
 * with keyset pagination, breadcrumbs, and item state indicators.
 *
 * Selection mode (1.2.0) lets a reader mark items read/unread in bulk without
 * opening them: archives are marked individually, folders recursively over their
 * descendant archives (the sticky read-flag feature).
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
  ],
  template: `
    <div class="browse-toolbar">
      @if (breadcrumbs().length > 0) {
        <div class="breadcrumbs">
          <a routerLink="/libraries/{{ libraryId() }}">Root</a>
          @for (crumb of breadcrumbs(); track crumb.id) {
            <span> / </span>
            <a routerLink="/libraries/{{ libraryId() }}/browse/{{ crumb.id }}">{{ crumb.displayName }}</a>
          }
        </div>
      } @else {
        <span class="breadcrumbs muted">Root</span>
      }

      <button mat-stroked-button class="select-toggle" (click)="toggleSelectMode()"
              [class.active]="selectMode()">
        <mat-icon>{{ selectMode() ? 'close' : 'checklist' }}</mat-icon>
        {{ selectMode() ? 'Done' : 'Select' }}
      </button>
    </div>

    <div class="nodes-grid">
      @for (node of nodes(); track node.id) {
        <div class="node-wrap" [class.selected]="isSelected(node)">
          <a class="node-card" [routerLink]="selectMode() ? null : getNodeLink(node)"
             (click)="onCardClick($event, node)">
            <div class="cover">
              @if (node.coverUrl) {
                <img [src]="node.coverUrl" alt="" loading="lazy" (error)="onCoverError($event)">
              }
              <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>

              @if (node.isRead) {
                <span class="badge read" matTooltip="Read">✓ Read</span>
              } @else if (node.readingState === 'InProgress') {
                <span class="badge reading">Reading</span>
              }

              @if (selectMode()) {
                <span class="check" [class.on]="isSelected(node)">
                  <mat-icon>{{ isSelected(node) ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
                </span>
              }
            </div>
            <div class="node-title" [title]="node.displayName">{{ node.displayName }}</div>
            <div class="node-sub">
              @if (node.pageCount !== null) { {{ node.pageCount }} pages }
              @else if (node.kind === 'Folder' && node.childArchiveCount !== null) { {{ node.childArchiveCount }} items }
              @if (node.availability !== 'Available') { · {{ node.availability }} }
            </div>
          </a>

          <!-- Admin-only per-folder reading-direction override (1.2.0). -->
          @if (auth.isAdmin() && node.kind === 'Folder' && !selectMode()) {
            <button class="dir-btn" mat-icon-button [matMenuTriggerFor]="dirMenu"
                    (click)="$event.stopPropagation(); $event.preventDefault()"
                    [class.set]="node.readerDefault !== null"
                    matTooltip="Default reading direction" aria-label="Default reading direction">
              <mat-icon>{{ node.readerDefault ? 'swap_horiz' : 'more_vert' }}</mat-icon>
            </button>
            <mat-menu #dirMenu="matMenu">
              @for (opt of directionOptions; track opt.label) {
                <button mat-menu-item (click)="setFolderDirection(node, opt.value)">
                  <mat-icon>{{ (node.readerDefault ?? null) === opt.value ? 'check' : '' }}</mat-icon>
                  {{ opt.label }}
                </button>
              }
            </mat-menu>
          }
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

    <!-- Selection action bar (1.2.0). -->
    @if (selectMode() && selected().size > 0) {
      <div class="action-bar">
        <span class="count">{{ selected().size }} selected</span>
        <button mat-button (click)="bulkMarkRead(true)" [disabled]="busy()">
          <mat-icon>check_circle</mat-icon> Mark read
        </button>
        <button mat-button (click)="bulkMarkRead(false)" [disabled]="busy()">
          <mat-icon>remove_done</mat-icon> Mark unread
        </button>
        <button mat-button (click)="clearSelection()" [disabled]="busy()">Clear</button>
      </div>
    }
  `,
  styles: [`
    .browse-toolbar {
      display: flex; align-items: center; justify-content: space-between;
      gap: 12px; margin-bottom: 16px;
    }
    .breadcrumbs { a { text-decoration: none; color: #1976d2; } }
    .breadcrumbs.muted { color: #999; }
    .select-toggle.active { background: rgba(124, 77, 255, 0.15); }
    .nodes-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
    .node-wrap { position: relative; border-radius: 8px; }
    .node-wrap.selected { outline: 2px solid #7c4dff; outline-offset: 3px; }
    .node-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .dir-btn {
      position: absolute; top: 2px; left: 2px; z-index: 3;
      width: 32px; height: 32px; line-height: 32px;
      background: rgba(0, 0, 0, 0.45); color: #fff;
    }
    .dir-btn.set { background: rgba(124, 77, 255, 0.9); }
    .dir-btn mat-icon { font-size: 18px; width: 18px; height: 18px; }
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
    .action-bar {
      position: sticky; bottom: 16px; z-index: 10;
      display: flex; align-items: center; gap: 8px;
      margin-top: 16px; padding: 8px 16px;
      background: #2a2a2a; color: #fff;
      border-radius: 24px; box-shadow: 0 4px 16px rgba(0,0,0,0.4);
      width: fit-content; margin-left: auto; margin-right: auto;
    }
    .action-bar .count { font-weight: 600; margin-right: 8px; }
  `],
})
export class LibraryBrowseComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  // Per-folder direction override options (1.2.0). null = inherit (clear).
  readonly directionOptions: { value: ReaderMode | null; label: string }[] = [
    { value: null, label: 'Inherit' },
    { value: 'PagedLtr', label: 'Left-to-right' },
    { value: 'PagedRtl', label: 'Right-to-left' },
    { value: 'VerticalWebtoon', label: 'Vertical' },
  ];

  readonly libraryId = signal('');
  readonly parentId = signal<string | null>(null);
  readonly nodes = signal<CatalogNodeDto[]>([]);
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  readonly hasMore = signal(false);
  private cursor: string | null = null;

  // Selection mode (1.2.0 read-marks).
  readonly selectMode = signal(false);
  readonly selected = signal<Set<string>>(new Set());
  readonly busy = signal(false);

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const libId = params.get('libraryId')!;
      const parentId = params.get('nodeId');
      this.libraryId.set(libId);
      this.parentId.set(parentId);
      this.cursor = null;
      this.nodes.set([]);
      this.clearSelection();
      this.loadNodes();
      if (parentId) this.loadBreadcrumbs(parentId);
      else this.breadcrumbs.set([]);
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

  onCoverError(event: Event): void {
    // Hide the broken image so the folder/book icon fallback shows through.
    (event.target as HTMLImageElement).style.display = 'none';
  }

  // --- Selection mode (1.2.0) ---

  toggleSelectMode(): void {
    this.selectMode.update((v) => !v);
    if (!this.selectMode()) this.clearSelection();
  }

  isSelected(node: CatalogNodeDto): boolean {
    return this.selected().has(node.id);
  }

  /** In select mode, a card click toggles selection instead of navigating. */
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
   * Applies the sticky read-mark to every selected node: archives individually,
   * folders recursively over their descendant archives. Currently-visible archives
   * update their badge; folder effects (on items inside them) are reported by count.
   */
  bulkMarkRead(read: boolean): void {
    const ids = this.selected();
    const chosen = this.nodes().filter((n) => ids.has(n.id));
    if (chosen.length === 0) return;

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
        // Reflect the new state on visible archive cards.
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
        this.clearSelection();
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** Set (or clear, when mode is null) a folder's global reading-direction override. */
  setFolderDirection(node: CatalogNodeDto, mode: ReaderMode | null): void {
    const call = mode
      ? this.api.setFolderReaderDefault(node.id, mode)
      : this.api.clearFolderReaderDefault(node.id);
    call.subscribe({
      next: () => {
        this.nodes.update(list =>
          list.map(n => n.id === node.id ? { ...n, readerDefault: mode } : n));
        this.snackBar.open(mode ? 'Folder reading direction set' : 'Folder reading direction cleared',
          'Close', { duration: 2000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 }),
    });
  }

  private loadNodes(): void {
    const libId = this.libraryId();
    if (!libId) return;

    this.api.browseLibrary(libId, this.parentId(), this.cursor).subscribe({
      next: (response: PageResponse<CatalogNodeDto>) => {
        this.nodes.update((current) => [...current, ...response.items]);
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
