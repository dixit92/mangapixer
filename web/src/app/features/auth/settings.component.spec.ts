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
    // The Performance card (1.10.0) and New Chapters card (1.12.0 refinement) both load
    // the user's library-view preferences on init.
    httpMock.expectOne('/api/v1/reading/library-preferences').flush({
      viewMode: 'card', density: 'comfortable', sort: 'name', direction: 'asc',
      cardSize: '150', libraryPageSize: 100, homeRecentWindowDays: 30,
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
      cardSize: '180', libraryPageSize: 100, homeRecentWindowDays: 30,
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

function inputChange(value: number): Event {
  const input = document.createElement('input');
  input.value = String(value);
  return { target: input } as unknown as Event;
}

/**
 * Covers the "New Chapters" settings card (1.12.0 refinement): the per-user home
 * "recently added" window control, which replaces RecentChaptersService's previously
 * hardcoded 30-day window. Loads and persists via the same library-view preferences
 * blob as the Performance card (homeRecentWindowDays alongside libraryPageSize),
 * echoing the whole blob back on save so nothing else round-trips lost.
 */
describe('SettingsComponent — New Chapters (home window)', () => {
  let httpMock: HttpTestingController;

  function createComponent(homeRecentWindowDays: number | undefined = 30) {
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
      viewMode: 'card', density: 'comfortable', sort: 'name', direction: '',
      cardSize: '', libraryPageSize: 50, homeRecentWindowDays,
    });
    httpMock.expectOne('/api/v1/reading/preferences').flush({
      defaultReaderMode: 'PagedLtr', preferDoubleSpread: false, reducedMotion: false,
      preferredBackground: null, alwaysOpenReadFromStart: false,
    });
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('loads the stored homeRecentWindowDays into the control', () => {
    const cmp = createComponent(14).componentInstance;
    expect(cmp.homeWindowLoaded()).toBe(true);
    expect(cmp.homeWindowDays()).toBe(14);
  });

  it('falls back to the 30-day default when unset (0)', () => {
    const cmp = createComponent(0).componentInstance;
    expect(cmp.homeWindowDays()).toBe(30);
  });

  it('persists a new value by echoing the whole preferences blob back', () => {
    const cmp = createComponent(30).componentInstance;

    cmp.setHomeWindowDays(inputChange(7));
    expect(cmp.homeWindowDays()).toBe(7);

    const req = httpMock.expectOne('/api/v1/reading/library-preferences');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(expect.objectContaining({
      viewMode: 'card', density: 'comfortable', sort: 'name', direction: '',
      cardSize: '', libraryPageSize: 50, homeRecentWindowDays: 7,
    }));
    req.flush(null);
  });

  it('clamps an out-of-range value client-side before saving', () => {
    const cmp = createComponent(30).componentInstance;

    cmp.setHomeWindowDays(inputChange(9000));
    expect(cmp.homeWindowDays()).toBe(365);

    const req = httpMock.expectOne('/api/v1/reading/library-preferences');
    expect(req.request.body.homeRecentWindowDays).toBe(365);
    req.flush(null);
  });

  it('reverts the control and surfaces an error when the save fails', () => {
    const cmp = createComponent(30).componentInstance;

    cmp.setHomeWindowDays(inputChange(7));
    const req = httpMock.expectOne('/api/v1/reading/library-preferences');
    req.flush(
      { error: 'server_error', message: 'nope', detail: null, correlationId: null },
      { status: 500, statusText: 'Server Error' },
    );

    expect(cmp.homeWindowDays()).toBe(30); // reverted to the loaded value
    expect(cmp.homeWindowError()).toBe('nope');
  });

  it('ignores a no-op edit that resolves to the current value', () => {
    const cmp = createComponent(30).componentInstance;
    cmp.setHomeWindowDays(inputChange(30)); // already 30
    httpMock.expectNone('/api/v1/reading/library-preferences');
  });
});
