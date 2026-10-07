import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Observable, of, throwError } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { LibraryDto, LibraryScanSchedule } from '../../../core/api/api-types';
import { LibraryScanScheduleComponent, SCAN_SCHEDULE_OPTIONS } from './library-scan-schedule.component';

function lib(overrides: Partial<LibraryDto> = {}): LibraryDto {
  return {
    id: 'lib1',
    name: 'Manga',
    isScanning: false,
    itemCount: 3,
    lastScanCompleted: null,
    defaultReaderMode: null,
    icon: null,
    ...overrides,
  };
}

@Component({
  standalone: true,
  imports: [LibraryScanScheduleComponent],
  template: `<app-library-scan-schedule [library]="library()" />`,
})
class HostComponent {
  readonly library = signal<LibraryDto>(lib());
}

@Component({
  standalone: true,
  imports: [LibraryScanScheduleComponent],
  template: `<app-library-scan-schedule [library]="library()" [summary]="true" />`,
})
class SummaryHostComponent {
  readonly library = signal<LibraryDto>(lib());
}

class FakeApi {
  adminDto: LibraryDto = lib({ scanSchedule: '1d', nextScheduledScanAt: '2099-01-01T00:00:00Z' });
  getCalls: string[] = [];
  putCalls: { id: string; value: LibraryScanSchedule | null }[] = [];
  timeCalls: { hour: number | null; weekday: number | null }[] = [];
  failPut = false;

  getLibrary(id: string): Observable<LibraryDto> {
    this.getCalls.push(id);
    return of(this.adminDto);
  }

  setLibraryScanSchedule(id: string, value: LibraryScanSchedule | null, hour: number | null = null, weekday: number | null = null): Observable<LibraryDto> {
    this.putCalls.push({ id, value });
    this.timeCalls.push({ hour, weekday });
    if (this.failPut) return throwError(() => new Error('nope'));
    return of({
      ...this.adminDto, scanSchedule: value, scanHour: hour, scanWeekday: weekday,
      nextScheduledScanAt: value === 'off' ? null : this.adminDto.nextScheduledScanAt,
    });
  }
}

describe('LibraryScanScheduleComponent', () => {
  let api: FakeApi;

  function create() {
    api = new FakeApi();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: ApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  function component(fixture: ReturnType<typeof create>): LibraryScanScheduleComponent {
    return fixture.debugElement.children[0].componentInstance as LibraryScanScheduleComponent;
  }

  it('lists the five presets in server order', () => {
    expect(SCAN_SCHEDULE_OPTIONS.map(o => o.value)).toEqual(['off', '1h', '6h', '1d', '7d']);
    expect(SCAN_SCHEDULE_OPTIONS.map(o => o.label)).toEqual(['Off', 'Hourly', 'Every 6 hours', 'Daily', 'Weekly']);
  });

  it('loads the admin DTO and shows the schedule, last and next scan', () => {
    const fixture = create();
    expect(api.getCalls).toEqual(['lib1']);
    const cmp = component(fixture);
    expect(cmp.schedule()).toBe('1d');
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Last scan:');
    expect(text).toContain('never');
    expect(text).toContain('Next scan (approx.):');
    expect(text).not.toContain('shortly');
  });

  it('treats a missing scanSchedule as the daily default', () => {
    const f = create();
    api.adminDto = lib();
    f.componentInstance.library.set(lib({ lastScanCompleted: '2026-09-24T10:00:00Z' }));
    f.detectChanges();
    expect(component(f).schedule()).toBe('1d');
  });

  it('shows "shortly" when the next scan time has passed', () => {
    const f = create();
    api.adminDto = lib({ scanSchedule: '1h', nextScheduledScanAt: '2000-01-01T00:00:00Z' });
    f.componentInstance.library.set(lib({ isScanning: true }));
    f.detectChanges();
    expect(component(f).isDue()).toBe(true);
    expect(f.nativeElement.textContent).toContain('shortly');
  });

  it('re-reads when the row scan state changes, not on an identical row', () => {
    const f = create();
    f.componentInstance.library.set(lib());
    f.detectChanges();
    expect(api.getCalls.length).toBe(1);
    f.componentInstance.library.set(lib({ lastScanCompleted: '2026-09-24T10:00:00Z' }));
    f.detectChanges();
    expect(api.getCalls.length).toBe(2);
  });

  it('saves a new preset and shows off as the next scan', () => {
    const f = create();
    const cmp = component(f);
    cmp.save('off');
    f.detectChanges();
    expect(api.putCalls).toEqual([{ id: 'lib1', value: 'off' }]);
    expect(cmp.schedule()).toBe('off');
    expect(cmp.nextScan()).toBeNull();
    expect(f.nativeElement.querySelector('.next').textContent).toContain('off');
  });

  it('reverts and reports an error when saving fails', () => {
    const f = create();
    api.failPut = true;
    const cmp = component(f);
    cmp.save('7d');
    f.detectChanges();
    expect(cmp.schedule()).toBe('1d');
    expect(cmp.saving()).toBe(false);
    expect(f.nativeElement.querySelector('[role="alert"]').textContent).toContain('Could not save');
  });

  it('offers a time of day for Daily and Weekly only, and a weekday for Weekly with a time (1.32.0)', () => {
    const f = create();
    const cmp = component(f);
    expect(f.nativeElement.querySelector('[data-testid="scan-hour"]')).not.toBeNull();
    expect(f.nativeElement.querySelector('[data-testid="scan-weekday"]')).toBeNull();

    cmp.saveHour(3);
    f.detectChanges();
    expect(api.timeCalls.at(-1)).toEqual({ hour: 3, weekday: null });
    expect(cmp.hour()).toBe(3);

    // Weekly keeps the hour and offers the weekday.
    cmp.save('7d');
    f.detectChanges();
    expect(api.timeCalls.at(-1)).toEqual({ hour: 3, weekday: null });
    expect(f.nativeElement.querySelector('[data-testid="scan-weekday"]')).not.toBeNull();
    cmp.saveWeekday(1);
    expect(api.timeCalls.at(-1)).toEqual({ hour: 3, weekday: 1 });

    // Hourly drops the time; "Any time" clears it.
    cmp.save('1h');
    f.detectChanges();
    expect(api.timeCalls.at(-1)).toEqual({ hour: null, weekday: null });
    expect(f.nativeElement.querySelector('[data-testid="scan-hour"]')).toBeNull();
    cmp.save('1d');
    cmp.saveHour(5);
    cmp.saveHour(-1);
    expect(api.timeCalls.at(-1)).toEqual({ hour: null, weekday: null });
  });

  it('labels hours as server time', () => {
    const f = create();
    // 1.35.0: a compact select in the Scheduled jobs table (its column says "At"); the accessible name keeps the zone.
    expect(f.nativeElement.querySelector('[data-testid="scan-hour"]').getAttribute('aria-label')).toBe('Scan time of day (server time)');
  });

  it('shows last and next scan in a given server zone (Scheduled jobs section)', () => {
    const f = create();
    const cmp = component(f);
    expect(cmp.inZone('2026-10-03T07:00:00Z')).toBe('Sat 3 Oct, 07:00');
  });

  describe('summary (Libraries card, 1.33.0)', () => {
    function createSummary(dto: Partial<LibraryDto>) {
      api = new FakeApi();
      api.adminDto = lib({ scanSchedule: '1d', nextScheduledScanAt: '2099-01-01T00:00:00Z', ...dto });
      TestBed.configureTestingModule({
        imports: [SummaryHostComponent],
        providers: [provideNoopAnimations(), { provide: ApiService, useValue: api }],
      });
      const fixture = TestBed.createComponent(SummaryHostComponent);
      fixture.detectChanges();
      return fixture;
    }

    function line(f: { nativeElement: HTMLElement }): string {
      return (f.nativeElement.querySelector('[data-testid="scan-summary"]')?.textContent ?? '').replace(/\s+/g, ' ').trim();
    }

    it('shows the schedule as text with a link and no controls', () => {
      const f = createSummary({ scanSchedule: '1d', scanHour: 3 });
      expect(line(f)).toContain('Auto-scan: Daily at 03:00 (server time)');
      expect(f.nativeElement.querySelector('select')).toBeNull();
      expect(f.nativeElement.querySelector('[data-testid="scan-schedule-link"]')?.textContent).toContain('Change in Scheduled jobs');
      expect(f.nativeElement.textContent).toContain('Last scan:');
    });

    it('names the weekday of a timed weekly scan, and nothing more for "any time" or off', () => {
      expect(line(createSummary({ scanSchedule: '7d', scanHour: 5, scanWeekday: 1 }))).toContain('Weekly on Monday at 05:00 (server time)');
      TestBed.resetTestingModule();
      expect(line(createSummary({ scanSchedule: '7d', scanHour: null, scanWeekday: null }))).toMatch(/Auto-scan: Weekly Change/);
      TestBed.resetTestingModule();
      expect(line(createSummary({ scanSchedule: '1d', scanHour: null }))).toMatch(/Auto-scan: Daily Change/);
      TestBed.resetTestingModule();
      expect(line(createSummary({ scanSchedule: 'off' }))).toMatch(/Auto-scan: Off Change/);
    });

    it('moves to the library\'s Scheduled jobs row and focuses its schedule select', () => {
      const f = createSummary({ scanSchedule: '1d' });
      const row = document.createElement('li');
      row.setAttribute('data-testid', 'job-library-scan-lib1');
      const select = document.createElement('select');
      row.appendChild(select);
      document.body.appendChild(row);
      const scrolled = vi.fn();
      row.scrollIntoView = scrolled;
      try {
        (f.nativeElement.querySelector('[data-testid="scan-schedule-link"]') as HTMLButtonElement).click();
        expect(scrolled).toHaveBeenCalled();
        expect(document.activeElement).toBe(select);
      } finally {
        row.remove();
      }
    });
  });
});
