import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { UpdateCheckCardComponent } from './update-check-card.component';
import { UpdateCheckStatusDto } from '../../core/api/api-types';

/**
 * Update Checker card (1.21.0). The API is mocked at the HTTP layer so the real
 * ApiService request shapes (GET status, GET ?force=true, PUT settings) are what
 * gets asserted. The checker is off by default; enabling triggers a server-side
 * check whose result flows back into this card.
 */
describe('UpdateCheckCardComponent', () => {
  const STATUS_URL = '/api/v1/operations/update-check';
  const SETTINGS_URL = '/api/v1/operations/update-check/settings';
  let httpMock: HttpTestingController;

  const status = (overrides: Partial<UpdateCheckStatusDto> = {}): UpdateCheckStatusDto => ({
    enabled: false,
    currentVersion: '1.21.0',
    latestVersion: null,
    updateAvailable: false,
    lastChecked: null,
    ...overrides,
  });

  function create() {
    TestBed.configureTestingModule({
      imports: [UpdateCheckCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(UpdateCheckCardComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    return fixture;
  }

  function createLoaded(initial = status()) {
    const fixture = create();
    // The init GET has no force param.
    httpMock.expectOne((r) => r.url === STATUS_URL && !r.params.has('force')).flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  afterEach(() => httpMock?.verify());

  it('loads status on init and shows off state by default', () => {
    const c = createLoaded().componentInstance;
    expect(c.loading()).toBe(false);
    expect(c.enabled()).toBe(false);
    expect(c.statusState()).toBe('off');
  });

  it('enabling PUTs the flag and reflects an available update from the response', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;

    c.setEnabled(true);
    expect(c.saving()).toBe(true);

    const req = httpMock.expectOne(SETTINGS_URL);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ enabled: true });
    req.flush(status({ enabled: true, latestVersion: '2.0.0', updateAvailable: true, lastChecked: '2026-01-01T00:00:00Z' }));
    fixture.detectChanges();

    expect(c.saving()).toBe(false);
    expect(c.enabled()).toBe(true);
    expect(c.statusState()).toBe('available');
    expect((fixture.nativeElement.querySelector('.status') as HTMLElement).textContent).toContain('Update available: v2.0.0');
  });

  it('"Check now" issues a forced GET and updates the status', () => {
    const fixture = createLoaded(status({ enabled: true }));
    const c = fixture.componentInstance;

    c.checkNow();
    expect(c.checking()).toBe(true);

    const req = httpMock.expectOne((r) => r.url === STATUS_URL && r.params.get('force') === 'true');
    expect(req.request.method).toBe('GET');
    req.flush(status({ enabled: true, latestVersion: '1.21.0', updateAvailable: false, lastChecked: '2026-01-02T00:00:00Z' }));
    fixture.detectChanges();

    expect(c.checking()).toBe(false);
    expect(c.statusState()).toBe('up-to-date');
  });

  it('does not check when disabled', () => {
    const c = createLoaded().componentInstance;
    c.checkNow();
    httpMock.expectNone((r) => r.params.get('force') === 'true');
  });

  it('surfaces the server message when the initial load fails', () => {
    const fixture = create();
    httpMock.expectOne((r) => r.url === STATUS_URL).flush(
      { error: 'forbidden', message: 'Admins only', detail: null, correlationId: null },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(fixture.componentInstance.loadFailed()).toBe(true);
    expect((fixture.nativeElement.querySelector('.error') as HTMLElement).textContent).toContain('Admins only');
  });
});
