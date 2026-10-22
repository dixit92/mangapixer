import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { CatalogNodeDto, JumpIndexBucketDto, JumpIndexDto, PageResponse } from '../../core/api/api-types';

/**
 * The smart jump rail in browse (1.41.0): requested for the listed FOLDER and the current name-sort DIRECTION (and the Volumes-view
 * choice, so the server can hide it there), rendered by `app-jump-rail` in letters or numbers mode, and a scroll-spy that follows
 * each bucket's first node by position - also when the window starts in the middle of a bucket after a jump.
 */
describe('LibraryBrowseComponent smart jump rail (1.41.0)', () => {
  function node(id: string): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: `${id}.cbz`, availability: 'Available', coverUrl: null,
      childFolderCount: null, childArchiveCount: null, pageCount: 10, readingState: 'Unread', lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null,
    } as CatalogNodeDto;
  }

  const numbers: JumpIndexBucketDto[] = [
    { label: '0', count: 50, firstCursor: null, firstNodeId: 'c000', range: '0-49' },
    { label: '50', count: 50, firstCursor: 'k049', firstNodeId: 'c050', range: '50-99' },
    { label: 'Other', count: 1, firstCursor: 'k099', firstNodeId: 'extra', range: null },
  ];

  function setup(opts: { index?: JumpIndexDto | (() => Subject<JumpIndexDto>); nodes?: CatalogNodeDto[]; page?: Partial<PageResponse<CatalogNodeDto>> } = {}) {
    const items = opts.nodes ?? [node('c000'), node('c001')];
    const page: PageResponse<CatalogNodeDto> = { items, totalCount: items.length, nextCursor: null, hasMore: false, ...opts.page };
    const index = opts.index ?? { libraryId: 'lib1', parentId: 'f1', direction: 'asc', mode: 'numbers', buckets: numbers };
    const apiSpy = {
      getVolumeView: vi.fn().mockReturnValue(of({ nodeId: 'f1', available: false, active: false, consolidated: false, stackCount: 0 })),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc' })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Numbered Saga' } as CatalogNodeDto)),
      getJumpIndex: vi.fn().mockImplementation(() => (typeof index === 'function' ? index() : of(index))),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => false, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : 'f1') }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, apiSpy };
  }

  it('asks for the jump index of the open folder in the current direction and shows its number chips', () => {
    const { el, apiSpy, comp } = setup();

    expect(apiSpy.getJumpIndex).toHaveBeenCalledWith('lib1', 'f1', 'asc', null);
    expect(comp.jumpMode()).toBe('numbers');
    const rail = el.querySelector('nav.jump-rail')!;
    expect(rail.getAttribute('aria-label')).toBe('Jump to number');
    expect(Array.from(rail.querySelectorAll('.jump-chip')).map((c) => c.textContent?.trim())).toEqual(['0', '50', 'Other']);
    expect(el.querySelector('.jump-menu-trigger mat-icon')!.textContent?.trim()).toBe('format_list_numbered');
  });

  it('reloads the index for the descending direction instead of hiding the rail', () => {
    const { comp, apiSpy } = setup();

    comp.setSortDirection('desc');

    expect(apiSpy.getJumpIndex).toHaveBeenLastCalledWith('lib1', 'f1', 'desc', null);
    expect(comp.jumpBuckets().length).toBe(3);
  });

  it('drops an index answer for a direction the user has already left', () => {
    const pending: Subject<JumpIndexDto>[] = [];
    const { comp } = setup({ index: () => { const s = new Subject<JumpIndexDto>(); pending.push(s); return s; } });

    comp.setSortDirection('desc');
    expect(pending.length).toBe(2);
    pending[0].next({ libraryId: 'lib1', parentId: 'f1', direction: 'asc', mode: 'numbers', buckets: numbers });

    expect(comp.jumpBuckets().length).toBe(0);
  });

  it('does not show a rail of a single chip', () => {
    const { el } = setup({ index: { libraryId: 'lib1', mode: 'letters', buckets: [{ label: 'A', count: 3, firstCursor: null, firstNodeId: 'c000' }] } });

    expect(el.querySelector('nav.jump-rail')).toBeNull();
    expect(el.querySelector('.jump-menu-trigger')).toBeNull();
  });

  /** Fake layout: the rail's bottom edge and each card's bottom edge, in viewport px. */
  function layout(el: HTMLElement, railBottom: number, cardBottoms: number[]): void {
    (el.querySelector('.jump-rail') as HTMLElement).getBoundingClientRect = () => ({ bottom: railBottom, top: railBottom - 40 } as DOMRect);
    const cards = el.querySelectorAll<HTMLElement>('.node-wrap');
    expect(cards.length).toBe(cardBottoms.length);
    cards.forEach((c, i) => { c.getBoundingClientRect = () => ({ bottom: cardBottoms[i], top: cardBottoms[i] - 200 } as DOMRect); });
  }

  it('scroll-spy follows bucket starts by position, also in a window that starts inside a bucket', () => {
    // After a jump the window starts at c048 (inside 0-49); c050 starts the "50" bucket.
    const { comp, el } = setup({ nodes: [node('c048'), node('c049'), node('c050'), node('c051')], page: { hasPrevious: true, prevCursor: 'k047' } });

    layout(el, 100, [300, 300, 520, 520]);
    comp.updateActiveJump();
    expect(comp.activeJump()?.label).toBe('0');

    layout(el, 100, [80, 80, 300, 300]);
    comp.updateActiveJump();
    expect(comp.activeJump()?.label).toBe('50');
  });

  /** The document's scroll metrics (jsdom has no layout): a page `height` tall in a viewport `view` tall, scrolled to `top`. */
  function scrollMetrics(height: number, view: number, top: number): void {
    const root = document.documentElement;
    Object.defineProperty(root, 'scrollHeight', { configurable: true, get: () => height });
    Object.defineProperty(root, 'clientHeight', { configurable: true, get: () => view });
    Object.defineProperty(root, 'scrollTop', { configurable: true, get: () => top });
  }

  afterEach(() => {
    const root = document.documentElement as unknown as Record<string, unknown>;
    for (const key of ['scrollHeight', 'clientHeight', 'scrollTop']) delete root[key];
  });

  it('1.47.0: after a chip near the list end the page cannot scroll further, so the clicked chip stays highlighted', () => {
    // The window ends at c051; the page is at its end, so "50" cannot reach the top row: the top row (c048, c049) belongs to "0".
    const { comp, el } = setup({ nodes: [node('c048'), node('c049'), node('c050'), node('c051')], page: { hasPrevious: true, prevCursor: 'k047' } });
    comp.activeJump.set(comp.jumpBuckets()[1]);
    layout(el, 100, [300, 300, 520, 520]);

    scrollMetrics(3000, 900, 2100); // at the end
    comp.updateActiveJump();
    expect(comp.activeJump()?.label).toBe('50');

    // Scrolled up a little: the page can scroll further again, so the spy follows the top row.
    scrollMetrics(3000, 900, 1800);
    comp.updateActiveJump();
    expect(comp.activeJump()?.label).toBe('0');
  });

  it('1.47.0: at the end of the page the highlight still follows the top row when the current bucket is no longer on screen', () => {
    const { comp, el } = setup({ nodes: [node('c048'), node('c049'), node('c050'), node('c051')], page: { hasPrevious: true, prevCursor: 'k047' } });
    comp.activeJump.set(comp.jumpBuckets()[0]);
    layout(el, 100, [80, 80, 300, 300]); // the first card of "0" (c000) is not loaded; "50" starts in view
    scrollMetrics(3000, 900, 2100);

    comp.updateActiveJump();

    expect(comp.activeJump()?.label).toBe('50');
  });

  it('a chip whose first card is loaded scrolls to that card, wherever the window started', () => {
    const { comp, el, apiSpy } = setup({ nodes: [node('c048'), node('c049'), node('c050')], page: { hasPrevious: true, prevCursor: 'k047' } });
    const scrollBy = vi.fn();
    (window as unknown as { scrollBy: unknown }).scrollBy = scrollBy;
    layout(el, 100, [300, 300, 520]);

    comp.jumpToBucket(comp.jumpBuckets()[1]);

    expect(scrollBy).toHaveBeenCalled();
    expect(apiSpy.browseLibrary).toHaveBeenCalledTimes(1); // no reload
    expect(comp.activeJump()?.label).toBe('50');
  });
});
