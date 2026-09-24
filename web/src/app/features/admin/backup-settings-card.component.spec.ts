import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { BackupSettingsCardComponent } from './backup-settings-card.component';
import { BackupSettingsDto, BackupSnapshotMoveStatusDto } from '../../core/api/api-types';

/**
 * Backup settings card (1.22.0). The API is mocked at the HTTP layer, so the
 * real ApiService request shapes (GET / PUT backups/settings) are asserted:
 * sources + configuration-managed read-only state, the password step for any
 * location change (Test and Save), validateOnly results, the retention-lowering
 * warning, the unavailable banner, and `(changed)` after a save. 1.23.0: the
 * safety-snapshot note, the "Move existing snapshots" offer (checked by
 * default) and the polled progress / result of the background move.
 */
describe('BackupSettingsCardComponent', () => {
  const URL = '/api/v1/operations/backups/settings';
  const MOVE_URL = '/api/v1/operations/backups/move';
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

  const move = (overrides: Partial<BackupSnapshotMoveStatusDto> = {}): BackupSnapshotMoveStatusDto => ({
    state: 'idle',
    fromKind: null,
    toKind: null,
    totalFiles: 0,
    filesDone: 0,
    totalBytes: 0,
    bytesDone: 0,
    movedCount: 0,
    prunedCount: 0,
    startedUtc: null,
    finishedUtc: null,
    issues: [],
    ...overrides,
  });

  function createLoaded(initial = dto(), snapshotCount = 0, moveAtLoad = move()) {
    TestBed.configureTestingModule({
      imports: [BackupSettingsCardComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(BackupSettingsCardComponent);
    fixture.componentRef.setInput('snapshotCount', snapshotCount);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock.expectOne((r) => r.url === MOVE_URL && r.method === 'GET').flush(moveAtLoad);
    httpMock.expectOne((r) => r.url === URL && r.method === 'GET').flush(initial);
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: HTMLElement) => el.textContent ?? '';

  afterEach(() => {
    httpMock?.verify();
    vi.useRealTimers();
  });

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

  it('explains that safety snapshots stay in the data folder', () => {
    const note = (createLoaded().nativeElement as HTMLElement).querySelector('[data-testid=safety-note]');
    expect(text(note as HTMLElement)).toContain('always stay in the data folder, newest 3 of each');
    expect(text(note as HTMLElement)).toContain('unmounted share');
  });

  it('offers no move while the location is unchanged or holds no snapshots', () => {
    const fixture = createLoaded(dto({ rotatingSnapshotCount: 0, rotatingSnapshotBytes: 0 }));
    const c = fixture.componentInstance;
    c.draftMode.set('custom');
    c.draftLocation.set('/backups');
    fixture.detectChanges();
    expect(c.moveOffer()).toBe(false);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid=move-snapshots]')).toBeNull();
  });

  it('moves existing snapshots by default and shows the progress until done', () => {
    vi.useFakeTimers();
    const fixture = createLoaded(dto({ rotatingSnapshotCount: 7, rotatingSnapshotBytes: 1_887_436_800 }));
    const c = fixture.componentInstance;
    let emitted = 0;
    c.changed.subscribe(() => emitted++);
    c.draftMode.set('custom');
    c.draftLocation.set('/backups');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const offer = root.querySelector('[data-testid=move-snapshots]') as HTMLElement;
    expect(text(offer)).toContain('Move existing snapshots (7 files, 1.8 GB)');
    expect(c.moveSnapshots()).toBe(true);

    c.save();
    c.password.set('secret');
    c.confirm();
    const req = httpMock.expectOne((r) => r.url === URL && r.method === 'PUT');
    expect(req.request.body).toEqual({
      location: { mode: 'custom', customLocation: '/backups' },
      moveExistingSnapshots: true,
      currentPassword: 'secret',
    });
    req.flush({ settings: dto({ locationKind: 'custom', locationSource: 'settings', customLocation: '/backups' }), validateOnly: false, willCreate: false, locationChanged: true, snapshotMoveStarted: true, warnings: [] });
    httpMock.expectOne(MOVE_URL).flush(move({ state: 'running', totalFiles: 7, filesDone: 3, totalBytes: 1_887_436_800, bytesDone: 786_432_000 }));
    fixture.detectChanges();

    expect(c.movedNotice()).toBe(false);
    expect(text(root)).toContain('Moving existing snapshots: 3 of 7 files (750.0 MB of 1.8 GB)');
    expect(c.moveRunning()).toBe(true); // location controls are locked meanwhile
    expect(emitted).toBe(1);

    vi.advanceTimersByTime(1000);
    httpMock.expectOne(MOVE_URL).flush(move({ state: 'completed', totalFiles: 7, filesDone: 7, movedCount: 7, prunedCount: 0, totalBytes: 1_887_436_800, bytesDone: 1_887_436_800 }));
    // Finished: the card reloads its settings and tells the host to refresh.
    httpMock.expectOne((r) => r.url === URL && r.method === 'GET').flush(dto({ locationKind: 'custom', customLocation: '/backups', rotatingSnapshotCount: 7 }));
    fixture.detectChanges();

    expect(text(root)).toContain('Moved 7 of 7 snapshot(s) to the new location.');
    expect(c.moveRunning()).toBe(false);
    expect(emitted).toBe(2);
    vi.advanceTimersByTime(5000);
    httpMock.expectNone(MOVE_URL);
  });

  it('keeps the old behaviour when the move is unchecked', () => {
    const fixture = createLoaded(dto({ rotatingSnapshotCount: 2, rotatingSnapshotBytes: 2048 }));
    const c = fixture.componentInstance;
    c.draftMode.set('custom');
    c.draftLocation.set('/backups');
    c.moveSnapshots.set(false);
    fixture.detectChanges();
    expect(text(fixture.nativeElement)).toContain('no longer listed or pruned');

    c.save();
    c.password.set('secret');
    c.confirm();
    const req = httpMock.expectOne((r) => r.url === URL && r.method === 'PUT');
    expect(req.request.body.moveExistingSnapshots).toBe(false);
    req.flush({ settings: dto({ locationKind: 'custom', customLocation: '/backups' }), validateOnly: false, willCreate: false, locationChanged: true, snapshotMoveStarted: false, warnings: [] });
    fixture.detectChanges();

    expect(text(fixture.nativeElement)).toContain('Existing snapshots stay in the previous location');
  });

  it('lists the snapshots that stayed behind after a move', () => {
    const fixture = createLoaded(dto(), 0, move({
      state: 'completed', totalFiles: 3, filesDone: 3, movedCount: 1,
      issues: [
        { fileName: 'rotating-20260101-000000.db', code: 'name_conflict', location: 'previous' },
        { fileName: 'rotating-20260102-000000.db', code: 'source_delete_failed', location: 'both' },
      ],
    }));
    const root = fixture.nativeElement as HTMLElement;
    const status = text(root.querySelector('[data-testid=snapshot-move]') as HTMLElement);
    expect(status).toContain('Moved 1 of 3 snapshot(s)');
    expect(status).toContain('rotating-20260101-000000.db');
    expect(status).toContain('still in the previous location');
    expect(status).toContain('in both locations');
  });

  it('resumes showing a move that is running when the card loads', () => {
    vi.useFakeTimers();
    const fixture = createLoaded(dto(), 0, move({ state: 'running', totalFiles: 2, filesDone: 1, totalBytes: 2048, bytesDone: 1024 }));
    const c = fixture.componentInstance;
    expect(c.moveRunning()).toBe(true);
    expect(text(fixture.nativeElement)).toContain('1 of 2 files');

    vi.advanceTimersByTime(1000);
    httpMock.expectOne(MOVE_URL).flush(move({ state: 'completed', totalFiles: 2, filesDone: 2, movedCount: 2 }));
    httpMock.expectOne((r) => r.url === URL && r.method === 'GET').flush(dto());
    fixture.detectChanges();
    expect(c.moveRunning()).toBe(false);
  });
});
