import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { FavoritesComponent } from './favorites.component';
import { ApiService } from '../../core/api/api.service';
import { CatalogNodeDto, LibraryDto, LibraryViewPreferencesDto, PageResponse } from '../../core/api/api-types';
import { CARD_SIZE_STORAGE_KEY, LIST_COLUMNS_STORAGE_KEY } from '../../core/preferences/device-display-preferences.service';
import {
  FAVORITES_CARD_SIZE_STORAGE_KEY,
  FAVORITES_LIST_COLUMNS_STORAGE_KEY,
  FAVORITES_SORT_STORAGE_KEY,
  FAVORITES_VIEW_MODE_STORAGE_KEY,
  FavoritesDisplayPreferencesService,
} from '../../core/preferences/favorites-display-preferences.service';

/**
 * The Favorites view (1.50.0 lane F, owner decisions 6-10): its own per-device display settings seeded once from browse's, the View /
 * Sort / Filter menus driving `GET /favorites`, the list view through the shared row, and the empty state of a filter.
 */
describe('FavoritesComponent - the Favorites view (1.50.0)', () => {
  function node(id: string, kind: 'Folder' | 'Archive', extra: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind, displayName: `Name ${id}`, availability: 'Available',
      coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: null, readingState: null,
      lastReadPage: null, readerDefault: null, isRead: false, readRollup: null, isFavorite: true, ...extra,
    };
  }

  function page(items: CatalogNodeDto[], hasMore = false): PageResponse<CatalogNodeDto> {
    return { items, totalCount: items.length, nextCursor: hasMore ? 'fn:0:1:x' : null, hasMore };
  }

  function lib(id: string, name: string): LibraryDto {
    return { id, name, isScanning: false, itemCount: null, lastScanCompleted: null, defaultReaderMode: null, icon: null };
  }

  function setup(opts: {
    pages?: PageResponse<CatalogNodeDto>[];
    libraries?: LibraryDto[];
    prefs?: Partial<LibraryViewPreferencesDto>;
  } = {}) {
    const getFavorites = vi.fn().mockReturnValue(of(page([])));
    (opts.pages ?? []).forEach((p) => getFavorites.mockReturnValueOnce(of(p)));
    const getLibraryPreferences = vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name', ...opts.prefs }));
    const apiSpy = {
      getFavorites,
      getLibraryPreferences,
      getLibraries: vi.fn().mockReturnValue(of(opts.libraries ?? [])),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [FavoritesComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    const fixture = TestBed.createComponent(FavoritesComponent);
    fixture.detectChanges();
    return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, getFavorites, getLibraryPreferences };
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  it('asks for the default listing first: recently favorited, no filter', () => {
    const { getFavorites } = setup();
    expect(getFavorites).toHaveBeenCalledWith(null, 50,
      { sort: 'favorited', direction: 'desc', readState: 'all', kind: 'all', libraryId: null });
  });

  describe('per-device display settings, seeded once from browse (decision 10)', () => {
    it('a device without its own values takes browse\'s card size, list columns and view mode, and stores them', () => {
      localStorage.setItem(CARD_SIZE_STORAGE_KEY, '200');
      localStorage.setItem(LIST_COLUMNS_STORAGE_KEY, '3');
      const { cmp, getLibraryPreferences } = setup({ prefs: { viewMode: 'list' } });
      expect(getLibraryPreferences).toHaveBeenCalledTimes(1);
      expect(cmp.cardSize()).toBe(200);
      expect(cmp.listColumns()).toBe(3);
      expect(cmp.viewMode()).toBe('list');
      expect(localStorage.getItem(FAVORITES_CARD_SIZE_STORAGE_KEY)).toBe('200');
      expect(localStorage.getItem(FAVORITES_LIST_COLUMNS_STORAGE_KEY)).toBe('3');
      expect(localStorage.getItem(FAVORITES_VIEW_MODE_STORAGE_KEY)).toBe('list');
    });

    it('seeds browse\'s server value when browse has no device copy, without storing anything for browse', () => {
      const { cmp } = setup({ prefs: { cardSize: '230', listColumns: 1 } });
      expect(cmp.cardSize()).toBe(230);
      expect(cmp.listColumns()).toBe(1);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBeNull();
    });

    it('a device that has its own values is not seeded again, and its changes never reach browse', () => {
      localStorage.setItem(FAVORITES_CARD_SIZE_STORAGE_KEY, '120');
      localStorage.setItem(FAVORITES_LIST_COLUMNS_STORAGE_KEY, '2');
      localStorage.setItem(FAVORITES_VIEW_MODE_STORAGE_KEY, 'card');
      localStorage.setItem(CARD_SIZE_STORAGE_KEY, '250');
      const { cmp, getLibraryPreferences } = setup();
      expect(getLibraryPreferences).not.toHaveBeenCalled();
      expect(cmp.cardSize()).toBe(120);

      cmp.onCardSizeChange({ target: { value: '180' } } as unknown as Event);
      cmp.setViewMode('list');
      cmp.onListColumnsChange({ target: { value: '3' } } as unknown as Event);
      expect(localStorage.getItem(FAVORITES_CARD_SIZE_STORAGE_KEY)).toBe('180');
      expect(localStorage.getItem(FAVORITES_VIEW_MODE_STORAGE_KEY)).toBe('list');
      expect(localStorage.getItem(FAVORITES_LIST_COLUMNS_STORAGE_KEY)).toBe('3');
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe('250');
      expect(localStorage.getItem(LIST_COLUMNS_STORAGE_KEY)).toBeNull();
    });

    it('a slider drag previews the size without storing it; the release stores it', () => {
      localStorage.setItem(FAVORITES_CARD_SIZE_STORAGE_KEY, '150');
      localStorage.setItem(FAVORITES_LIST_COLUMNS_STORAGE_KEY, '2');
      localStorage.setItem(FAVORITES_VIEW_MODE_STORAGE_KEY, 'card');
      const { cmp, el, fixture } = setup();
      cmp.onCardSizeInput({ target: { value: '210' } } as unknown as Event);
      fixture.detectChanges();
      expect((el.querySelector('[data-testid="favorites-list"]') as HTMLElement).style.getPropertyValue('--card-size')).toBe('210px');
      expect(localStorage.getItem(FAVORITES_CARD_SIZE_STORAGE_KEY)).toBe('150');
    });
  });

  describe('sort (decision 6, persisted per device - decision 9)', () => {
    it('picking a sort stores it on the device and reloads from the top with it', () => {
      const { cmp, getFavorites } = setup();
      cmp.setSort('name', 'desc');
      expect(localStorage.getItem(FAVORITES_SORT_STORAGE_KEY)).toBe('name-desc');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50,
        { sort: 'name', direction: 'desc', readState: 'all', kind: 'all', libraryId: null });
      cmp.setSort('recentlyUpdated', 'desc');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ sort: 'recentlyUpdated' }));
      // The same sort again is no request.
      const calls = getFavorites.mock.calls.length;
      cmp.setSort('recentlyUpdated', 'desc');
      expect(getFavorites.mock.calls.length).toBe(calls);
    });

    it('a stored sort is the first request of the next visit', () => {
      localStorage.setItem(FAVORITES_SORT_STORAGE_KEY, 'recentlyAdded');
      const { getFavorites } = setup();
      expect(getFavorites).toHaveBeenCalledWith(null, 50, expect.objectContaining({ sort: 'recentlyAdded', direction: 'desc' }));
    });

    it('the View menu lists the five sorts and marks the current one', async () => {
      const { el, fixture } = setup();
      (el.querySelector('[data-testid="favorites-view-button"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      const items = Array.from(document.querySelectorAll('[data-testid^="favorites-sort-"]'));
      expect(items.map((i) => i.getAttribute('data-testid'))).toEqual([
        'favorites-sort-favorited', 'favorites-sort-name', 'favorites-sort-name-desc', 'favorites-sort-recentlyAdded',
        'favorites-sort-recentlyUpdated',
      ]);
      expect(items[0].getAttribute('aria-checked')).toBe('true');
      expect(items[1].getAttribute('aria-checked')).toBe('false');
    });
  });

  describe('filters (decision 7, transient)', () => {
    it('each filter reloads from the top with it, and Clear filters drops them all', () => {
      const { cmp, getFavorites } = setup();
      cmp.setReadState('unread');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ readState: 'unread' }));
      cmp.setKind('archives');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ readState: 'unread', kind: 'archives' }));
      cmp.setLibrary('lib2');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50,
        expect.objectContaining({ readState: 'unread', kind: 'archives', libraryId: 'lib2' }));
      expect(cmp.filterActive()).toBe(true);
      expect(cmp.filterLabel()).toBe('Filtered');
      cmp.clearFilters();
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50,
        { sort: 'favorited', direction: 'desc', readState: 'all', kind: 'all', libraryId: null });
      expect(cmp.filterLabel()).toBe('Filter');
      // Filters are never stored.
      expect(Object.keys(localStorage).filter((k) => k.includes('filter') || k.includes('read'))).toEqual([]);
    });

    it('the Filter menu offers the libraries the viewer sees (only when there are two or more)', async () => {
      const { el, fixture } = setup({ libraries: [lib('lib1', 'Manga'), lib('lib2', 'Comics')] });
      (el.querySelector('[data-testid="favorites-filter-button"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      const ids = Array.from(document.querySelectorAll('[data-testid^="favorites-"]')).map((i) => i.getAttribute('data-testid'));
      expect(ids).toEqual(expect.arrayContaining([
        'favorites-read-all', 'favorites-read-reading', 'favorites-read-read', 'favorites-read-unread',
        'favorites-kind-all', 'favorites-kind-folders', 'favorites-kind-archives',
        'favorites-library-all', 'favorites-library-lib1', 'favorites-library-lib2',
      ]));
    });

    it('a filter that leaves nothing says so and offers the way back', () => {
      const { cmp, el, fixture } = setup();
      cmp.setReadState('read');
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="favorites-empty-filtered"]')?.textContent).toContain('No favorites match these filters.');
    });

    it('an answer of an older sort or filter never lands over the newer one', () => {
      const { cmp, getFavorites } = setup();
      let pending: { next: (p: PageResponse<CatalogNodeDto>) => void } | null = null;
      const late = { subscribe: (o: { next: (p: PageResponse<CatalogNodeDto>) => void }) => { pending = o; return { unsubscribe() { /* none */ } }; } };
      getFavorites.mockReturnValueOnce(late);
      getFavorites.mockReturnValueOnce(of(page([node('new', 'Archive')])));
      cmp.setReadState('read');
      cmp.setReadState('unread');
      pending!.next(page([node('old', 'Archive')]));
      expect(cmp.nodes().map((n) => n.id)).toEqual(['new']);
    });
  });

  describe('list view (the shared row)', () => {
    it('renders a row per entry: a stack with its count, no star and its ?favorites=1 link; a single with its star', () => {
      localStorage.setItem(FAVORITES_VIEW_MODE_STORAGE_KEY, 'list');
      localStorage.setItem(FAVORITES_CARD_SIZE_STORAGE_KEY, '150');
      localStorage.setItem(FAVORITES_LIST_COLUMNS_STORAGE_KEY, '2');
      const { el } = setup({ pages: [page([
        node('fold', 'Folder', { favoriteStackCount: 3, isFavorite: false }),
        node('arc', 'Archive', { pageCount: 24, coverUrl: '/api/v1/items/arc/cover' }),
      ])] });
      const rows = Array.from(el.querySelectorAll('app-node-row'));
      expect(rows.length).toBe(2);
      expect(el.querySelectorAll('.fav-card').length).toBe(0);
      expect(rows[0].querySelector('a.node-card')?.getAttribute('href')).toBe('/libraries/lib1/browse/fold?favorites=1');
      expect(rows[0].querySelector('[data-testid="row-stack-count"]')?.textContent?.trim()).toBe('3');
      expect(rows[0].querySelector('app-star-toggle')).toBeNull();
      expect(rows[0].querySelector('.node-sub')?.textContent?.trim()).toBe('3 favorites');
      expect(rows[1].querySelector('a.node-card')?.getAttribute('href')).toBe('/reader/arc');
      expect(rows[1].querySelector('app-star-toggle')).not.toBeNull();
      expect(rows[1].querySelector('.node-sub')?.textContent?.trim()).toBe('24 pages');
      expect(rows[1].querySelector('.cover img')?.getAttribute('src')).toBe('/api/v1/items/arc/cover');
    });

    it('switching between Card and List keeps the loaded entries', () => {
      const { cmp, el, fixture, getFavorites } = setup({ pages: [page([node('a', 'Archive'), node('b', 'Folder')])] });
      const calls = getFavorites.mock.calls.length;
      cmp.setViewMode('list');
      fixture.detectChanges();
      expect(el.querySelectorAll('app-node-row').length).toBe(2);
      cmp.setViewMode('card');
      fixture.detectChanges();
      expect(el.querySelectorAll('.fav-card').length).toBe(2);
      expect(getFavorites.mock.calls.length).toBe(calls);
    });
  });

  it('pages on with the cursor under the same sort and filters', () => {
    const { cmp, getFavorites } = setup({ pages: [page([node('a1', 'Archive')], true), page([node('a2', 'Archive')])] });
    cmp.loadMore();
    expect(getFavorites.mock.calls[1]).toEqual(['fn:0:1:x', 50,
      { sort: 'favorited', direction: 'desc', readState: 'all', kind: 'all', libraryId: null }]);
    expect(cmp.nodes().map((n) => n.id)).toEqual(['a1', 'a2']);
  });

  it('the display service: seedOnce never overwrites a value the device has', () => {
    localStorage.setItem(FAVORITES_CARD_SIZE_STORAGE_KEY, '130');
    const svc = TestBed.inject(FavoritesDisplayPreferencesService);
    expect(svc.needsSeed()).toBe(true);
    svc.seedOnce({ cardSize: 240, listColumns: 3, viewMode: 'list' });
    expect(svc.cardSize()).toBe(130);
    expect(svc.listColumns()).toBe(3);
    expect(svc.viewMode()).toBe('list');
    expect(svc.needsSeed()).toBe(false);
    svc.seedOnce({ cardSize: 110, listColumns: 1, viewMode: 'card' });
    expect([svc.cardSize(), svc.listColumns(), svc.viewMode()]).toEqual([130, 3, 'list']);
    expect(svc.setSort('recentlyAdded', 'asc')).toEqual({ sort: 'recentlyAdded', direction: 'desc' });
  });
});
