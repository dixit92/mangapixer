import { Component, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, ParamMap, Params, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatChipListbox, MatChipsModule } from '@angular/material/chips';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { CatalogNodeDto, LibraryReadStateFilter, SearchResultsDto, SeriesMatchDto } from '../../core/api/api-types';
import { bucketLabel, isFilterParam } from '../metadata/info/info-labels';
import { FacetFilterDto, FacetSearchFields, FacetWorkDto, FacetWorkLocationDto } from '../metadata/info/info-types';

/**
 * The search page's state as it appears in the URL (1.45.0): `q` (the search text), `read` (the read-state filter; absent = all) and
 * `f` (lane P: tag filters `<bucket>:<value id>`, repeatable, all must match). ONE model read from and written to the query string.
 */
export interface SearchParams {
  q: string;
  read: LibraryReadStateFilter;
  f: string[];
}

const READ_FILTERS: readonly LibraryReadStateFilter[] = ['all', 'reading', 'read', 'unread'];

/** Reads the page state from the URL; unknown values fall back to the defaults, a malformed or repeated `f` is dropped. */
export function searchParamsFromQuery(query: ParamMap): SearchParams {
  const read = query.get('read') as LibraryReadStateFilter | null;
  const f = [...new Set(query.getAll('f').filter(isFilterParam))];
  return { q: query.get('q') ?? '', read: read && READ_FILTERS.includes(read) ? read : 'all', f };
}

/** The query string for a page state: defaults are left out (an empty search is a bare `/search`). */
export function searchParamsToQuery(params: SearchParams): Params {
  return {
    q: params.q.trim() ? params.q : null,
    read: params.read !== 'all' ? params.read : null,
    f: params.f.length > 0 ? params.f : null,
  };
}

function sameParams(a: SearchParams, b: SearchParams): boolean {
  return a.q === b.q && a.read === b.read && a.f.length === b.f.length && a.f.every((v, i) => v === b.f[i]);
}

/** The search answer with lane Q's tag-search fields (works + resolved filters). */
type SearchAnswer = SearchResultsDto & FacetSearchFields;

/** A filter chip in the field: the `f` value, and its name once the server resolved it. */
interface FilterChip {
  param: string;
  label: string;
  /** False when the server did not know the value (or the viewer may not see it): the chip says so and can be removed. */
  known: boolean;
}

/**
 * Search component. Uses the FTS5 trigram search via the API. 1.45.0 (lane P): the tag view - a chip of the Info panel opens this page
 * with `f=<bucket>:<value id>`; the filters show as removable chips in the field (they scroll sideways in their own row on a phone),
 * typed field syntax (`creator:"..."`) passes through `q`, and the results are WORK cards (cover, title, where the work lives as a
 * breadcrumb of display names, "2 locations" for a split series) with the same keyset "Load more" and read filter. Without a filter the
 * page is the folder-name search it was.
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
    <mat-form-field appearance="outline" class="search-field" [class.has-filters]="filterChips().length > 0">
      <mat-label>Search libraries</mat-label>
      <!-- Tag filters (1.45.0): removable chips in the field, in their own row that scrolls sideways when it is full. -->
      <mat-chip-grid #filterGrid class="filter-chips" aria-label="Tag filters" data-testid="search-filters">
        @for (chip of filterChips(); track chip.param) {
          <mat-chip-row (removed)="removeFilter(chip.param)" [class.unknown]="!chip.known" data-testid="search-filter-chip">
            <span class="filter-label">{{ chip.label }}</span>
            <button matChipRemove type="button" [attr.aria-label]="'Remove filter ' + chip.label"><mat-icon>cancel</mat-icon></button>
          </mat-chip-row>
        }
        <input [matChipInputFor]="filterGrid" [(ngModel)]="query" (ngModelChange)="onSearch()"
               [placeholder]="filterChips().length > 0 ? 'Add search text...' : 'Enter search text...'" aria-label="Search text"
               data-testid="search-input">
      </mat-chip-grid>
      <mat-icon matSuffix>search</mat-icon>
    </mat-form-field>

    <!-- Read-state filter (1.45.0): the browse Show filter's choices as one chip row over the results, applied on the server before
         paging (the count and "Load more" stay right). Shown once there is search text, so a filter with no hits can be switched back. -->
    @if (active()) {
      <mat-chip-listbox class="read-filter" aria-label="Show" [value]="params().read" (change)="onReadChip($event.value)"
                        hideSingleSelectionIndicator>
        @for (opt of readOptions; track opt.value) {
          <mat-chip-option [value]="opt.value">{{ opt.label }}</mat-chip-option>
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

    @if (works().length > 0) {
      <p class="result-count">{{ totalCount() }} work{{ totalCount() === 1 ? '' : 's' }}</p>
      <div class="results-grid" data-testid="work-results">
        @for (work of works(); track work.workId) {
          <div class="result-card work-card" data-testid="work-card">
            @if (work.locations[0]; as first) {
              <a [routerLink]="locationLink(first)" class="work-link">
                <div class="cover">
                  @if (work.coverUrl) {
                    <img appCover [src]="work.coverUrl" alt="" loading="lazy">
                  }
                  <mat-icon class="cover-fallback">{{ first.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>
                </div>
                <div class="result-title" [title]="work.title">{{ work.title }}</div>
              </a>
              <nav class="crumbs" [attr.aria-label]="'Where ' + work.title + ' is'">
                <span class="crumb-line" [title]="trailText(first)">{{ trailText(first) }}</span>
              </nav>
              @if (work.locationCount > 1) {
                <button type="button" class="locations-toggle" (click)="toggleLocations(work.workId)"
                        [attr.aria-expanded]="expanded().has(work.workId)" data-testid="work-locations-toggle">
                  {{ work.locationCount }} locations
                  <mat-icon>{{ expanded().has(work.workId) ? 'expand_less' : 'expand_more' }}</mat-icon>
                </button>
                @if (expanded().has(work.workId)) {
                  <ul class="locations" data-testid="work-locations">
                    @for (loc of work.locations; track loc.nodeId) {
                      <li><a [routerLink]="locationLink(loc)" [title]="trailText(loc)">{{ trailText(loc) }}</a></li>
                    }
                  </ul>
                }
              }
            }
          </div>
        }
      </div>
      @if (hasMore()) {
        <div class="load-more">
          <button mat-stroked-button type="button" (click)="loadMore()" [disabled]="loadingMore()">
            {{ loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        </div>
      }
    } @else if (results().length > 0) {
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
      <p class="no-results">{{ noResultsText() }}</p>
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
    /* The filter chips get their own row inside the field; a full row scrolls sideways instead of growing the field (D1 2.6). */
    .filter-chips { display: block; width: 100%; min-width: 0; }
    .filter-chips input { width: 100%; min-width: 0; }
    :host ::ng-deep .filter-chips .mdc-evolution-chip-set__chips { flex-wrap: wrap; min-width: 0; max-width: 100%; }
    :host ::ng-deep .has-filters .filter-chips .mdc-evolution-chip-set__chips {
      flex-wrap: nowrap; overflow-x: auto; overscroll-behavior-x: contain; scrollbar-width: thin;
    }
    :host ::ng-deep .filter-chips .mat-mdc-chip-row { flex: none; max-width: min(260px, 80vw); }
    .filter-label { display: inline-block; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: middle; }
    .unknown .filter-label { color: var(--mp-text-muted); font-style: italic; }
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
    .work-card { min-width: 0; }
    .work-link { display: block; text-decoration: none; color: inherit; }
    .crumbs { margin-top: 2px; min-width: 0; }
    .crumb-line {
      display: block; font-size: 12px; color: var(--mp-text-muted);
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .locations-toggle {
      display: inline-flex; align-items: center; gap: 2px; margin-top: 2px; padding: 4px 0; min-height: 32px;
      border: none; background: none; color: var(--mp-accent); font: inherit; font-size: 12px; cursor: pointer;
    }
    .locations-toggle mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .locations { margin: 0; padding: 0; list-style: none; font-size: 12px; }
    .locations li { margin: 2px 0; min-width: 0; }
    .locations a { display: block; color: var(--mp-accent); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding: 4px 0; }
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
  readonly params = signal<SearchParams>({ q: '', read: 'all', f: [] });
  readonly results = signal<CatalogNodeDto[]>([]);
  /** With a tag filter (1.45.0): the matching works, instead of `results`. */
  readonly works = signal<FacetWorkDto[]>([]);
  /** The filters as the server named them in its last answer. */
  private readonly resolvedFilters = signal<FacetFilterDto[]>([]);
  /** Works whose locations list is open. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
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

  /** There is something to search for: text, a tag filter, or both. */
  readonly active = computed(() => this.params().q.trim().length > 0 || this.params().f.length > 0);

  /** The field's filter chips, in URL order, named once the server has answered. */
  readonly filterChips = computed<FilterChip[]>(() => {
    const byParam = new Map(this.resolvedFilters().map((r) => [r.param, r]));
    return this.params().f.map((param) => {
      const r = byParam.get(param);
      if (r) return { param, label: `${bucketLabel(r.bucket)}: ${r.display}`, known: true };
      return { param, label: this.searched() ? 'Unknown tag' : '…', known: !this.searched() };
    });
  });

  readonly noResultsText = computed(() => {
    if (this.params().f.length > 0) return 'No works with these tags.';
    return this.params().read === 'all' ? 'No results found.' : 'No results with this filter.';
  });

  /** Per-user opt-in (1.21.0): show the favorite star + boost favorited results to top. */
  readonly prominence = signal(false);

  private readonly readFilterBox = viewChild(MatChipListbox);
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

  /**
   * A filter chip was clicked (1.45.0). Clicking the selected chip again would leave the row with no choice: it is selected again
   * instead (one choice is always shown - and announced - as selected).
   */
  onReadChip(value: LibraryReadStateFilter | null | undefined): void {
    if (value == null) {
      const box = this.readFilterBox();
      if (box) box.value = this.params().read;
      return;
    }
    this.setReadFilter(value);
  }

  /** Removes one tag filter (its chip's remove button, or Backspace in an empty field) and searches again. */
  removeFilter(param: string): void {
    if (!this.params().f.includes(param)) return;
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.params.update((p) => ({ ...p, q: this.query, f: p.f.filter((v) => v !== param) }));
    this.writeUrl();
    this.runSearch();
  }

  toggleLocations(workId: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (next.has(workId)) next.delete(workId);
      else next.add(workId);
      return next;
    });
  }

  /** Where a work's location opens: a folder in browse, an archive (a one-shot) in the reader. */
  locationLink(loc: FacetWorkLocationDto): string[] {
    return loc.kind === 'Folder' ? ['/libraries', loc.libraryId, 'browse', loc.nodeId] : ['/reader', loc.nodeId];
  }

  /** "Library › Folder › Work": the location's place as display names (never a path). */
  trailText(loc: FacetWorkLocationDto): string {
    return [...loc.trail.map((t) => t.displayName), loc.displayName].join(' › ');
  }

  /** Sets the read-state filter and runs the search again from its first page. */
  setReadFilter(read: LibraryReadStateFilter): void {
    if (read === this.params().read) return;
    this.params.update((p) => ({ ...p, read }));
    this.writeUrl();
    this.runSearch();
  }

  /** "Load more" (1.45.0): the page after the last result, same text and filter, appended. */
  loadMore(): void {
    const cursor = this.nextCursor;
    if (!cursor || this.loadingMore()) return;
    const { q, read, f } = this.params();
    const generation = this.generation;
    this.loadingMore.set(true);
    this.api.search(q, { cursor, readState: read, ...filterOption(f) }).subscribe({
      next: (answer) => {
        if (generation !== this.generation) return;
        const response = answer as SearchAnswer;
        if (response.works) {
          const shownWorks = new Set(this.works().map((w) => w.workId));
          this.works.update((list) => [...list, ...response.works!.filter((w) => !shownWorks.has(w.workId))]);
          this.takePaging(response);
          this.loadingMore.set(false);
          return;
        }
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

  /**
   * The filters as the server resolved them. A merged creator's old value answers with its target and `redirectedFrom`
   * (D1 2.2): the URL then names the target, so a bookmark keeps working and the field shows the creator it opens.
   */
  private takeFilters(filters: FacetFilterDto[]): void {
    this.resolvedFilters.set(filters);
    const moved = filters.filter((r) => r.redirectedFrom && r.redirectedFrom !== r.param && this.params().f.includes(r.redirectedFrom));
    if (moved.length === 0) return;
    const to = new Map(moved.map((r) => [r.redirectedFrom!, r.param]));
    this.params.update((p) => ({ ...p, f: [...new Set(p.f.map((v) => to.get(v) ?? v))] }));
    this.writeUrl();
  }

  private takePaging(response: SearchResultsDto): void {
    this.nextCursor = response.hasMore ? (response.nextCursor ?? null) : null;
    this.hasMore.set(this.nextCursor !== null);
  }

  /** The first page for `params()`. */
  private runSearch(): void {
    const generation = ++this.generation;
    const { q, read, f } = this.params();
    this.nextCursor = null;
    this.hasMore.set(false);
    this.loadingMore.set(false);
    this.expanded.set(new Set());
    if (!q.trim() && f.length === 0) {
      this.results.set([]);
      this.works.set([]);
      this.seriesMatches.set([]);
      this.resolvedFilters.set([]);
      this.totalCount.set(0);
      this.searched.set(false);
      return;
    }
    // Until the answer names the filters, their chips show a placeholder (not "Unknown tag").
    this.searched.set(false);

    this.api.search(q, { readState: read, ...filterOption(f) }).subscribe({
      next: (answer) => {
        if (generation !== this.generation) return;
        const response = answer as SearchAnswer;
        this.takeFilters(response.filters ?? []);
        if (response.works) {
          this.works.set(response.works);
          this.results.set([]);
          this.seriesMatches.set([]);
          this.matchedIds = new Set();
          this.totalCount.set(response.totalCount);
          this.takePaging(response);
          this.searched.set(true);
          return;
        }
        this.works.set([]);
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
        this.works.set([]);
        this.seriesMatches.set([]);
        this.totalCount.set(0);
        this.searched.set(true);
      },
    });
  }
}

/** The `filters` search option, only when there are filters (a plain search sends exactly what it sent before). */
function filterOption(f: readonly string[]): { filters?: readonly string[] } {
  return f.length > 0 ? { filters: f } : {};
}
