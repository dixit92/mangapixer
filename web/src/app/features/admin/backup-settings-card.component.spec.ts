import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { BackupSettingsCardComponent } from './backup-settings-card.component';
import { BackupSettingsDto } from '../../core/api/api-types';

/**
 * Backup settings card (1.22.0). The API is mocked at the HTTP layer, so the
 * real ApiService request shapes (GET / PUT backups/settings) are asserted:
 * sources + configuration-managed read-only state, the password step for any
 * location change (Test and Save), validateOnly results, the retention-lowering
 * warning, the unavailable banner, and `(changed)` after a save.
 */
describe('BackupSettingsCardComponent', () => {
  const URL = '/api/v1/operations/backups/settings';
  let httpMock: HttpTestingController;

  const dto = (overrides: Partial<BackupSettingsDto> = {}): BackupSettingsDto => ({
    enabled: true,
    enabledSource: 'default',
    intervalHours: 24,
    intervalHoursSource: 'default',
    retentionCount: 7,
    retentionCountSource: 'default',
    locationKind: 'default',
    locationSource: 'default',
    customLocation: null,
    locationChangeAllowed: true,
    locationStatus: 'ok',
    platform: 'linux',
    ...overrides,
  });

  function createLoaded(initial = dto(), snapshotCount = 0) {
    TestBed.configureTestingModule({
      imports: [BackupSettingsCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(BackupSettingsCardComponent);
    fixture.componentRef.setInput('snapshotCount', snapshotCount);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne((r) => r.url === URL && r.method === 'GET').flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: HTMLElement) => el.textContent ?? '';

  afterEach(() => httpMock?.verify());

  it('loads the settings into the draft', () => {
    const c = createLoaded().componentInstance;
    expect(c.loading()).toBe(false);
    expect(c.draftEnabled()).toBe(true);
    expect(c.draftInterval()).toBe(24);
    expect(c.intervalChoice()).toBe('24');
    expect(c.draftMode()).toBe('default');
    expect(c.dirty()).toBe(false);
  });

  it('renders configuration-managed fields read-only', () => {
    const fixture = createLoaded(dto({
      retentionCountSource: 'configuration',
      locationSource: 'configuration',
      locationKind: 'custom',
      customLocation: '/backups',
      locationChangeAllowed: false,
    }));
    const root = fixture.nativeElement as HTMLElement;
    expect((root.querySelector('#bk-retention') as HTMLInputElement).disabled).toBe(true);
    expect((root.querySelector('input.location') as HTMLInputElement).disabled).toBe(true);
    expect(text(root)).toContain('Managed by server configuration');
    // No Test button when the location cannot change.
    expect(Array.from(root.querySelectorAll('button')).some((b) => text(b).trim() === 'Test')).toBe(false);
  });

  it('saves schedule changes without a password and emits changed', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    let emitted = 0;
    c.changed.subscribe(() => emitted++);

    c.draftEnabled.set(false);
    c.onIntervalChoice('12');
    c.draftRetention.set(3);
    c.save();

    const req = httpMock.expectOne((r) => r.url === URL && r.method === 'PUT');
    expect(req.request.body).toEqual({ enabled: false, intervalHours: 12, retentionCount: 3 });
    req.flush({ settings: dto({ enabled: false, intervalHours: 12, retentionCount: 3, enabledSource: 'settings' }), validateOnly: false, willCreate: false, locationChanged: false, warnings: [] });
    fixture.detectChanges();

    expect(emitted).toBe(1);
    expect(c.dirty()).toBe(false);
    expect(c.movedNotice()).toBe(false);
  });

  it('asks for the current password before saving a location change', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    c.draftMode.set('custom');
    c.draftLocation.set('/backups');
    c.save();
    fixture.detectChanges();

    // No request yet: the password step is shown first.
    httpMock.expectNone(URL);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid=reauth]')).toBeTruthy();

    c.password.set('secret');
    c.confirm();
    const req = httpMock.expectOne((r) => r.url === URL && r.method === 'PUT');
    expect(req.request.body).toEqual({
      location: { mode: 'custom', customLocation: '/backups' },
      currentPassword: 'secret',
    });
    req.flush({ settings: dto({ locationKind: 'custom', locationSource: 'settings', customLocation: '/backups' }), validateOnly: false, willCreate: true, locationChanged: true, warnings: [] });
    fixture.detectChanges();

    expect(c.pending()).toBeNull();
    expect(c.password()).toBe('');
    expect(text(fixture.nativeElement)).toContain('Existing snapshots stay in the previous location');
  });

  it('Test sends validateOnly with the password and shows warnings', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    c.draftMode.set('custom');
    c.draftLocation.set('/backups');
    c.test();
    c.password.set('secret');
    c.confirm();

    const req = httpMock.expectOne(URL);
    expect(req.request.body).toEqual({
      location: { mode: 'custom', customLocation: '/backups' },
      validateOnly: true,
      currentPassword: 'secret',
    });
    req.flush({ settings: dto(), validateOnly: true, willCreate: true, locationChanged: true, warnings: ['low_free_space'] });
    fixture.detectChanges();

    const result = (fixture.nativeElement as HTMLElement).querySelector('[data-testid=test-result]') as HTMLElement;
    expect(text(result)).toContain('will be created');
    expect(text(result)).toContain('little free space');
    // Settings are unchanged by a test.
    expect(c.settings()?.locationKind).toBe('default');
  });

  it('shows the server error for a rejected location', () => {
    const fixture = createLoaded();
    const c = fixture.componentInstance;
    c.draftMode.set('custom');
    c.draftLocation.set('/data/backups');
    c.save();
    c.password.set('secret');
    c.confirm();
    httpMock.expectOne(URL).flush(
      { error: 'location_overlaps_protected', message: 'The backup folder must be outside the data, cache, scratch, media, library and program folders.' },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(text(fixture.nativeElement)).toContain('must be outside the data');
    expect(c.password()).toBe('');
  });

  it('warns how many snapshots a lower retention will delete', () => {
    const fixture = createLoaded(dto(), 7);
    const c = fixture.componentInstance;
    c.draftRetention.set(3);
    fixture.detectChanges();
    expect(c.retentionWarning()).toBe(4);
    expect(text(fixture.nativeElement.querySelector('[data-testid=retention-warning]'))).toContain('4 oldest');
  });

  it('shows the unavailable banner', () => {
    const fixture = createLoaded(dto({ locationKind: 'custom', customLocation: '/backups', locationStatus: 'unavailable' }));
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid=location-banner]')).toBeTruthy();
  });

  it('uses Windows path idioms on a Windows server', () => {
    const fixture = createLoaded(dto({ platform: 'windows', locationKind: 'custom', customLocation: 'D:\\bk' }));
    const input = (fixture.nativeElement as HTMLElement).querySelector('input.location') as HTMLInputElement;
    expect(input.placeholder).toContain('\\\\nas\\archive');
  });

  it('keeps a non-preset interval as a custom value', () => {
    const c = createLoaded(dto({ intervalHours: 36 })).componentInstance;
    expect(c.intervalChoice()).toBe('custom');
  });
});
