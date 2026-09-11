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
 * per-library continue-reading view sourced from the incognito-and-continue-
 * data lane's `GET /reading/continue/by-library/{id}` endpoint.
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
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha' },
      { itemId: 'i2', displayName: 'Two', pageIndex: 0, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L2', libraryName: 'Beta' },
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

  it('fetches the per-library continue-reading endpoint when a sidebar library is selected', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.select('L1');
    expect(cmp.selectedLibrary()?.name).toBe('Alpha');
    expect(cmp.continueLoading()).toBe(true);

    const req = httpMock.expectOne((r) => r.url === '/api/v1/reading/continue/by-library/L1');
    expect(req.request.method).toBe('GET');
    req.flush([
      { itemId: 'i1', displayName: 'One', pageIndex: 2, contentVersion: 1, updatedAt: '2026-09-10T00:00:00Z', libraryId: 'L1', libraryName: 'Alpha' },
    ]);

    expect(cmp.continueLoading()).toBe(false);
    expect(cmp.visibleContinue().map((e) => e.itemId)).toEqual(['i1']);

    cmp.select(null);
    expect(cmp.visibleContinue().length).toBe(2);
  });

  it('clears the per-library view when the library has nothing in progress', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.select('L2');
    httpMock.expectOne((r) => r.url === '/api/v1/reading/continue/by-library/L2').flush([]);

    expect(cmp.visibleContinue().length).toBe(0);
  });

  it('removes an item from the list on dismiss', () => {
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

  // Sidebar collapse (1.5.0 taste pass): a per-device UI preference persisted to
  // localStorage so the icon-rail choice survives reloads.
  describe('sidebar collapse', () => {
    beforeEach(() => localStorage.removeItem('mangaplex-home-nav-collapsed'));

    it('defaults to expanded and toggles + persists the collapsed state', () => {
      const fixture = createComponent();
      const cmp = fixture.componentInstance;

      expect(cmp.collapsed()).toBe(false);

      cmp.toggleCollapsed();
      expect(cmp.collapsed()).toBe(true);
      expect(localStorage.getItem('mangaplex-home-nav-collapsed')).toBe('1');

      cmp.toggleCollapsed();
      expect(cmp.collapsed()).toBe(false);
      expect(localStorage.getItem('mangaplex-home-nav-collapsed')).toBe('0');
    });

    it('restores a persisted collapsed state on init', () => {
      localStorage.setItem('mangaplex-home-nav-collapsed', '1');
      const fixture = createComponent();
      expect(fixture.componentInstance.collapsed()).toBe(true);
    });
  });
});
