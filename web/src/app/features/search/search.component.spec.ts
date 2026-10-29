import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { convertToParamMap, provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BreakpointObserver } from '@angular/cdk/layout';
import { of, Subject, throwError } from 'rxjs';

import { SearchComponent, searchParamsFromQuery, searchParamsToQuery } from './search.component';
import { By } from '@angular/platform-browser';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, SearchResultsDto, SearchSuggestDto } from '../../core/api/api-types';
import { FacetLocationDto, FacetSearchFields, FacetWorkDto, SearchFilterDto } from '../metadata/info/info-types';

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
 * results get the star (badge + interactive toggle); when OFF, results render normally with no star.
 * 1.49.0: the SERVER lifts favourites to the top across pages - the page keeps the server's order either way.
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

  it('keeps the server order (the server lifts favourites, 1.49.0) and shows the star when prominence is ON', () => {
    setupWith(true, [
      makeNode({ id: 'fav', displayName: 'Favorited', isFavorite: true }),
      makeNode({ id: 'plain', displayName: 'Plain', isFavorite: false }),
      makeNode({ id: 'fav2', displayName: 'Favorited later', isFavorite: true }),
    ]);
    runSearch('x');

    // No client-side reordering any more: a page-local boost would contradict the server's order across pages.
    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['fav', 'plain', 'fav2']);
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
    expect(fixture.componentInstance.params()).toEqual({ q: 'alpha', read: 'unread', f: [] });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: 'unread', f: null }, replaceUrl: true }));
    expect(fixture.componentInstance.results().map((n) => n.id)).toEqual(['a2']);
  });

  it('keeps the selected chip selected when it is clicked again, without a new search', () => {
    setup(page([makeNode({ id: 'a1' })], 1, null));
    type('alpha');
    const calls = search.mock.calls.length;

    chip('All').click();
    fixture.detectChanges();

    expect(search.mock.calls.length).toBe(calls);
    expect(chip('All').getAttribute('aria-selected')).toBe('true');
    expect(fixture.componentInstance.params().read).toBe('all');
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
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: null, f: null } }));
    type('');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: null, read: null, f: null } }));
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
    // The listbox applies its value to the chips once they are created (after the results arrived).
    await fixture.whenStable();
    fixture.detectChanges();

    expect(search).toHaveBeenCalledTimes(1);
    expect(search).toHaveBeenCalledWith('Alpha', { readState: 'unread' });
    expect(fixture.componentInstance.query).toBe('Alpha');
    const selected = (fixture.nativeElement as HTMLElement).querySelector('.read-filter mat-chip-option.mat-mdc-chip-selected');
    expect(selected?.textContent?.trim()).toBe('Unread');
  });
});

describe('search URL parameters (1.45.0)', () => {
  it('reads q and read, and ignores an unknown read value', () => {
    expect(searchParamsFromQuery(convertToParamMap({ q: 'x', read: 'reading' }))).toEqual({ q: 'x', read: 'reading', f: [] });
    expect(searchParamsFromQuery(convertToParamMap({ q: 'x', read: 'bogus' }))).toEqual({ q: 'x', read: 'all', f: [] });
    expect(searchParamsFromQuery(convertToParamMap({}))).toEqual({ q: '', read: 'all', f: [] });
  });

  it('1.49.0: keeps exclusions and any-of filters from the URL', () => {
    const p = searchParamsFromQuery(convertToParamMap({ f: ['-genre:fv_1', 'tag:fv_2|fv_3', '--x:fv_4', 'tag:fv_5|'] }));
    expect(p.f).toEqual(['-genre:fv_1', 'tag:fv_2|fv_3']);
  });

  it('1.45.0: reads repeated tag filters in order, dropping malformed and repeated ones', () => {
    const map = convertToParamMap({ f: ['creator:fv_1', 'bogus', 'genre:fv_2', 'creator:fv_1', 'year:'] });
    expect(searchParamsFromQuery(map)).toEqual({ q: '', read: 'all', f: ['creator:fv_1', 'genre:fv_2'] });
  });

  it('round-trips and leaves the defaults out', () => {
    expect(searchParamsToQuery({ q: 'x', read: 'unread', f: [] })).toEqual({ q: 'x', read: 'unread', f: null });
    expect(searchParamsToQuery({ q: '  ', read: 'all', f: [] })).toEqual({ q: null, read: null, f: null });
    expect(searchParamsToQuery({ q: '', read: 'all', f: ['creator:fv_1', 'genre:fv_2'] })).toEqual({ q: null, read: null, f: ['creator:fv_1', 'genre:fv_2'] });
  });
});

/** 1.45.0 lane P: the tag view on lane Q's shapes - `f` filters as chips in the field, WORK cards, redirects of merged creators, Load more. */
describe('SearchComponent tag filters and work cards (1.45.0)', () => {
  type Answer = SearchResultsDto & FacetSearchFields;

  const location = (o: Partial<FacetLocationDto> = {}): FacetLocationDto => ({
    nodeId: 'f1', libraryId: 'L1', libraryName: 'Manga', trail: [{ id: 'f1', displayName: 'Series A' }], ...o,
  });
  const work = (o: Partial<FacetWorkDto> = {}): FacetWorkDto => ({
    title: 'Series A', node: makeNode({ id: 'f1', kind: 'Folder', displayName: 'Series A', coverUrl: '/api/v1/items/a1/cover' }),
    coverUrl: '/api/v1/items/a1/cover', locations: [location()], locationCount: 1, ...o,
  });
  const filter = (o: Partial<SearchFilterDto> = {}): SearchFilterDto => ({
    bucket: 'Creators', source: 'Chip', id: 'fv_1', display: 'Synthetic Author', matched: true, valueIds: ['fv_1'], ...o,
  });
  const answer = (works: FacetWorkDto[], filters: SearchFilterDto[], o: Partial<Answer> = {}): Answer => ({
    query: '', items: [], totalCount: works.length, nextCursor: null, hasMore: false, works, filters, ...o,
  });

  async function open(url: string, ...answers: Answer[]) {
    const search = vi.fn();
    for (const a of answers) search.mockReturnValueOnce(of(a));
    search.mockReturnValue(of(answer([], [])));
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: { search, getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card' })) } },
      ],
    });
    const router = TestBed.inject(Router);
    await router.navigateByUrl(url);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, search, navigate };
  }

  const chipLabels = (el: HTMLElement) =>
    Array.from(el.querySelectorAll('[data-testid="search-filter-chip"] .filter-label')).map((c) => c.textContent!.trim());

  it('searches the filters in the URL (AND) without text, names them in the field and lists WORK cards', async () => {
    const { el, search } = await open('/?f=creator:fv_1&f=genre:fv_2', answer(
      [work(), work({ title: 'One-shot B', coverUrl: null,
        node: makeNode({ id: 'a9', kind: 'Archive', displayName: 'One-shot B.cbz', isRead: true }),
        locations: [location({ nodeId: 'a9', libraryId: 'L2', libraryName: 'Manga 2', trail: [{ id: 'f9', displayName: 'One Shots' }, { id: 'a9', displayName: 'One-shot B.cbz' }] })] })],
      [filter(), filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] })],
    ));
    expect(search).toHaveBeenCalledWith('', { readState: 'all', filters: ['creator:fv_1', 'genre:fv_2'] });
    expect(chipLabels(el)).toEqual(['Creators: Synthetic Author', 'Genres: Comedy']);
    expect(el.querySelector('.result-count')!.textContent!.trim()).toBe('2 works');
    const cards = Array.from(el.querySelectorAll('[data-testid="work-card"]'));
    expect(cards.map((c) => c.querySelector('.result-title')!.textContent)).toEqual(['Series A', 'One-shot B']);
    // A folder opens in browse, a one-shot archive in the reader (its own cover endpoint); the breadcrumb is display names only.
    expect(cards[0].querySelector('a.work-link')!.getAttribute('href')).toBe('/libraries/L1/browse/f1');
    expect(cards[1].querySelector('a.work-link')!.getAttribute('href')).toBe('/reader/a9');
    expect(cards[1].querySelector('img')!.getAttribute('src')).toBe('/api/v1/items/a9/cover');
    expect(cards[1].querySelector('.badge')!.textContent!.trim()).toBe('✓ Read');
    expect(cards[1].querySelector('.crumb-line')!.textContent).toBe('Manga 2 › One Shots › One-shot B.cbz');
    // 1.47.0: the read-state chips are offered in the tag view too (the server applies them to works).
    expect(el.querySelector('.read-filter')).not.toBeNull();
  });

  it('1.47.0: the read chips filter the works - the read state goes to the server with the tags, and into the URL', async () => {
    const { el, fixture, search, navigate } = await open('/?f=genre:fv_2&read=read', answer(
      [work()], [filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] })],
    ), answer([], [filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] })]));
    expect(search).toHaveBeenCalledWith('', { readState: 'read', filters: ['genre:fv_2'] });
    const selected = el.querySelector('.read-filter mat-chip-option.mat-mdc-chip-selected');
    expect(selected!.textContent!.trim()).toBe('Read');
    const unread = Array.from(el.querySelectorAll<HTMLElement>('.read-filter mat-chip-option')).find((c) => c.textContent?.trim() === 'Unread')!;
    unread.querySelector('button')!.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('', { readState: 'unread', filters: ['genre:fv_2'] });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: null, read: 'unread', f: ['genre:fv_2'] } }));
    // The filters stay as chips; nothing matched this read state.
    expect(chipLabels(el)).toEqual(['Genres: Comedy']);
    expect(el.querySelector('.no-results')!.textContent!.trim()).toBe('No works with these tags and this filter.');
    expect(el.querySelector('.read-filter')).not.toBeNull();
  });

  it('expands "N locations" for a split series, with the ones not listed counted', async () => {
    const { el, fixture } = await open('/?f=creator:fv_1', answer(
      [work({ locations: [location(), location({ nodeId: 'f2', libraryId: 'L2', libraryName: 'Manga 2', trail: [{ id: 'f2', displayName: 'Series A 101-200' }] })], locationCount: 7 })],
      [filter()],
    ));
    const toggle = el.querySelector<HTMLButtonElement>('[data-testid="work-locations-toggle"]')!;
    expect(toggle.textContent).toContain('7 locations');
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    fixture.detectChanges();
    const links = Array.from(el.querySelectorAll('[data-testid="work-locations"] a'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['/libraries/L1/browse/f1', '/libraries/L2/browse/f2']);
    expect(links[1].textContent).toBe('Manga 2 › Series A 101-200');
    expect(el.querySelector('[data-testid="work-locations"] .more-places')!.textContent).toBe('and 5 more');
  });

  it('removes a filter chip and searches again without it, keeping the text', async () => {
    const { el, fixture, search, navigate } = await open('/?q=alpha&f=creator:fv_1&f=genre:fv_2', answer(
      [work()], [filter(), filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] })],
    ));
    const remove = el.querySelectorAll<HTMLButtonElement>('[data-testid="search-filter-chip"] button[matChipRemove]')[0];
    expect(remove.getAttribute('aria-label')).toBe('Remove filter Creators: Synthetic Author');
    remove.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('alpha', { readState: 'all', filters: ['genre:fv_2'] });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: null, f: ['genre:fv_2'] } }));
  });

  it('goes back to the folder-name search (and its read chips) when the last filter is removed', async () => {
    const { el, fixture, search } = await open('/?q=alpha&f=creator:fv_1', answer([work()], [filter()]));
    el.querySelector<HTMLButtonElement>('[data-testid="search-filter-chip"] button[matChipRemove]')!.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('alpha', { readState: 'all' });
    expect(fixture.componentInstance.works()).toEqual([]);
    expect(el.querySelector('.read-filter')).not.toBeNull();
  });

  it('follows a merged creator: the URL names the creator it was merged into, without a second search', async () => {
    const { el, search, navigate } = await open('/?f=creator:fv_old', answer(
      [work()], [filter({ id: 'fv_old', display: 'Kept Name', valueIds: ['fv_new'], redirectedFrom: 'fv_old' })],
    ));
    expect(search).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: null, read: null, f: ['creator:fv_new'] }, replaceUrl: true }));
    expect(chipLabels(el)).toEqual(['Creators: Kept Name']);
  });

  it('says so when a tag is unknown (or not visible) and finds nothing, and the chip can still be removed', async () => {
    const { el } = await open('/?f=creator:fv_gone', answer([], [filter({ id: 'fv_gone', display: undefined, matched: false, valueIds: [] })]));
    expect(chipLabels(el)).toEqual(['Unknown tag']);
    expect(el.querySelector('.no-results')!.textContent!.trim()).toBe('No works with these tags.');
    expect(el.querySelector('[data-testid="search-filter-chip"] button[matChipRemove]')).not.toBeNull();
  });

  it('passes typed field syntax through the text and loads more works with the cursor', async () => {
    const typed = filter({ source: 'Typed', id: undefined, text: 'Synthetic Author' });
    const { el, fixture, search } = await open('/?q=creator:%22Synthetic%20Author%22',
      answer([work()], [typed], { totalCount: 2, nextCursor: 'w:1', hasMore: true }),
      answer([work({ title: 'Series B', node: makeNode({ id: 'f7', kind: 'Folder' }) })], [typed], { totalCount: 2 }),
    );
    expect(search).toHaveBeenCalledWith('creator:"Synthetic Author"', { readState: 'all' });
    // A filter typed in the text is not a removable chip (the text shows it).
    expect(chipLabels(el)).toEqual([]);
    const more = Array.from(el.querySelectorAll<HTMLButtonElement>('.load-more button')).find((b) => b.textContent!.includes('Load more'))!;
    more.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('creator:"Synthetic Author"', { cursor: 'w:1', readState: 'all' });
    expect(fixture.componentInstance.works().map((w) => w.title)).toEqual(['Series A', 'Series B']);
    expect(el.querySelector('.load-more')).toBeNull();
  });

  // 1.49.0 (lane T, owner decision 9 a): exclusion, any-of and the Refine list.
  it('1.49.0: names an excluded and an any-of filter, and toggles a chip to exclude it', async () => {
    const { el, fixture, search, navigate } = await open('/?q=alpha&f=-genre:fv_2&f=tag:fv_3%7Cfv_4', answer(
      [work()],
      [filter({ bucket: 'Genres', id: 'fv_2', display: 'Horror', valueIds: ['fv_2'], negated: true }),
        filter({ bucket: 'Tags', id: 'fv_3|fv_4', display: 'Robots or Space', valueIds: ['fv_3', 'fv_4'] })],
    ));
    expect(search).toHaveBeenCalledWith('alpha', { readState: 'all', filters: ['-genre:fv_2', 'tag:fv_3|fv_4'] });
    expect(chipLabels(el)).toEqual(['Not Genres: Horror', 'Tags: Robots or Space']);
    const chips = el.querySelectorAll('[data-testid="search-filter-chip"]');
    expect(chips[0].getAttribute('data-excluded')).toBe('true');
    const toggle = chips[1].querySelector<HTMLButtonElement>('[data-testid="search-filter-exclude"]')!;
    expect(toggle.getAttribute('aria-label')).toBe('Exclude Tags: Robots or Space');
    toggle.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('alpha', { readState: 'all', filters: ['-genre:fv_2', '-tag:fv_3|fv_4'] });
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'alpha', read: null, f: ['-genre:fv_2', '-tag:fv_3|fv_4'] } }));
  });

  it('1.49.0: lists Refine by bucket with counts; a chip adds, "or" joins the bucket\'s tag, the minus excludes', async () => {
    const refine = [
      { bucket: 'Tags' as const, more: 3, chips: [{ id: 'fv_9', display: 'Robots', count: 4, roles: 'None', conflict: false, filter: 'tag:fv_9' }] },
      { bucket: 'Genres' as const, more: 0, chips: [{ id: 'fv_5', display: 'Drama', count: 2, roles: 'None', conflict: false, filter: 'genre:fv_5' }] },
    ];
    const { el, fixture, search } = await open('/?f=genre:fv_2', answer([work()], [filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] })], { refine }));
    const box = el.querySelector('[data-testid="search-refine"]')!;
    // Buckets in the panel's order (Genres before Tags), each with its label and counts.
    expect(Array.from(box.querySelectorAll('.refine-bucket')).map((b) => b.getAttribute('data-bucket'))).toEqual(['Genres', 'Tags']);
    expect(box.querySelector('.refine-bucket[data-bucket="Tags"] .count')!.textContent).toBe('4');
    expect(box.querySelector('.refine-bucket[data-bucket="Tags"] .refine-more')!.textContent!.trim()).toBe('+3 more');
    // "or" only where the search already includes a tag of that bucket.
    expect(box.querySelector('.refine-bucket[data-bucket="Tags"] [data-testid="refine-or"]')).toBeNull();
    box.querySelector<HTMLButtonElement>('.refine-bucket[data-bucket="Genres"] [data-testid="refine-or"]')!.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('', { readState: 'all', filters: ['genre:fv_2|fv_5'] });
    expect(fixture.componentInstance.params().f).toEqual(['genre:fv_2|fv_5']);

    fixture.componentInstance.addRefine('tag:fv_9', 'not');
    expect(search).toHaveBeenLastCalledWith('', { readState: 'all', filters: ['genre:fv_2|fv_5', '-tag:fv_9'] });
    fixture.componentInstance.addRefine('tag:fv_9', 'all');
    expect(search).toHaveBeenLastCalledWith('', { readState: 'all', filters: ['genre:fv_2|fv_5', 'tag:fv_9'] });
  });

  // 1.50.0 (lane U): "+N more" opens the bucket; "or" asks which included tag to join.
  const refineChip = (n: number) => ({ id: `fv_${n}`, display: `Tag ${n}`, count: 2, roles: 'None', conflict: false, filter: `tag:fv_${n}` });
  const tagBucket = (shown: number, more: number) => ({ bucket: 'Tags' as const, more, chips: Array.from({ length: shown }, (_, i) => refineChip(i + 1)) });
  const comedy = () => filter({ bucket: 'Genres', id: 'fv_2', display: 'Comedy', valueIds: ['fv_2'] });
  const chipNames = (el: HTMLElement, bucket: string) =>
    Array.from(el.querySelectorAll(`.refine-bucket[data-bucket="${bucket}"] [data-testid="refine-chip"] .name`)).map((c) => c.textContent);

  it('1.50.0: "+N more" lists the whole bucket from one expand=tag request (a one-work page), and "Show fewer" closes it again', async () => {
    const { el, fixture, search } = await open('/?f=genre:fv_2', answer([work()], [comedy()], { refine: [tagBucket(2, 4)] }));
    expect(chipNames(el, 'Tags')).toEqual(['Tag 1', 'Tag 2']);
    expect(el.querySelector('[data-testid="refine-fewer"]')).toBeNull();

    search.mockReturnValueOnce(of(answer([work()], [], { refine: [tagBucket(6, 0)] })));
    el.querySelector<HTMLButtonElement>('[data-testid="refine-more"]')!.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('', { readState: 'all', pageSize: 1, expand: 'tag', filters: ['genre:fv_2'] });
    expect(chipNames(el, 'Tags')).toEqual(['Tag 1', 'Tag 2', 'Tag 3', 'Tag 4', 'Tag 5', 'Tag 6']);
    expect(el.querySelector('[data-testid="refine-more"]')).toBeNull();

    el.querySelector<HTMLButtonElement>('[data-testid="refine-fewer"]')!.click();
    fixture.detectChanges();
    expect(chipNames(el, 'Tags')).toEqual(['Tag 1', 'Tag 2']);
    expect(el.querySelector('[data-testid="refine-more"]')!.textContent!.trim()).toBe('+4 more');
    expect(el.querySelector('[data-testid="refine-fewer"]')).toBeNull();
  });

  it('1.50.0: a new search closes an opened bucket, and an answer to the old search is dropped', async () => {
    const { el, fixture, search } = await open('/?f=genre:fv_2', answer([work()], [comedy()], { refine: [tagBucket(2, 4)] }));
    const late = new Subject<Answer>();
    search.mockReturnValueOnce(late);
    el.querySelector<HTMLButtonElement>('[data-testid="refine-more"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('[data-testid="refine-more"]')!.disabled).toBe(true);

    // The person adds a tag meanwhile (a new search): the old expand answer must not open anything.
    search.mockReturnValueOnce(of(answer([work()], [comedy()], { refine: [tagBucket(2, 4)] })));
    fixture.componentInstance.addRefine('tag:fv_1', 'all');
    fixture.detectChanges();
    late.next(answer([work()], [], { refine: [tagBucket(6, 0)] }));
    fixture.detectChanges();
    expect(chipNames(el, 'Tags')).toEqual(['Tag 1', 'Tag 2']);
    expect(fixture.componentInstance.expanding()).toBeNull();
  });

  it('1.50.0: says so when the rest of a bucket cannot be loaded, and the button tries again', async () => {
    const { el, fixture, search } = await open('/?f=genre:fv_2', answer([work()], [comedy()], { refine: [tagBucket(2, 4)] }));
    search.mockReturnValueOnce(throwError(() => new Error('down')));
    el.querySelector<HTMLButtonElement>('[data-testid="refine-more"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="refine-error"]')).not.toBeNull();
    expect(chipNames(el, 'Tags')).toEqual(['Tag 1', 'Tag 2']);

    search.mockReturnValueOnce(of(answer([work()], [], { refine: [tagBucket(6, 0)] })));
    el.querySelector<HTMLButtonElement>('[data-testid="refine-more"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="refine-error"]')).toBeNull();
    expect(chipNames(el, 'Tags')).toHaveLength(6);
  });

  it('1.50.0: "or" with one included tag in the bucket joins it at once', async () => {
    const { el, fixture } = await open('/?f=tag:fv_1', answer([work()], [filter({ bucket: 'Tags', id: 'fv_1', display: 'Tag 1', valueIds: ['fv_1'] })], { refine: [{ ...tagBucket(0, 0), chips: [refineChip(5)] }] }));
    el.querySelector<HTMLButtonElement>('[data-testid="refine-or"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="refine-or-menu"]')).toBeNull();
    expect(fixture.componentInstance.params().f).toEqual(['tag:fv_1|fv_5']);
  });

  it('1.50.0: "or" with two or more included tags asks which one to join (not the last), and Cancel / Escape close the menu', async () => {
    const included = [
      filter({ bucket: 'Tags', id: 'fv_1', display: 'Tag 1', valueIds: ['fv_1'] }),
      filter({ bucket: 'Tags', id: 'fv_2', display: 'Tag 2', valueIds: ['fv_2'] }),
      filter({ bucket: 'Genres', id: 'fv_7', display: 'Comedy', valueIds: ['fv_7'] }),
    ];
    const { el, fixture } = await open('/?f=tag:fv_1&f=tag:fv_2&f=genre:fv_7', answer([work()], included, { refine: [{ ...tagBucket(0, 0), chips: [refineChip(5)] }] }));
    el.querySelector<HTMLButtonElement>('[data-testid="refine-or"]')!.click();
    fixture.detectChanges();

    const menu = el.querySelector('[data-testid="refine-or-menu"]')!;
    expect(menu).not.toBeNull();
    expect(Array.from(menu.querySelectorAll('[data-testid="refine-or-target"]')).map((b) => b.textContent!.trim())).toEqual(['Tags: Tag 1', 'Tags: Tag 2']);
    expect(fixture.componentInstance.params().f).toEqual(['tag:fv_1', 'tag:fv_2', 'genre:fv_7']); // nothing joined yet

    // Cancel closes it without a change.
    menu.querySelector<HTMLButtonElement>('[data-testid="refine-or-cancel"]')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="refine-or-menu"]')).toBeNull();
    expect(fixture.componentInstance.params().f).toEqual(['tag:fv_1', 'tag:fv_2', 'genre:fv_7']);

    // Escape closes it too; the FIRST tag is chosen this time.
    el.querySelector<HTMLButtonElement>('[data-testid="refine-or"]')!.click();
    fixture.detectChanges();
    el.querySelector('[data-testid="refine-or-menu"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="refine-or-menu"]')).toBeNull();

    el.querySelector<HTMLButtonElement>('[data-testid="refine-or"]')!.click();
    fixture.detectChanges();
    el.querySelectorAll<HTMLButtonElement>('[data-testid="refine-or-target"]')[0].click();
    fixture.detectChanges();
    expect(fixture.componentInstance.params().f).toEqual(['tag:fv_1|fv_5', 'tag:fv_2', 'genre:fv_7']);
    expect(el.querySelector('[data-testid="refine-or-menu"]')).toBeNull();
  });

  it('1.49.0: a typed-only tag search with no works says so in the tag words', async () => {
    const typed = filter({ bucket: 'Genres', source: 'Typed', id: undefined, text: 'action', negated: true });
    const { el } = await open('/?q=synthetic%20-genre:action', answer([], [typed]));
    expect(el.querySelector('.no-results')!.textContent!.trim()).toBe('No works with these tags.');
  });

  it('1.49.0: the "?" panel lists the syntax; an example goes into the field and searches', async () => {
    const { el, fixture, search } = await open('/?q=alpha', answer([], []));
    expect(el.querySelector('[data-testid="search-syntax"]')).toBeNull();
    const toggle = el.querySelector<HTMLButtonElement>('[data-testid="search-syntax-toggle"]')!;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    toggle.click();
    fixture.detectChanges();
    const examples = Array.from(el.querySelectorAll<HTMLButtonElement>('[data-testid="search-syntax-example"]'));
    expect(examples.map((b) => b.textContent!.trim())).toContain('-genre:horror');
    expect(examples.map((b) => b.textContent!.trim())).toContain('year:2010..2015');
    examples.find((b) => b.textContent!.trim() === '-genre:horror')!.click();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('alpha -genre:horror', { readState: 'all' });
  });
});

/**
 * 1.49.0 (lane S): the plain search's counts by kind, "+N inside" on folder cards, the close-match (typo) note and the short-input note.
 */
describe('SearchComponent search by words (1.49.0)', () => {
  let fixture: ComponentFixture<SearchComponent>;
  let search: ReturnType<typeof vi.fn>;

  function setup(...responses: SearchResultsDto[]): void {
    search = vi.fn();
    for (const r of responses) search.mockReturnValueOnce(of(r));
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        {
          provide: ApiService,
          useValue: { search, getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })) },
        },
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

  const el = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const text = (testId: string): string | null => el().querySelector(`[data-testid="${testId}"]`)?.textContent?.trim() ?? null;

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('says "N folders, M archives", leaving a zero kind out', () => {
    setup(
      {
        query: 'q', items: [makeNode({ id: 'f', kind: 'Folder' }), makeNode({ id: 'a' })], totalCount: 66, nextCursor: null, hasMore: false,
        folderCount: 1, archiveCount: 65,
      },
      { query: 'q', items: [makeNode({ id: 'a' })], totalCount: 60, nextCursor: null, hasMore: false, folderCount: 0, archiveCount: 60 },
      { query: 'q', items: [makeNode({ id: 'f', kind: 'Folder' })], totalCount: 2, nextCursor: null, hasMore: false, folderCount: 2, archiveCount: 0 },
    );
    runSearch('jump saga');
    expect(text('search-count')).toBe('1 folder, 65 archives');
    runSearch('jump sagas');
    expect(text('search-count')).toBe('60 archives');
    runSearch('jump sag');
    expect(text('search-count')).toBe('2 folders');
  });

  it('shows "+N inside" on a folder card and merges the next page\'s counts', () => {
    setup(
      {
        query: 'q', items: [makeNode({ id: 'f1', kind: 'Folder', displayName: 'Berserk' })], totalCount: 2, nextCursor: 'c1', hasMore: true,
        folderCount: 2, archiveCount: 0, insideCounts: { f1: 41 },
      },
      {
        query: 'q', items: [makeNode({ id: 'f2', kind: 'Folder', displayName: 'Berserk Deluxe' })], totalCount: 2, nextCursor: null, hasMore: false,
        folderCount: 2, archiveCount: 0, insideCounts: { f2: 1 },
      },
    );
    runSearch('berserk');
    expect(Array.from(el().querySelectorAll('[data-testid="search-inside"]')).map((e) => e.textContent?.trim())).toEqual(['+41 inside']);
    fixture.componentInstance.loadMore();
    fixture.detectChanges();
    expect(search).toHaveBeenLastCalledWith('berserk', { cursor: 'c1', readState: 'all' });
    expect(Array.from(el().querySelectorAll('[data-testid="search-inside"]')).map((e) => e.textContent?.trim()))
      .toEqual(['+41 inside', '+1 inside']);
  });

  it('notes close matches for a typo - all of them, or after the exact hits', () => {
    setup(
      { query: 'q', items: [makeNode({ id: 'a' })], totalCount: 1, nextCursor: null, hasMore: false, folderCount: 0, archiveCount: 1, closeMatchCount: 1 },
      { query: 'q', items: [makeNode({ id: 'a' }), makeNode({ id: 'b' })], totalCount: 2, nextCursor: null, hasMore: false, folderCount: 0, archiveCount: 2, closeMatchCount: 1 },
      { query: 'q', items: [makeNode({ id: 'a' })], totalCount: 1, nextCursor: null, hasMore: false, folderCount: 0, archiveCount: 1, closeMatchCount: 0 },
    );
    runSearch('berserc');
    expect(text('search-close-hint')).toBe('No exact matches. Showing close matches for "berserc".');
    runSearch('berserk');
    expect(text('search-close-hint')).toBe('Also showing close matches for "berserk".');
    runSearch('akira');
    expect(text('search-close-hint')).toBeNull();
  });

  it('explains a 1-2 character search (word starts, no typo search) and not a longer one', () => {
    const one = { query: 'q', items: [makeNode({ id: 'a' })], totalCount: 1, nextCursor: null, hasMore: false, folderCount: 0, archiveCount: 1 };
    setup(one, one, one, one);
    runSearch('ak');
    expect(text('search-short-hint')).toContain('Names with a word starting with "ak"');
    runSearch('re z');
    expect(text('search-short-hint')).toContain('"re z"');
    runSearch('é');
    expect(text('search-short-hint')).not.toBeNull();
    runSearch('aki');
    expect(text('search-short-hint')).toBeNull();
  });

  it('counts a series-match anchor once, in its own row, not in the kind counts', () => {
    const anchor = makeNode({ id: 'f1', kind: 'Folder', displayName: 'Dungeon Folder' });
    setup({
      query: 'q', items: [anchor, makeNode({ id: 'a1' })], totalCount: 2, nextCursor: null, hasMore: false, folderCount: 1, archiveCount: 1,
      seriesMatches: [{ node: anchor, matchedTitle: 'Delicious in Dungeon' }],
    });
    runSearch('dungeon');
    expect(text('search-count')).toBe('1 archive');
  });
});

/**
 * 1.49.0 (lane O): on a phone the header has no search box, so the Search page shows the same grouped quick results under its field
 * while typing - a result opens, a typed field's value completes the token. 1.50.0 (lane U, owner decision 5): desktop and tablet show
 * them too, as a dropdown with a keyboard hint line and arrow / Enter / Escape on the field.
 */
describe('SearchComponent quick results', () => {
  const answer: SearchSuggestDto = {
    query: 'ber',
    series: [],
    folders: [{ kind: 'folder', id: 'f1', label: 'Berserk Deluxe', libraryId: 'L1', nodeKind: 'Folder' }],
    archives: [],
    creators: [{ kind: 'creator', id: 'fv_c', label: 'Naoki Urasawa', filter: 'creator:fv_c', count: 1 }],
    tags: [],
    field: null,
  };

  function setup(phone: boolean): { fixture: ComponentFixture<SearchComponent>; api: { search: ReturnType<typeof vi.fn>; searchSuggest: ReturnType<typeof vi.fn> } } {
    const api = {
      search: vi.fn().mockReturnValue(of({ query: 'q', items: [], totalCount: 0, nextCursor: null, hasMore: false })),
      searchSuggest: vi.fn().mockReturnValue(of(answer)),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
    };
    TestBed.configureTestingModule({
      imports: [SearchComponent],
      providers: [
        provideRouter([]),
        provideNoopAnimations(),
        { provide: ApiService, useValue: api },
        { provide: BreakpointObserver, useValue: { observe: () => of({ matches: phone, breakpoints: {} }) } },
      ],
    });
    const fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    return { fixture, api };
  }

  function typeInto(fixture: ComponentFixture<SearchComponent>, text: string): void {
    const input = fixture.nativeElement.querySelector('[data-testid="search-input"]') as HTMLInputElement;
    input.dispatchEvent(new Event('focus'));
    fixture.componentInstance.query = text;
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(300);
    fixture.detectChanges();
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('shows the grouped quick results under the field while typing', () => {
    const { fixture, api } = setup(true);
    typeInto(fixture, 'ber');

    expect(api.searchSuggest).toHaveBeenCalledWith('ber');
    const panel = fixture.nativeElement.querySelector('[data-testid="search-quick"]') as HTMLElement;
    expect(panel).not.toBeNull();
    expect([...panel.querySelectorAll('[role="group"]')].map((g) => g.getAttribute('data-group'))).toEqual(['folders', 'creators']);
    expect([...panel.querySelectorAll('.qr-label')].map((e) => e.textContent)).toEqual(['Berserk Deluxe', 'Naoki Urasawa']);
  });

  it('opens a folder result, and adds a creator result as a tag view (clearing the text)', async () => {
    const { fixture } = setup(true);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    typeInto(fixture, 'ber');

    (fixture.nativeElement.querySelectorAll('[data-testid="quick-result"]')[0] as HTMLElement).click();
    expect(navigate).toHaveBeenCalledWith(['/libraries', 'L1', 'browse', 'f1']);

    typeInto(fixture, 'ber2');
    (fixture.nativeElement.querySelectorAll('[data-testid="quick-result"]')[1] as HTMLElement).click();
    expect(fixture.componentInstance.query).toBe('');
    expect(fixture.componentInstance.params().f).toEqual(['creator:fv_c']);
  });

  it('opening a result cancels the search still waiting on its 300 ms (it would write the page\'s URL back over the navigation)', () => {
    const { fixture, api } = setup(true);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    (fixture.nativeElement.querySelector('[data-testid="search-input"]') as HTMLInputElement).dispatchEvent(new Event('focus'));
    fixture.componentInstance.query = 'ber';
    fixture.componentInstance.onSearch();
    vi.advanceTimersByTime(200); // the quick answer is in; the page's own search is still waiting
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="quick-result"]') as HTMLElement).click();
    vi.advanceTimersByTime(500);
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/libraries', 'L1', 'browse', 'f1']);
    expect(api.search).not.toHaveBeenCalled();
  });

  it('completes a typed field\'s token from its values', () => {
    const { fixture, api } = setup(true);
    api.searchSuggest.mockReturnValue(of({
      ...answer, folders: [], creators: [],
      field: { name: 'tag', prefix: 'tim', start: 8, items: [{ label: 'Time Travel', insert: 'tag:"Time Travel"', count: 2 }] },
    }));
    typeInto(fixture, 'berserk tag:tim');

    (fixture.nativeElement.querySelector('[data-testid="quick-result"]') as HTMLElement).click();
    expect(fixture.componentInstance.query).toBe('berserk tag:"Time Travel" ');
  });

  const keyOn = (fixture: ComponentFixture<SearchComponent>, key: string): KeyboardEvent => {
    const input = fixture.nativeElement.querySelector('[data-testid="search-input"]') as HTMLInputElement;
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    input.dispatchEvent(event);
    fixture.detectChanges();
    return event;
  };
  const panelOf = (fixture: ComponentFixture<SearchComponent>) => fixture.nativeElement.querySelector('[data-testid="search-quick"]') as HTMLElement | null;

  it('on a phone: Enter with none chosen hides them, and there is no keyboard hint', () => {
    const phone = setup(true);
    typeInto(phone.fixture, 'ber');
    expect(panelOf(phone.fixture)!.classList).not.toContain('quick-desktop');
    expect(phone.fixture.nativeElement.querySelector('[data-testid="quick-hint"]')).toBeNull();
    keyOn(phone.fixture, 'Enter');
    expect(panelOf(phone.fixture)).toBeNull();
  });

  it('on desktop and tablet: asks the same, shows a dropdown with the keyboard hint line', () => {
    const wide = setup(false);
    typeInto(wide.fixture, 'ber');
    expect(wide.api.searchSuggest).toHaveBeenCalledWith('ber');
    const panel = panelOf(wide.fixture)!;
    expect(panel.classList).toContain('quick-desktop');
    expect([...panel.querySelectorAll('.qr-label')].map((e) => e.textContent)).toEqual(['Berserk Deluxe', 'Naoki Urasawa']);
    const hint = panel.querySelector('[data-testid="quick-hint"]')!;
    expect(hint.textContent).toContain('to move');
    expect(hint.textContent).toContain('Enter');
    expect(hint.textContent).toContain('Esc');
    expect(hint.getAttribute('aria-hidden')).toBe('true');
  });

  it('the keyboard moves through the results (aria-activedescendant follows), Enter opens the chosen one', () => {
    const { fixture } = setup(false);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    typeInto(fixture, 'ber');
    const input = fixture.nativeElement.querySelector('[data-testid="search-input"]') as HTMLInputElement;
    expect(input.getAttribute('aria-activedescendant')).toBeNull();
    expect(input.getAttribute('aria-controls')).toBe('search-quick-list');

    expect(keyOn(fixture, 'ArrowDown').defaultPrevented).toBe(true);
    expect(input.getAttribute('aria-activedescendant')).toBe('sq-folders-f1');
    expect(fixture.nativeElement.querySelector('[role="option"][aria-selected="true"]')!.textContent).toContain('Berserk Deluxe');
    keyOn(fixture, 'ArrowDown');
    expect(input.getAttribute('aria-activedescendant')).toBe('sq-creators-fv_c');
    keyOn(fixture, 'ArrowUp');
    expect(input.getAttribute('aria-activedescendant')).toBe('sq-folders-f1');

    expect(keyOn(fixture, 'Enter').defaultPrevented).toBe(true);
    expect(navigate).toHaveBeenCalledWith(['/libraries', 'L1', 'browse', 'f1']);
    expect(panelOf(fixture)).toBeNull();
  });

  it('Escape closes the list (and only then lets the key through); an arrow opens it again', () => {
    const { fixture } = setup(false);
    typeInto(fixture, 'ber');
    expect(keyOn(fixture, 'Escape').defaultPrevented).toBe(true);
    expect(panelOf(fixture)).toBeNull();
    expect(keyOn(fixture, 'Escape').defaultPrevented).toBe(false);
    keyOn(fixture, 'ArrowDown');
    expect(panelOf(fixture)).not.toBeNull();
  });

  it('Enter with no result chosen leaves the typed search to the page (no navigation, list closes)', () => {
    const { fixture, api } = setup(false);
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    typeInto(fixture, 'ber');
    expect(keyOn(fixture, 'Enter').defaultPrevented).toBe(false);
    expect(panelOf(fixture)).toBeNull();
    expect(api.search).toHaveBeenCalled(); // the page's own search ran from the typed text
    expect(navigate).not.toHaveBeenCalledWith(['/libraries', 'L1', 'browse', 'f1']);
  });
});
