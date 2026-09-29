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
import { CatalogNodeDto, PageResponse, VolumeStackSummaryDto, VolumeViewDto } from '../../core/api/api-types';

/**
 * The Volumes view in browse (1.29.0): stack cards on the shared stack card with the incomplete mark, the link into the stack
 * view, the Volumes | Folders switch (persisted through library-preferences, sent as `group`), and selection that never
 * includes a stack. The list itself comes from the server; these tests drive the component with canned pages.
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

  function setup(opts: { nodes?: CatalogNodeDto[]; view?: Partial<VolumeViewDto>; prefs?: Record<string, unknown>; admin?: boolean; viewMode?: string } = {}) {
    const nodes = opts.nodes ?? [stackNode('1'), stackNode('2', { missingCount: 0, chapterCount: 8, presentCount: 8 }), archiveNode('loose')];
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const view: VolumeViewDto = { nodeId: 'f1', available: true, active: true, consolidated: false, stackCount: 2, ...opts.view };
    const apiSpy = {
      getVolumeView: vi.fn().mockReturnValue(of(view)),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode: opts.viewMode ?? 'card', density: 'comfortable', sort: 'name', direction: 'asc', ...opts.prefs })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Series' } as CatalogNodeDto)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => !!opts.admin, currentUser: () => null } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : 'f1') }) } },
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

  it('makes the switch inert while another sort or a filter makes the list flat', () => {
    const { fixture, comp, el } = setup();
    const volumes = () => el.querySelector('[data-testid="view-volumes"]') as HTMLButtonElement;
    expect(volumes().disabled).toBe(false);

    comp.setReadStateFilter('unread');
    fixture.detectChanges();
    expect(volumes().disabled).toBe(true);
    comp.setReadStateFilter('all');
    fixture.detectChanges();
    expect(volumes().disabled).toBe(false);

    comp.toggleFavoritesOnly();
    fixture.detectChanges();
    expect(volumes().disabled).toBe(true);
  });

  it('never selects a stack: a tap, Select all and a range leave it out', () => {
    const { fixture, comp, el } = setup();
    comp.toggleSelectMode();
    fixture.detectChanges();

    (el.querySelectorAll('.node-card')[0] as HTMLElement).click();
    expect(comp.selected().size).toBe(0);
    comp.selectAll();
    expect([...comp.selected()]).toEqual(['loose']);
    comp.clearSelection();
    comp.selectAllUnread();
    expect([...comp.selected()]).toEqual(['loose']);
    // No select check on a stack; the archive has one.
    expect(el.querySelectorAll('.node-wrap')[0].querySelector('.check')).toBeNull();
    expect(el.querySelectorAll('.node-wrap')[2].querySelector('.check')).not.toBeNull();
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

  it('keeps the list mode working: a stack row has a cover, its mark and no select box', () => {
    const { el } = setup({ viewMode: 'list' });

    const row = el.querySelectorAll('.node-wrap')[0];
    expect(row.querySelector('app-stack-card')).toBeNull(); // rows use the plain small cover
    expect(row.querySelector('[data-testid="stack-incomplete"]')).not.toBeNull();
    expect(row.querySelector('.row-select')).toBeNull();
    expect(row.querySelector('app-star-toggle')).toBeNull();
    expect(el.querySelectorAll('.node-wrap')[2].querySelector('.row-select')).not.toBeNull();
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
    expect(comp.selected().size).toBe(0);
  });

  it('shows the series status line while the Volumes view of a linked series is shown', () => {
    const { fixture, comp, el } = setup({ view: { hasSeriesStatus: true, seriesStatus: 'Ongoing', missingVolumes: 2, missingChapters: 3, releaseKnown: true, language: 'en' } });
    expect(el.querySelector('[data-testid="series-status"]')!.textContent).toContain('Ongoing - 2 volumes, 3 chapters missing');

    comp.setSeriesView(false);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="series-status"]')).toBeNull();
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
