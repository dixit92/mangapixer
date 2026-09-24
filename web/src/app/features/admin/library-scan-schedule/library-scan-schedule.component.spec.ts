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

class FakeApi {
  adminDto: LibraryDto = lib({ scanSchedule: '1d', nextScheduledScanAt: '2099-01-01T00:00:00Z' });
  getCalls: string[] = [];
  putCalls: { id: string; value: LibraryScanSchedule | null }[] = [];
  failPut = false;

  getLibrary(id: string): Observable<LibraryDto> {
    this.getCalls.push(id);
    return of(this.adminDto);
  }

  setLibraryScanSchedule(id: string, value: LibraryScanSchedule | null): Observable<LibraryDto> {
    this.putCalls.push({ id, value });
    if (this.failPut) return throwError(() => new Error('nope'));
    return of({ ...this.adminDto, scanSchedule: value, nextScheduledScanAt: value === 'off' ? null : this.adminDto.nextScheduledScanAt });
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
});
