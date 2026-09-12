import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { ActivatedRoute, provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError, Subject } from 'rxjs';

import { LibraryBrowseComponent, jumpLabelFor } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
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

/**
 * Range selection (1.7.0). A hundreds-of-files folder needs contiguous-range
 * selection ("Shift-select a series"), not one-by-one tap toggling. These
 * tests drive the component's public surface directly (onCardClick with a
 * synthetic MouseEvent, and the long-press / "Select to here" methods) rather
 * than simulating real pointer timing, since the range MATH and mode
 * transitions are what must be correct.
 */
describe('LibraryBrowseComponent range multi-select', () => {
  function node(id: string, isRead = false): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: 10, readingState: null, lastReadPage: null, readerDefault: null, isRead,
    } as CatalogNodeDto;
  }

  function setup(nodes: CatalogNodeDto[]) {
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
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
    return { fixture, comp: fixture.componentInstance };
  }

  function mouseEvent(opts: Partial<MouseEvent> = {}): MouseEvent {
    return {
      shiftKey: false, ctrlKey: false, metaKey: false,
      preventDefault: vi.fn(), stopPropagation: vi.fn(),
      ...opts,
    } as unknown as MouseEvent;
  }

  const ids = ['a', 'b', 'c', 'd', 'e'];
  function setupFive() {
    return setup(ids.map((id) => node(id)));
  }

  it('a plain click in select mode toggles one item and sets the anchor', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);

    comp.onCardClick(mouseEvent(), node('b'));

    expect([...comp.selected()]).toEqual(['b']);
    expect(comp.anchorIndex()).toBe(1);
  });

  it('a plain click toggles the item off again (anchor still moves to it)', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b'));

    comp.onCardClick(mouseEvent(), node('b'));

    expect(comp.selected().size).toBe(0);
    expect(comp.anchorIndex()).toBe(1);
  });

  it('ctrl-click toggles a single item just like a plain click', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);

    comp.onCardClick(mouseEvent({ ctrlKey: true }), node('c'));

    expect([...comp.selected()]).toEqual(['c']);
    expect(comp.anchorIndex()).toBe(2);
  });

  it('shift-click fills the inclusive range from the anchor to the clicked card, in display order', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b')); // anchor = index 1

    comp.onCardClick(mouseEvent({ shiftKey: true }), node('d')); // index 3

    expect([...comp.selected()].sort()).toEqual(['b', 'c', 'd']);
  });

  it('shift-click works backwards from the anchor too', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('d')); // anchor = index 3

    comp.onCardClick(mouseEvent({ shiftKey: true }), node('b')); // index 1

    expect([...comp.selected()].sort()).toEqual(['b', 'c', 'd']);
  });

  it('shift-click adds the range to whatever is already selected rather than replacing it', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('a')); // select + anchor a (index 0)
    comp.onCardClick(mouseEvent({ ctrlKey: true }), node('e')); // also select e, anchor moves to e (index 4)

    comp.onCardClick(mouseEvent({ shiftKey: true }), node('c')); // range from e(4) to c(2)

    expect([...comp.selected()].sort()).toEqual(['a', 'c', 'd', 'e']);
  });

  it('shift-click without a prior anchor toggles the single card instead', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);

    comp.onCardClick(mouseEvent({ shiftKey: true }), node('c'));

    expect([...comp.selected()]).toEqual(['c']);
    expect(comp.anchorIndex()).toBe(2);
  });

  it('a click outside select mode is a no-op (normal navigation)', () => {
    const { comp } = setupFive();

    comp.onCardClick(mouseEvent(), node('b'));

    expect(comp.selected().size).toBe(0);
    expect(comp.anchorIndex()).toBeNull();
  });

  // --- Touch: long-press -> "Select to here" ---

  it('long-press enters select mode and selects+anchors the pressed card when not already selecting', () => {
    const { comp } = setupFive();

    (comp as unknown as { onLongPress: (n: CatalogNodeDto) => void }).onLongPress(node('c'));

    expect(comp.selectMode()).toBe(true);
    expect([...comp.selected()]).toEqual(['c']);
    expect(comp.anchorIndex()).toBe(2);
    expect(comp.rangePromptNode()).toBeNull();
  });

  it('long-press with an anchor already set opens "Select to here" without changing the selection yet', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b')); // anchor = index 1

    (comp as unknown as { onLongPress: (n: CatalogNodeDto) => void }).onLongPress(node('d'));

    expect(comp.rangePromptNode()?.id).toBe('d');
    expect([...comp.selected()]).toEqual(['b']); // unchanged until confirmed
  });

  it('confirmSelectToHere fills the range from the anchor to the prompted card', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b'));
    (comp as unknown as { onLongPress: (n: CatalogNodeDto) => void }).onLongPress(node('d'));

    comp.confirmSelectToHere();

    expect([...comp.selected()].sort()).toEqual(['b', 'c', 'd']);
    expect(comp.rangePromptNode()).toBeNull();
  });

  it('dismissRangePrompt closes the prompt without selecting the range', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b'));
    (comp as unknown as { onLongPress: (n: CatalogNodeDto) => void }).onLongPress(node('d'));

    comp.dismissRangePrompt();

    expect(comp.rangePromptNode()).toBeNull();
    expect([...comp.selected()]).toEqual(['b']);
  });

  // --- Whole-folder selection ---

  it('selectAll selects every currently-listed node', () => {
    const { comp } = setupFive();

    comp.selectAll();

    expect([...comp.selected()].sort()).toEqual(ids);
  });

  it('selectAllUnread selects only the unread listed nodes', () => {
    const { comp } = setup([node('a', true), node('b', false), node('c', false)]);

    comp.selectAllUnread();

    expect([...comp.selected()].sort()).toEqual(['b', 'c']);
  });

  it('selectAllRead selects only the read listed nodes', () => {
    const { comp } = setup([node('a', true), node('b', false), node('c', true)]);

    comp.selectAllRead();

    expect([...comp.selected()].sort()).toEqual(['a', 'c']);
  });

  it('clearSelection also resets the anchor and any open range prompt', () => {
    const { comp } = setupFive();
    comp.selectMode.set(true);
    comp.onCardClick(mouseEvent(), node('b'));
    (comp as unknown as { onLongPress: (n: CatalogNodeDto) => void }).onLongPress(node('d'));

    comp.clearSelection();

    expect(comp.selected().size).toBe(0);
    expect(comp.anchorIndex()).toBeNull();
    expect(comp.rangePromptNode()).toBeNull();
  });
});

/**
 * Stale read-status after Back (1.7.1 fix). The 1.6.2 `LibraryBrowseReuseStrategy`
 * RETAINS this component instance across a reader round-trip (to preserve scroll
 * and avoid the black-frame/top-reset regression) instead of destroying and
 * re-creating it, so `ngOnInit` never re-runs on return from the reader — nothing
 * would otherwise re-fetch the list. `ReadStateService` closes that gap: the
 * reader notifies on exit, and the subscription set up once in `ngOnInit` (which
 * — being a plain subscription on a component that is only DETACHED, never
 * destroyed, by the reuse strategy — survives the whole round trip) patches just
 * the affected card in place.
 */
describe('LibraryBrowseComponent stale read-status refresh (1.7.1)', () => {
  function node(id: string, isRead = false): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: 10, readingState: isRead ? 'Completed' : 'Unread', lastReadPage: null,
      readerDefault: null, isRead,
    } as CatalogNodeDto;
  }

  function progressDto(overrides: Partial<{ pageIndex: number; state: ReadingState }> = {}) {
    return {
      itemId: 'a1', pageIndex: overrides.pageIndex ?? 9, contentVersion: 1,
      updatedAt: new Date().toISOString(), state: overrides.state ?? 'Completed',
      revision: 1, isStale: false,
    };
  }

  function setup(nodes: CatalogNodeDto[]) {
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const getReadMark = vi.fn();
    const getProgress = vi.fn();
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getReadMark,
      getProgress,
    };
    const authSpy = { isAdmin: () => false };
    const readState = new ReadStateService();

    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
        { provide: ReadStateService, useValue: readState },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { comp: fixture.componentInstance, getReadMark, getProgress, readState };
  }

  it('patches the affected card in place when the reader announces a read-state change', () => {
    const { comp, getReadMark, getProgress, readState } = setup([node('a1', false), node('a2', false)]);
    getReadMark.mockReturnValue(of({ itemId: 'a1', isRead: true }));
    getProgress.mockReturnValue(of(progressDto({ pageIndex: 9, state: 'Completed' })));

    readState.notifyChanged('a1');

    expect(getReadMark).toHaveBeenCalledWith('a1');
    expect(getProgress).toHaveBeenCalledWith('a1');
    const updated = comp.nodes().find((n) => n.id === 'a1')!;
    expect(updated.isRead).toBe(true);
    expect(updated.readingState).toBe('Completed');
    expect(updated.lastReadPage).toBe(9);
    // The other card, and the array identity of unrelated entries, are untouched.
    expect(comp.nodes().find((n) => n.id === 'a2')!.isRead).toBe(false);
  });

  it('never rebuilds the node list itself — only the one changed node is patched', () => {
    const { comp, getReadMark, getProgress, readState } = setup([node('a1', false)]);
    const browseLibrary = (TestBed.inject(ApiService) as unknown as { browseLibrary: ReturnType<typeof vi.fn> }).browseLibrary;
    browseLibrary.mockClear();
    getReadMark.mockReturnValue(of({ itemId: 'a1', isRead: true }));
    getProgress.mockReturnValue(of(progressDto()));

    readState.notifyChanged('a1');

    // 1.7.3: itemChanged$ ALSO drives a targeted pageSize:1 fetch for the pinned
    // Continue row (see "continue-row refresh" below) — but that fetch never
    // touches nodes()/cursor/hasMore, so the loaded list stays exactly as it was.
    expect(browseLibrary).toHaveBeenCalledTimes(1);
    expect(browseLibrary.mock.calls[0][3]).toBe(1); // pageSize
    expect(comp.nodes().length).toBe(1);
  });

  it('ignores a change notification for an item not currently listed (no stray fetch)', () => {
    const { comp, getReadMark, readState } = setup([node('a1', false)]);

    readState.notifyChanged('some-other-item');

    expect(getReadMark).not.toHaveBeenCalled();
    expect(comp.nodes().find((n) => n.id === 'a1')!.isRead).toBe(false);
  });

  it('is non-fatal when the read-mark fetch fails — the card keeps its last-known state', () => {
    const { comp, getReadMark, getProgress, readState } = setup([node('a1', false)]);
    getReadMark.mockReturnValue(throwError(() => new Error('network')));
    getProgress.mockReturnValue(of(progressDto()));

    expect(() => readState.notifyChanged('a1')).not.toThrow();
    expect(comp.nodes().find((n) => n.id === 'a1')!.isRead).toBe(false);
  });
});

/**
 * Continue-row auto-refresh (1.7.3 fix). The pinned Continue row
 * (`app-continue-row`) is purely presentational — it just renders whatever
 * `nextUnread` this component hands it, and never re-fetches on its own. Before
 * this fix, finishing the folder's current chapter never recomputed
 * `nextUnread`, so the row kept pointing at the just-finished chapter until a
 * manual refresh. `ReadStateService.itemChanged$` (the same signal the 1.7.1
 * stale-card fix uses) now also drives a targeted, pageSize:1 `browseLibrary`
 * fetch that reads only `PageResponse.nextUnread` — folder-scoped, independent
 * of pagination — so it can never disturb `nodes()`/cursor/scroll.
 */
describe('LibraryBrowseComponent continue-row refresh (1.7.3)', () => {
  function unreadNode(id: string): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: 10, readingState: 'Unread', lastReadPage: null, readerDefault: null, isRead: false,
    } as CatalogNodeDto;
  }

  function setup(initialNextUnread: CatalogNodeDto | null, browseLibraryImpl?: (...args: unknown[]) => unknown) {
    const initialPage: PageResponse<CatalogNodeDto> = {
      items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: initialNextUnread,
    };
    const browseLibrary = browseLibraryImpl
      ? vi.fn(browseLibraryImpl)
      : vi.fn().mockReturnValue(of(initialPage));
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name' })),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary,
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getReadMark: vi.fn().mockReturnValue(of({ itemId: '', isRead: false })),
      getProgress: vi.fn().mockReturnValue(of(null)),
    };
    const authSpy = { isAdmin: () => false };
    const readState = new ReadStateService();

    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: authSpy },
        { provide: ReadStateService, useValue: readState },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });

    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges(); // ngOnInit → initial browseLibrary call, seeds nextUnread
    return { comp: fixture.componentInstance, browseLibrary, readState };
  }

  it('re-fetches nextUnread and updates the Continue row when the reader announces a change', () => {
    const finished = unreadNode('ch1');
    const next = unreadNode('ch2');
    let call = 0;
    const { comp, readState } = setup(finished, () => of(
      call++ === 0
        ? { items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: finished }
        : { items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: next },
    ));
    expect(comp.nextUnread()?.id).toBe('ch1');

    readState.notifyChanged('ch1');

    expect(comp.nextUnread()?.id).toBe('ch2');
  });

  it('requests a lightweight pageSize:1 page — never the full list — and never touches nodes()/cursor', () => {
    const { comp, browseLibrary, readState } = setup(null);
    browseLibrary.mockClear();

    readState.notifyChanged('ch1');

    expect(browseLibrary).toHaveBeenCalledTimes(1);
    const [libraryId, parentId, cursor, pageSize] = browseLibrary.mock.calls[0];
    expect(libraryId).toBe('lib1');
    expect(parentId).toBeNull();
    expect(cursor).toBeNull();
    expect(pageSize).toBe(1);
    expect(comp.nodes().length).toBe(0);
    expect(comp.hasMore()).toBe(false);
  });

  it('drops off the finished chapter (nextUnread → null) once the folder has nothing left unread', () => {
    let call = 0;
    const { comp, readState } = setup(unreadNode('ch1'), () => of(
      call++ === 0
        ? { items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: unreadNode('ch1') }
        : { items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: null },
    ));
    expect(comp.nextUnread()).not.toBeNull();

    readState.notifyChanged('ch1');

    expect(comp.nextUnread()).toBeNull();
  });

  it('guards against double-refresh churn: switchMap cancels a still-in-flight refresh', () => {
    // responses[0] is the ngOnInit browse call (unrelated, left unresolved —
    // irrelevant here); responses[1]/[2] are the two overlapping continue-row
    // refreshes triggered below, one per notifyChanged.
    const responses: Subject<PageResponse<CatalogNodeDto>>[] = [];
    const { comp, readState } = setup(null, () => {
      const subject = new Subject<PageResponse<CatalogNodeDto>>();
      responses.push(subject);
      return subject.asObservable();
    });

    readState.notifyChanged('ch1');
    readState.notifyChanged('ch2'); // arrives before ch1's refresh resolves
    expect(responses.length).toBe(3);

    // Resolving the now-STALE (ch1) refresh must not win over the newer one —
    // switchMap already unsubscribed it when ch2's request started.
    responses[1].next({ items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: unreadNode('stale') });
    responses[2].next({ items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: unreadNode('fresh') });

    expect(comp.nextUnread()?.id).toBe('fresh');
  });

  it('is non-fatal when the refresh fetch fails — the row keeps its last-known nextUnread', () => {
    const finished = unreadNode('ch1');
    let call = 0;
    const { comp, readState } = setup(finished, () => call++ === 0
      ? of({ items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread: finished })
      : throwError(() => new Error('network')));

    expect(() => readState.notifyChanged('ch1')).not.toThrow();
    expect(comp.nextUnread()?.id).toBe('ch1');
  });
});

/**
 * Infinite scroll + sticky navigation (1.8.0). The manual "Load More" is
 * replaced by an IntersectionObserver sentinel; the jump rail is sticky with a
 * scroll-spy active letter; tapping the top bar's neutral area scrolls to the
 * top; the initial/per-page count is a per-user preference. jsdom has no
 * IntersectionObserver/ResizeObserver, so a recording fake is installed where
 * a test needs one (the component treats their absence as "fallback button").
 */
describe('LibraryBrowseComponent infinite scroll + sticky nav (1.8.0)', () => {
  type IoCallback = (entries: { isIntersecting: boolean }[]) => void;
  class FakeIntersectionObserver {
    static instances: FakeIntersectionObserver[] = [];
    readonly observe = vi.fn();
    readonly unobserve = vi.fn();
    readonly disconnect = vi.fn();
    constructor(readonly cb: IoCallback, readonly opts?: IntersectionObserverInit) {
      FakeIntersectionObserver.instances.push(this);
    }
    /** Simulate the sentinel entering the root margin. */
    intersect(): void { this.cb([{ isIntersecting: true }]); }
  }

  function node(id: string, displayName = id): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: 10, readingState: 'Unread', lastReadPage: null, readerDefault: null, isRead: false,
      readRollup: null,
    } as CatalogNodeDto;
  }

  function page(items: CatalogNodeDto[], nextCursor: string | null): PageResponse<CatalogNodeDto> {
    return { items, totalCount: items.length, nextCursor, hasMore: nextCursor !== null };
  }

  function setup(opts: {
    prefs?: Partial<LibraryViewPreferencesDto>;
    browse?: (...args: unknown[]) => unknown;
    buckets?: JumpIndexBucketDto[];
    withIntersectionObserver?: boolean;
  } = {}) {
    if (opts.withIntersectionObserver) {
      FakeIntersectionObserver.instances = [];
      vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);
    }
    const prefs = { viewMode: 'card', density: 'comfortable', sort: 'name', ...opts.prefs } as LibraryViewPreferencesDto;
    const browseLibrary = vi.fn(opts.browse ?? (() => of(page([], null))));
    const setLibraryPreferences = vi.fn().mockReturnValue(of(undefined));
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of(prefs)),
      setLibraryPreferences,
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary,
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: opts.buckets ?? [] })),
      getReadMark: vi.fn().mockReturnValue(of({ itemId: '', isRead: false })),
      getProgress: vi.fn().mockReturnValue(of(null)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => false } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, browseLibrary, setLibraryPreferences, el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => vi.unstubAllGlobals());

  // --- bucket label port ---

  it('jumpLabelFor mirrors the server bucketing (Latin, leading noise, digits, scripts, other)', () => {
    expect(jumpLabelFor('berserk')).toBe('B');
    expect(jumpLabelFor('[Archive] Apple')).toBe('A');
    expect(jumpLabelFor('"The" Thing')).toBe('T');
    expect(jumpLabelFor('20th Century Boys')).toBe('#');
    expect(jumpLabelFor('ワンピース')).toBe('Kana');
    expect(jumpLabelFor('나루토')).toBe('Hangul');
    expect(jumpLabelFor('進撃の巨人')).toBe('CJK');
    expect(jumpLabelFor('Война')).toBe('Cyrillic');
    expect(jumpLabelFor('★☆')).toBe('Other');
    expect(jumpLabelFor('')).toBe('Other');
    expect(jumpLabelFor('...')).toBe('Other');
  });

  // --- infinite scroll ---

  it('renders a sentinel (and no Load More button) while more pages exist', () => {
    const { el } = setup({ withIntersectionObserver: true, browse: () => of(page([node('a')], 'c1')) });
    expect(el.querySelector('.scroll-sentinel')).not.toBeNull();
    expect(el.querySelector('.scroll-sentinel button')).toBeNull();
  });

  it('shows no sentinel once the last page is loaded', () => {
    const { el } = setup({ withIntersectionObserver: true, browse: () => of(page([node('a')], null)) });
    expect(el.querySelector('.scroll-sentinel')).toBeNull();
  });

  it('appends the next page (never rebuilds) when the sentinel intersects, then re-arms the observer', () => {
    let call = 0;
    const { comp, browseLibrary, fixture } = setup({
      withIntersectionObserver: true,
      browse: () => of(call++ === 0 ? page([node('a')], 'c1') : page([node('b')], null)),
    });
    expect(FakeIntersectionObserver.instances.length).toBe(1);
    const io = FakeIntersectionObserver.instances[0];
    expect(io.observe).toHaveBeenCalledTimes(1);
    expect(io.opts?.rootMargin).toContain('600px');

    const before = comp.nodes()[0];
    io.intersect();
    fixture.detectChanges();

    expect(browseLibrary).toHaveBeenCalledTimes(2);
    expect(browseLibrary.mock.calls[1][2]).toBe('c1'); // cursor of the next page
    expect(comp.nodes().map((n) => n.id)).toEqual(['a', 'b']);
    expect(comp.nodes()[0]).toBe(before); // same object: the existing card is untouched
    expect(comp.hasMore()).toBe(false);
    expect(fixture.nativeElement.querySelector('.scroll-sentinel')).toBeNull();
  });

  it('re-observes the sentinel after an append that still has more (crossing-only observer semantics)', () => {
    let call = 0;
    const { fixture } = setup({
      withIntersectionObserver: true,
      browse: () => of(call++ === 0 ? page([node('a')], 'c1') : page([node('b')], 'c2')),
    });
    const io = FakeIntersectionObserver.instances[0];
    io.intersect();
    fixture.detectChanges();
    expect(io.unobserve).toHaveBeenCalledTimes(1);
    expect(io.observe).toHaveBeenCalledTimes(2);
  });

  it('does not double-load while a page is in flight', () => {
    const pending = new Subject<PageResponse<CatalogNodeDto>>();
    let call = 0;
    const { comp, browseLibrary } = setup({
      withIntersectionObserver: true,
      browse: () => (call++ === 0 ? of(page([node('a')], 'c1')) : pending.asObservable()),
    });
    const io = FakeIntersectionObserver.instances[0];
    io.intersect();
    io.intersect();
    comp.loadMore();
    expect(browseLibrary).toHaveBeenCalledTimes(2);
    expect(comp.loadingMore()).toBe(true);
    pending.next(page([node('b')], null));
    expect(comp.loadingMore()).toBe(false);
    expect(comp.nodes().length).toBe(2);
  });

  it('drops a late append response when the list was reset meanwhile (sort change)', () => {
    const pending = new Subject<PageResponse<CatalogNodeDto>>();
    let call = 0;
    const { comp } = setup({
      withIntersectionObserver: true,
      browse: () => {
        const n = call++;
        if (n === 0) return of(page([node('a')], 'c1'));
        if (n === 1) return pending.asObservable();       // the append, still in flight
        return of(page([node('z')], null));               // the reload after setSort
      },
    });
    FakeIntersectionObserver.instances[0].intersect();
    comp.setSort('recentlyAdded');
    expect(comp.nodes().map((n) => n.id)).toEqual(['z']);
    pending.next(page([node('b')], null)); // stale
    expect(comp.nodes().map((n) => n.id)).toEqual(['z']);
    expect(comp.loadingMore()).toBe(false);
  });

  it('falls back to a Load More button when IntersectionObserver is unavailable', () => {
    const { el, comp } = setup({ browse: () => of(page([node('a')], 'c1')) });
    expect(comp.autoLoadSupported).toBe(false);
    expect(el.querySelector('.scroll-sentinel button')).not.toBeNull();
  });

  // --- initial-count preference ---

  it('requests the stored libraryPageSize for the initial page', () => {
    const { browseLibrary } = setup({ prefs: { libraryPageSize: 100 } });
    expect(browseLibrary.mock.calls[0][3]).toBe(100);
  });

  it('falls back to 50 when libraryPageSize is unset, 0, or out of range', () => {
    expect(setup({}).browseLibrary.mock.calls[0][3]).toBe(50);
    TestBed.resetTestingModule();
    expect(setup({ prefs: { libraryPageSize: 0 } }).browseLibrary.mock.calls[0][3]).toBe(50);
    TestBed.resetTestingModule();
    expect(setup({ prefs: { libraryPageSize: 5000 } }).browseLibrary.mock.calls[0][3]).toBe(50);
  });

  it('setPageSize persists the choice and reloads from the top at the new size', () => {
    const { comp, browseLibrary, setLibraryPreferences } = setup({ browse: () => of(page([node('a')], 'c1')) });
    comp.setPageSize(200);
    expect(comp.pageSize()).toBe(200);
    expect(setLibraryPreferences).toHaveBeenCalledWith(expect.objectContaining({ libraryPageSize: 200 }));
    const last = browseLibrary.mock.calls.at(-1)!;
    expect(last[2]).toBeNull(); // from the top
    expect(last[3]).toBe(200);
  });

  it('setPageSize with the current value is a no-op (no persist, no reload)', () => {
    const { comp, browseLibrary, setLibraryPreferences } = setup({});
    comp.setPageSize(50);
    expect(setLibraryPreferences).not.toHaveBeenCalled();
    expect(browseLibrary).toHaveBeenCalledTimes(1);
  });

  it('offers the page-size choices in the View menu model', () => {
    const { comp } = setup({});
    expect(comp.pageSizeOptions).toEqual([25, 50, 100, 200]);
  });

  // --- tap top bar -> scroll to top ---

  it('tapping the top bar\'s neutral area scrolls the window to the top', () => {
    const { el } = setup({});
    const scrollTo = vi.fn();
    vi.stubGlobal('scrollTo', scrollTo);
    (window as unknown as { scrollTo: unknown }).scrollTo = scrollTo;
    (el.querySelector('.browse-bar') as HTMLElement).click();
    expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ top: 0 }));
  });

  it('does not hijack the breadcrumb link or the buttons in the bar', () => {
    const { el } = setup({});
    const scrollTo = vi.fn();
    (window as unknown as { scrollTo: unknown }).scrollTo = scrollTo;
    // The crumb's own RouterLink handler must still run (it navigates); the
    // test router has no routes, so stub the navigation itself.
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    (el.querySelector('.breadcrumbs a') as HTMLElement).click();
    (el.querySelector('.select-toggle') as HTMLElement).click();
    expect(scrollTo).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  // --- sticky rail + scroll-spy ---

  it('binds the rail\'s sticky offset to the measured top-bar height', () => {
    const { comp, el, fixture } = setup({ buckets: [{ label: 'A', count: 1, firstCursor: null }] });
    comp.barHeight.set(57);
    fixture.detectChanges();
    expect((el.querySelector('.jump-rail') as HTMLElement).style.top).toBe('57px');
  });

  /** Fake layout: the rail's bottom edge and each card's bottom edge, in viewport px. */
  function layout(el: HTMLElement, railBottom: number, cardBottoms: number[]): void {
    const rail = el.querySelector('.jump-rail') as HTMLElement;
    rail.getBoundingClientRect = () => ({ bottom: railBottom, top: railBottom - 40 } as DOMRect);
    const cards = el.querySelectorAll<HTMLElement>('.node-wrap');
    expect(cards.length).toBe(cardBottoms.length);
    cards.forEach((c, i) => { c.getBoundingClientRect = () => ({ bottom: cardBottoms[i], top: cardBottoms[i] - 200 } as DOMRect); });
  }

  it('scroll-spy marks the bucket of the topmost card still below the sticky stack', () => {
    const { comp, el } = setup({
      buckets: [{ label: 'A', count: 2, firstCursor: null }, { label: 'B', count: 1, firstCursor: 'cA' }],
      browse: () => of(page([node('a1', 'Alpha'), node('a2', 'Apex'), node('b1', 'Beta')], null)),
    });
    // Row 1 (Alpha, Apex) has scrolled under the rail; row 2 (Beta) is the first visible.
    layout(el, 100, [80, 80, 300]);
    comp.updateActiveJump();
    expect(comp.activeJump()).toBe('B');

    // Scrolled back up: row 1 visible again.
    layout(el, 100, [250, 250, 470]);
    comp.updateActiveJump();
    expect(comp.activeJump()).toBe('A');
  });

  it('scroll-spy keeps the current letter while a row that ends it is still the first visible row', () => {
    const { comp, el } = setup({
      buckets: [{ label: 'A', count: 1, firstCursor: null }, { label: 'B', count: 1, firstCursor: 'cA' }],
      browse: () => of(page([node('a1', 'Alpha'), node('b1', 'Beta')], null)),
    });
    // One row holding [Alpha, Beta]; the user jumped to B, so B stays highlighted.
    comp.activeJump.set('B');
    layout(el, 100, [300, 300]);
    comp.updateActiveJump();
    expect(comp.activeJump()).toBe('B');
    // Without a current letter in that row, the row's first card wins.
    comp.activeJump.set(null);
    comp.updateActiveJump();
    expect(comp.activeJump()).toBe('A');
  });

  it('a rail click for a letter already loaded from the start scrolls in place instead of reloading', () => {
    const { comp, el, browseLibrary } = setup({
      buckets: [{ label: 'A', count: 1, firstCursor: null }, { label: 'B', count: 1, firstCursor: 'cA' }],
      browse: () => of(page([node('a1', 'Alpha'), node('b1', 'Beta')], null)),
    });
    // A responsive fake scroll: each scrollBy shifts the cards, as the real
    // document would, so the jump's settle loop converges.
    let cardBottoms = [300, 300];
    const scrollBy = vi.fn((opts: ScrollToOptions) => {
      cardBottoms = cardBottoms.map((b) => b - (opts.top ?? 0));
      layout(el, 100, cardBottoms);
    });
    (window as unknown as { scrollBy: unknown }).scrollBy = scrollBy;
    layout(el, 100, cardBottoms);
    comp.jumpToBucket({ label: 'B', count: 1, firstCursor: 'cA' });
    expect(scrollBy).toHaveBeenCalledTimes(1);
    expect(scrollBy.mock.calls[0][0].top).toBe(300 - 200 - 100 - 8); // card top (300-200) to just under the rail (100)
    expect(browseLibrary).toHaveBeenCalledTimes(1); // no reload
    expect(comp.activeJump()).toBe('B');
    expect(comp.nodes().length).toBe(2);
  });

  it('a rail click for a letter outside the loaded window still reloads from the bucket cursor', () => {
    const { comp, browseLibrary } = setup({
      buckets: [{ label: 'A', count: 1, firstCursor: null }, { label: 'Z', count: 1, firstCursor: 'cY' }],
      browse: () => of(page([node('a1', 'Alpha')], 'c1')),
    });
    comp.jumpToBucket({ label: 'Z', count: 1, firstCursor: 'cY' });
    expect(browseLibrary).toHaveBeenCalledTimes(2);
    expect(browseLibrary.mock.calls[1][2]).toBe('cY');
    expect(comp.activeJump()).toBe('Z');
  });
});

/**
 * View-menu selected-state highlight (1.8.1). The four View submenus (view mode,
 * Sort by, Order, Items per load) used to mark the active option with a checkmark
 * ICON; this replaces that with an accent COLOR HIGHLIGHT (the `selected-option`
 * class + accent background/text) while keeping the option's own icon. For
 * accessibility the active option is a `menuitemradio` carrying `aria-checked`.
 *
 * The menu renders in a CDK overlay (outside the component's host element), so
 * these tests OPEN the menu via its trigger and then query the overlay through
 * `document` (the panel carries the `view-options-menu` class applied on
 * <mat-menu>), rather than `fixture.nativeElement`.
 */
describe('LibraryBrowseComponent view menu selected highlight (1.8.1)', () => {
  function setup(prefs: Partial<LibraryViewPreferencesDto> = {}) {
    const fullPrefs = { viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc', ...prefs } as LibraryViewPreferencesDto;
    const emptyPage: PageResponse<CatalogNodeDto> = { items: [], totalCount: 0, nextCursor: null, hasMore: false };
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of(fullPrefs)),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(emptyPage)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getReadMark: vi.fn().mockReturnValue(of({ itemId: '', isRead: false })),
      getProgress: vi.fn().mockReturnValue(of(null)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => false } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : null) }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  /** Open the View menu and return its overlay panel element. */
  function openViewMenu(el: HTMLElement, fixture: ComponentFixture<LibraryBrowseComponent>): HTMLElement {
    (el.querySelector('.view-toggle') as HTMLElement).click();
    fixture.detectChanges();
    const panel = document.querySelector('.view-options-menu') as HTMLElement;
    expect(panel, 'the view-options-menu overlay panel').not.toBeNull();
    return panel;
  }

  /** Find a menu item button by its (whitespace-normalized) trailing label text. */
  function itemByLabel(panel: HTMLElement, label: string): HTMLElement {
    const items = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    const match = items.find((i) => (i.textContent ?? '').replace(/\s+/g, ' ').trim().endsWith(label));
    expect(match, `menu item ending with "${label}"`).toBeDefined();
    return match!;
  }

  it('every menu item is a menuitemradio (single-selection semantics for a11y)', () => {
    const { fixture, el } = setup();
    const panel = openViewMenu(el, fixture);
    const items = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    expect(items.length).toBeGreaterThan(0);
    for (const item of items) {
      expect(item.getAttribute('role')).toBe('menuitemradio');
    }
  });

  it('highlights the DEFAULT selection (card / name / asc / 50) in all four submenus', () => {
    const { fixture, el } = setup(); // card, name, asc, default pageSize 50
    const panel = openViewMenu(el, fixture);

    // Each pair: the active option carries the highlight class + aria-checked=true;
    // the inactive sibling does not.
    for (const [active, inactive] of [
      ['Card', 'List'],
      ['Name', 'Recently added'],
      ['Ascending', 'Descending'],
      ['50', '100'],
    ]) {
      const on = itemByLabel(panel, active);
      const off = itemByLabel(panel, inactive);
      expect(on.classList.contains('selected-option'), `${active} highlighted`).toBe(true);
      expect(on.getAttribute('aria-checked'), `${active} aria-checked`).toBe('true');
      expect(off.classList.contains('selected-option'), `${inactive} not highlighted`).toBe(false);
      expect(off.getAttribute('aria-checked'), `${inactive} aria-checked`).toBe('false');
    }
  });

  it('the selected option keeps its OWN icon (the checkmark is gone)', () => {
    const { fixture, el } = setup(); // card is the active view mode
    const panel = openViewMenu(el, fixture);
    const card = itemByLabel(panel, 'Card');
    // Icons are rendered as ligature text; the active item shows grid_view, not check.
    expect(card.querySelector('mat-icon')?.textContent?.trim()).toBe('grid_view');
    expect(card.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
  });

  it('the highlight follows a NON-default stored selection in every submenu', () => {
    const { fixture, el } = setup({ viewMode: 'list', sort: 'recentlyAdded', direction: 'desc', libraryPageSize: 100 });
    const panel = openViewMenu(el, fixture);

    for (const [active, inactive] of [
      ['List', 'Card'],
      ['Recently added', 'Name'],
      ['Descending', 'Ascending'],
      ['100', '50'],
    ]) {
      expect(itemByLabel(panel, active).classList.contains('selected-option'), `${active} highlighted`).toBe(true);
      expect(itemByLabel(panel, inactive).classList.contains('selected-option'), `${inactive} not highlighted`).toBe(false);
    }
    // And the active list item shows its own glyph, not a checkmark.
    expect(itemByLabel(panel, 'List').querySelector('mat-icon')?.textContent?.trim()).toBe('view_list');
  });
});
