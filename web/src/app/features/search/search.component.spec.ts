import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SearchComponent } from './search.component';
import { By } from '@angular/platform-browser';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, SearchResultsDto } from '../../core/api/api-types';

/**
 * Search component tests. Covers the 1.8.1 fix: folder results render the
 * backend-provided coverUrl (resolved from the first descendant archive by
 * SortKey) instead of always falling back to the folder icon. Archives keep
 * using their own cover endpoint (the search projection sets no coverUrl for
 * archives), and folders with no readable descendant stay coverless.
 */
function makeNode(partial: Partial<CatalogNodeDto>): CatalogNodeDto {
  return {
    id: 'id',
    parentId: '',
    libraryId: 'L1',
    kind: 'Archive',
    displayName: 'x',
    availability: 'Available',
    coverUrl: null,
    childFolderCount: null,
    childArchiveCount: null,
    pageCount: null,
    readingState: null,
    lastReadPage: null,
    readerDefault: null,
    isRead: false,
    readRollup: null,
    ...partial,
  } as CatalogNodeDto;
}

describe('SearchComponent', () => {
  let fixture: ComponentFixture<SearchComponent>;

  function setup(items: CatalogNodeDto[]): void {
    const response: SearchResultsDto = {
      query: 'q',
      items,
      totalCount: items.length,
      nextCursor: null,
      hasMore: false,
    };
    const apiSpy = {
      search: vi.fn().mockReturnValue(of(response)),
      // Search now loads the favorites-prominence preference on init (1.21.0).
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
    };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
  }

  /** Type a query and fire the 300ms debounce (fake timers). */
  function runSearch(query: string): void {
    fixture.componentInstance.query = query;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('renders a cover image for a folder result that carries a backend coverUrl', () => {
    setup([
      makeNode({ id: 'f1', kind: 'Folder', displayName: 'Failure Frame', coverUrl: '/api/v1/items/v1/cover' }),
    ]);
    runSearch('failure');

    const card = fixture.nativeElement.querySelector('.result-card') as HTMLElement;
    const img = card.querySelector('.cover img') as HTMLImageElement | null;
    expect(img).not.toBeNull();
    expect(img!.getAttribute('src')).toBe('/api/v1/items/v1/cover');
  });

  it('renders only the folder icon for a folder result with no coverUrl', () => {
    setup([
      makeNode({ id: 'f2', kind: 'Folder', displayName: 'Empty Folder', coverUrl: null }),
    ]);
    runSearch('empty');

    const card = fixture.nativeElement.querySelector('.result-card') as HTMLElement;
    expect(card.querySelector('.cover img')).toBeNull();
    const icon = card.querySelector('.cover-fallback') as HTMLElement;
    expect(icon.textContent?.trim()).toBe('folder');
  });

  it('renders a cover image for an archive result from its own cover endpoint', () => {
    setup([
      makeNode({ id: 'a1', kind: 'Archive', displayName: 'Failure Frame Vol 1', coverUrl: null }),
    ]);
    runSearch('vol');

    const card = fixture.nativeElement.querySelector('.result-card') as HTMLElement;
    const img = card.querySelector('.cover img') as HTMLImageElement | null;
    expect(img).not.toBeNull();
    expect(img!.getAttribute('src')).toBe('/api/v1/items/a1/cover');
  });

  it('shows the (i) and the hover zones (cover, title) only on results with series information (1.27.0)', () => {
    setup([
      makeNode({ id: 's1', kind: 'Folder', displayName: 'Series One', hasSeriesInfo: true }),
      makeNode({ id: 's2', kind: 'Folder', displayName: 'Series Two', hasSeriesInfo: false }),
    ]);
    runSearch('series');

    const cards = fixture.nativeElement.querySelectorAll('.result-card') as NodeListOf<HTMLElement>;
    expect(cards[0].querySelector('.cover [data-testid="info-toggle"]')).not.toBeNull();
    expect(cards[1].querySelector('[data-testid="info-toggle"]')).toBeNull();
    const zones = fixture.debugElement.queryAll(By.directive(SeriesInfoHoverDirective))
      .map((d) => d.injector.get(SeriesInfoHoverDirective).nodeId() ?? '-');
    expect(zones).toEqual(['s1', 's1', '-', '-']);

    // A link change elsewhere updates the result in place.
    TestBed.inject(MetadataStateService).announce('s2', true);
    fixture.detectChanges();
    expect(cards[1].querySelector('[data-testid="info-toggle"]')).not.toBeNull();
    expect(fixture.componentInstance.results().find((n) => n.id === 's2')!.hasSeriesInfo).toBe(true);
  });
});

/**
 * Folder-vs-archive kind badge (1.12.0). Every search result carries `kind`
 * (CatalogNodeKind: Folder / Archive); a small badge distinguishes the two at a
 * glance, independent of whether a cover image is showing, and is accessible via
 * `aria-label`. Icons mirror the app's existing cover-fallback iconography
 * (`folder` / `menu_book`).
 */
describe('SearchComponent kind badge (1.12.0)', () => {
  let fixture: ComponentFixture<SearchComponent>;

  function setup(items: CatalogNodeDto[]): void {
    const response: SearchResultsDto = { query: 'q', items, totalCount: items.length, nextCursor: null, hasMore: false };
    const apiSpy = {
      search: vi.fn().mockReturnValue(of(response)),
      // Search now loads the favorites-prominence preference on init (1.21.0).
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
    };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
  }

  function runSearch(query: string): void {
    fixture.componentInstance.query = query;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('shows a "Folder" badge with the folder icon for a folder result', () => {
    setup([makeNode({ id: 'f1', kind: 'Folder', displayName: 'Some Series' })]);
    runSearch('some');

    const badge = fixture.nativeElement.querySelector('.result-card .kind-badge') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.getAttribute('aria-label')).toBe('Folder');
    expect(badge.getAttribute('role')).toBe('img');
    expect(badge.querySelector('mat-icon')?.textContent?.trim()).toBe('folder');
  });

  it('shows an "Archive" badge with the menu_book icon for an archive result', () => {
    setup([makeNode({ id: 'a1', kind: 'Archive', displayName: 'Some Volume' })]);
    runSearch('some');

    const badge = fixture.nativeElement.querySelector('.result-card .kind-badge') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.getAttribute('aria-label')).toBe('Archive');
    expect(badge.querySelector('mat-icon')?.textContent?.trim()).toBe('menu_book');
  });

  it('still shows the kind badge when the result has a real cover image', () => {
    setup([makeNode({ id: 'f2', kind: 'Folder', displayName: 'Covered Series', coverUrl: '/api/v1/items/x/cover' })]);
    runSearch('covered');

    const card = fixture.nativeElement.querySelector('.result-card') as HTMLElement;
    expect(card.querySelector('.cover img')).not.toBeNull();
    const badge = card.querySelector('.kind-badge') as HTMLElement;
    expect(badge).not.toBeNull();
    expect(badge.getAttribute('aria-label')).toBe('Folder');
  });

  it('renders one badge per result, matching each result\'s own kind', () => {
    setup([
      makeNode({ id: 'f3', kind: 'Folder', displayName: 'A Folder' }),
      makeNode({ id: 'a3', kind: 'Archive', displayName: 'An Archive' }),
    ]);
    runSearch('a');

    const badges = fixture.nativeElement.querySelectorAll('.kind-badge') as NodeListOf<HTMLElement>;
    expect(badges.length).toBe(2);
    expect(Array.from(badges).map((b) => b.getAttribute('aria-label'))).toEqual(['Folder', 'Archive']);
  });
});

/**
 * Favorites search prominence (1.21.0): the per-user opt-in. When ON, favorited
 * results get the star (badge + interactive toggle) AND are boosted to the top; when
 * OFF, results render normally with no star and no reordering.
 */
describe('SearchComponent favorites prominence (1.21.0)', () => {
  let fixture: ComponentFixture<SearchComponent>;

  function setupWith(prominence: boolean, items: CatalogNodeDto[]): void {
    const response: SearchResultsDto = { query: 'q', items, totalCount: items.length, nextCursor: null, hasMore: false };
    const apiSpy = {
      search: vi.fn().mockReturnValue(of(response)),
      getLibraryPreferences: vi.fn().mockReturnValue(
        of({ viewMode: 'card', density: 'comfortable', sort: 'name', favoritesSearchProminence: prominence }),
      ),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: apiSpy }],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
  }

  function runSearch(query: string): void {
    fixture.componentInstance.query = query;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('boosts favorited results to the top and shows the star when prominence is ON', () => {
    setupWith(true, [
      makeNode({ id: 'plain', displayName: 'Plain', isFavorite: false }),
      makeNode({ id: 'fav', displayName: 'Favorited', isFavorite: true }),
    ]);
    runSearch('x');

    // Favorited result is boosted ahead of the earlier-listed plain one.
    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['fav', 'plain']);
    // The star toggle renders on results when prominence is on.
    expect(fixture.nativeElement.querySelector('app-star-toggle')).not.toBeNull();
  });

  it('renders normally (no star, no reordering) when prominence is OFF', () => {
    setupWith(false, [
      makeNode({ id: 'plain', displayName: 'Plain', isFavorite: false }),
      makeNode({ id: 'fav', displayName: 'Favorited', isFavorite: true }),
    ]);
    runSearch('x');

    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['plain', 'fav']);
    expect(fixture.nativeElement.querySelector('app-star-toggle')).toBeNull();
  });
});

/** Alt-title "Series matches" row (1.26.0). */
describe('SearchComponent series matches (1.26.0)', () => {
  let fixture: ComponentFixture<SearchComponent>;

  function setup(response: SearchResultsDto): void {
    const apiSpy = {
      search: vi.fn().mockReturnValue(of(response)),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
    };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: apiSpy }],
    });
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    fixture.componentInstance.query = 'delicious';
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('renders a Series matches row with an "aka" caption above the normal results', () => {
    setup({
      query: 'delicious',
      items: [makeNode({ id: 'n1', displayName: 'Delicious Sweets' })],
      totalCount: 1,
      nextCursor: null,
      hasMore: false,
      seriesMatches: [{ node: makeNode({ id: 's1', kind: 'Folder', displayName: 'Dungeon Folder' }), matchedTitle: 'Delicious in Dungeon' }],
    });

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.series-matches .section-heading')?.textContent?.trim()).toBe('Series matches');
    const aka = el.querySelector('.series-matches .result-aka') as HTMLElement;
    expect(aka.textContent?.trim()).toBe('aka Delicious in Dungeon');
    expect(el.querySelectorAll('.result-card').length).toBe(2);
    expect(el.querySelectorAll('.result-aka').length).toBe(1);
  });

  it('shows an anchor that is also a normal hit only once, in Series matches', () => {
    const anchor = makeNode({ id: 's1', kind: 'Folder', displayName: 'Delicious Folder' });
    setup({
      query: 'delicious',
      items: [anchor, makeNode({ id: 'n1', displayName: 'Delicious Sweets' })],
      totalCount: 2,
      nextCursor: null,
      hasMore: false,
      seriesMatches: [{ node: anchor, matchedTitle: 'Delicious in Dungeon' }],
    });

    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['n1']);
    expect(fixture.componentInstance.totalCount()).toBe(1);
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.result-card').length).toBe(2);
  });

  it('shows the row alone (no "No results") when only series matches exist, and no row when absent', () => {
    setup({
      query: 'delicious',
      items: [],
      totalCount: 0,
      nextCursor: null,
      hasMore: false,
      seriesMatches: [{ node: makeNode({ id: 's1', kind: 'Folder', displayName: 'Dungeon Folder' }), matchedTitle: 'Delicious in Dungeon' }],
    });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.no-results')).toBeNull();
    expect(el.querySelector('.series-matches')).not.toBeNull();
  });

  it('renders no Series matches row when the response has none', () => {
    setup({ query: 'x', items: [makeNode({ id: 'n1' })], totalCount: 1, nextCursor: null, hasMore: false });
    expect((fixture.nativeElement as HTMLElement).querySelector('.series-matches')).toBeNull();
  });
});
