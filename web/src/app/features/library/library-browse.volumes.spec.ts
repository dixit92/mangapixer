import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, Observable, Subject, of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { CatalogNodeDto, PageResponse, VolumeStackDto, VolumeStackSummaryDto, VolumeViewDto } from '../../core/api/api-types';

/**
 * The Volumes view in browse (1.29.0): stack cards on the shared stack card with the incomplete mark, the link into the stack
 * view, the Volumes | Folders switch (persisted through library-preferences, sent as `group`), and (1.30.0) selection of a
 * whole stack - its actions apply to every member archive - while a missing-volume placeholder is never selectable. The list
 * itself comes from the server; these tests drive the component with canned pages.
 */
describe('LibraryBrowseComponent Volumes view (1.29.0)', () => {
  function stackNode(key: string, over: Partial<VolumeStackSummaryDto> = {}): CatalogNodeDto {
    return {
      id: `vs.f1.${key}`, parentId: 'f1', libraryId: 'lib1', kind: 'VolumeStack', displayName: `Volume ${key}`,
      availability: 'Available', coverUrl: `/api/v1/items/c${key}/cover`, childFolderCount: null, childArchiveCount: null,
      pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false, readRollup: 'Unread', hasSeriesInfo: false,
      volumeStack: { key, label: `Volume ${key}`, presentCount: 8, chapterCount: 9, missingCount: 1, extraCount: 0, hasVolumeArchive: false, confidence: 'Exact', ...over },
    } as CatalogNodeDto;
  }

  function archiveNode(id: string): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: id, availability: 'Available', coverUrl: null,
      childFolderCount: null, childArchiveCount: null, pageCount: 10, readingState: null, lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null, hasSeriesInfo: false,
    } as CatalogNodeDto;
  }

  /** The stack endpoint's answer: two chapter files and a missing chapter, members `c<key>a` and `c<key>b`. */
  function stackDto(key: string): VolumeStackDto {
    return {
      folderId: 'f1', key, label: `Volume ${key}`, confidence: 'Exact', source: 'MangaDex', presentCount: 2, chapterCount: 3, missingCount: 1,
      extraCount: 0, slots: [
        { kind: 'Item', chapter: '1', item: archiveNode(`c${key}a`) },
        { kind: 'Item', chapter: '2', item: archiveNode(`c${key}b`) },
        { kind: 'Missing', chapter: '3' },
      ],
    };
  }

  function setup(opts: { nodes?: CatalogNodeDto[]; view?: Partial<VolumeViewDto>; prefs?: Record<string, unknown>; admin?: boolean; viewMode?: string;
    route?: Observable<{ get: (k: string) => string | null }>; viewOf?: (nodeId: string) => VolumeViewDto;
    query?: Record<string, string>; page?: Partial<PageResponse<CatalogNodeDto>> } = {}) {
    const nodes = opts.nodes ?? [stackNode('1'), stackNode('2', { missingCount: 0, chapterCount: 8, presentCount: 8 }), archiveNode('loose')];
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false, ...opts.page };
    const view: VolumeViewDto = { nodeId: 'f1', available: true, active: true, consolidated: false, stackCount: 2, ...opts.view };
    const apiSpy = {
      getVolumeView: opts.viewOf ? vi.fn().mockImplementation((id: string) => of(opts.viewOf!(id))) : vi.fn().mockReturnValue(of(view)),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: opts.viewMode ?? 'card', density: 'comfortable', sort: 'name', direction: 'asc', ...opts.prefs })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Series' } as CatalogNodeDto)),
      getVolumeStack: vi.fn().mockImplementation((_folder: string, key: string) => of(stackDto(key))),
      setItemRead: vi.fn().mockImplementation((id: string, read: boolean) => of({ itemId: id, isRead: read })),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => !!opts.admin, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: {
          paramMap: opts.route ?? of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : 'f1') }),
          snapshot: { queryParamMap: { get: (k: string) => opts.query?.[k] ?? null } },
        } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, apiSpy };
  }

  it('renders a stack on the shared stack card with the incomplete mark and no star or (i)', () => {
    const { el } = setup();

    const first = el.querySelectorAll('.node-wrap')[0];
    expect(first.querySelector('app-stack-card')).not.toBeNull();
    expect(first.querySelector('[data-testid="stack-incomplete"]')!.textContent?.trim()).toBe('8/9');
    expect(first.querySelector('app-star-toggle')).toBeNull();
    expect(first.querySelector('app-info-toggle')).toBeNull();
    expect(first.querySelector('.node-title')!.textContent).toBe('Volume 1');
    expect(first.querySelector('.node-sub')!.textContent?.trim()).toBe('8 of 9 chapters');
    // A complete volume has no mark; a plain archive is not a stack card.
    const second = el.querySelectorAll('.node-wrap')[1];
    expect(second.querySelector('[data-testid="stack-incomplete"]')).toBeNull();
    expect(second.querySelector('app-stack-card')).not.toBeNull();
    expect(el.querySelectorAll('.node-wrap')[2].querySelector('app-stack-card')).toBeNull();
  });

  it('links a stack to its own view inside the real folder', () => {
    const { el } = setup();

    expect(el.querySelectorAll('.node-card')[0].getAttribute('href')).toBe('/libraries/lib1/browse/f1/volume/1');
    expect(el.querySelectorAll('.node-card')[2].getAttribute('href')).toBe('/reader/loose');
  });

  it('describes a real volume file merged with chapters', () => {
    const { el } = setup({ nodes: [stackNode('3', { hasVolumeArchive: true, presentCount: 5, missingCount: 0, chapterCount: 4 })] });

    expect(el.querySelector('.node-sub')!.textContent?.trim()).toBe('Volume + 4 chapters');
  });

  it('shows the Volumes | Folders switch only where a Volumes view exists', () => {
    expect(setup().el.querySelector('[data-testid="volume-view-switch"]')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(setup({ view: { available: false, active: false, stackCount: 0 } }).el.querySelector('[data-testid="volume-view-switch"]')).toBeNull();
  });

  it('opens a series from a folder without a Volumes view with the server default, not that folder\'s "flat" (1.30.0)', () => {
    const route = new BehaviorSubject<{ get: (k: string) => string | null }>({ get: (k) => (k === 'libraryId' ? 'lib1' : 'category') });
    const { apiSpy } = setup({
      route,
      viewOf: (id) => id === 'category'
        ? { nodeId: id, available: false, active: false, defaultActive: false, consolidated: false, stackCount: 0 }
        : { nodeId: id, available: true, active: true, defaultActive: true, consolidated: false, stackCount: 1 },
    });
    expect(apiSpy.browseLibrary.mock.calls[0][1]).toBe('category');
    route.next({ get: (k) => (k === 'libraryId' ? 'lib1' : 'series') });
    const seriesCall = apiSpy.browseLibrary.mock.calls.at(-1)!;
    expect(seriesCall[1]).toBe('series');
    expect(seriesCall[10]).toBeNull(); // the category's default ("flat") must not leak into the series
  });

  it('follows the server default until the viewer chooses, and the stored choice after', () => {
    const server = setup({ view: { active: false } });
    expect(server.comp.volumesActive()).toBe(false);
    expect(server.apiSpy.browseLibrary.mock.calls[0][10]).toBeNull(); // no explicit group: the server decides
    TestBed.resetTestingModule();

    const stored = setup({ view: { active: false }, prefs: { seriesViewMode: 'Volumes' } });
    expect(stored.comp.volumesActive()).toBe(true);
    expect(stored.apiSpy.browseLibrary.mock.calls[0][10]).toBe('volumes');
  });

  it('switching to Folders persists the choice, reloads flat and keeps the other preferences', () => {
    const { fixture, el, apiSpy } = setup({ prefs: { cardSize: '170', homeRecentWindowDays: 14 } });
    const before = apiSpy.browseLibrary.mock.calls.length;

    (el.querySelector('[data-testid="view-folders"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(apiSpy.setLibraryPreferences).toHaveBeenCalledTimes(1);
    expect(apiSpy.setLibraryPreferences.mock.calls[0][0]).toMatchObject({ seriesViewMode: 'Folders', cardSize: '170', homeRecentWindowDays: 14 });
    expect(apiSpy.browseLibrary.mock.calls.length).toBe(before + 1);
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![10]).toBe('flat');
    expect(el.querySelector('[data-testid="view-folders"]')!.getAttribute('aria-pressed')).toBe('true');

    (el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(apiSpy.setLibraryPreferences.mock.calls.at(-1)![0]).toMatchObject({ seriesViewMode: 'Volumes' });
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![10]).toBe('volumes');
  });

  it('choosing what the folder shows by default clears the stored choice, so the admin default applies again', () => {
    // The admin turned the default off; this viewer once chose Volumes, which still wins.
    const { fixture, comp, el, apiSpy } = setup({ view: { active: true, defaultActive: false }, prefs: { seriesViewMode: 'Volumes' } });
    expect(comp.volumesActive()).toBe(true);

    (el.querySelector('[data-testid="view-folders"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(apiSpy.setLibraryPreferences.mock.calls.at(-1)![0]).toMatchObject({ seriesViewMode: null });
    expect(comp.seriesView()).toBeNull();
    expect(comp.volumesActive()).toBe(false);
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![10]).toBe('flat'); // explicit: does not race the preference save
    expect(el.querySelector('[data-testid="view-folders"]')!.getAttribute('aria-pressed')).toBe('true');

    // The other side is a real choice again, remembered as before.
    (el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(apiSpy.setLibraryPreferences.mock.calls.at(-1)![0]).toMatchObject({ seriesViewMode: 'Volumes' });
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![10]).toBe('volumes');
  });

  it('says when this series\' covers are still being downloaded, only in the Volumes view', () => {
    const open = vi.spyOn(MatSnackBar.prototype, 'open');
    setup({ view: { coversPending: 3 } });
    expect(open).toHaveBeenCalledTimes(1);
    expect(open.mock.calls[0][0]).toBe('Downloading 3 volume covers in the background - they appear as they arrive.');
    TestBed.resetTestingModule();
    open.mockClear();

    setup({ view: { coversPending: 0 } });
    TestBed.resetTestingModule();
    setup({ view: { coversPending: 2 }, prefs: { seriesViewMode: 'Folders' } });
    expect(open).not.toHaveBeenCalled();
    open.mockRestore();
  });

  it('keeps the switch live: filters work inside the Volumes view, another sort shows Folders (1.31.0)', () => {
    const { fixture, comp, el } = setup();
    const volumes = () => el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement;
    expect(volumes().disabled).toBe(false);

    comp.setReadStateFilter('unread');
    fixture.detectChanges();
    expect(volumes().disabled).toBe(false);
    expect(volumes().getAttribute('aria-pressed')).toBe('true'); // still the Volumes view, filtered
    comp.toggleFavoritesOnly();
    fixture.detectChanges();
    expect(volumes().getAttribute('aria-pressed')).toBe('true');

    comp.setSort('recentlyAdded');
    fixture.detectChanges();
    expect(volumes().disabled).toBe(false);
    expect(volumes().getAttribute('aria-pressed')).toBe('false'); // the list is flat under another sort
    expect(el.querySelector('[data-testid="view-folders"]')!.getAttribute('aria-pressed')).toBe('true');
  });

  it('picking Volumes under another stored sort saves Name, says so, and Undo restores the sort (1.31.0)', () => {
    const action = new Subject<void>();
    const open = vi.spyOn(MatSnackBar.prototype, 'open').mockReturnValue({ onAction: () => action } as never);
    const { fixture, comp, el, apiSpy } = setup({ prefs: { sort: 'recentlyAdded', direction: 'desc' } });
    expect(comp.sort()).toBe('recentlyAdded');

    (el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(comp.sort()).toBe('name');
    expect(comp.sortDirection()).toBe('asc');
    expect(apiSpy.setLibraryPreferences.mock.calls.at(-1)![0]).toMatchObject({ sort: 'name', direction: 'asc' });
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![4]).toBe('name');
    expect(open).toHaveBeenCalledWith('Sorted by name for the Volumes view', 'Undo', { duration: 6000 });

    action.next();
    fixture.detectChanges();
    expect(comp.sort()).toBe('recentlyAdded');
    expect(apiSpy.setLibraryPreferences.mock.calls.at(-1)![0]).toMatchObject({ sort: 'recentlyAdded', direction: 'desc' });
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![4]).toBe('recentlyAdded');
    open.mockRestore();
  });

  it('a home "New chapters" tap asks for the Volumes view and takes the server\'s Name sort for this visit only (1.31.0)', () => {
    const { comp, apiSpy } = setup({ query: { sort: 'recentlyUpdated', volumes: 'prefer' }, page: { effectiveSort: 'name' } });

    expect(apiSpy.browseLibrary.mock.calls[0][4]).toBe('recentlyUpdated');
    expect(apiSpy.browseLibrary.mock.calls[0][11]).toBe(true); // preferVolumes on the first page only
    expect(comp.sort()).toBe('name');
    expect(comp.volumesSuspended()).toBe(false);
    expect(apiSpy.setLibraryPreferences).not.toHaveBeenCalled(); // never persisted

    comp.setReadStateFilter('unread'); // a later reload no longer asks
    expect(apiSpy.browseLibrary.mock.calls.at(-1)![11]).toBe(false);
  });

  it('keeps the home sort when the server does not open the Volumes view, and a switch reset then stays transient (1.31.0)', () => {
    const action = new Subject<void>();
    const open = vi.spyOn(MatSnackBar.prototype, 'open').mockReturnValue({ onAction: () => action } as never);
    const { fixture, comp, el, apiSpy } = setup({ query: { sort: 'recentlyUpdated', volumes: 'prefer' } });
    expect(comp.sort()).toBe('recentlyUpdated');

    (el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(comp.sort()).toBe('name');
    // The stored sort stays the user's own (Name here) - the transient home sort is never written.
    expect(apiSpy.setLibraryPreferences.mock.calls.every((c) => c[0].sort === 'name')).toBe(true);

    action.next();
    expect(comp.sort()).toBe('recentlyUpdated');
    expect(apiSpy.setLibraryPreferences.mock.calls.every((c) => c[0].sort === 'name')).toBe(true);
    open.mockRestore();
  });

  it('selects a whole stack: a tap, Select all, Select all unread and a range include it (1.30.0)', () => {
    const readStack = { ...stackNode('3'), readRollup: 'Read' } as CatalogNodeDto;
    const { fixture, comp, el } = setup({ nodes: [stackNode('1'), stackNode('2'), readStack, archiveNode('loose')] });
    comp.toggleSelectMode();
    fixture.detectChanges();

    (el.querySelectorAll('.node-card')[0] as HTMLElement).click();
    expect([...comp.selected()]).toEqual(['vs.f1.1']);
    // The select check shows on a stack card, and the incomplete mark makes room for it.
    fixture.detectChanges();
    const first = el.querySelectorAll('.node-wrap')[0];
    expect(first.classList).toContain('selected');
    expect(first.querySelector('.check.on')).not.toBeNull();
    expect(first.querySelector('[data-testid="stack-incomplete"]')!.classList).toContain('moved');

    comp.selectAll();
    expect([...comp.selected()].sort()).toEqual(['loose', 'vs.f1.1', 'vs.f1.2', 'vs.f1.3']);
    comp.clearSelection();
    comp.selectAllUnread();
    expect([...comp.selected()].sort()).toEqual(['loose', 'vs.f1.1', 'vs.f1.2']); // the read stack is left out
    comp.clearSelection();
    comp.selectAllRead();
    expect([...comp.selected()]).toEqual(['vs.f1.3']);

    // Shift-click fills the range across stacks and the archive.
    comp.clearSelection();
    (el.querySelectorAll('.node-card')[0] as HTMLElement).click();
    (el.querySelectorAll('.node-card')[3] as HTMLElement).dispatchEvent(new MouseEvent('click', { shiftKey: true, bubbles: true, cancelable: true }));
    expect(comp.selected().size).toBe(4);
  });

  it('keeps a stack out of the admin actions that address stored nodes', () => {
    const { fixture, comp } = setup({ admin: true });
    comp.toggleSelectMode();
    fixture.detectChanges();

    comp.selectAll();

    expect([...comp.selected()].sort()).toEqual(['loose', 'vs.f1.1', 'vs.f1.2']);
    expect([...comp.selectedNodeIds()]).toEqual(['loose']);
  });

  it('marks a whole stack read in one step: every member archive, then its card shows Read', () => {
    const { fixture, comp, el, apiSpy } = setup();
    comp.toggleSelectMode();
    fixture.detectChanges();
    (el.querySelectorAll('.node-card')[0] as HTMLElement).click();

    comp.bulkMarkRead(true);
    fixture.detectChanges();

    expect(apiSpy.getVolumeStack).toHaveBeenCalledWith('f1', '1');
    expect(apiSpy.setItemRead.mock.calls.map((c) => [c[0], c[1]])).toEqual([['c1a', true], ['c1b', true]]);
    expect(comp.nodes().find((n) => n.id === 'vs.f1.1')!.readRollup).toBe('Read');
    expect(el.querySelectorAll('.node-wrap')[0].querySelector('.badge.read')).not.toBeNull();
    expect(comp.busy()).toBe(false);

    comp.bulkMarkRead(false);
    expect(apiSpy.setItemRead.mock.calls.slice(2).map((c) => [c[0], c[1]])).toEqual([['c1a', false], ['c1b', false]]);
    expect(comp.nodes().find((n) => n.id === 'vs.f1.1')!.readRollup).toBe('Unread');
  });

  it('marks a stack together with loose archives, and never asks for a stack it does not hold selected', () => {
    const { comp, apiSpy } = setup();
    comp.selected.set(new Set(['vs.f1.2', 'loose']));

    comp.bulkMarkRead(true);

    expect(apiSpy.getVolumeStack).toHaveBeenCalledTimes(1);
    expect(apiSpy.getVolumeStack).toHaveBeenCalledWith('f1', '2');
    expect(apiSpy.setItemRead.mock.calls.map((c) => c[0]).sort()).toEqual(['c2a', 'c2b', 'loose']);
  });

  it('adds a whole stack to the favorites through its member archives, and marks the card', () => {
    const { comp, apiSpy } = setup();
    comp.selected.set(new Set(['vs.f1.1', 'loose']));

    comp.bulkFavorite(true);

    expect(apiSpy.setFavorite.mock.calls.map((c) => [c[0], c[1]]).sort()).toEqual([['c1a', true], ['c1b', true], ['loose', true]]);
    expect(comp.nodes().find((n) => n.id === 'vs.f1.1')!.isFavorite).toBe(true);
    expect(comp.nodes().find((n) => n.id === 'vs.f1.2')!.isFavorite).toBeFalsy();

    comp.bulkFavorite(false);
    expect(comp.nodes().find((n) => n.id === 'vs.f1.1')!.isFavorite).toBe(false);
  });

  it('re-derives a stack card when a chapter inside it changes (the stack page or the reader), debounced', () => {
    vi.useFakeTimers();
    try {
      const { comp, apiSpy } = setup();
      const readState = TestBed.inject(ReadStateService);
      apiSpy.getVolumeStack.mockImplementation((_folder: string, key: string) => of({
        ...stackDto(key),
        slots: [
          { kind: 'Item', chapter: '1', item: { ...archiveNode(`c${key}a`), isRead: true } },
          { kind: 'Item', chapter: '2', item: { ...archiveNode(`c${key}b`), isFavorite: key === '1' } },
        ],
      } as VolumeStackDto));

      readState.notifyChanged('c1a');
      readState.notifyChanged('c1b');
      expect(apiSpy.getVolumeStack).not.toHaveBeenCalled();
      vi.advanceTimersByTime(400);

      // One refresh for the two notifications: a request per listed stack (the archive is not one).
      expect(apiSpy.getVolumeStack).toHaveBeenCalledTimes(2);
      const first = comp.nodes().find((n) => n.id === 'vs.f1.1')!;
      expect(first.readRollup).toBe('Reading'); // one of two members read
      expect(first.isFavorite).toBe(true);
      expect(comp.nodes().find((n) => n.id === 'vs.f1.2')!.isFavorite).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the admin "View..." action enabled for exactly one selected folder', () => {
    const folder = { ...archiveNode('sub'), kind: 'Folder', pageCount: null } as CatalogNodeDto;
    const { fixture, comp, el } = setup({ nodes: [stackNode('1'), folder], admin: true });
    comp.toggleSelectMode();
    fixture.detectChanges();
    const action = () => el.querySelector('[data-testid="folder-view-action"]') as HTMLButtonElement;

    expect(action().disabled).toBe(true);
    (el.querySelectorAll('.node-card')[1] as HTMLElement).click();
    fixture.detectChanges();
    expect(action().disabled).toBe(false);
  });

  it('keeps the list mode working: a stack row has a cover, its mark and a select box, but no star of its own', () => {
    const { el } = setup({ viewMode: 'list' });

    const row = el.querySelectorAll('.node-wrap')[0];
    expect(row.querySelector('app-stack-card')).toBeNull(); // rows use the plain small cover
    expect(row.querySelector('[data-testid="stack-incomplete"]')).not.toBeNull();
    expect(row.querySelector('.row-select')).not.toBeNull(); // a whole stack is selectable (1.30.0)
    expect(row.querySelector('app-star-toggle')).toBeNull(); // a stack is not a node: no star of its own
    expect(el.querySelectorAll('.node-wrap')[2].querySelector('.row-select')).not.toBeNull();
    expect(el.querySelectorAll('.node-wrap')[2].querySelector('app-star-toggle')).not.toBeNull();
  });

  it('selects a stack through the list row checkbox, which turns select mode on', () => {
    const { fixture, comp, el } = setup({ viewMode: 'list' });

    (el.querySelectorAll('.node-wrap')[1].querySelector('.row-select') as HTMLElement).click();
    fixture.detectChanges();

    expect(comp.selectMode()).toBe(true);
    expect([...comp.selected()]).toEqual(['vs.f1.2']);
    expect(el.querySelectorAll('.node-wrap')[1].classList).toContain('selected');
  });

  it('renders a missing volume as a dashed placeholder in its place, never a link or a selection', () => {
    const missing = { ...stackNode('2'), id: 'vm.f1.2', coverUrl: null, availability: 'Unavailable',
      volumeStack: { key: '2', label: 'Volume 2', presentCount: 0, missingCount: 0, extraCount: 0, hasVolumeArchive: false, confidence: 'Exact', missing: true } } as CatalogNodeDto;
    const { fixture, comp, el } = setup({ nodes: [stackNode('1'), missing, stackNode('3')] });

    const wraps = el.querySelectorAll('.node-wrap');
    expect(wraps).toHaveLength(3);
    const placeholder = wraps[1].querySelector('[data-testid="missing-volume"]')!;
    expect(placeholder.getAttribute('aria-label')).toBe('Volume 2, missing');
    expect(wraps[1].querySelector('a')).toBeNull();
    comp.toggleSelectMode();
    fixture.detectChanges();
    comp.selectAll();
    // The two real stacks are selected (1.30.0); the placeholder never is - not by Select all, a tap or a range.
    expect([...comp.selected()].sort()).toEqual(['vs.f1.1', 'vs.f1.3']);
    expect(wraps[1].querySelector('.check')).toBeNull();
    comp.onCardClick({ preventDefault: () => undefined, stopPropagation: () => undefined, shiftKey: false } as unknown as MouseEvent, missing);
    expect(comp.selected().has('vm.f1.2')).toBe(false);
  });

  it('shows the series status line while the Volumes view of a linked series is shown', () => {
    const { fixture, comp, el } = setup({ view: { hasSeriesStatus: true, seriesStatus: 'Ongoing', missingVolumes: 2, missingChapters: 3, releaseKnown: true, language: 'en' } });
    expect(el.querySelector('[data-testid="series-status"]')!.textContent).toContain('Ongoing · 2 volumes, 3 chapters missing');

    comp.setSeriesView(false);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="series-status"]')).toBeNull();
  });

  it('shows a webtoon\'s chapter list with a Chapters switch and a chapters-only status line (1.34.0)', () => {
    const { el } = setup({
      nodes: [archiveNode('c1'), archiveNode('c2'), archiveNode('c4')],
      view: { stackCount: 0, chaptersOnly: true, hasSeriesStatus: true, seriesStatus: 'Ongoing', origin: 'Korea', missingVolumes: 0, missingChapters: 1,
        releaseKnown: true, language: 'en' },
    });
    expect(el.querySelector('[data-testid="view-volumes"]')!.textContent).toContain('Chapters');
    const status = el.querySelector('[data-testid="series-status"]')!.textContent!;
    expect(status).toContain('1 chapter missing');
    expect(status).not.toContain('volume');
    expect(el.querySelector('[data-testid="missing-volume"]')).toBeNull();
  });

  it('shows no status line for a folder without its own link', () => {
    expect(setup().el.querySelector('[data-testid="series-status"]')).toBeNull();
  });

  it('marks a stack whose archives include a starred one, and counts a split chapter once', () => {
    const starred = { ...stackNode('1', { presentCount: 7, extraCount: 0, chapterCount: 4, chaptersPresent: 3, missingCount: 1 }), isFavorite: true };
    const { el } = setup({ nodes: [starred, stackNode('2')] });

    const first = el.querySelectorAll('.node-wrap')[0];
    expect(first.querySelector('[data-testid="stack-star"]')).not.toBeNull();
    expect(first.querySelector('.node-sub')!.textContent?.trim()).toBe('3 of 4 chapters');
    expect(first.querySelector('[data-testid="stack-incomplete"]')!.textContent?.trim()).toBe('3/4');
    expect(el.querySelectorAll('.node-wrap')[1].querySelector('[data-testid="stack-star"]')).toBeNull();
  });
});
