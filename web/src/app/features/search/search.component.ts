import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

import { ApiService } from '../../core/api/api.service';
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
    MatCardModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
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
          <mat-card [routerLink]="getNodeLink(node)" class="result-card">
            <mat-card-content>
              @if (node.kind === 'Folder') {
                <mat-icon>folder</mat-icon>
              } @else {
                <mat-icon>menu_book</mat-icon>
              }
              <h3>{{ node.displayName }}</h3>
            </mat-card-content>
          </mat-card>
        }
      </div>
    } @else if (searched()) {
      <p class="no-results">No results found.</p>
    }
  `,
  styles: [`
    .search-field { width: 100%; max-width: 600px; }
    .result-count { color: #666; margin: 16px 0; }
    .results-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 12px;
    }
    .result-card { cursor: pointer; }
    mat-icon { font-size: 40px; width: 40px; height: 40px; color: #666; }
    h3 { margin: 8px 0 0 0; font-size: 14px; }
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
