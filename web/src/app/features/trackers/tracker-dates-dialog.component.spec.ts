import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { vi } from 'vitest';

import { TrackerSeriesRowDto } from '../../core/api/api-types';
import { TrackerDatesDialogComponent, todayIso } from './tracker-dates-dialog.component';
import { seriesRow } from './trackers.testing';

/** "Set dates…" (1.43.0, owner A1): the user's start / finish dates for MyAnimeList, Automatic clears one, checked like the server. */
describe('TrackerDatesDialogComponent', () => {
  function create(row: TrackerSeriesRowDto) {
    const close = vi.fn();
    TestBed.configureTestingModule({
      imports: [TrackerDatesDialogComponent],
      providers: [
        provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { row } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(TrackerDatesDialogComponent);
    fixture.detectChanges();
    return { fixture, close, c: fixture.componentInstance, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;

  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('starts on Automatic for dates the user did not set, and sends both dates (null = automatic)', () => {
    const { fixture, c, http, close, el } = create(seriesRow());
    expect(c.started()).toBeNull();
    expect(el.textContent).toContain('Automatic: Oct 1, 2026');
    c.finished.set('2026-10-10');
    fixture.detectChanges();
    q(el, 'tracker-dates-save').click();
    const req = http.expectOne('/api/v1/trackers/mal/entries/s1/dates');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ startedOn: null, finishedOn: '2026-10-10' });
    const saved = seriesRow({ dates: { ...seriesRow().dates, finishedOn: '2026-10-10', finishedOnOverridden: true, pending: true } });
    req.flush(saved);
    expect(close).toHaveBeenCalledWith(saved);
  });

  it('keeps the user\'s own date and clears it with Automatic', () => {
    const { fixture, c, el } = create(seriesRow({ dates: { ...seriesRow().dates, startedOn: '2026-09-01', startedOnOverridden: true } }));
    expect(c.started()).toBe('2026-09-01');
    q(el, 'tracker-dates-start-auto').click();
    fixture.detectChanges();
    expect(c.started()).toBeNull();
  });

  it('refuses a finish before the start, and a date before 1900, without a request', () => {
    const { fixture, c, el, http } = create(seriesRow());
    c.started.set('2026-10-05');
    c.finished.set('2026-10-01');
    fixture.detectChanges();
    expect(q(el, 'tracker-dates-invalid').textContent).toContain('before the start');
    expect((q(el, 'tracker-dates-save') as HTMLButtonElement).disabled).toBe(true);
    c.finished.set(null);
    c.started.set('1899-12-31');
    fixture.detectChanges();
    expect(q(el, 'tracker-dates-invalid').textContent).toContain('from 1900');
    http.expectNone('/api/v1/trackers/mal/entries/s1/dates');
  });

  // ---- 1.43.2 (owner, 2026-10-16): "Set today" beside each date ----

  /** A fixed clock: 2026-10-16 03:30 UTC, in a zone 7 hours behind UTC (an evening of Oct 15 in the Americas). */
  function eveningInTheAmericas(): void {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-16T03:30:00Z'));
    vi.spyOn(Date.prototype, 'getTimezoneOffset').mockReturnValue(420);
  }

  it('"Set today" fills its own field only, with today in the browser\'s time zone - not the UTC date', () => {
    eveningInTheAmericas();
    const { fixture, c, el, http } = create(seriesRow());
    expect(q(el, 'tracker-dates-start-today').textContent).toContain('Set today');
    q(el, 'tracker-dates-start-today').click();
    fixture.detectChanges();
    expect(c.started()).toBe('2026-10-15');
    expect(c.finished()).toBeNull();
    expect(el.textContent).toContain('Your date');

    c.started.set(null);
    q(el, 'tracker-dates-finish-today').click();
    fixture.detectChanges();
    expect(c.finished()).toBe('2026-10-15');
    expect(c.started()).toBeNull();
    // Nothing is sent until Save.
    http.expectNone('/api/v1/trackers/mal/entries/s1/dates');
  });

  it('"Set today" as the finish keeps the finish-before-start check', () => {
    eveningInTheAmericas();
    const { fixture, c, el, http } = create(seriesRow());
    c.started.set('2026-10-20');
    q(el, 'tracker-dates-finish-today').click();
    fixture.detectChanges();
    expect(c.finished()).toBe('2026-10-15');
    expect(q(el, 'tracker-dates-invalid').textContent).toContain('The finish date is before the start date.');
    expect((q(el, 'tracker-dates-save') as HTMLButtonElement).disabled).toBe(true);
    // Today as the start too: valid, and Save sends both.
    q(el, 'tracker-dates-start-today').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-dates-invalid')).toBeNull();
    q(el, 'tracker-dates-save').click();
    expect(http.expectOne('/api/v1/trackers/mal/entries/s1/dates').request.body).toEqual({ startedOn: '2026-10-15', finishedOn: '2026-10-15' });
  });

  it('shows a refused save in plain words', () => {
    const { fixture, el, http } = create(seriesRow());
    q(el, 'tracker-dates-save').click();
    http.expectOne('/api/v1/trackers/mal/entries/s1/dates').flush({ error: 'invalid_date', message: 'x' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();
    expect(q(el, 'tracker-dates-error').textContent).toContain('Give dates from 1900 to today');
  });
});

describe('todayIso', () => {
  it('is the browser calendar date', () => {
    expect(todayIso(new Date(2026, 0, 5, 23, 59))).toBe('2026-01-05');
  });

  it('is the local date, not the UTC one, on both sides of UTC midnight', () => {
    const evening = Object.assign(new Date('2026-10-16T03:30:00Z'), { getTimezoneOffset: () => 420 }); // UTC-7
    expect(todayIso(evening)).toBe('2026-10-15');
    const morning = Object.assign(new Date('2026-10-15T20:30:00Z'), { getTimezoneOffset: () => -540 }); // UTC+9
    expect(todayIso(morning)).toBe('2026-10-16');
  });
});
