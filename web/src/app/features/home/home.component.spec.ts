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
 * Covers the Plex-pattern home logic (1.4.0): the sidebar selection and the
 * library-filtered continue-reading view. `libraryId` on the entries is supplied
 * by the incognito-and-continue-data lane; the fixtures include it so the filter
 * is exercised ahead of that merge.
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
      { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: null },
      { id: 'L2', name: 'Beta', isScanning: false, itemCount: 5, lastScanCompleted: null, defaultReaderMode: null },
    ]);
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue').flush([
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1' },
      { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2' },
    ]);
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('defaults to Home with the full consolidated continue-reading list', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    expect(cmp.selectedLibraryId()).toBeNull();
    expect(cmp.libraries().length).toBe(2);
    expect(cmp.visibleContinue().length).toBe(2);
  });

  it('filters continue-reading to the selected library', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.select('L1');
    expect(cmp.selectedLibrary()?.name).toBe('Alpha');
    expect(cmp.visibleContinue().map((e) => e.itemId)).toEqual(['i1']);

    cmp.select(null);
    expect(cmp.visibleContinue().length).toBe(2);
  });

  it('removes an item from the list on dismiss', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;
    const evt = new Event('click');

    cmp.dismiss(evt, { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z' });
    httpMock.expectOne('/api/v1/reading/continue/i1').flush(null);

    expect(cmp.continueReading().map((e) => e.itemId)).toEqual(['i2']);
  });
});
