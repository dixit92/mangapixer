import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { FavoritesComponent, groupHeaders } from './favorites.component';
import { ApiService } from '../../core/api/api.service';
import {
  CatalogNodeDto, FavoriteLibraryGroupDto, FavoritesJumpIndexDto, JumpIndexBucketDto, LibraryDto, PageResponse,
} from '../../core/api/api-types';
import {
  FAVORITES_GROUP_STORAGE_KEY,
  FAVORITES_SORT_STORAGE_KEY,
  FavoritesDisplayPreferencesService,
  decodeSort,
} from '../../core/preferences/favorites-display-preferences.service';

/**
 * 1.51.0 lane F (owner decisions 2, 8-10): the Favorites page's Recently read sort, its jump rail (name sort only, never while grouped,
 * a chip in place or through its cursor, then paging up), and "Group by library" (a toolbar toggle remembered per device, a plain header
 * wherever the library changes in both views, none while one library is filtered).
 */
describe('FavoritesComponent - jump rail, Recently read and library groups (1.51.0)', () => {
  function node(id: string, libraryId: string, kind: 'Folder' | 'Archive' = 'Archive', extra: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId, kind, displayName: `Name ${id}`, availability: 'Available',
      coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: null, readingState: null,
      lastReadPage: null, readerDefault: null, isRead: false, readRollup: null, isFavorite: true, ...extra,
    };
  }

  function page(items: CatalogNodeDto[], extra: Partial<PageResponse<CatalogNodeDto>> = {}): PageResponse<CatalogNodeDto> {
    return { items, totalCount: items.length, nextCursor: null, hasMore: false, ...extra };
  }

  function lib(id: string, name: string): LibraryDto {
    return { id, name, isScanning: false, itemCount: null, lastScanCompleted: null, defaultReaderMode: null, icon: null };
  }

  function bucket(label: string, count: number, firstNodeId: string, firstCursor: string | null): JumpIndexBucketDto {
    return { label, count, firstNodeId, firstCursor };
  }

  const LIBS = [lib('libA', 'Alpha Lib'), lib('libB', 'beta Lib')];
  const GROUPS: FavoriteLibraryGroupDto[] = [{ libraryId: 'libA', name: 'Alpha Lib', count: 2 }, { libraryId: 'libB', name: 'beta Lib', count: 1 }];

  function setup(opts: { pages?: PageResponse<CatalogNodeDto>[]; rail?: FavoritesJumpIndexDto; libraries?: LibraryDto[] } = {}) {
    const getFavorites = vi.fn().mockReturnValue(of(page([])));
    (opts.pages ?? []).forEach((p) => getFavorites.mockReturnValueOnce(of(p)));
    const getFavoritesJumpIndex = vi.fn().mockReturnValue(of(opts.rail ?? { buckets: [] }));
    const apiSpy = {
      getFavorites,
      getFavoritesJumpIndex,
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
      getLibraries: vi.fn().mockReturnValue(of(opts.libraries ?? LIBS)),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [FavoritesComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: apiSpy }],
    });
    const fixture = TestBed.createComponent(FavoritesComponent);
    fixture.detectChanges();
    return { fixture, cmp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, getFavorites, getFavoritesJumpIndex };
  }

  /** Twelve archives A..L, one per letter, in library libA. */
  const TWELVE = 'ABCDEFGHIJKL'.split('').map((l) => node(`n${l}`, 'libA'));
  const RAIL: FavoritesJumpIndexDto = {
    buckets: TWELVE.map((n, i) => bucket(n.id.slice(1), 1, n.id, i === 0 ? null : `fn:0:${i}:k`)),
    direction: 'asc',
    mode: 'letters',
  };

  beforeEach(() => {
    localStorage.clear();
    TestBed.resetTestingModule();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
  });

  describe('Recently read (decision 2)', () => {
    it('is a sort of its own, newest first, stored on the device', () => {
      const { cmp, getFavorites } = setup();
      cmp.setSort('recentlyRead', 'asc');
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ sort: 'recentlyRead', direction: 'desc' }));
      expect(localStorage.getItem(FAVORITES_SORT_STORAGE_KEY)).toBe('recentlyRead');
      expect(decodeSort('recentlyRead')).toEqual({ sort: 'recentlyRead', direction: 'desc' });
    });
  });

  describe('the jump rail (decisions 2 and 8)', () => {
    it('shows under the name sort, asks for the same direction and filters, and is gone under any other sort', () => {
      localStorage.setItem(FAVORITES_SORT_STORAGE_KEY, 'name-desc');
      const { el, fixture, cmp, getFavoritesJumpIndex } = setup({ pages: [page(TWELVE)], rail: RAIL });
      fixture.detectChanges();
      expect(getFavoritesJumpIndex).toHaveBeenCalledWith({ direction: 'desc', readState: 'all', kind: 'all', libraryId: null });
      expect(el.querySelector('app-jump-rail nav.jump-rail')).not.toBeNull();
      expect(el.querySelectorAll('app-jump-rail nav .jump-chip').length).toBe(12);

      cmp.setReadState('unread');
      expect(getFavoritesJumpIndex).toHaveBeenLastCalledWith(expect.objectContaining({ readState: 'unread' }));
      const calls = getFavoritesJumpIndex.mock.calls.length;
      cmp.setSort('recentlyAdded', 'desc');
      fixture.detectChanges();
      expect(getFavoritesJumpIndex.mock.calls.length).toBe(calls);
      expect(el.querySelector('app-jump-rail')).toBeNull();
    });

    it('is never asked for and never shown while grouped by library', () => {
      localStorage.setItem(FAVORITES_SORT_STORAGE_KEY, 'name');
      localStorage.setItem(FAVORITES_GROUP_STORAGE_KEY, 'library');
      const { el, fixture, getFavoritesJumpIndex } = setup({ pages: [page(TWELVE)], rail: RAIL });
      fixture.detectChanges();
      expect(getFavoritesJumpIndex).not.toHaveBeenCalled();
      expect(el.querySelector('app-jump-rail')).toBeNull();
    });

    it('a chip whose first card is loaded scrolls in place; any other reloads from its cursor, and the window then pages up', () => {
      localStorage.setItem(FAVORITES_SORT_STORAGE_KEY, 'name');
      const far = bucket('Z', 3, 'nZ', 'fn:0:99:k');
      const { cmp, fixture, getFavorites } = setup({
        pages: [
          page(TWELVE, { hasMore: true, nextCursor: 'fn:0:12:k' }),
          page([node('nZ', 'libA')], { hasPrevious: true, prevCursor: 'fn:0:100:k' }),
          page([node('nY', 'libA')], { hasPrevious: false, prevCursor: null }),
        ],
        rail: { buckets: [...RAIL.buckets, far] },
      });
      fixture.detectChanges();
      const calls = getFavorites.mock.calls.length;
      cmp.jumpToBucket(RAIL.buckets[5]);
      expect(getFavorites.mock.calls.length).toBe(calls); // loaded: no request
      expect(cmp.activeJump()).toBe(RAIL.buckets[5]);

      cmp.jumpToBucket(far);
      expect(getFavorites).toHaveBeenLastCalledWith('fn:0:99:k', 50, expect.objectContaining({ sort: 'name' }));
      fixture.detectChanges();
      expect(cmp.nodes().map((n) => n.id)).toEqual(['nZ']);
      expect(cmp.hasPrevious()).toBe(true);

      cmp.loadPrevious();
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ sort: 'name', before: 'fn:0:100:k' }));
      expect(cmp.nodes().map((n) => n.id)).toEqual(['nY', 'nZ']);
      expect(cmp.hasPrevious()).toBe(false);
    });

    it('a window that starts at the top has no top sentinel; one that starts mid-list has', () => {
      const { el, fixture } = setup({ pages: [page(TWELVE, { hasPrevious: true, prevCursor: 'f:1:1' })] });
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="favorites-top-sentinel"]')).not.toBeNull();
      TestBed.resetTestingModule();
      const top = setup({ pages: [page(TWELVE)] });
      top.fixture.detectChanges();
      expect(top.el.querySelector('[data-testid="favorites-top-sentinel"]')).toBeNull();
    });
  });

  describe('group by library (owner addition, decisions 9 and 10)', () => {
    const MIXED = [node('a1', 'libA'), node('a2', 'libA', 'Folder'), node('b1', 'libB')];

    it('the toolbar toggle groups the list, is stored on this device, and asks the server for the grouped order', () => {
      const { el, fixture, getFavorites } = setup();
      fixture.detectChanges();
      const toggle = el.querySelector('[data-testid="favorites-group-button"]') as HTMLButtonElement;
      expect(toggle.getAttribute('aria-pressed')).toBe('false');
      toggle.click();
      fixture.detectChanges();
      expect(localStorage.getItem(FAVORITES_GROUP_STORAGE_KEY)).toBe('library');
      expect(TestBed.inject(FavoritesDisplayPreferencesService).groupByLibrary()).toBe(true);
      expect(getFavorites).toHaveBeenLastCalledWith(null, 50, expect.objectContaining({ groupByLibrary: true }));
      expect(toggle.getAttribute('aria-pressed')).toBe('true');
      toggle.click();
      fixture.detectChanges();
      expect(localStorage.getItem(FAVORITES_GROUP_STORAGE_KEY)).toBe('none');
      expect(getFavorites.mock.lastCall![2]).not.toHaveProperty('groupByLibrary');
    });

    it('the toggle shows only for a viewer with two or more libraries', () => {
      const { el, fixture } = setup({ libraries: [lib('libA', 'Alpha Lib')] });
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="favorites-group-button"]')).toBeNull();
    });

    it('a stored choice is the first request of the next visit', () => {
      localStorage.setItem(FAVORITES_GROUP_STORAGE_KEY, 'library');
      const { getFavorites } = setup();
      expect(getFavorites).toHaveBeenCalledWith(null, 50, expect.objectContaining({ groupByLibrary: true }));
    });

    for (const view of ['card', 'list'] as const) {
      it(`a plain header (name + count) wherever the library changes - ${view} view`, () => {
        localStorage.setItem(FAVORITES_GROUP_STORAGE_KEY, 'library');
        localStorage.setItem('mangapixer-favorites-view-mode', view);
        const { el, fixture } = setup({ pages: [page(MIXED, { favoriteGroups: GROUPS })] });
        fixture.detectChanges();
        const headers = Array.from(el.querySelectorAll('[data-testid="favorites-group-header"]'));
        expect(headers.map((h) => `${h.querySelector('.lib-name')!.textContent!.trim()} ${h.querySelector('.lib-count')!.textContent!.trim()}`))
          .toEqual(['Alpha Lib 2', 'beta Lib 1']);
        expect(headers.every((h) => h.getAttribute('role') === 'heading')).toBe(true);
        // Each header sits right before the first entry of its library.
        const grid = el.querySelector('[data-testid="favorites-list"]')!;
        const kids = Array.from(grid.children);
        expect(kids.indexOf(headers[0])).toBe(0);
        expect(kids[kids.indexOf(headers[1]) - 1].classList.contains('fav-item')).toBe(true);
        expect(el.querySelectorAll('.fav-item').length).toBe(3);
        // Plain headers: nothing to click or collapse.
        expect(headers[0].querySelector('button, a')).toBeNull();
      });
    }

    it('no header while not grouped, nor while one library is filtered', () => {
      const ungrouped = setup({ pages: [page(MIXED)] });
      ungrouped.fixture.detectChanges();
      expect(ungrouped.el.querySelector('[data-testid="favorites-group-header"]')).toBeNull();
      TestBed.resetTestingModule();

      localStorage.setItem(FAVORITES_GROUP_STORAGE_KEY, 'library');
      const { el, fixture, cmp, getFavorites } = setup({ pages: [page(MIXED, { favoriteGroups: GROUPS })] });
      fixture.detectChanges();
      expect(el.querySelectorAll('[data-testid="favorites-group-header"]').length).toBe(2);
      cmp.setLibrary('libA');
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="favorites-group-header"]')).toBeNull();
      expect(getFavorites.mock.lastCall![2]).not.toHaveProperty('groupByLibrary');
    });

    it('groupHeaders: one per library run, names from the answer, else the library list', () => {
      const headers = groupHeaders([node('x', 'libA'), node('y', 'libA'), node('z', 'libC')], GROUPS, [...LIBS, lib('libC', 'Gamma')]);
      expect(headers).toEqual([{ libraryId: 'libA', name: 'Alpha Lib', count: 2 }, null, { libraryId: 'libC', name: 'Gamma', count: null }]);
    });
  });
});
