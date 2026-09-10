import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibraryBrowseComponent } from './library-browse.component';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, LibraryDto, LibraryViewPreferencesDto, PageResponse, JumpIndexBucketDto } from '../../core/api/api-types';

/**
 * Breadcrumb tests for LibraryBrowseComponent (1.3.1 fix — Lane A). The library-name
 * crumb must link to the browse root `/libraries/{id}/browse` — not the library
 * landing page `/libraries/{id}` — and must be clickable at the root level too
 * (where there are no sub-folder breadcrumbs).
 */
describe('LibraryBrowseComponent breadcrumb root link', () => {
  function setup(libraryId: string, parentId: string | null) {
    const prefs: LibraryViewPreferencesDto = { viewMode: 'grid', density: 'comfortable', sort: 'name' };
    const libs: LibraryDto[] = [{ id: libraryId, name: 'Test Lib', isScanning: false, itemCount: 0, lastScanCompleted: null, defaultReaderMode: null }];
    const emptyPage: PageResponse<CatalogNodeDto> = { items: [], totalCount: 0, nextCursor: null, hasMore: false };

    const apiSpy = {
      getLibraryPreferences: vi.fn().mockReturnValue(of(prefs)),
      getLibraries: vi.fn().mockReturnValue(of(libs)),
      browseLibrary: vi.fn().mockReturnValue(of(emptyPage)),
      getBreadcrumbs: vi.fn().mockReturnValue(of({ nodeId: 'x', trail: [] })),
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
    const { fixture } = setup('lib1', 'node1');
    const el: HTMLElement = fixture.nativeElement;
    const links = el.querySelectorAll('.breadcrumbs a');
    // First link is the library-name crumb; it must point to /libraries/lib1/browse
    expect(links.length).toBeGreaterThan(0);
    expect(links[0].getAttribute('href')).toBe('/libraries/lib1/browse');
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
function comp_jump(fixture: any, label: string) {
  const comp = fixture.componentInstance as LibraryBrowseComponent;
  const buckets = comp.jumpBuckets();
  const bucket = buckets.find((b) => b.label === label);
  expect(bucket).toBeDefined();
  comp.jumpToBucket(bucket!);
}
