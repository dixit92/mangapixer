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
});
