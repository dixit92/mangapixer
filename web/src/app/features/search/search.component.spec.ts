import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SearchComponent } from './search.component';
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
    const apiSpy = { search: vi.fn().mockReturnValue(of(response)) };
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
    const apiSpy = { search: vi.fn().mockReturnValue(of(response)) };
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
