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
import { CatalogNodeDto, PageResponse } from '../../core/api/api-types';
import { MetadataApiService } from '../metadata/metadata-api.service';
import { SeriesInfoOverlayService } from '../metadata/series-info-overlay.service';
import { seriesInfo } from '../metadata/series-info.testing';

/**
 * Series-info wiring in browse (1.24.0): the card (i) (cover bottom-left) and the
 * list-row (i) only for nodes that carry their own information, hidden in select
 * mode; the top-bar "Series info" button inside a folder; the admin selection menu.
 */
describe('LibraryBrowseComponent series info (1.24.0)', () => {
  function node(id: string, kind: 'Folder' | 'Archive', hasSeriesInfo: boolean): CatalogNodeDto {
    return {
      id, parentId: 'p', libraryId: 'lib1', kind, displayName: id,
      availability: 'Available', coverUrl: null, childFolderCount: null, childArchiveCount: null,
      pageCount: kind === 'Archive' ? 10 : null, readingState: null, lastReadPage: null, readerDefault: null,
      isRead: false, readRollup: null, hasSeriesInfo,
    } as CatalogNodeDto;
  }

  function setup(viewMode: 'card' | 'list', nodes: CatalogNodeDto[], parentId: string | null = null, admin = false) {
    const page: PageResponse<CatalogNodeDto> = { items: nodes, totalCount: nodes.length, nextCursor: null, hasMore: false };
    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode, density: 'comfortable', sort: 'name', direction: 'asc' })),
      setLibraryPreferences: vi.fn().mockReturnValue(of(undefined)),
      getLibraries: vi.fn().mockReturnValue(of([{ id: 'lib1', name: 'L', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }])),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getNode: vi.fn().mockReturnValue(of({ displayName: 'Folder' } as CatalogNodeDto)),
    };
    const metadata = { getSeriesInfo: vi.fn().mockReturnValue(of(seriesInfo({ state: 'Web' }))) };
    const open = vi.fn(() => Promise.resolve());
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: MetadataApiService, useValue: metadata },
        { provide: SeriesInfoOverlayService, useValue: { open } },
        { provide: AuthService, useValue: { isAdmin: () => admin } },
        { provide: ReadStateService, useValue: new ReadStateService() },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : parentId) }) } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, metadata, open };
  }

  it('card view: shows the (i) only on nodes with their own series info, in the cover', () => {
    const { el } = setup('card', [node('with', 'Folder', true), node('without', 'Archive', false)]);
    const toggles = el.querySelectorAll('.cover app-info-toggle');
    expect(toggles).toHaveLength(1);
    expect(toggles[0].classList.contains('overlay')).toBe(true);
  });

  it('card view: tapping the (i) opens the overlay for that node', () => {
    const { el, open } = setup('card', [node('with', 'Folder', true)]);
    (el.querySelector('[data-testid="info-toggle"]') as HTMLButtonElement).click();
    expect(open).toHaveBeenCalledWith('with');
  });

  it('hides the (i) in select mode, where taps select', () => {
    const { fixture, comp, el } = setup('card', [node('with', 'Folder', true)]);
    comp.toggleSelectMode();
    fixture.detectChanges();
    expect(el.querySelector('app-info-toggle')).toBeNull();
  });

  it('list view: puts the (i) in the row markers after the star', () => {
    const { el } = setup('list', [node('with', 'Archive', true), node('without', 'Archive', false)]);
    const markers = el.querySelectorAll('.row-markers');
    expect(markers[0].querySelector('app-info-toggle')).not.toBeNull();
    expect(markers[1].querySelector('app-info-toggle')).toBeNull();
    const children = Array.from(markers[0].children).map((c) => c.tagName.toLowerCase());
    expect(children.indexOf('app-star-toggle')).toBeLessThan(children.indexOf('app-info-toggle'));
  });

  it('shows the top-bar Series info button inside a folder, not at the library root', () => {
    const inFolder = setup('card', [], 'folder-1');
    expect(inFolder.metadata.getSeriesInfo).toHaveBeenCalledWith('folder-1');
    expect(inFolder.el.querySelector('[data-testid="series-info-button"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const atRoot = setup('card', [], null);
    expect(atRoot.el.querySelector('app-series-info-button')).toBeNull();
  });

  it('offers the Series selection menu to admins only', () => {
    const reader = setup('card', [node('f', 'Folder', false)]);
    reader.comp.toggleSelectMode();
    reader.fixture.detectChanges();
    expect(reader.el.querySelector('app-series-selection-actions')).toBeNull();
    TestBed.resetTestingModule();
    const admin = setup('card', [node('f', 'Folder', false)], null, true);
    admin.comp.toggleSelectMode();
    admin.fixture.detectChanges();
    expect(admin.el.querySelector('app-series-selection-actions')).not.toBeNull();
  });
});
