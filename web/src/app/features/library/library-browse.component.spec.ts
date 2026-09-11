import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, LibraryDto, LibraryViewPreferencesDto, PageResponse, JumpIndexBucketDto, ReadingState } from '../../core/api/api-types';

/**
 * Breadcrumb tests for LibraryBrowseComponent. Two behaviors:
 *  - (1.3.1 fix — Lane A) the library-name crumb must link to the browse root
 *    `/libraries/{id}/browse` — not the library landing page `/libraries/{id}` —
 *    and must be clickable at the root level too (no sub-folder breadcrumbs).
 *  - (1.5.0 Task B) the folder you are currently in is shown as the LAST segment
 *    as plain, non-clickable text; ancestors stay clickable. Its name comes from
 *    the existing `GET /nodes/{id}` lookup (frontend-only, no contract change).
 */
describe('LibraryBrowseComponent breadcrumbs', () => {
  function setup(
    libraryId: string,
    parentId: string | null,
    trail: { id: string; displayName: string }[] = [],
    folderName = 'Current Folder',
  ) {
    const prefs: LibraryViewPreferencesDto = { viewMode: 'grid', density: 'comfortable', sort: 'name' };
    const libs: LibraryDto[] = [{ id: libraryId, name: 'Test Lib', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }];
    const emptyPage: PageResponse<CatalogNodeDto> = { items: [], totalCount: 0, nextCursor: null, hasMore: false };
    const currentNode = {
      id: parentId ?? 'x', parentId: 'p', libraryId, kind: 'Folder', displayName: folderName,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false,
    } as CatalogNodeDto;

    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of(prefs)),
      getLibraries: vi.fn().mockReturnValue(of(libs)),
      browseLibrary: vi.fn().mockReturnValue(of(emptyPage)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: parentId ?? 'x', trail })),
      getNode: vi.fn().mockReturnValue(of(currentNode)),
    };
    const authSpy = { isAdmin: () => false };

    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => k === 'libraryId' ? libraryId : parentId }) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges(); // ngOnInit → preferences → route subscription → loads
    return { fixture };
  }

  it('links the library-name crumb to the browse root when sub-breadcrumbs exist', () => {
    const { fixture } = setup('lib1', 'node1', [{ id: 'anc1', displayName: 'Ancestor' }]);
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll('.breadcrumbs a');
    // First link is the library-name crumb; it must point to /libraries/lib1/browse
    expect(links.length).toBeGreaterThan(0);
    expect(links[0].getAttribute('href')).toBe('/libraries/lib1/browse');
    // The ancestor is a clickable crumb pointing at its own browse node.
    expect(links[1].getAttribute('href')).toBe('/libraries/lib1/browse/anc1');
  });

  it('links the library-name crumb to the browse root at the root level too', () => {
    const { fixture } = setup('lib1', null);
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll('.breadcrumbs a');
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('/libraries/lib1/browse');
  });

  it('does not link to the library landing page /libraries/{id}', () => {
    const { fixture } = setup('lib1', null);
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll('.breadcrumbs a');
    for (const link of Array.from(links)) {
      expect(link.getAttribute('href')).not.toBe('/libraries/lib1');
    }
  });

  // Task B (1.5.0): current folder as the last, non-clickable segment.
  it('renders the current folder name as plain, non-clickable text', () => {
    const { fixture } = setup('lib1', 'node1', [{ id: 'anc1', displayName: 'Ancestor' }], 'My Folder');
    const el: HTMLElement = fixture.nativeElement;

    const current = el.querySelector('.breadcrumbs .current');
    expect(current).not.toBeNull();
    expect(current!.textContent?.trim()).toBe('My Folder');
    // It must not be a link (no <a>, and aria-current marks it as the location).
    expect(current!.tagName).toBe('SPAN');
    expect(current!.getAttribute('aria-current')).toBe('page');

    // The folder name must not appear as any clickable breadcrumb link.
    const linkHrefs = Array.from(el.querySelectorAll('.breadcrumbs a')).map((a) => a.textContent?.trim());
    expect(linkHrefs).not.toContain('My Folder');
  });

  it('populates the current folder from the node lookup (getNode)', () => {
    const { fixture } = setup('lib1', 'node1', [], 'Deep One');
    expect(fixture.componentInstance.currentFolderName()).toBe('Deep One');
  });

  it('shows no current-folder segment at the library root', () => {
    const { fixture } = setup('lib1', null);
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.breadcrumbs .current')).toBeNull();
    expect(fixture.componentInstance.currentFolderName()).toBe('');
  });
});

/**
 * Unit tests for the A–Z/script jump rail in LibraryBrowseComponent (1.4.0 Lane E).
 *
 * These tests drive the component through its public signals and the
 * jumpToBucket method. The route is faked to the library root (no nodeId) so
 * the rail loads; the API calls are intercepted with HttpTestingController.
 */
describe('LibraryBrowseComponent jump rail', () => {
  function create(libId = 'lib1') {
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        {
          provide: ActivatedRoute,
          useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? libId : null) }) },
        },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    const httpMock = TestBed.inject(HttpTestingController);
    return { fixture, httpMock };
  }

  /** Flush the preferences + libraries + browse + jump-index calls fired on init. */
  function flushInitial(httpMock: HttpTestingController, buckets: JumpIndexBucketDto[] = []) {
    // getLibraryPreferences
    httpMock.match((req) => req.url.endsWith('/reading/library-preferences'))[0]
      .flush({ viewMode: 'grid', density: 'comfortable', sort: 'name' });
    // getLibraries (for library name)
    httpMock.match((req) => req.url.endsWith('/libraries'))[0]
      .flush([{ id: 'lib1', name: 'Test', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }]);
    // browseLibrary
    httpMock.match((req) => req.url.includes('/libraries/lib1/browse'))[0]
      .flush({ items: [], totalCount: 0, nextCursor: null, hasMore: false });
    // getJumpIndex
    httpMock.match((req) => req.url.endsWith('/libraries/lib1/jump-index'))[0]
      .flush({ libraryId: 'lib1', buckets });
  }

  it('renders the rail with buckets from the jump-index endpoint', () => {
    const { fixture, httpMock } = create();
    fixture.detectChanges();
    flushInitial(httpMock, [
      { label: 'A', count: 3, firstCursor: null },
      { label: 'B', count: 2, firstCursor: 'cursorA' },
      { label: 'Kana', count: 5, firstCursor: 'cursorB' },
    ]);
    fixture.detectChanges();

    const chips = fixture.nativeElement.querySelectorAll('.jump-chip') as HTMLElement[];
    expect(chips.length).toBe(3);
    expect(chips[0].textContent?.trim()).toBe('A');
    expect(chips[1].textContent?.trim()).toBe('B');
    expect(chips[2].textContent?.trim()).toBe('Kana');
  });

  it('does not render the rail when there are no buckets', () => {
    const { fixture, httpMock } = create();
    fixture.detectChanges();
    flushInitial(httpMock, []);

    const rail = fixture.nativeElement.querySelector('.jump-rail');
    expect(rail).toBeNull();
  });

  it('jumpToBucket sets the cursor and reloads from the bucket cursor', () => {
    const { fixture, httpMock } = create();
    fixture.detectChanges();
    flushInitial(httpMock, [
      { label: 'A', count: 1, firstCursor: null },
      { label: 'B', count: 1, firstCursor: 'cursorA' },
    ]);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    expect(comp.activeJump()).toBeNull();

    comp.jumpToBucket({ label: 'B', count: 1, firstCursor: 'cursorA' });
    expect(comp.activeJump()).toBe('B');

    // A browse request should fire with the bucket cursor.
    const browseReq = httpMock.match((req) =>
      req.url.includes('/libraries/lib1/browse') && req.params.get('cursor') === 'cursorA')[0];
    browseReq.flush({ items: [{ id: 'b1', displayName: 'Beta' }], totalCount: 1, nextCursor: null, hasMore: false });
    httpMock.verify();

    expect(comp.nodes().length).toBe(1);
    expect(comp.nodes()[0].displayName).toBe('Beta');
  });

  it('marks the active bucket chip', () => {
    const { fixture, httpMock } = create();
    fixture.detectChanges();
    flushInitial(httpMock, [
      { label: 'A', count: 1, firstCursor: null },
      { label: 'B', count: 1, firstCursor: 'cursorA' },
    ]);
    fixture.detectChanges();

    comp_jump(fixture, 'B');
    fixture.detectChanges();

    const chips = fixture.nativeElement.querySelectorAll('.jump-chip') as HTMLElement[];
    expect(chips[0].classList.contains('active')).toBe(false);
    expect(chips[1].classList.contains('active')).toBe(true);
  });

  it('hides the rail when the sort changes away from name', () => {
    const { fixture, httpMock } = create();
    fixture.detectChanges();
    flushInitial(httpMock, [
      { label: 'A', count: 1, firstCursor: null },
    ]);
    fixture.detectChanges();

    const comp = fixture.componentInstance;
    expect(comp.jumpBuckets().length).toBe(1);

    // setSort fires a persist + a browse; flush both.
    comp.setSort('recentlyAdded');
    httpMock.match((req) => req.url.endsWith('/reading/library-preferences') && req.method === 'PUT')[0]
      .flush({});
    httpMock.match((req) => req.url.includes('/libraries/lib1/browse'))[0]
      .flush({ items: [], totalCount: 0, nextCursor: null, hasMore: false });

    expect(comp.jumpBuckets().length).toBe(0);
  });
});

/** Helper: jump to a bucket and flush the resulting browse request. */
function comp_jump(fixture: ComponentFixture<LibraryBrowseComponent>, label: string) {
  const comp = fixture.componentInstance;
  const buckets = comp.jumpBuckets();
  const bucket = buckets.find((b) => b.label === label);
  expect(bucket).toBeDefined();
  comp.jumpToBucket(bucket!);
}

/**
 * Card view + size slider (1.6.0). The former Grid/Poster modes collapse into a
 * single Card view whose size is a continuous slider that subsumes the old
 * comfortable/compact density. These tests drive the migration of pre-1.6.0 stored
 * preferences and the persistence of the new card size through the component's
 * public surface.
 */
describe('LibraryBrowseComponent card view', () => {
  function setup(prefs: Partial<LibraryViewPreferencesDto>) {
    const fullPrefs = { viewMode: 'card', density: 'comfortable', sort: 'name', ...prefs } as LibraryViewPreferencesDto;
    const libs: LibraryDto[] = [{ id: 'lib1', name: 'Test Lib', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }];
    const emptyPage: PageResponse<CatalogNodeDto> = { items: [], totalCount: 0, nextCursor: null, hasMore: false };
    const setLibraryPreferences = vi.fn().mockReturnValue(of(undefined));

    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of(fullPrefs)),
      setLibraryPreferences,
      getLibraries: vi.fn().mockReturnValue(of(libs)),
      browseLibrary: vi.fn().mockReturnValue(of(emptyPage)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getNode: vi.fn().mockReturnValue(of({} as CatalogNodeDto)),
    };
    const authSpy = { isAdmin: () => false };

    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, setLibraryPreferences };
  }

  it('offers exactly Card and List in the view menu (Grid/Poster/density are gone)', () => {
    const { comp } = setup({ viewMode: 'card' });
    expect(comp.viewOptions.map((o) => o.value)).toEqual(['card', 'list']);
  });

  it('migrates legacy poster+comfortable to card at the former poster size', () => {
    const { comp } = setup({ viewMode: 'poster', density: 'comfortable' });
    expect(comp.viewMode()).toBe('card');
    expect(comp.cardSize()).toBe(210);
  });

  it('migrates legacy grid+compact to card at the former compact grid size', () => {
    const { comp } = setup({ viewMode: 'grid', density: 'compact' });
    expect(comp.viewMode()).toBe('card');
    expect(comp.cardSize()).toBe(112);
  });

  it('keeps a stored list preference as list', () => {
    const { comp } = setup({ viewMode: 'list' });
    expect(comp.viewMode()).toBe('list');
  });

  it('prefers an explicit stored cardSize over the legacy derivation', () => {
    const { comp } = setup({ viewMode: 'card', density: 'compact', cardSize: '200' });
    expect(comp.cardSize()).toBe(200);
  });

  it('setCardSize clamps out-of-range values and persists the new size', () => {
    const { comp, setLibraryPreferences } = setup({ viewMode: 'card' });
    comp.setCardSize(9999);
    expect(comp.cardSize()).toBe(comp.cardSizeMax);
    expect(setLibraryPreferences).toHaveBeenCalledWith(
      expect.objectContaining({ viewMode: 'card', cardSize: String(comp.cardSizeMax) }));
  });

  it('shows the size slider only in card mode', () => {
    const { fixture, comp } = setup({ viewMode: 'card' });
    expect(fixture.nativeElement.querySelector('.size-slider')).not.toBeNull();
    comp.setViewMode('list');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.size-slider')).toBeNull();
  });

  it('feeds the card size into the grid as the --card-size custom property', () => {
    const { fixture } = setup({ viewMode: 'card', cardSize: '180' });
    const nodes = fixture.nativeElement.querySelector('.nodes') as HTMLElement;
    expect(nodes.style.getPropertyValue('--card-size')).toBe('180px');
  });
});

/**
 * Mark-unread fix (1.6.0). Clearing the sticky read-mark is a no-op for an item
 * that was opened but never marked read (InProgress, no read-mark), so it would
 * stay "reading". The selection-mode mark-unread path must ALSO reset progress for
 * InProgress archives so they leave the browse "Reading" badge and the
 * continue-reading strip.
 */
describe('LibraryBrowseComponent mark unread', () => {
  function archive(id: string, state: ReadingState | null, isRead = false): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: 10, readingState: state, lastReadPage: state === 'InProgress' ? 3 : null,
      readerDefault: null, isRead,
    } as CatalogNodeDto;
  }

  function setup(nodes: CatalogNodeDto[]) {
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const setItemRead = vi.fn().mockReturnValue(of({ itemId: '', isRead: false }));
    const resetProgress = vi.fn().mockReturnValue(of(undefined));
    const setFolderRead = vi.fn().mockReturnValue(of({ affected: 0, total: 0 }));

    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getNode: vi.fn().mockReturnValue(of({} as CatalogNodeDto)),
      setItemRead,
      resetProgress,
      setFolderRead,
    };
    const authSpy = { isAdmin: () => false };

    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, setItemRead, resetProgress };
  }

  function selectAll(comp: LibraryBrowseComponent) {
    comp.selected.set(new Set(comp.nodes().map((n) => n.id)));
  }

  it('resets progress for an InProgress archive when marking unread and clears its Reading state', () => {
    const { comp, setItemRead, resetProgress } = setup([archive('a1', 'InProgress')]);
    selectAll(comp);

    comp.bulkMarkRead(false);

    expect(setItemRead).toHaveBeenCalledWith('a1', false);
    expect(resetProgress).toHaveBeenCalledWith('a1');
    const node = comp.nodes().find((n) => n.id === 'a1')!;
    expect(node.readingState).toBe('Unread');
    expect(node.lastReadPage).toBeNull();
    expect(node.isRead).toBe(false);
  });

  it('does not reset progress for an archive that is not InProgress', () => {
    const { comp, setItemRead, resetProgress } = setup([archive('a2', 'Unread')]);
    selectAll(comp);

    comp.bulkMarkRead(false);

    expect(setItemRead).toHaveBeenCalledWith('a2', false);
    expect(resetProgress).not.toHaveBeenCalled();
  });

  it('never resets progress when marking READ (the intended mark-read path is unchanged)', () => {
    const { comp, setItemRead, resetProgress } = setup([archive('a3', 'InProgress')]);
    selectAll(comp);

    comp.bulkMarkRead(true);

    expect(setItemRead).toHaveBeenCalledWith('a3', true);
    expect(resetProgress).not.toHaveBeenCalled();
    expect(comp.nodes().find((n) => n.id === 'a3')!.isRead).toBe(true);
  });
});
