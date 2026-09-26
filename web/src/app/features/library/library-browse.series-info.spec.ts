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
import { FavoritesStateService } from '../../core/favorites/favorites-state.service';
import { MetadataApiService } from '../metadata/metadata-api.service';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { SeriesInfoOverlayService } from '../metadata/series-info-overlay.service';
import { seriesInfo } from '../metadata/series-info.testing';

/**
 * Series-info wiring in browse (1.24.0): the card (i) (cover bottom-left) and the
 * list-row (i) only for nodes that carry their own information, hidden in select
 * mode (with the card star); synced in place by `MetadataStateService` without a
 * browse reload; the top-bar "Series info" button inside a folder; the admin
 * selection menu.
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
      setFavorite: vi.fn().mockReturnValue(of(undefined)),
    };
    const metadata = {
      getSeriesInfo: vi.fn().mockReturnValue(of(seriesInfo({ state: 'Web' }))),
      getSettings: vi.fn().mockReturnValue(of({ showSeriesInfo: true, libraries: [] })),
    };
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
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, metadata, open, apiSpy };
  }

  it('card view: shows the (i) only on nodes with their own series info, in the cover', () => {
    const { el } = setup('card', [node('with', 'Folder', true), node('without', 'Archive', false)]);
    const shown = el.querySelectorAll('.cover app-info-toggle [data-testid="info-toggle"]');
    expect(shown).toHaveLength(1);
    expect(shown[0].closest('app-info-toggle')!.classList.contains('overlay')).toBe(true);
  });

  it('card view: the (i) appears right after a Link and goes after an Unlink, without reloading browse', () => {
    const { fixture, comp, el, apiSpy } = setup('card', [node('a', 'Folder', false), node('b', 'Archive', false)]);
    const shownIds = () => Array.from(el.querySelectorAll('.node-card')).filter((c) => c.querySelector('[data-testid="info-toggle"]'))
      .map((c) => c.querySelector('.node-title')!.textContent);
    expect(shownIds()).toEqual([]);
    const browseCalls = apiSpy.browseLibrary.mock.calls.length;

    const state = TestBed.inject(MetadataStateService);
    state.announce('b', true);
    fixture.detectChanges();
    expect(shownIds()).toEqual(['b']);
    expect(comp.nodes().find((n) => n.id === 'b')!.hasSeriesInfo).toBe(true); // DTO patched in place

    // Survives a select-mode round trip (the toggle is re-created from the patched DTO).
    comp.toggleSelectMode();
    fixture.detectChanges();
    comp.toggleSelectMode();
    fixture.detectChanges();
    expect(shownIds()).toEqual(['b']);

    state.announce('b', false);
    fixture.detectChanges();
    expect(shownIds()).toEqual([]);
    expect(apiSpy.browseLibrary.mock.calls.length).toBe(browseCalls); // never reloaded
  });

  it('card view: hides the favorites star in select mode and keeps a toggled star after it', () => {
    const { fixture, comp, el } = setup('card', [node('a', 'Folder', false)]);
    expect(el.querySelector('.cover app-star-toggle')).not.toBeNull();
    TestBed.inject(FavoritesStateService).setFavorite('a', true).subscribe(); // starred elsewhere (reader)
    comp.toggleSelectMode();
    fixture.detectChanges();
    expect(el.querySelector('.cover app-star-toggle')).toBeNull(); // the select check owns the corner
    comp.toggleSelectMode();
    fixture.detectChanges();
    expect(el.querySelector('.cover app-star-toggle [aria-pressed="true"]')).not.toBeNull();
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
    expect(markers[0].querySelector('[data-testid="info-toggle"]')).not.toBeNull();
    expect(markers[1].querySelector('[data-testid="info-toggle"]')).toBeNull();
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
    expect(admin.el.querySelector('[data-testid="series-selection-menu"]')).not.toBeNull();
  });
});
