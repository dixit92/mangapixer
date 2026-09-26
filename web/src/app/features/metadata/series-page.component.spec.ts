import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { SeriesPageComponent } from './series-page.component';
import { seriesInfo } from './series-info.testing';

/**
 * Series page `/series/:nodeId` (1.24.0): anchor redirect, sections from the shared
 * DTO, the ComicInfo items table, "Continue reading" from the existing browse
 * nextUnread, and the hidden / missing states.
 */
describe('SeriesPageComponent', () => {
  function create(
    nodeId: string, info: SeriesInfoDto | 'error', nextUnread: CatalogNodeDto | null = null, admin = false,
    archiveNode: Partial<CatalogNodeDto> | null = null,
  ) {
    const metadata = {
      getSeriesInfo: vi.fn(() => (info === 'error' ? throwError(() => ({ error: 'not_found' })) : of(info))),
    };
    const api = {
      browseLibrary: vi.fn(() => of({ items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread })),
      getNode: vi.fn(() => of(archiveNode as CatalogNodeDto)),
    };
    TestBed.configureTestingModule({
      imports: [SeriesPageComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: MetadataApiService, useValue: metadata },
        { provide: ApiService, useValue: api },
        { provide: AuthService, useValue: { isAdmin: () => admin } },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ nodeId })) } },
      ],
    });
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SeriesPageComponent);
    fixture.detectChanges();
    return { fixture, metadata, api, navigate, el: fixture.nativeElement as HTMLElement };
  }

  it('redirects a descendant node id to the anchor, replacing the URL', () => {
    const { navigate, metadata } = create('chapter-3', seriesInfo({ nodeId: 'chapter-3', anchorNodeId: 'series-1' }));
    expect(metadata.getSeriesInfo).toHaveBeenCalledWith('chapter-3', true);
    expect(navigate).toHaveBeenCalledWith(['/series', 'series-1'], { replaceUrl: true });
  });

  it('renders the anchor with its sections and item table', () => {
    const { el, navigate } = create('series-1', seriesInfo({
      nodeId: 'series-1',
      anchorNodeId: 'series-1',
      state: 'WebAndComicInfo',
      web: { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: '2026-09-25T00:00:00Z' },
      description: 'About this synthetic series.',
      creators: [{ name: 'A Writer', role: 'writer' }],
      genres: ['Action'],
      comicInfo: { itemsWithComicInfo: 2, itemsTotal: 3 },
      items: [
        { nodeId: 'c1', displayName: 'Ch 1', number: '1', volume: 1, title: 'Start', year: 2020 },
        { nodeId: 'c2', displayName: 'Ch 2', number: '2', volume: 1, title: null, year: 2021 },
      ],
    }));
    expect(navigate).not.toHaveBeenCalled();
    // The description appears ONCE (About), not also in the header summary.
    expect(el.textContent!.split('About this synthetic series.').length - 1).toBe(1);
    expect(el.querySelector('.hero .description')).toBeNull();
    // Both sources exist: the precedence line is shown.
    expect(el.querySelector('[data-testid="series-precedence"]')).not.toBeNull();
    expect(el.textContent).toContain('Story');
    const rows = el.querySelectorAll('[data-testid="series-items"] tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[1].textContent).toContain('Ch 2'); // title falls back to the display name
    const browse = el.querySelector('[data-testid="browse-folder"]') as HTMLAnchorElement;
    expect(browse.getAttribute('href')).toBe('/libraries/lib1/browse/series-1');
  });

  it('shows no precedence line unless both web data and ComicInfo exist', () => {
    const { el } = create('series-1', seriesInfo({
      nodeId: 'series-1', anchorNodeId: 'series-1', state: 'ComicInfo', comicInfo: { itemsWithComicInfo: 1, itemsTotal: 1 },
    }));
    expect(el.textContent).toContain('ComicInfo: 1 of 1 items');
    expect(el.querySelector('[data-testid="series-precedence"]')).toBeNull();
  });

  it('offers Read + Show in folder (not Browse folder) for an archive anchor', () => {
    const { el, api } = create('a1', seriesInfo({
      nodeId: 'a1', nodeKind: 'Archive', anchorNodeId: 'a1', anchorKind: 'Archive',
    }), null, false, { id: 'a1', parentId: 'f9', readingState: 'Unread' });
    expect(api.getNode).toHaveBeenCalledWith('a1');
    expect(el.querySelector('[data-testid="browse-folder"]')).toBeNull();
    const read = el.querySelector('[data-testid="read-archive"]') as HTMLAnchorElement;
    expect(read.getAttribute('href')).toBe('/reader/a1');
    expect(read.textContent).toContain('Read');
    const show = el.querySelector('[data-testid="show-in-folder"]') as HTMLAnchorElement;
    expect(show.getAttribute('href')).toBe('/libraries/lib1/browse/f9');
    expect(api.browseLibrary).not.toHaveBeenCalled();
  });

  it('says Continue reading for an archive anchor in progress', () => {
    const { el } = create('a1', seriesInfo({
      nodeId: 'a1', nodeKind: 'Archive', anchorNodeId: 'a1', anchorKind: 'Archive',
    }), null, false, { id: 'a1', parentId: 'f9', readingState: 'InProgress' });
    expect(el.querySelector('[data-testid="read-archive"]')!.textContent).toContain('Continue reading');
  });

  it('offers Continue reading from the folder\'s next unread archive', () => {
    const next = { id: 'c2', parentId: 'series-1', libraryId: 'lib1', kind: 'Archive', displayName: 'Ch 2', availability: 'Available' } as CatalogNodeDto;
    const { el, api } = create('series-1', seriesInfo({ nodeId: 'series-1', anchorNodeId: 'series-1' }), next);
    expect(api.browseLibrary).toHaveBeenCalledWith('lib1', 'series-1', null, 1);
    const cont = el.querySelector('[data-testid="continue-reading"]') as HTMLAnchorElement;
    expect(cont.getAttribute('href')).toBe('/reader/c2');
  });

  it('behaves as "no series information" when metadata is hidden', () => {
    const { el, api } = create('series-1', seriesInfo({ nodeId: 'series-1', anchorNodeId: 'series-1', state: 'None', title: null }));
    expect(el.querySelector('[data-testid="series-none"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="continue-reading"]')).toBeNull();
    expect(api.browseLibrary).not.toHaveBeenCalled();
  });

  it('shows the not-available state on 404', () => {
    const { el } = create('nope', 'error');
    expect(el.textContent).toContain('not available');
  });

  it('shows the admin section to admins', () => {
    const { el } = create('series-1', seriesInfo({ nodeId: 'series-1', anchorNodeId: 'series-1' }), null, true);
    expect(el.querySelector('app-series-admin-actions')).not.toBeNull();
  });
});
