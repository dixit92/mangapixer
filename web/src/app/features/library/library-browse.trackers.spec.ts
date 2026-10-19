import { vi } from 'vitest';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, LibraryDto, LibraryViewMode, PageResponse } from '../../core/api/api-types';
import { TrackerStateService } from '../trackers/tracker-state.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { seriesRow, summary, trackerAccount } from '../trackers/trackers.testing';

/**
 * Tracker sync in the folder browse (1.43.0 owner redesign): the card mark C - a gold strip on the top edge of a cover the user's
 * tracker counts as read (`trackerRead`), in card and list mode, never on the others - and the series header's gold "Tracking"
 * button, shown only for a user with a connected tracker on a folder whose series has a summary.
 */
describe('LibraryBrowseComponent tracker marks and the Tracking button', () => {
  function archive(id: string, trackerRead: string[] | null): CatalogNodeDto {
    return {
      id, parentId: 'f1', libraryId: 'lib1', kind: 'Archive', displayName: `Chapter ${id}`, availability: 'Available',
      coverUrl: `/api/v1/items/${id}/cover`, childFolderCount: null, childArchiveCount: null, pageCount: 20, readingState: null,
      lastReadPage: null, readerDefault: null, isRead: id === 'a1', readRollup: null, hasSeriesInfo: false, trackerRead,
    } as CatalogNodeDto;
  }

  function setup(viewMode: LibraryViewMode, connected = false) {
    const libs: LibraryDto[] = [{ id: 'lib1', name: 'Test Lib', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null, icon: null }];
    const page: PageResponse<CatalogNodeDto> = {
      items: [archive('a1', ['mal']), archive('a2', ['mal', 'mangaupdates']), archive('a3', null)], totalCount: 3, nextCursor: null, hasMore: false,
    };
    const apiSpy = {
      getVolumeView: vi.fn().mockReturnValue(of({ nodeId: 'f1', available: false, active: false, consolidated: false, stackCount: 0 })),
      getLibraryPreferences: vi.fn().mockReturnValue(of({ viewMode, density: 'comfortable', sort: 'name' })),
      getLibraries: vi.fn().mockReturnValue(of(libs)),
      browseLibrary: vi.fn().mockReturnValue(of(page)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'f1', trail: [] })),
      getNode: vi.fn().mockReturnValue(of({ ...archive('f1', null), kind: 'Folder', displayName: 'Saga' })),
      getJumpIndex: vi.fn().mockReturnValue(of({ libraryId: 'lib1', buckets: [] })),
      getReadMark: vi.fn((itemId: string) => of({ itemId, isRead: true })),
      getProgress: vi.fn().mockReturnValue(of(null)),
    };
    TestBed.configureTestingModule({
      imports: [LibraryBrowseComponent],
      providers: [
        provideRouter([
          { path: 'libraries/:libraryId/browse/:nodeId', component: BlankComponent },
          { path: 'reader/:itemId', component: BlankComponent },
        ]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ApiService, useValue: apiSpy },
        { provide: AuthService, useValue: { isAdmin: () => false } },
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: (k: string) => (k === 'libraryId' ? 'lib1' : 'f1') }) } },
      ],
    });
    // The app shell loads the overview at sign-in; here it is set directly.
    if (connected) TestBed.inject(TrackerStateService).set({ trackers: [summary({ account: trackerAccount() })] });
    const fixture = TestBed.createComponent(LibraryBrowseComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    return { fixture, http, el: fixture.nativeElement as HTMLElement, apiSpy, page };
  }

  const marks = (el: HTMLElement) => [...el.querySelectorAll<HTMLElement>('[data-testid="tracker-read-mark"]')];

  // Other parts of the page (series info, covers) make their own requests; only the tracker ones are asserted here.
  afterEach(() => TestBed.resetTestingModule());

  it('card mode: the strip sits on the cover of the items a tracker counts as read, with their names, and nowhere else', () => {
    const { el, http } = setup('card');
    http.expectNone('/api/v1/trackers/nodes/f1/summary');
    const found = marks(el);
    expect(found).toHaveLength(2);
    for (const m of found) expect(m.closest('.cover')).not.toBeNull();
    expect(found.map((m) => m.getAttribute('aria-label'))).toEqual(['Read on MyAnimeList', 'Read on MyAnimeList and MangaUpdates']);
    const cards = [...el.querySelectorAll('.node-wrap')];
    expect(cards[2].querySelector('[data-testid="tracker-read-mark"]')).toBeNull();
    // MangaPixer's own read badge stays as it was.
    expect(cards[0].querySelector('.badge.read')).not.toBeNull();
  });

  it('list mode: the ring goes around the whole row, not the thumbnail (owner, 2026-10-15)', () => {
    const { el } = setup('list');
    const found = marks(el);
    expect(found).toHaveLength(2);
    for (const m of found) {
      expect(m.closest('app-node-row .node-card')).not.toBeNull();
      expect(m.closest('app-node-row .cover')).toBeNull();
    }
  });

  it('no Tracking button without a connected tracker; with one, a gold button that opens the series popover', () => {
    const none = setup('card');
    expect(none.el.querySelector('[data-testid="series-tracking-button"]')).toBeNull();
    none.http.expectNone('/api/v1/trackers/nodes/f1/summary');
    TestBed.resetTestingModule();

    const { fixture, el, http } = setup('card', true);
    http.expectOne('/api/v1/trackers/nodes/f1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    const button = el.querySelector('[data-testid="series-tracking-button"]') as HTMLButtonElement;
    expect(button).not.toBeNull();
    // Next to "Series info" (its slot follows the Tracking button in the bar).
    expect(button.closest('app-series-tracking-button')!.nextElementSibling!.tagName.toLowerCase()).toBe('app-series-info-button');
  });

  it('1.45.0 (K8): back from the reader, every listed card\'s ring is re-read in place - at once and once more 3 s later', async () => {
    const { fixture, el, apiSpy, page } = setup('card', true);
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/libraries/lib1/browse/f1');
    const listCalls = () => apiSpy.browseLibrary.mock.calls.filter((a) => a[3] !== 1); // not the Continue row's 1-item look
    const first = listCalls()[0];
    expect(listCalls()).toHaveLength(1);
    expect(marks(el)).toHaveLength(2);
    const untouched = fixture.componentInstance.nodes()[1];

    // In the reader: chapter a3 is finished; MyAnimeList now counts a3 (and a1 / a2 stay counted).
    await router.navigateByUrl('/reader/a3');
    TestBed.inject(ReadStateService).notifyChanged('a3');
    expect(listCalls()).toHaveLength(1); // nothing while the reader is shown
    apiSpy.browseLibrary.mockReturnValue(of({ ...page, items: page.items.map((n) => ({ ...n, trackerRead: n.trackerRead ?? ['mal'] })) }));

    vi.useFakeTimers();
    try {
      await router.navigateByUrl('/libraries/lib1/browse/f1');
      fixture.detectChanges();
      // The same page request as the list's own (folder, sort, filters, cursor), and the ring is on all three cards - no reload.
      expect(listCalls()).toHaveLength(2);
      expect(listCalls()[1]).toEqual(first);
      expect(marks(el)).toHaveLength(3);
      // Only the ring changed: an unchanged card keeps its object (nothing re-rendered, the scroll stays).
      expect(fixture.componentInstance.nodes()[1]).toBe(untouched);
      vi.advanceTimersByTime(LibraryBrowseComponent.TRACKER_RETRY_MS);
      expect(listCalls()).toHaveLength(3);
      // Another navigation without a new read change asks nothing.
      await router.navigateByUrl('/libraries/lib1/browse/f1?x=1');
      vi.advanceTimersByTime(LibraryBrowseComponent.TRACKER_RETRY_MS);
      expect(listCalls()).toHaveLength(3);
    } finally {
      vi.useRealTimers();
    }
  });

  it('1.45.0 (K8): a viewer without a connected tracker gets no extra request on the way back', async () => {
    const { apiSpy } = setup('card', false);
    TestBed.inject(TrackerStateService).set({ trackers: [] });
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/reader/a3');
    TestBed.inject(ReadStateService).notifyChanged('a3');
    await router.navigateByUrl('/libraries/lib1/browse/f1');
    expect(apiSpy.browseLibrary.mock.calls.filter((a) => a[3] !== 1)).toHaveLength(1);
  });

  it('UX6: with the Tracking button the bar may wrap (the trail keeps its room; on a phone it takes its own row); without it, not', () => {
    const none = setup('card', true);
    none.http.expectOne('/api/v1/trackers/nodes/f1/summary').flush({ seriesId: null, rows: [] });
    none.fixture.detectChanges();
    expect(none.el.querySelector('.browse-bar')!.classList).not.toContain('has-tracking');
    TestBed.resetTestingModule();

    const { fixture, el, http } = setup('card', true);
    http.expectOne('/api/v1/trackers/nodes/f1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="series-tracking-button"]')).not.toBeNull();
    expect(el.querySelector('.browse-bar')!.classList).toContain('has-tracking');
  });
});

@Component({ standalone: true, template: '' })
class BlankComponent {}
