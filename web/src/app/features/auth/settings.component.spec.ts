import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  HttpTestingController,
  provideHttpClientTesting,
} from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatCheckboxChange } from '@angular/material/checkbox';

import { SettingsComponent } from './settings.component';

function checkboxChange(checked: boolean): MatCheckboxChange {
  return { checked } as MatCheckboxChange;
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
