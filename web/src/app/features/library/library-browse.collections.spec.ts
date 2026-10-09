import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { CatalogNodeDto, CollectionStackDto, PageResponse, VolumeViewDto } from '../../core/api/api-types';

/**
 * Tankoubon stacks in browse (1.37.0): in a folder that is neither a series nor a collection, the stories linked to one collected
 * volume come as ONE stack card (`kind: 'VolumeStack'` with `collectionStack`) - the shared stacked card without volume badges, "N
 * stories", a link into its own page; selecting it applies the actions to its stories through the collection endpoint (never the
 * volume one). The list comes from the server; these tests drive the component with canned pages.
 */
describe('LibraryBrowseComponent tankoubon stacks (1.37.0)', () => {
  function collectionNode(key: string, count: number, over: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id: `cs.f1.${key}`, parentId: 'f1', libraryId: 'lib1', kind: 'VolumeStack', displayName: 'Synthetic Collected Volume',
      availability: 'Available', coverUrl: `/api/v1/nodes/f1/collection-stacks/${key}/cover?v=1`, childFolderCount: null, childArchiveCount: null,
      pageCount: null, readingState: null, lastReadPage: null, readerDefault: null, isRead: false, readRollup: 'Unread', hasSeriesInfo: false,
      volumeStack: null, collectionStack: { key, title: 'Synthetic Collected Volume', storyCount: count }, ...over,
    } as CatalogNodeDto;
  }

  function archiveNode(id: string, over: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: id, availability: 'Available', coverUrl: null,
      childFolderCount: null, childArchiveCount: null, pageCount: 10, readingState: null, lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null, hasSeriesInfo: false, ...over,
    } as CatalogNodeDto;
  }

  function collectionDto(key: string, items: CatalogNodeDto[]): CollectionStackDto {
    return { folderId: 'f1', key, title: 'Synthetic Collected Volume', storyCount: items.length, items };
  }

  function setup(nodes: CatalogNodeDto[] = [archiveNode('alpha'), collectionNode('rec1', 2), archiveNode('gamma')]) {
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const view: VolumeViewDto = { nodeId: 'f1', available: true, active: true, consolidated: false, stackCount: 0, collectionStackCount: 1 };
    const apiSpy = {
      getVolumeView: vi.fn().mockReturnValue(of(view)),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc' })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Sample Artist' } as CatalogNodeDto)),
      getVolumeStack: vi.fn(),
      getCollectionStack: vi.fn().mockImplementation((_folder: string, key: string) =>
        of(collectionDto(key, [archiveNode(`${key}-b`), archiveNode(`${key}-d`)]))),
      setItemRead: vi.fn().mockImplementation((id: string, read: boolean) => of({ itemId: id, isRead: read })),
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => false, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: {
          paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : 'f1') }),
          snapshot: { queryParamMap: { get: () => null } },
        } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, apiSpy };
  }

  it('renders the stories of one collected volume as one stacked card: its title, "N stories", no volume badges', () => {
    const { el } = setup();

    const cards = el.querySelectorAll('.node-wrap');
    expect(cards).toHaveLength(3);
    const stack = cards[1];
    expect(stack.querySelector('[data-testid="collection-stack-card"]')).not.toBeNull();
    expect(stack.querySelector('.node-title')!.textContent).toBe('Synthetic Collected Volume');
    expect(stack.querySelector('.node-sub')!.textContent?.trim()).toBe('2 stories');
    expect(stack.querySelector('img')!.getAttribute('src')).toBe('/api/v1/nodes/f1/collection-stacks/rec1/cover?v=1');
    expect(stack.querySelector('[data-testid="stack-incomplete"]')).toBeNull();
    expect(stack.querySelector('app-official-release-badge')).toBeNull();
    expect(cards[0].querySelector('app-stack-card')).toBeNull();
  });

  it('links the stack to its own page inside the real folder, and a story to the reader', () => {
    const { el } = setup();

    const links = Array.from(el.querySelectorAll('.node-card')).map((a) => a.getAttribute('href'));
    expect(links).toEqual(['/reader/alpha', '/libraries/lib1/browse/f1/collection/rec1', '/reader/gamma']);
  });

  it('shows the Volumes | Folders switch for a folder whose only stacks are stories', () => {
    expect(setup().el.querySelector('[data-testid="volume-view-switch"]')).not.toBeNull();
  });

  it('marks a selected stack read through its stories (the collection endpoint, never the volume one)', () => {
    const { fixture, comp, el, apiSpy } = setup();
    comp.selected.set(new Set(['cs.f1.rec1', 'alpha']));

    comp.bulkMarkRead(true);
    fixture.detectChanges();

    expect(apiSpy.getCollectionStack).toHaveBeenCalledWith('f1', 'rec1');
    expect(apiSpy.getVolumeStack).not.toHaveBeenCalled();
    expect(apiSpy.setItemRead.mock.calls.map((c) => c[0]).sort()).toEqual(['alpha', 'rec1-b', 'rec1-d']);
    expect(comp.nodes().find((n) => n.id === 'cs.f1.rec1')!.readRollup).toBe('Read');
    expect(el.querySelectorAll('.node-wrap')[1].querySelector('.badge.read')).not.toBeNull();
  });

  it('adds a selected stack to the favorites through its stories, and stars the card', () => {
    const { comp, apiSpy } = setup();
    comp.selected.set(new Set(['cs.f1.rec1']));

    comp.bulkFavorite(true);

    expect(apiSpy.setFavorite.mock.calls.map((c) => c[0]).sort()).toEqual(['rec1-b', 'rec1-d']);
    expect(comp.nodes().find((n) => n.id === 'cs.f1.rec1')!.isFavorite).toBe(true);
  });

  it('re-derives the stack card from its stories when one of them changes', () => {
    vi.useFakeTimers();
    try {
      const { comp, apiSpy } = setup();
      apiSpy.getCollectionStack.mockImplementation((_folder: string, key: string) =>
        of(collectionDto(key, [archiveNode(`${key}-b`, { isRead: true }), archiveNode(`${key}-d`)])));

      TestBed.inject(ReadStateService).notifyChanged('rec1-b');
      vi.advanceTimersByTime(400);

      expect(apiSpy.getCollectionStack).toHaveBeenCalledTimes(1);
      expect(apiSpy.getVolumeStack).not.toHaveBeenCalled();
      expect(comp.nodes().find((n) => n.id === 'cs.f1.rec1')!.readRollup).toBe('Reading');
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps a stack of stories out of the admin actions that address stored nodes', () => {
    const { comp } = setup();
    comp.selected.set(new Set(['cs.f1.rec1', 'alpha']));

    expect([...comp.selectedNodeIds()]).toEqual(['alpha']);
  });

  it('shows the stack as a list row with "N stories"', () => {
    const { comp, fixture, el } = setup();
    comp.viewMode.set('list');
    fixture.detectChanges();

    const row = el.querySelectorAll('app-node-row')[1];
    expect(row.querySelector('.node-title')!.textContent).toBe('Synthetic Collected Volume');
    expect(row.querySelector('.node-sub')!.textContent?.trim()).toBe('2 stories');
    expect(row.querySelector('a.node-card')!.getAttribute('href')).toBe('/libraries/lib1/browse/f1/collection/rec1');
  });
});
