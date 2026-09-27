import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { FavoritesComponent } from './favorites.component';
import { ApiService } from '../../core/api/api.service';
import { FavoritesStateService } from '../../core/favorites/favorites-state.service';
import { CatalogNodeDto, PageResponse } from '../../core/api/api-types';
import { favoriteQueryParams, favoriteTrackKey, isFavoriteStack } from './favorite-stack';

/**
 * Favorites page (1.21.0) with stacks (1.27.0): two or more starred archives in one
 * folder arrive from `GET /favorites` as ONE folder item carrying `favoriteStackCount`;
 * the page renders it as a stacked card that opens the folder with `?favorites=1`.
 */
describe('FavoritesComponent stacks (1.27.0)', () => {
  function node(id: string, kind: 'Folder' | 'Archive', extra: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind, displayName: `Name ${id}`, availability: 'Available',
      coverUrl: null, childFolderCount: null, childArchiveCount: null, pageCount: null, readingState: null,
      lastReadPage: null, readerDefault: null, isRead: false, readRollup: null, isFavorite: true, ...extra,
    };
  }

  function page(items: CatalogNodeDto[], hasMore = false): PageResponse<CatalogNodeDto> {
    return { items, totalCount: items.length, nextCursor: hasMore ? 'f:1:1' : null, hasMore };
  }

  function setup(...pages: PageResponse<CatalogNodeDto>[]) {
    const getFavorites = vi.fn();
    pages.forEach((p) => getFavorites.mockReturnValueOnce(of(p)));
    const apiSpy = { getFavorites, setFavorite: vi.fn().mockReturnValue(of(undefined)) };
    TestBed.configureTestingModule({
      imports: [FavoritesComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: ApiService, useValue: apiSpy }],
    });
    const fixture = TestBed.createComponent(FavoritesComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, getFavorites };
  }

  afterEach(() => vi.restoreAllMocks());

  it('renders a stack as a stacked card that opens the folder filtered to favorites', () => {
    const { el } = setup(page([
      node('fold', 'Folder', { favoriteStackCount: 4, isFavorite: false, coverUrl: '/api/v1/items/c1/cover' }),
      node('arc', 'Archive'),
    ]));

    const cards = el.querySelectorAll('.fav-card');
    expect(cards.length).toBe(2);

    const stack = cards[0].querySelector('a.stack-link') as HTMLAnchorElement;
    expect(stack).not.toBeNull();
    expect(stack.getAttribute('href')).toBe('/libraries/lib1/browse/fold?favorites=1');
    expect(stack.querySelector('.count-badge')?.textContent?.trim()).toBe('4');
    expect(cards[0].querySelector('.fav-sub')?.textContent?.trim()).toBe('4 favorites');
    expect(stack.getAttribute('aria-label')).toBe('Name fold, 4 favorites');
    // A stack has no star: it would star the folder itself.
    expect(cards[0].querySelector('app-star-toggle')).toBeNull();

    const single = cards[1].querySelector('a.fav-link') as HTMLAnchorElement;
    expect(single.getAttribute('href')).toBe('/reader/arc');
    expect(cards[1].querySelector('app-star-toggle')).not.toBeNull();
  });

  it('keeps a starred folder and the stack of its children apart', () => {
    const { el, fixture } = setup(page([
      node('fold', 'Folder', { favoriteStackCount: 2 }),
      node('fold', 'Folder'),
    ]));
    expect(el.querySelectorAll('.fav-card').length).toBe(2);
    expect(el.querySelectorAll('a.stack-link').length).toBe(1);

    // Unstarring the folder elsewhere drops its own card; the stack stands for its
    // starred children and stays.
    TestBed.inject(FavoritesStateService).setFavorite('fold', false).subscribe();
    fixture.detectChanges();
    expect(fixture.componentInstance.nodes().map(favoriteTrackKey)).toEqual(['stack:fold']);
    expect(el.querySelectorAll('a.stack-link').length).toBe(1);
  });

  it('appends the next page after Load more', () => {
    const { el, fixture, getFavorites } = setup(
      page([node('a1', 'Archive')], true),
      page([node('fold', 'Folder', { favoriteStackCount: 2 })]),
    );
    fixture.componentInstance.loadMore();
    fixture.detectChanges();
    expect(getFavorites.mock.calls[1][0]).toBe('f:1:1');
    expect(el.querySelectorAll('.fav-card').length).toBe(2);
    expect(el.querySelectorAll('a.stack-link').length).toBe(1);
  });

  it('helpers: only a count of 2+ is a stack; singles carry no query params', () => {
    expect(isFavoriteStack(node('f', 'Folder', { favoriteStackCount: 2 }))).toBe(true);
    expect(isFavoriteStack(node('f', 'Folder', { favoriteStackCount: null }))).toBe(false);
    expect(isFavoriteStack(node('f', 'Folder'))).toBe(false);
    expect(favoriteQueryParams(node('a', 'Archive'))).toBeNull();
    expect(favoriteQueryParams(node('f', 'Folder', { favoriteStackCount: 3 }))).toEqual({ favorites: '1' });
  });
});
