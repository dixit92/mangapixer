import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../../core/api/api.service';
import { FavoritesStateService } from '../../core/favorites/favorites-state.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { CatalogNodeDto } from '../../core/api/api-types';

/**
 * Favorites view (1.21.0) — the dedicated in-shell destination the sidebar "Favorites"
 * entry routes to. Reuses the browse/search card grammar (cover + kind badge + star)
 * over the `GET /favorites` source, ordered recently-favorited (newest first) with the
 * same keyset paging as browse. Unstarring a card here (or anywhere else, via
 * `FavoritesStateService.changed$`) drops it from the list in place.
 */
@Component({
  selector: 'app-favorites',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatIconModule,
    MatButtonModule,
    MatTooltipModule,
    CoverImageDirective,
    StarToggleComponent,
  ],
  template: `
    <h2>Favorites</h2>

    @if (nodes().length > 0) {
      <div class="results-grid">
        @for (node of nodes(); track node.id) {
          <div class="fav-card">
            <a [routerLink]="getNodeLink(node)" class="fav-link">
              <div class="cover">
                @if (coverSrc(node); as src) {
                  <img appCover [src]="src" alt="" loading="lazy">
                }
                <mat-icon class="cover-fallback">{{ kindIcon(node) }}</mat-icon>
                <span class="kind-badge" [matTooltip]="kindLabel(node)" [attr.aria-label]="kindLabel(node)" role="img">
                  <mat-icon>{{ kindIcon(node) }}</mat-icon>
                </span>
                <app-star-toggle [nodeId]="node.id" [favorite]="true" [overlay]="true" [compact]="true" />
              </div>
              <div class="fav-title" [title]="node.displayName">{{ node.displayName }}</div>
            </a>
          </div>
        }
      </div>

      @if (hasMore()) {
        <div class="load-more">
          <button mat-stroked-button (click)="loadMore()" [disabled]="loading()">
            {{ loading() ? 'Loading…' : 'Load more' }}
          </button>
        </div>
      }
    } @else if (loaded()) {
      <p class="empty">
        <mat-icon>star_border</mat-icon>
        No favorites yet. Tap the star on any archive or folder to add it here.
      </p>
    }
  `,
  styles: [`
    .results-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
    .fav-link { text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgba(255,255,255,0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: #777; position: absolute; z-index: 0; }
    .kind-badge {
      position: absolute; top: 4px; right: 4px; z-index: 2;
      display: flex; align-items: center; justify-content: center;
      width: 20px; height: 20px; border-radius: 50%;
      background: rgba(0, 0, 0, 0.65); color: #fff;
    }
    .kind-badge mat-icon { font-size: 14px; width: 14px; height: 14px; line-height: 14px; }
    .fav-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .load-more { display: flex; justify-content: center; margin: 24px 0; }
    .empty {
      color: #999; padding: 48px 16px; text-align: center;
      display: flex; flex-direction: column; align-items: center; gap: 8px;
    }
    .empty mat-icon { font-size: 40px; width: 40px; height: 40px; color: #777; }
  `],
})
export class FavoritesComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly favorites = inject(FavoritesStateService);

  readonly nodes = signal<CatalogNodeDto[]>([]);
  readonly hasMore = signal(false);
  readonly loading = signal(false);
  readonly loaded = signal(false);

  private cursor: string | null = null;
  private sub?: Subscription;

  ngOnInit(): void {
    this.load(false);
    // A node unstarred anywhere drops out of this list in place.
    this.sub = this.favorites.changed$.subscribe((change) => {
      if (!change.favorite) {
        this.nodes.update((list) => list.filter((n) => n.id !== change.nodeId));
      }
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  loadMore(): void {
    if (this.hasMore() && !this.loading()) this.load(true);
  }

  private load(append: boolean): void {
    this.loading.set(true);
    this.api.getFavorites(append ? this.cursor : null).subscribe({
      next: (page) => {
        this.nodes.update((prev) => (append ? [...prev, ...page.items] : page.items));
        this.cursor = page.nextCursor;
        this.hasMore.set(page.hasMore);
        this.loading.set(false);
        this.loaded.set(true);
      },
      error: () => {
        this.loading.set(false);
        this.loaded.set(true);
      },
    });
  }

  getNodeLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') return ['/libraries', node.libraryId, 'browse', node.id];
    return ['/reader', node.id];
  }

  coverSrc(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }

  kindIcon(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'folder' : 'menu_book';
  }

  kindLabel(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'Folder' : 'Archive';
  }
}
