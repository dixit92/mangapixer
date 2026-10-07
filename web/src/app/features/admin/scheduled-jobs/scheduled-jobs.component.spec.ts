import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ScheduledJobsComponent, cadenceLabel, offsetLabel, serverTime } from './scheduled-jobs.component';
import { ScheduledJobDto, ScheduledJobsDto } from '../../../core/api/api-types';

/**
 * Scheduled jobs (1.32.0). The API is mocked at the HTTP layer so the real ApiService request shapes are asserted: every job is
 * listed with its last and next run in SERVER time (whatever the browser's zone), the hour selects PUT /admin/jobs/{key}, the
 * cadence selects and the pace switch PUT the cadence, read-only jobs show their rhythm, and a failed save says so and reloads.
 */
describe('ScheduledJobsComponent', () => {
  const URL = '/api/v1/admin/jobs';
  let httpMock: HttpTestingController | undefined;

  const job = (key: string, overrides: Partial<ScheduledJobDto> = {}): ScheduledJobDto => ({
    key, kind: 'daily', enabled: true, configurable: true, ...overrides,
  });

  const dto = (overrides: Partial<ScheduledJobsDto> = {}): ScheduledJobsDto => ({
    serverTime: '2026-10-02T18:05:00Z',
    timeZone: 'America/New_York',
    utcOffsetMinutes: -240,
    jobs: [
      job('library-scan', { libraryId: 'lib1', libraryName: 'Manga', kind: 'interval', scanSchedule: '1d', intervalHours: 24 }),
      job('metadata-refresh', { hour: 3, defaultHour: 3, lastStartedAt: '2026-10-02T07:00:00Z', lastOutcome: 'ok', lastDetail: '12 refreshed',
        nextRunAt: '2026-10-03T07:00:00Z' }),
      job('backup', { kind: 'interval', intervalHours: 6, hour: null }),
      job('trash', { hour: 4, enabled: false }),
      job('cache-eviction', { hour: 5, nextRunAt: '2026-10-03T09:00:00Z' }),
      job('auto-match', { kind: 'continuous', configurable: false, waitingCode: 'budget_exhausted' }),
      job('session-cleanup', { kind: 'interval', configurable: false, intervalHours: 1 }),
      job('update-check', { kind: 'onDemand', configurable: false, enabled: false }),
    ],
    refresh: {
      ongoingDays: 30, finishedDays: 90, followPace: true, paceSource: 'faster', allowedOngoingDays: [7, 14, 30], allowedFinishedDays: [30, 90, 180],
      usedToday: 12, overdue: 3, byDays: [{ days: 14, count: 19 }, { days: 30, count: 216 }, { days: 90, count: 368 }],
    },
    ...overrides,
  });

  function createLoaded(initial = dto()) {
    TestBed.configureTestingModule({
      imports: [ScheduledJobsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(ScheduledJobsComponent);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === URL).flush(initial);
    fixture.detectChanges();
    // The library row's scan schedule control reads its admin DTO.
    httpMock!.match((r) => r.url === '/api/v1/admin/libraries/lib1').forEach((r) => r.flush({
      id: 'lib1', name: 'Manga', isScanning: false, itemCount: 1, lastScanCompleted: null, defaultReaderMode: null, icon: null, scanSchedule: '1d',
    }));
    fixture.detectChanges();
    return fixture;
  }

  const text = (el: HTMLElement, testId: string) => (el.querySelector(`[data-testid="${testId}"]`) as HTMLElement).textContent ?? '';

  beforeEach(() => { httpMock = undefined; });
  afterEach(() => httpMock?.verify());

  it('formats times in the server zone, offsets and cadences', () => {
    expect(serverTime('2026-10-03T07:00:00Z', 'America/New_York')).toBe('Sat 3 Oct, 03:00');
    expect(serverTime('2026-10-03T07:00:00Z', 'UTC', false)).toBe('07:00');
    expect(serverTime('2026-10-03T07:00:00Z', 'Not/AZone', false)).toBe('07:00');
    expect(offsetLabel(-240)).toBe('UTC-04:00');
    expect(offsetLabel(330)).toBe('UTC+05:30');
    expect([7, 14, 30, 90, 180].map(cadenceLabel)).toEqual(['Every week', 'Every 2 weeks', 'Every month', 'Every 3 months', 'Every 6 months']);
  });

  it('shows the server clock and every job with its last and next run', () => {
    const f = createLoaded();
    const el = f.nativeElement as HTMLElement;
    expect(text(el, 'jobs-clock')).toContain('Times are server time: America/New_York (UTC-04:00), now 14:05.');
    expect(text(el, 'jobs-group-scans')).toContain('Library scans');
    expect(text(el, 'job-library-scan-lib1')).toContain('Manga');
    const refresh = text(el, 'job-metadata-refresh');
    expect(refresh).toContain('Last run: Fri 2 Oct, 03:00 - 12 refreshed');
    expect(refresh).toContain('Next run: Sat 3 Oct, 03:00');
    expect(text(el, 'job-refresh-counts')).toContain('Due now: 3 · checked today: 12');
    expect(refresh).toContain('Checked every 2 weeks: 19, every month: 216, every 3 months: 368.');
    expect(text(el, 'job-trash')).toContain('Off: nothing is removed unless you choose "Empty trash now"');
    expect(el.querySelector('#job-trash')).not.toBeNull();
    expect(text(el, 'job-auto-match')).toContain('Continuous - new folders within about a minute');
    expect(text(el, 'job-auto-match')).toContain('Waiting: daily budget used until midnight (server time)');
    expect(text(el, 'job-session-cleanup')).toContain('Hourly');
    expect(text(el, 'job-update-check')).toContain('Next run: Off');
    // A backup every 6 hours takes no hour.
    expect((el.querySelector('[data-testid="job-hour-backup"]') as HTMLSelectElement).disabled).toBe(true);
    expect(text(el, 'job-backup')).toContain('Only for daily or longer intervals');
  });

  it('saves an hour, and "Any time" as null for backups', () => {
    const f = createLoaded(dto({ jobs: [job('cache-eviction', { hour: 5 }), job('backup', { kind: 'interval', intervalHours: 24, hour: null })] }));
    const el = f.nativeElement as HTMLElement;
    const select = el.querySelector('[data-testid="job-hour-cache-eviction"]') as HTMLSelectElement;
    select.value = '2';
    select.dispatchEvent(new Event('change'));
    const put = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/cache-eviction`);
    expect(put.request.body).toEqual({ hour: 2 });
    put.flush(dto({ jobs: [job('cache-eviction', { hour: 2 }), job('backup', { kind: 'interval', intervalHours: 24, hour: null })] }));
    f.detectChanges();
    expect((el.querySelector('[data-testid="job-hour-cache-eviction"]') as HTMLSelectElement).value).toBe('2');

    const backup = el.querySelector('[data-testid="job-hour-backup"]') as HTMLSelectElement;
    expect(backup.disabled).toBe(false);
    backup.value = '-1';
    backup.dispatchEvent(new Event('change'));
    const clear = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/backup`);
    expect(clear.request.body).toEqual({ hour: null });
    clear.flush(dto());
  });

  it('"Check ongoing series" holds the pace as one of its choices, with "Pace from" under it (1.35.0)', () => {
    const f = createLoaded();
    const el = f.nativeElement as HTMLElement;
    const ongoing = el.querySelector('[data-testid="job-cadence-ongoing"]') as HTMLSelectElement;
    expect(ongoing.value).toBe('pace');
    expect([...ongoing.options].map(o => o.textContent?.trim())).toEqual(['Follow their pace', 'Every week', 'Every 2 weeks', 'Every month']);
    expect((el.querySelector('[data-testid="job-cadence-pace-source"]') as HTMLSelectElement).value).toBe('faster');
    expect(text(el, 'job-cadence-hint')).toContain('new chapters or new volumes, whichever comes more often');

    const source = el.querySelector('[data-testid="job-cadence-pace-source"]') as HTMLSelectElement;
    source.value = 'chapters';
    source.dispatchEvent(new Event('change'));
    const put = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/metadata-refresh/cadence`);
    expect(put.request.body).toEqual({ paceSource: 'chapters' });
    put.flush(dto({ refresh: { ...dto().refresh, paceSource: 'chapters' } }));
    f.detectChanges();
    expect(text(el, 'job-cadence-hint')).toContain('which follows scanlation releases');

    ongoing.value = '7';
    ongoing.dispatchEvent(new Event('change'));
    const fixed = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/metadata-refresh/cadence`);
    expect(fixed.request.body).toEqual({ followPace: false, ongoingDays: 7 });
    fixed.flush(dto({ refresh: { ...dto().refresh, followPace: false, ongoingDays: 7 } }));
    f.detectChanges();
    expect(el.querySelector('[data-testid="job-cadence-pace-source"]')).toBeNull();
    expect((el.querySelector('[data-testid="job-cadence-ongoing"]') as HTMLSelectElement).value).toBe('7');
    expect(text(el, 'job-cadence-hint')).toContain('Every ongoing series is checked every week.');

    const back = el.querySelector('[data-testid="job-cadence-ongoing"]') as HTMLSelectElement;
    back.value = 'pace';
    back.dispatchEvent(new Event('change'));
    const pace = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/metadata-refresh/cadence`);
    expect(pace.request.body).toEqual({ followPace: true });
    pace.flush(dto());
  });

  it('groups the jobs: library scans as one table, then Web information, Library upkeep, Maintenance (1.35.0)', () => {
    const f = createLoaded(dto({ jobs: [...dto().jobs, job('thumbnails', { kind: 'startup', configurable: false }), job('some-new-job', { configurable: false })] }));
    const el = f.nativeElement as HTMLElement;
    const sections = [...el.querySelectorAll('section.group')].map(s => s.getAttribute('data-testid'));
    expect(sections).toEqual(['jobs-group-scans', 'jobs-group-web', 'jobs-group-upkeep', 'jobs-group-maintenance']);
    const keys = (group: string) => [...el.querySelectorAll(`[data-testid="jobs-group-${group}"] li`)].map(li => li.getAttribute('data-testid'));
    expect(keys('web')).toEqual(['job-metadata-refresh', 'job-auto-match', 'job-update-check']);
    expect(keys('upkeep')).toEqual(['job-thumbnails']);
    expect(keys('maintenance')).toEqual(['job-backup', 'job-trash', 'job-cache-eviction', 'job-session-cleanup', 'job-some-new-job']);
    // One control style: the scan row's schedule is a native select like every other setting on the page.
    expect(el.querySelector('[data-testid="job-library-scan-lib1"] select[aria-label="Automatic scan schedule"]')).not.toBeNull();
    expect(el.querySelector('mat-select')).toBeNull();
  });

  it('reports a failed save and reloads', () => {
    const f = createLoaded();
    f.componentInstance.setHour('metadata-refresh', 1);
    httpMock!.expectOne((r) => r.method === 'PUT').flush({ error: 'invalid_hour' }, { status: 400, statusText: 'Bad Request' });
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === URL).flush(dto());
    f.detectChanges();
    httpMock!.match((r) => r.url === '/api/v1/admin/libraries/lib1').forEach((r) => r.flush({ id: 'lib1', name: 'Manga', isScanning: false,
      itemCount: 1, lastScanCompleted: null, defaultReaderMode: null, icon: null, scanSchedule: '1d' }));
    expect((f.nativeElement as HTMLElement).querySelector('[role="alert"]')?.textContent).toContain('Could not save');
  });

  const trashOverview = () => ({
    settings: { automaticCleaning: false, retentionDays: 30, allowedRetentionDays: [1, 7, 30], automaticHour: 4 },
    windowStart: '2026-09-01T12:00:00Z',
    libraries: [],
    total: { nodes: 3, archives: 3, folders: 0, userStateRows: 4, files: 3, bytes: 6144 },
    bundles: { files: 2, bytes: 4096 },
    lastEmpty: null,
    lastBundleClean: null,
  });

  const withTrash = (overrides: Partial<ScheduledJobDto>) => dto({ jobs: [job('trash', { hour: 4, ...overrides })] });

  it('the trash row holds the automatic cleaning switch and hour (1.32.0): turning it on asks first, cancel sends nothing', () => {
    const f = createLoaded(withTrash({ enabled: false }));
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="job-trash-auto"]')).not.toBeNull();
    expect((el.querySelector('[data-testid="job-hour-trash"]') as HTMLSelectElement).options.length).toBe(24);
    const toggle = { checked: true };
    f.componentInstance.onTrashToggle(true, toggle);
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === '/api/v1/admin/trash').flush(trashOverview());
    f.detectChanges();
    httpMock!.expectNone('/api/v1/admin/trash/settings');
    const confirm = el.querySelector('[data-testid="job-trash-confirm"]') as HTMLElement;
    expect(confirm.textContent).toContain('Turn automatic cleaning on?');
    expect(confirm.textContent).toContain('Every day at 04:00 (server time)');
    expect(confirm.textContent).toContain('The first run removes what is ready now: 3 items');
    f.componentInstance.cancelTrashOn();
    f.detectChanges();
    expect(toggle.checked).toBe(false);
    expect(el.querySelector('[data-testid="job-trash-confirm"]')).toBeNull();
    httpMock!.expectNone('/api/v1/admin/trash/settings');
  });

  it('confirming turns it on through the trash settings endpoint, reloads the jobs and tells the Trash card', () => {
    const f = createLoaded(withTrash({ enabled: false }));
    const changed = vi.fn();
    f.componentInstance.trashChanged.subscribe(changed);
    f.componentInstance.onTrashToggle(true, { checked: true });
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === '/api/v1/admin/trash').flush(trashOverview());
    f.componentInstance.confirmTrashOn();
    const put = httpMock!.expectOne('/api/v1/admin/trash/settings');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ automaticCleaning: true });
    put.flush({ ...trashOverview().settings, automaticCleaning: true });
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === URL).flush(withTrash({ enabled: true, nextRunAt: '2026-10-03T08:00:00Z' }));
    f.detectChanges();
    expect(changed).toHaveBeenCalledTimes(1);
    expect(f.componentInstance.trashAsk()).toBeNull();
    expect(text(f.nativeElement, 'job-trash')).toContain('Next run: Sat 3 Oct, 04:00');
  });

  it('turning it off saves at once, and a trash hour change tells the Trash card too', () => {
    const f = createLoaded(withTrash({ enabled: true }));
    const changed = vi.fn();
    f.componentInstance.trashChanged.subscribe(changed);
    f.componentInstance.onTrashToggle(false, { checked: false });
    const put = httpMock!.expectOne('/api/v1/admin/trash/settings');
    expect(put.request.body).toEqual({ automaticCleaning: false });
    put.flush({ ...trashOverview().settings, automaticCleaning: false });
    httpMock!.expectOne((r) => r.method === 'GET' && r.url === URL).flush(withTrash({ enabled: false }));
    expect(changed).toHaveBeenCalledTimes(1);

    f.componentInstance.setHour('trash', 22);
    const hour = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/trash`);
    expect(hour.request.body).toEqual({ hour: 22 });
    hour.flush(withTrash({ hour: 22 }));
    expect(changed).toHaveBeenCalledTimes(2);
  });

  it('a refused switch keeps the previous setting and says so', () => {
    const f = createLoaded(withTrash({ enabled: true }));
    const changed = vi.fn();
    f.componentInstance.trashChanged.subscribe(changed);
    f.componentInstance.onTrashToggle(false, { checked: false });
    httpMock!.expectOne('/api/v1/admin/trash/settings').flush({ message: 'No.' }, { status: 400, statusText: 'Bad Request' });
    f.detectChanges();
    expect(changed).not.toHaveBeenCalled();
    expect(f.componentInstance.busy()).toBe(false);
    expect((f.nativeElement as HTMLElement).querySelector('[role="alert"]')).not.toBeNull();
  });
});
