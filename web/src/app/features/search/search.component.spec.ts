import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, Subject } from 'rxjs';

import { SearchComponent, searchParamsFromQuery, searchParamsToQuery } from './search.component';
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

/**
 * 1.45.0 phase K4: the read mark on each result card (as browse), the Read / Reading / Unread filter chip row (sent to the server, which
 * filters before paging), keyset "Load more", and the URL parameters `q` / `read` (one params model).
 */
describe('SearchComponent read state, filter, Load more and URL (1.45.0)', () => {
  let fixture: ComponentFixture<SearchComponent>;
  let search: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.spyOn>;

  function page(items: CatalogNodeDto[], totalCount: number, nextCursor: string | null): SearchResultsDto {
    return { query: 'q', items, totalCount, nextCursor, hasMore: nextCursor !== null };
  }

  function setup(...responses: SearchResultsDto[]): void {
    search = vi.fn();
    for (const r of responses) search.mockReturnValueOnce(of(r));
    search.mockReturnValue(of(page([], 0, null)));
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        {
          provide: ApiService,
          useValue: {
            search,
            getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
          },
        },
      ],
    });
    navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
  }

  function type(query: string): void {
    fixture.componentInstance.query = query;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  /** A filter chip's own button (mat-chip-option's primary action, `role="option"`). */
  const chip = (label: string) =>
    Array.from(el().querySelectorAll('.read-filter mat-chip-option')).find((c) => c.textContent?.trim() === label)
      ?.querySelector('button') as HTMLButtonElement;

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('marks read and in-progress archives and read / partly read folders as browse cards do', () => {
    setup(page([
      makeNode({ id: 'a1', displayName: 'Read One', isRead: true }),
      makeNode({ id: 'a2', displayName: 'Reading One', readingState: 'InProgress', lastReadPage: 3 }),
      makeNode({ id: 'a3', displayName: 'Unread One' }),
      makeNode({ id: 'f1', kind: 'Folder', displayName: 'Read Folder', readRollup: 'Read' }),
      makeNode({ id: 'f2', kind: 'Folder', displayName: 'Partly Folder', readRollup: 'Reading' }),
      makeNode({ id: 'f3', kind: 'Folder', displayName: 'Unread Folder', readRollup: 'Unread' }),
    ], 6, null));
    type('one');

    const badges = Array.from(el().querySelectorAll('.result-card')).map((c) => c.querySelector('.badge')?.textContent?.trim() ?? '');
    expect(badges).toEqual(['✓ Read', 'Reading', '', '✓ Read', 'Reading', '']);
  });

  it('shows the chip row once there is search text and sends the chosen filter, keeping the text', () => {
    setup(page([makeNode({ id: 'a1' })], 1, null), page([makeNode({ id: 'a2' })], 1, null));
    expect(el().querySelector('.read-filter')).toBeNull();
    type('alpha');

    expect(Array.from(el().querySelectorAll('.read-filter mat-chip-option')).map((c) => c.textContent?.trim()))
      .toEqual(['All', 'Reading', 'Read', 'Unread']);
    expect(search).toHaveBeenLastCalledWith('alpha', { readState: 'all' });

    chip('Unread').click();
    fixture.detectChanges();

    expect(search).toHaveBeenLastCalledWith('alpha', { readState: 'unread' });
    expect(fixture.componentInstance.params()).toEqual({ q: 'alpha', read: 'unread' });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: 'unread' }, replaceUrl: true }));
    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['a2']);
  });

  it('says "No results with this filter." when a filter leaves nothing, and keeps the chips to switch back', () => {
    setup(page([makeNode({ id: 'a1' })], 1, null), page([], 0, null));
    type('alpha');
    chip('Read').click();
    fixture.detectChanges();

    expect(el().querySelector('.no-results')?.textContent?.trim()).toBe('No results with this filter.');
    expect(chip('All')).toBeTruthy();
  });

  it('loads the next page with the cursor and the same filter, appends it, and hides Load more on the last page', () => {
    setup(
      page([makeNode({ id: 'a1' }), makeNode({ id: 'a2' })], 3, 'k:2:1a2'),
      page([makeNode({ id: 'a3' })], 3, null),
    );
    fixture.componentInstance.setReadFilter('unread');
    type('alpha');

    const button = () => Array.from(el().querySelectorAll('.load-more button')).find((b) => b.textContent?.includes('Load more')) as
      HTMLButtonElement | undefined;
    expect(el().querySelectorAll('.result-card').length).toBe(2);
    expect(button()).toBeTruthy();

    button()!.click();
    fixture.detectChanges();

    expect(search).toHaveBeenLastCalledWith('alpha', { cursor: 'k:2:1a2', readState: 'unread' });
    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['a1', 'a2', 'a3']);
    expect(el().querySelector('.result-count')?.textContent?.trim()).toBe('3 results');
    expect(button()).toBeUndefined();
  });

  it('drops a Load more answer that arrives after a new search started', () => {
    const late = new Subject<SearchResultsDto>();
    setup(page([makeNode({ id: 'a1' })], 2, 'k:1:1a1'));
    search.mockReturnValueOnce(late);
    type('alpha');
    fixture.componentInstance.loadMore();
    type('beta');

    late.next(page([makeNode({ id: 'a2' })], 2, null));
    fixture.detectChanges();

    expect(search).toHaveBeenLastCalledWith('beta', { readState: 'all' });
    expect(fixture.componentInstance.results()).toEqual([]);
    expect(fixture.componentInstance.loadingMore()).toBe(false);
  });

  it('writes the text to the URL and leaves defaults out', () => {
    setup(page([makeNode({ id: 'a1' })], 1, null));
    type('alpha');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: null } }));
    type('');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: null, read: null } }));
  });
});

describe('SearchComponent opened from a URL (1.45.0)', () => {
  it('runs the search in the URL at once, with its filter, and shows it in the field and the chips', async () => {
    const search = vi.fn().mockReturnValue(of({ query: 'Alpha', items: [makeNode({ id: 'a1' })], totalCount: 1, nextCursor: null, hasMore: false }));
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: { search, getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card' })) } },
      ],
    });
    await TestBed.inject(Router).navigateByUrl('/?q=Alpha&read=unread');
    const fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();

    expect(search).toHaveBeenCalledTimes(1);
    expect(search).toHaveBeenCalledWith('Alpha', { readState: 'unread' });
    expect(fixture.componentInstance.query).toBe('Alpha');
    const selected = (fixture.nativeElement as HTMLElement).querySelector('.read-filter button[role="option"][aria-selected="true"]');
    expect(selected?.textContent?.trim()).toBe('Unread');
  });
});

describe('search URL parameters (1.45.0)', () => {
  it('reads q and read, and ignores an unknown read value', () => {
    expect(searchParamsFromQuery(convertToParamMap({ q: 'x', read: 'reading' }))).toEqual({ q: 'x', read: 'reading' });
    expect(searchParamsFromQuery(convertToParamMap({ q: 'x', read: 'bogus' }))).toEqual({ q: 'x', read: 'all' });
    expect(searchParamsFromQuery(convertToParamMap({}))).toEqual({ q: '', read: 'all' });
  });

  it('round-trips and leaves the defaults out', () => {
    expect(searchParamsToQuery({ q: 'x', read: 'unread' })).toEqual({ q: 'x', read: 'unread' });
    expect(searchParamsToQuery({ q: '  ', read: 'all' })).toEqual({ q: null, read: null });
  });
});
