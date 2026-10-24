import { DatePipe } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { TakenDownRecordsDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { TakenDownRecordsComponent } from './taken-down-records.component';

/** "MangaDex records taken down" on the Runs tab (1.47.0, owner decision 10a): a count and a list from stored rows, hidden when none. */
describe('TakenDownRecordsComponent', () => {
  const list: TakenDownRecordsDto = {
    total: 3,
    limit: 200,
    items: [
      {
        recordId: 'r1', title: 'Older Takedown', since: '2026-10-04T06:00:00Z', lastSeen: '2026-10-03T06:00:00Z', sweeps: 7,
        series: [{ nodeId: 'n1', libraryId: 'lib1', displayName: 'Shelf Old' }, { nodeId: 'n2', libraryId: 'lib1', displayName: 'Shelf Twin' }],
        moreSeries: 2,
      },
      { recordId: 'r2', title: 'Newer Takedown', since: '2026-10-10T06:00:00Z', lastSeen: null, sweeps: 1, series: [{ nodeId: 'n3', libraryId: 'lib2', displayName: 'Shelf New' }] },
    ],
  };

  function create(answer: TakenDownRecordsDto | 'error') {
    const api = { getTakenDown: vi.fn(() => (answer === 'error' ? throwError(() => ({ status: 500 })) : of(answer))) };
    TestBed.configureTestingModule({
      imports: [TakenDownRecordsComponent],
      providers: [provideRouter([]), { provide: MetadataApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(TakenDownRecordsComponent);
    fixture.detectChanges();
    return { api, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the count, each record with its series, since when, the checks in a row and when it was last found', () => {
    const { api, el } = create(list);
    expect(api.getTakenDown).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-testid="taken-down-count"]')!.textContent).toBe('3');
    const rows = Array.from(el.querySelectorAll('[data-testid="taken-down-row"]'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Older Takedown');
    expect(rows[0].textContent).toContain('Shelf Old, Shelf Twin');
    expect(rows[0].textContent).toContain('and 2 more');
    // The dates read in the viewer's time zone (as the pipe does), so the expectation uses the same pipe.
    const day = (iso: string) => new DatePipe('en-US').transform(iso, 'mediumDate');
    expect(rows[0].querySelector('[data-testid="taken-down-since"]')!.textContent!.replace(/\s+/g, ' ').trim())
      .toBe(`Missing since ${day('2026-10-04T06:00:00Z')} · 7 checks in a row · last found ${day('2026-10-03T06:00:00Z')}`);
    // Never found since the record was stored: no "last found"; a single check reads in the singular.
    const second = rows[1].querySelector('[data-testid="taken-down-since"]')!.textContent!;
    expect(second).toContain('1 check in a row');
    expect(second).not.toContain('last found');
    expect((rows[0].querySelector('.series a') as HTMLAnchorElement).getAttribute('href')).toBe('/libraries/lib1/browse/n1');
  });

  it('says how many of the total are shown when the list is capped', () => {
    const { el } = create(list);
    expect(el.textContent).toContain('Showing the 2 oldest of 3.');
  });

  it('shows nothing when no record is missing, and nothing when the read fails', () => {
    expect(create({ total: 0, limit: 200, items: [] }).el.querySelector('[data-testid="taken-down"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create('error').el.querySelector('[data-testid="taken-down"]')).toBeNull();
  });
});
