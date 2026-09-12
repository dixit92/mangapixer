import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { CatalogNodeDto, SearchResultsDto } from '../../core/api/api-types';

/**
 * Search component. Uses the FTS5 trigram search via the API.
 */
@Component({
  selector: 'app-search',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    CoverImageDirective,
  ],
  template: `
    <h2>Search</h2>
    <mat-form-field appearance="outline" class="search-field">
      <mat-label>Search libraries</mat-label>
      <input matInput [(ngModel)]="query" (ngModelChange)="onSearch()" placeholder="Enter search text...">
      <mat-icon matSuffix>search</mat-icon>
    </mat-form-field>

    @if (results().length > 0) {
      <p class="result-count">{{ totalCount() }} results</p>
      <div class="results-grid">
        @for (node of results(); track node.id) {
          <a [routerLink]="getNodeLink(node)" class="result-card">
            <div class="cover">
              @if (coverSrc(node); as src) {
                <img appCover [src]="src" alt="" loading="lazy">
              }
              <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>
            </div>
            <div class="result-title" [title]="node.displayName">{{ node.displayName }}</div>
          </a>
        }
      </div>
    } @else if (searched()) {
      <p class="no-results">No results found.</p>
    }
  `,
  styles: [`
    .search-field { width: 100%; max-width: 600px; }
    .result-count { color: #999; margin: 16px 0; }
    .results-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
    .result-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgba(255,255,255,0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: #777; position: absolute; z-index: 0; }
    .result-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .no-results { color: #999; padding: 32px; text-align: center; }
  `],
})
export class SearchComponent {
  private readonly api = inject(ApiService);

  query = '';
  readonly results = signal<CatalogNodeDto[]>([]);
  readonly totalCount = signal(0);
  readonly searched = signal(false);

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  onSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.doSearch(), 300);
  }

  getNodeLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') {
      return ['/libraries', node.libraryId, 'browse', node.id];
    }
    return ['/reader', node.id];
  }

  /**
   * Resolved cover URL for a search result. Folders use the backend-provided
   * `coverUrl` (resolved from the first descendant archive by SortKey — parity
   * with browse, added for search in 1.8.0); archives fall back to their own
   * cover endpoint (the search projection does not set coverUrl for archives).
   * Folders with no readable descendant stay coverless and render the icon.
   */
  coverSrc(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }

  private doSearch(): void {
    if (!this.query.trim()) {
      this.results.set([]);
      this.totalCount.set(0);
      this.searched.set(false);
      return;
    }

    this.api.search(this.query).subscribe({
      next: (response: SearchResultsDto) => {
        this.results.set(response.items);
        this.totalCount.set(response.totalCount);
        this.searched.set(true);
      },
      error: () => {
        this.results.set([]);
        this.totalCount.set(0);
        this.searched.set(true);
      },
    });
  }
}
