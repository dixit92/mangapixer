import { TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';
import { HttpTestingController } from '@angular/common/http/testing';

import { LibraryBrowseComponent } from './library-browse.component';
import { JumpIndexBucketDto } from '../../core/api/api-types';

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
