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
  function create(nodeId: string, info: SeriesInfoDto | 'error', nextUnread: CatalogNodeDto | null = null, admin = false) {
    const metadata = {
      getSeriesInfo: vi.fn(() => (info === 'error' ? throwError(() => ({ error: 'not_found' })) : of(info))),
    };
    const api = { browseLibrary: vi.fn(() => of({ items: [], totalCount: 0, nextCursor: null, hasMore: false, nextUnread })) };
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
    expect(el.textContent).toContain('About this synthetic series.');
    expect(el.textContent).toContain('Story');
    const rows = el.querySelectorAll('[data-testid="series-items"] tbody tr');
    expect(rows.length).toBe(2);
    expect(rows[1].textContent).toContain('Ch 2'); // title falls back to the display name
    const browse = el.querySelector('[data-testid="browse-folder"]') as HTMLAnchorElement;
    expect(browse.getAttribute('href')).toBe('/libraries/lib1/browse/series-1');
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
