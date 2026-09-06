import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { ApiService } from '../../core/api/api.service';
import {
  CatalogNodeDto,
  CatalogNodeKind,
  CatalogNodeAvailability,
  PageResponse,
} from '../../core/api/api-types';

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
    MatCardModule,
    MatIconModule,
    MatButtonModule,
    MatChipsModule,
    MatProgressBarModule,
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
        <mat-card class="node-card" [routerLink]="getNodeLink(node)">
          <mat-card-content>
            @if (node.kind === 'Folder') {
              <mat-icon>folder</mat-icon>
            } @else {
              <mat-icon>menu_book</mat-icon>
            }
            <h3>{{ node.displayName }}</h3>
            @if (node.pageCount !== null) {
              <p>{{ node.pageCount }} pages</p>
            }
            @if (node.availability !== 'Available') {
              <mat-chip-set>
                <mat-chip [color]="getAvailabilityColor(node.availability)">
                  {{ node.availability }}
                </mat-chip>
              </mat-chip-set>
            }
          </mat-card-content>
        </mat-card>
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
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 12px;
    }
    .node-card { cursor: pointer; }
    mat-icon { font-size: 40px; width: 40px; height: 40px; color: #666; }
    h3 { margin: 8px 0 4px 0; font-size: 14px; }
    p { margin: 0; color: #666; font-size: 12px; }
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

  getAvailabilityColor(availability: CatalogNodeAvailability): 'primary' | 'accent' | 'warn' {
    switch (availability) {
      case 'Preparing': return 'primary';
      case 'Corrupt': case 'Unavailable': case 'Tombstoned': return 'warn';
      default: return 'accent';
    }
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
