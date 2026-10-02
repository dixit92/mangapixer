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
      ongoingDays: 30, finishedDays: 90, followPace: true, allowedOngoingDays: [7, 14, 30], allowedFinishedDays: [30, 90, 180],
      usedToday: 12, maxPerDay: 200, overdue: 3, byDays: [{ days: 14, count: 19 }, { days: 30, count: 216 }, { days: 90, count: 368 }],
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
    expect(text(el, 'job-library-scan-lib1')).toContain('Library scan: Manga');
    const refresh = text(el, 'job-metadata-refresh');
    expect(refresh).toContain('Last run: Fri 2 Oct, 03:00 - 12 refreshed');
    expect(refresh).toContain('Next run: Sat 3 Oct, 03:00');
    expect(text(el, 'job-refresh-counts')).toContain('Today: 12 of 200 checked.');
    expect(text(el, 'job-refresh-counts')).toContain('3 series are past their check date.');
    expect(refresh).toContain('Checked every 2 weeks: 19, every month: 216, every 3 months: 368.');
    expect(text(el, 'job-trash')).toContain('Off - turn automatic cleaning on in the Trash card.');
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

  it('saves the cadence choices and the pace switch', () => {
    const f = createLoaded();
    const el = f.nativeElement as HTMLElement;
    const ongoing = el.querySelector('[data-testid="job-cadence-ongoing"]') as HTMLSelectElement;
    ongoing.value = '7';
    ongoing.dispatchEvent(new Event('change'));
    const put = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/metadata-refresh/cadence`);
    expect(put.request.body).toEqual({ ongoingDays: 7 });
    put.flush(dto());
    f.detectChanges();

    f.componentInstance.setCadence({ followPace: false });
    const pace = httpMock!.expectOne((r) => r.method === 'PUT' && r.url === `${URL}/metadata-refresh/cadence`);
    expect(pace.request.body).toEqual({ followPace: false });
    pace.flush(dto());
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
});
