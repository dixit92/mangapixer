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
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { CatalogNodeDto } from '../../core/api/api-types';
import {
  favoriteLink,
  favoriteQueryParams,
  favoriteStackLabel,
  favoriteTrackKey,
  isFavoriteStack,
} from './favorite-stack';

/**
 * Favorites view (1.21.0) — the dedicated in-shell destination the sidebar "Favorites"
 * entry routes to. Reuses the browse/search card grammar (cover + kind badge + star)
 * over the `GET /favorites` source, ordered recently-favorited (newest first) with the
 * same keyset paging as browse. Unstarring a card here (or anywhere else, via
 * `FavoritesStateService.changed$`) drops it from the list in place.
 *
 * Series info (1.27.0): the (i) in the cover's bottom-left corner and the hover summary
 * (cover and title), for singles and stacks alike (a stack's is its folder's).
 *
 * Stacks (1.27.0): two or more starred archives in one folder arrive as ONE item for that
 * folder (`favoriteStackCount`), shown as a stacked card that opens the folder filtered to
 * favorites (`?favorites=1`, transient). See `favorite-stack.ts`.
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
    InfoToggleComponent,
    SeriesInfoHoverDirective,
  ],
  template: `
    <h2>Favorites</h2>

    @if (nodes().length > 0) {
      <div class="results-grid">
        @for (node of nodes(); track trackKey(node)) {
          @if (isStack(node)) {
            <!-- Stack (1.27.0): two or more starred archives in one folder. Opens the
                 folder with a transient "Favorites only" filter; no star here (it would
                 star the folder itself). -->
            <div class="fav-card">
              <a #cardEl [routerLink]="getNodeLink(node)" [queryParams]="stackParams(node)" class="fav-link stack-link"
                 [attr.aria-label]="node.displayName + ', ' + stackLabel(node)">
                <div class="stack">
                  <div class="cover" [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">
                    @if (coverSrc(node); as src) {
                      <img appCover [src]="src" alt="" loading="lazy">
                    }
                    <mat-icon class="cover-fallback">folder</mat-icon>
                    <span class="count-badge" [matTooltip]="stackLabel(node)">{{ node.favoriteStackCount }}</span>
                    <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo" [overlay]="true" />
                  </div>
                </div>
                <div class="fav-title" [title]="node.displayName"
                     [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
                <div class="fav-sub">{{ stackLabel(node) }}</div>
              </a>
            </div>
          } @else {
            <div class="fav-card">
              <a #cardEl [routerLink]="getNodeLink(node)" class="fav-link">
                <div class="cover" [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">
                  @if (coverSrc(node); as src) {
                    <img appCover [src]="src" alt="" loading="lazy">
                  }
                  <mat-icon class="cover-fallback">{{ kindIcon(node) }}</mat-icon>
                  <span class="kind-badge" [matTooltip]="kindLabel(node)" [attr.aria-label]="kindLabel(node)" role="img">
                    <mat-icon>{{ kindIcon(node) }}</mat-icon>
                  </span>
                  <app-star-toggle [nodeId]="node.id" [favorite]="true" [overlay]="true" [compact]="true" />
                  <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo" [overlay]="true" />
                </div>
                <div class="fav-title" [title]="node.displayName"
                     [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
              </a>
            </div>
          }
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
      padding-top: 8px; /* room for a stack's paper edges */
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
    .fav-sub { font-size: 12px; color: #999; }
    /* Stack (1.27.0): the Home "New chapters" stacked-paper edges behind the cover, and
       the number of favorites inside in the top-right corner. */
    .stack { position: relative; }
    .stack .cover { position: relative; z-index: 1; }
    .stack::before, .stack::after {
      content: ''; position: absolute; inset: 0; border-radius: 8px; z-index: 0;
      background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.08);
    }
    .stack::before { transform: translate(4px, -4px); }
    .stack::after { transform: translate(8px, -8px); opacity: 0.55; }
    .stack-link:hover .cover, .stack-link:focus-visible .cover { outline: 2px solid rgba(124,77,255,0.6); outline-offset: 1px; }
    .count-badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 10px;
      background: rgba(124, 77, 255, 0.92); color: #fff;
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
    // A node unstarred anywhere drops out of this list in place. A stack stays: it
    // stands for its starred children, not for the folder's own star.
    this.sub = this.favorites.changed$.subscribe((change) => {
      if (!change.favorite) {
        this.nodes.update((list) => list.filter((n) => n.id !== change.nodeId || isFavoriteStack(n)));
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
    return favoriteLink(node);
  }

  /** Series info (1.27.0): the (i) and the hover summary, for the item's own information. */
  hoverNodeId(node: CatalogNodeDto): string | null {
    return node.hasSeriesInfo ? node.id : null;
  }

  isStack(node: CatalogNodeDto): boolean {
    return isFavoriteStack(node);
  }

  stackParams(node: CatalogNodeDto): Record<string, string> | null {
    return favoriteQueryParams(node);
  }

  stackLabel(node: CatalogNodeDto): string {
    return favoriteStackLabel(node);
  }

  trackKey(node: CatalogNodeDto): string {
    return favoriteTrackKey(node);
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
