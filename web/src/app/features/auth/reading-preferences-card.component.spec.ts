import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatSlideToggleChange } from '@angular/material/slide-toggle';

import { ReadingPreferencesCardComponent } from './reading-preferences-card.component';

/**
 * Reading preferences card (1.9.0): the "Always open read archives from the start"
 * toggle. Loads the full preferences DTO on init and echoes it back on save so the
 * other reader preferences are preserved.
 */
describe('ReadingPreferencesCardComponent', () => {
  let httpMock: HttpTestingController;

  const prefs = (alwaysOpenReadFromStart: boolean) => ({
    defaultReaderMode: 'PagedLtr', preferDoubleSpread: false, reducedMotion: false,
    preferredBackground: null, alwaysOpenReadFromStart,
  });

  function create() {
    TestBed.configureTestingModule({
      imports: [ReadingPreferencesCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(ReadingPreferencesCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock.verify());

  it('loads the current preference on init', () => {
    const fixture = create();
    httpMock.expectOne('/api/v1/reading/preferences').flush(prefs(true));
    expect(fixture.componentInstance.alwaysOpenReadFromStart()).toBe(true);
  });

  it('PUTs the full preferences DTO with the flipped toggle, preserving other fields', () => {
    const fixture = create();
    httpMock.expectOne('/api/v1/reading/preferences').flush(prefs(false));

    fixture.componentInstance.toggle({ checked: true } as MatSlideToggleChange);

    const req = httpMock.expectOne('/api/v1/reading/preferences');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.alwaysOpenReadFromStart).toBe(true);
    expect(req.request.body.defaultReaderMode).toBe('PagedLtr'); // other fields preserved
    req.flush(null);
    expect(fixture.componentInstance.alwaysOpenReadFromStart()).toBe(true);
  });

  it('reverts the toggle and surfaces an error when the save fails', () => {
    const fixture = create();
    httpMock.expectOne('/api/v1/reading/preferences').flush(prefs(false));

    fixture.componentInstance.toggle({ checked: true } as MatSlideToggleChange);
    httpMock.expectOne('/api/v1/reading/preferences').flush(
      { error: 'server_error', message: 'nope', detail: null, correlationId: null },
      { status: 500, statusText: 'Server Error' });

    expect(fixture.componentInstance.alwaysOpenReadFromStart()).toBe(false);
    expect(fixture.componentInstance.error()).toBeTruthy();
  });
});
