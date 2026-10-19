import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatChipsModule } from '@angular/material/chips';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { CatalogNodeDto, LibraryReadStateFilter, SearchResultsDto, SeriesMatchDto } from '../../core/api/api-types';

/**
 * The search page's state as it appears in the URL (1.45.0): `q` (the search text) and `read` (the read-state filter; absent = all).
 * ONE model read from and written to the query string, so a later filter (`f=`) is one more field here and in the two functions below.
 */
export interface SearchParams {
  q: string;
  read: LibraryReadStateFilter;
}

const READ_FILTERS: readonly LibraryReadStateFilter[] = ['all', 'reading', 'read', 'unread'];

/** Reads the page state from the URL; unknown values fall back to the defaults. */
export function searchParamsFromQuery(query: ParamMap): SearchParams {
  const read = query.get('read') as LibraryReadStateFilter | null;
  return { q: query.get('q') ?? '', read: read && READ_FILTERS.includes(read) ? read : 'all' };
}

/** The query string for a page state: defaults are left out (an empty search is a bare `/search`). */
export function searchParamsToQuery(params: SearchParams): Params {
  return { q: params.q.trim() ? params.q : null, read: params.read !== 'all' ? params.read : null };
}

function sameParams(a: SearchParams, b: SearchParams): boolean {
  return a.q === b.q && a.read === b.read;
}

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
    MatTooltipModule,
    MatChipsModule,
    CoverImageDirective,
    StarToggleComponent,
    InfoToggleComponent,
    SeriesInfoHoverDirective,
    FolderRollupBadgeComponent,
  ],
  template: `
    <h2>Search</h2>
    <mat-form-field appearance="outline" class="search-field">
      <mat-label>Search libraries</mat-label>
      <input matInput [(ngModel)]="query" (ngModelChange)="onSearch()" placeholder="Enter search text...">
      <mat-icon matSuffix>search</mat-icon>
    </mat-form-field>

    <!-- Read-state filter (1.45.0): the browse Show filter's choices as one chip row over the results, applied on the server before
         paging (the count and "Load more" stay right). Shown once there is search text, so a filter with no hits can be switched back. -->
    @if (params().q.trim()) {
      <mat-chip-listbox class="read-filter" aria-label="Show" [value]="params().read" (change)="setReadFilter($event.value)"
                        hideSingleSelectionIndicator>
        @for (opt of readOptions; track opt.value) {
          <mat-chip-option [value]="opt.value" [selectable]="params().read !== opt.value">{{ opt.label }}</mat-chip-option>
        }
      </mat-chip-listbox>
    }

    @if (seriesMatches().length > 0) {
      <section class="series-matches" aria-labelledby="series-matches-heading">
        <h3 id="series-matches-heading" class="section-heading">Series matches</h3>
        <div class="results-grid">
          @for (match of seriesMatches(); track match.node.id) {
            <ng-container *ngTemplateOutlet="card; context: { node: match.node, aka: match.matchedTitle }" />
          }
        </div>
      </section>
    }

    @if (results().length > 0) {
      <p class="result-count">{{ totalCount() }} results</p>
      <div class="results-grid">
        @for (node of results(); track node.id) {
          <ng-container *ngTemplateOutlet="card; context: { node: node, aka: null }" />
        }
      </div>
      <!-- Keyset paging (1.45.0): the next page after the last result, with the same text and filter. -->
      @if (hasMore()) {
        <div class="load-more">
          <button mat-stroked-button type="button" (click)="loadMore()" [disabled]="loadingMore()">
            {{ loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        </div>
      }
    } @else if (searched() && seriesMatches().length === 0) {
      <p class="no-results">{{ params().read === 'all' ? 'No results found.' : 'No results with this filter.' }}</p>
    }

    <ng-template #card let-node="node" let-aka="aka">
      <!-- Series info (1.27.0): the (i) in the cover's bottom-left corner as in browse, and
           the cover and title are hover zones for the summary popover. -->
      <a #cardEl [routerLink]="getNodeLink(node)" class="result-card">
        <div class="cover" [appSeriesInfoHover]="node.hasSeriesInfo ? node.id : null" [hoverAnchor]="cardEl">
          @if (coverSrc(node); as src) {
            <img appCover [src]="src" alt="" loading="lazy">
          }
          <mat-icon class="cover-fallback">{{ kindIcon(node) }}</mat-icon>
          <!-- Folder-vs-archive badge (1.12.0): shown over every result - including
               ones with a real cover image, where the fallback icon above is hidden -
               so the result kind stays legible regardless of cover art. -->
          <span class="kind-badge" [matTooltip]="kindLabel(node)" [attr.aria-label]="kindLabel(node)" role="img">
            <mat-icon>{{ kindIcon(node) }}</mat-icon>
          </span>
          <!-- Favorites prominence (1.21.0): the star (badge + interactive toggle)
               appears in search only when the user opted in; when off, results render
               normally. Boosting favorited results to the top is done in runSearch() and loadMore(). -->
          @if (prominence()) {
            <app-star-toggle [nodeId]="node.id" [favorite]="!!node.isFavorite" [overlay]="true" [compact]="true" />
          }
          <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo" [overlay]="true" />
          <!-- The viewer's read state (1.45.0), as browse cards show it: an archive's mark, a folder's rollup (top-right corner). -->
          @if (node.isRead) {
            <span class="badge read" matTooltip="Read">✓ Read</span>
          } @else if (node.readingState === 'InProgress') {
            <span class="badge reading">Reading</span>
          }
          <app-folder-rollup-badge [rollup]="node.readRollup" />
        </div>
        <div class="result-title" [title]="node.displayName"
             [appSeriesInfoHover]="node.hasSeriesInfo ? node.id : null" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
        @if (aka) {
          <div class="result-aka" [matTooltip]="aka">aka {{ aka }}</div>
        }
      </a>
    </ng-template>
  `,
  styles: [`
    .search-field { width: 100%; max-width: 600px; }
    .result-count { color: var(--mp-text-muted); margin: 16px 0; }
    .results-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
      gap: 16px;
    }
    .result-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgb(var(--mp-ink-rgb) / 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: var(--mp-text-faint); position: absolute; z-index: 0; }
    /* Folder-vs-archive kind badge (1.12.0): a small, legible corner marker so the
       node kind reads at a glance even when a real cover image is showing (the
       cover-fallback icon above is hidden then). Mirrors the compact circular-chip
       sizing already used for browse's list-view badges. */
    .kind-badge {
      position: absolute; top: 4px; left: 4px; z-index: 2;
      display: flex; align-items: center; justify-content: center;
      width: 20px; height: 20px; border-radius: 50%;
      background: rgb(var(--mp-shade-rgb) / 0.65); color: var(--mp-on-scrim);
    }
    .kind-badge mat-icon { font-size: 14px; width: 14px; height: 14px; line-height: 14px; }
    .result-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .series-matches { margin-bottom: 8px; }
    .section-heading { margin: 16px 0 12px; font-size: 16px; font-weight: 500; }
    .result-aka {
      margin-top: 2px; font-size: 12px; color: var(--mp-text-muted);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .no-results { color: var(--mp-text-muted); padding: 32px; text-align: center; }
    /* Read marks: the browse card badge (library-browse.component.ts), top-right - the kind badge has top-left, the star bottom-right. */
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgb(var(--mp-accent-strong-rgb) / 0.9); color: var(--mp-on-accent);
    }
    .badge.read { background: rgb(var(--mp-success-strong-rgb) / 0.95); }
    .read-filter { display: block; margin: 0 0 8px; }
    .load-more { display: flex; justify-content: center; margin: 24px 0 8px; }
  `],
})
export class SearchComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** The text field (two-way bound); it becomes `params().q` when the debounce fires. */
  query = '';
  /** The searched state, mirrored in the URL (1.45.0). */
  readonly params = signal<SearchParams>({ q: '', read: 'all' });
  readonly results = signal<CatalogNodeDto[]>([]);
  readonly seriesMatches = signal<SeriesMatchDto[]>([]);
  readonly totalCount = signal(0);
  readonly searched = signal(false);
  /** Keyset paging (1.45.0): whether a next page exists, and a "Load more" request is running. */
  readonly hasMore = signal(false);
  readonly loadingMore = signal(false);

  /** The read-state filter chips: the browse Show filter's choices and order. */
  readonly readOptions: { value: LibraryReadStateFilter; label: string }[] = [
    { value: 'all', label: 'All' },
    { value: 'reading', label: 'Reading' },
    { value: 'read', label: 'Read' },
    { value: 'unread', label: 'Unread' },
  ];

  /** Per-user opt-in (1.21.0): show the favorite star + boost favorited results to top. */
  readonly prominence = signal(false);

  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private nextCursor: string | null = null;
  /** Series-match anchors of the current search: a normal hit that is one shows only in "Series matches", on every page. */
  private matchedIds = new Set<string>();
  /** Bumped by every new search, so a late answer to an older one (or to its "Load more") is dropped. */
  private generation = 0;

  constructor() {
    // A link change (identify dialog, admin menu) updates the result in place (1.27.0).
    inject(MetadataStateService).changed$.pipe(takeUntilDestroyed()).subscribe(({ nodeId, hasSeriesInfo }) => {
      const patch = (n: CatalogNodeDto) => (n.id === nodeId ? { ...n, hasSeriesInfo } : n);
      this.results.update((list) => list.map(patch));
      this.seriesMatches.update((list) => list.map((m) => ({ ...m, node: patch(m.node) })));
    });

    // Favorites search prominence is a per-user preference in the library-view blob.
    this.api.getLibraryPreferences().subscribe({
      next: (prefs) => this.prominence.set(prefs.favoritesSearchProminence ?? false),
      error: () => this.prominence.set(false),
    });

    // The URL is the page's state (1.45.0): a shared link, a reload or Back opens the same search. Our own URL writes come back here
    // with the state we already hold and are ignored.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((map) => {
      const next = searchParamsFromQuery(map);
      if (sameParams(next, this.params())) return;
      if (this.searchTimer) clearTimeout(this.searchTimer);
      this.query = next.q;
      this.params.set(next);
      this.runSearch();
    });
  }

  onSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.params.update((p) => ({ ...p, q: this.query }));
      this.writeUrl();
      this.runSearch();
    }, 300);
  }

  /** A filter chip (1.45.0). Re-clicking the selected chip changes nothing (it is not deselectable). */
  setReadFilter(read: LibraryReadStateFilter | null | undefined): void {
    const value = read ?? 'all';
    if (value === this.params().read) return;
    this.params.update((p) => ({ ...p, read: value }));
    this.writeUrl();
    this.runSearch();
  }

  /** "Load more" (1.45.0): the page after the last result, same text and filter, appended. */
  loadMore(): void {
    const cursor = this.nextCursor;
    if (!cursor || this.loadingMore()) return;
    const { q, read } = this.params();
    const generation = this.generation;
    this.loadingMore.set(true);
    this.api.search(q, { cursor, readState: read }).subscribe({
      next: (response) => {
        if (generation !== this.generation) return;
        const shown = new Set(this.results().map((n) => n.id));
        const rest = response.items.filter((n) => !this.matchedIds.has(n.id) && !shown.has(n.id));
        const anchors = response.items.filter((n) => this.matchedIds.has(n.id)).length;
        // Boosted per page: favourites of a later page lead that page instead of jumping above what is already on screen.
        this.results.update((list) => [...list, ...this.boost(rest)]);
        this.totalCount.update((total) => total - anchors);
        this.takePaging(response);
        this.loadingMore.set(false);
      },
      error: () => {
        if (generation === this.generation) this.loadingMore.set(false);
      },
    });
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

  /** Material-symbol icon for a result's kind (1.12.0) - matches the browse grid's iconography. */
  kindIcon(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'folder' : 'menu_book';
  }

  /** Accessible label for the folder/archive kind badge (1.12.0). */
  kindLabel(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'Folder' : 'Archive';
  }

  /**
   * When favorites search prominence is on, boost favorited results to the top while
   * preserving the server's relative order within each group (a stable partition). When
   * off, the server order is returned unchanged.
   */
  private boost(items: CatalogNodeDto[]): CatalogNodeDto[] {
    if (!this.prominence()) return items;
    const favorited = items.filter((n) => n.isFavorite);
    const rest = items.filter((n) => !n.isFavorite);
    return [...favorited, ...rest];
  }

  private writeUrl(): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: searchParamsToQuery(this.params()), replaceUrl: true });
  }

  private takePaging(response: SearchResultsDto): void {
    this.nextCursor = response.hasMore ? (response.nextCursor ?? null) : null;
    this.hasMore.set(this.nextCursor !== null);
  }

  /** The first page for `params()`. */
  private runSearch(): void {
    const generation = ++this.generation;
    const { q, read } = this.params();
    this.nextCursor = null;
    this.hasMore.set(false);
    this.loadingMore.set(false);
    if (!q.trim()) {
      this.results.set([]);
      this.seriesMatches.set([]);
      this.totalCount.set(0);
      this.searched.set(false);
      return;
    }

    this.api.search(q, { readState: read }).subscribe({
      next: (response: SearchResultsDto) => {
        if (generation !== this.generation) return;
        // An anchor that is also a normal hit shows once, in "Series matches".
        const matches = response.seriesMatches ?? [];
        this.matchedIds = new Set(matches.map((m) => m.node.id));
        const rest = response.items.filter((n) => !this.matchedIds.has(n.id));
        this.seriesMatches.set(matches);
        this.results.set(this.boost(rest));
        this.totalCount.set(response.totalCount - (response.items.length - rest.length));
        this.takePaging(response);
        this.searched.set(true);
      },
      error: () => {
        if (generation !== this.generation) return;
        this.results.set([]);
        this.seriesMatches.set([]);
        this.totalCount.set(0);
        this.searched.set(true);
      },
    });
  }
}
