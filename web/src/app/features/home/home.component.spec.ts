import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HomeComponent } from './home.component';

/**
 * Home page tests (1.5.0). The library sidebar was promoted to the app shell, so
 * home no longer owns a sidebar and no longer filters continue-reading by a
 * locally selected library. Home is now just the consolidated continue-reading
 * row plus the library grid (each card carrying a Task C reading-direction
 * indicator).
 */
describe('HomeComponent', () => {
  let httpMock: HttpTestingController;

  function createComponent() {
    TestBed.configureTestingModule({
      imports: [HomeComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
      ],
    });
    const fixture = TestBed.createComponent(HomeComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges(); // triggers ngOnInit

    httpMock.expectOne('/api/v1/libraries').flush([
      { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: 'PagedRtl' },
      { id: 'L2', name: 'Beta', isScanning: false, itemCount: 5, lastScanCompleted: null, defaultReaderMode: null },
    ]);
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush([
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha' },
      { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2', libraryName: 'Beta' },
    ]);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('shows the consolidated continue-reading list and the library grid', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    expect(cmp.libraries().length).toBe(2);
    expect(cmp.continueReading().length).toBe(2);

    // One card per library, labelled by name. (Card navigation to the browse
    // root is a RouterLink on <mat-card>; the anchor-based link targets are
    // asserted in the sidebar spec, where hrefs are actually rendered.)
    const cards = fixture.nativeElement.querySelectorAll('.library-card') as NodeListOf<HTMLElement>;
    expect(cards).toHaveLength(2);
    expect(cards[0].textContent).toContain('Alpha');
    expect(cards[1].textContent).toContain('Beta');
  });

  it('shows a reading-direction indicator only for libraries with an explicit mode (Task C)', () => {
    const fixture = createComponent();
    const cards = fixture.nativeElement.querySelectorAll('.library-card') as NodeListOf<HTMLElement>;

    // L1 = PagedRtl -> badge with the RTL label; L2 = null -> no badge.
    const l1Dir = cards[0].querySelector('.card-dir');
    expect(l1Dir).not.toBeNull();
    expect(l1Dir!.getAttribute('aria-label')).toBe('Reading direction: Right to left');
    expect(cards[1].querySelector('.card-dir')).toBeNull();
  });

  it('removes an item from the continue-reading list on dismiss', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    const evt = new Event('click');

    cmp.dismiss(evt, {
      itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1,
      updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha',
    });
    httpMock.expectOne('/api/v1/reading/continue/i1').flush(null);

    expect(cmp.continueReading().map((e) => e.itemId)).toEqual(['i2']);
  });
});
