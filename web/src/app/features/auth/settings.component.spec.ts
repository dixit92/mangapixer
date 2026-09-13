import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatCheckboxChange } from '@angular/material/checkbox';
import { MatSelectChange } from '@angular/material/select';

import { SettingsComponent } from './settings.component';

function checkboxChange(checked: boolean): MatCheckboxChange {
  return { checked } as MatCheckboxChange;
}

function selectChange(value: number): MatSelectChange {
  return { value } as MatSelectChange;
}

/**
 * Covers the "Private Libraries" settings UI (1.4.0): a per-library checkbox
 * list wired to the incognito-and-continue-data lane's private-libraries
 * prefs endpoints, with replacement semantics on every toggle.
 */
describe('SettingsComponent — Private libraries', () => {
  let httpMock: HttpTestingController;

  function createComponent() {
    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
      ],
    });
    const fixture = TestBed.createComponent(SettingsComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges(); // triggers ngOnInit

    httpMock.expectOne('/api/v1/libraries').flush([
      { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: null },
      { id: 'L2', name: 'Beta', isScanning: false, itemCount: 5, lastScanCompleted: null, defaultReaderMode: null },
    ]);
    httpMock.expectOne('/api/v1/reading/private-libraries').flush({ libraryIds: ['L2'] });
    // The Performance card (1.10.0) loads the user's library-view preferences on init.
    httpMock.expectOne('/api/v1/reading/library-preferences').flush({
      viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc',
      cardSize: '150', libraryPageSize: 100,
    });
    // The embedded reading-preferences card (1.9.0) loads the user's preferences on init.
    httpMock.expectOne('/api/v1/reading/preferences').flush({
      defaultReaderMode: 'PagedLtr', preferDoubleSpread: false, reducedMotion: false,
      preferredBackground: null, alwaysOpenReadFromStart: false,
    });
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('loads libraries and marks the currently Private ones', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    expect(cmp.libraries().length).toBe(2);
    expect(cmp.privateLibraryIds().has('L2')).toBe(true);
    expect(cmp.privateLibraryIds().has('L1')).toBe(false);
  });

  it('PUTs the full replacement set when a library is marked Private', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.togglePrivate('L1', checkboxChange(true));

    expect(cmp.privateLibraryIds().has('L1')).toBe(true);
    const req = httpMock.expectOne('/api/v1/reading/private-libraries');
    expect(req.request.method).toBe('PUT');
    expect(new Set(req.request.body.libraryIds)).toEqual(new Set(['L1', 'L2']));
    req.flush(null);
  });

  it('PUTs the full replacement set when a library is unmarked', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.togglePrivate('L2', checkboxChange(false));

    expect(cmp.privateLibraryIds().has('L2')).toBe(false);
    const req = httpMock.expectOne('/api/v1/reading/private-libraries');
    expect(req.request.body.libraryIds).toEqual([]);
    req.flush(null);
  });

  it('reverts the checkbox and surfaces an error when the save fails', () => {
    const fixture = createComponent();
    const cmp = fixture.componentInstance;

    cmp.togglePrivate('L1', checkboxChange(true));
    const req = httpMock.expectOne('/api/v1/reading/private-libraries');
    req.flush(
      { error: 'server_error', message: 'Failed to update Private libraries', detail: null, correlationId: null },
      { status: 500, statusText: 'Server Error' },
    );

    expect(cmp.privateLibraryIds().has('L1')).toBe(false);
    expect(cmp.privateLibrariesError()).toBe('Failed to update Private libraries');
  });
});

/**
 * Covers the "Performance" settings card (1.10.0, F5): the items-per-load control,
 * relocated out of the browse View menu into Settings. It loads the per-user
 * library-view preferences, offers the initial-load size options, and persists the
 * choice by echoing the whole preferences blob back with only libraryPageSize changed.
 */
describe('SettingsComponent — Performance (items per load)', () => {
  let httpMock: HttpTestingController;

  function createComponent() {
    TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
      ],
    });
    const fixture = TestBed.createComponent(SettingsComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();

    httpMock.expectOne('/api/v1/libraries').flush([]);
    httpMock.expectOne('/api/v1/reading/private-libraries').flush({ libraryIds: [] });
    httpMock.expectOne('/api/v1/reading/library-preferences').flush({
      viewMode: 'list', density: 'compact', sort: 'recentlyAdded', direction: 'desc',
      cardSize: '180', libraryPageSize: 100,
    });
    httpMock.expectOne('/api/v1/reading/preferences').flush({
      defaultReaderMode: 'PagedLtr', preferDoubleSpread: false, reducedMotion: false,
      preferredBackground: null, alwaysOpenReadFromStart: false,
    });
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('loads the stored libraryPageSize into the control', () => {
    const cmp = createComponent().componentInstance;
    expect(cmp.pageSizeLoaded()).toBe(true);
    expect(cmp.pageSize()).toBe(100);
    expect(cmp.pageSizeOptions).toEqual([25, 50, 100, 200]);
  });

  it('persists a new size by echoing the whole preferences blob back', () => {
    const cmp = createComponent().componentInstance;

    cmp.setPageSize(selectChange(200));
    expect(cmp.pageSize()).toBe(200);

    const req = httpMock.expectOne('/api/v1/reading/library-preferences');
    expect(req.request.method).toBe('PUT');
    // Only libraryPageSize changes; the other browse-view fields round-trip untouched.
    expect(req.request.body).toEqual(expect.objectContaining({
      viewMode: 'list', density: 'compact', sort: 'recentlyAdded', direction: 'desc',
      cardSize: '180', libraryPageSize: 200,
    }));
    req.flush(null);
  });

  it('reverts the control and surfaces an error when the save fails', () => {
    const cmp = createComponent().componentInstance;

    cmp.setPageSize(selectChange(25));
    const req = httpMock.expectOne('/api/v1/reading/library-preferences');
    req.flush(
      { error: 'server_error', message: 'nope', detail: null, correlationId: null },
      { status: 500, statusText: 'Server Error' },
    );

    expect(cmp.pageSize()).toBe(100); // reverted to the loaded value
    expect(cmp.pageSizeError()).toBe('nope');
  });

  it('ignores a no-op selection of the current size', () => {
    const cmp = createComponent().componentInstance;
    cmp.setPageSize(selectChange(100)); // already 100
    httpMock.expectNone('/api/v1/reading/library-preferences');
  });
});
