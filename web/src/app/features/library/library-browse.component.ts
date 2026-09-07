import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';

import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, PageResponse } from '../../core/api/api-types';

/**
 * Library browse component. Shows the actual folder/archive tree
 * with keyset pagination, breadcrumbs, and item state indicators.
 */
@Component({
  selector: 'app-library-browse',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatIconModule,
    MatButtonModule,
  ],
  template: `
    @if (breadcrumbs().length > 0) {
      <div class="breadcrumbs">
        <a routerLink="/libraries/{{ libraryId() }}">Root</a>
        @for (crumb of breadcrumbs(); track crumb.id) {
          <span> / </span>
          <a routerLink="/libraries/{{ libraryId() }}/browse/{{ crumb.id }}">{{ crumb.displayName }}</a>
        }
      </div>
    }

    <div class="nodes-grid">
      @for (node of nodes(); track node.id) {
        <a class="node-card" [routerLink]="getNodeLink(node)">
          <div class="cover">
            @if (node.coverUrl) {
              <img [src]="node.coverUrl" alt="" loading="lazy" (error)="onCoverError($event)">
            }
            <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>
            @if (node.readingState === 'InProgress') {
              <span class="badge reading">Reading</span>
            } @else if (node.readingState === 'Completed') {
              <span class="badge done">✓</span>
            }
          </div>
          <div class="node-title" [title]="node.displayName">{{ node.displayName }}</div>
          <div class="node-sub">
            @if (node.pageCount !== null) { {{ node.pageCount }} pages }
            @else if (node.kind === 'Folder' && node.childArchiveCount !== null) { {{ node.childArchiveCount }} items }
            @if (node.availability !== 'Available') { · {{ node.availability }} }
          </div>
        </a>
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
    .breadcrumbs { margin-bottom: 16px; a { text-decoration: none; color: #1976d2; } }
    .nodes-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
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
    .badge.done { background: rgba(76, 175, 80, 0.9); }
    .node-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .node-sub { font-size: 12px; color: #999; }
    .empty { color: #999; padding: 32px; text-align: center; }
    .load-more { text-align: center; margin-top: 16px; }
  `],
})
export class LibraryBrowseComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);

  readonly libraryId = signal('');
  readonly parentId = signal<string | null>(null);
  readonly nodes = signal<CatalogNodeDto[]>([]);
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  readonly hasMore = signal(false);
  private cursor: string | null = null;

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const libId = params.get('libraryId')!;
      const parentId = params.get('nodeId');
      this.libraryId.set(libId);
      this.parentId.set(parentId);
      this.cursor = null;
      this.nodes.set([]);
      this.loadNodes();
      if (parentId) this.loadBreadcrumbs(parentId);
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
