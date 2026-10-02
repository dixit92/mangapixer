import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { SeriesRefreshCadenceDto } from '../../../core/api/api-types';
import { SeriesRefreshCadenceComponent, cadenceLine, intervalLabel } from './series-refresh-cadence.component';

@Component({
  standalone: true,
  imports: [SeriesRefreshCadenceComponent],
  template: `<app-series-refresh-cadence [linkNodeId]="id()" />`,
})
class HostComponent {
  readonly id = signal<string | null>('n1');
}

describe('SeriesRefreshCadenceComponent', () => {
  const now = Date.parse('2026-10-02T12:00:00Z');
  const cadence = (overrides: Partial<SeriesRefreshCadenceDto> = {}): SeriesRefreshCadenceDto => ({
    days: 14, reason: 'pace', volumeIntervalDays: 70, fetchedAt: '2026-09-30T12:00:00Z', nextCheckAt: '2026-10-14T12:00:00Z', ...overrides,
  });

  it('words the cadence and its reason', () => {
    expect(cadenceLine(cadence(), now)).toBe('Checked every 2 weeks (a new volume about every 2 months); next check in 12 days.');
    expect(cadenceLine(cadence({ days: 30, reason: 'choice', volumeIntervalDays: null }), now))
      .toBe('Checked every month (your choice for ongoing series); next check in 12 days.');
    expect(cadenceLine(cadence({ days: 90, reason: 'finished' }), now)).toContain('Checked every 3 months (finished)');
    expect(cadenceLine(cadence({ days: 90, reason: 'paused' }), now)).toContain('(on hiatus, or nothing new for six months)');
    expect(cadenceLine(cadence({ nextCheckAt: '2026-10-01T12:00:00Z' }), now)).toContain('next check at the next refresh');
    expect([5, 21, 45, 100, 400].map(intervalLabel)).toEqual(['every week', 'every 3 weeks', 'every 6 weeks', 'every 3 months', 'every 13 months']);
  });

  it('loads the line for the link node and shows nothing without a link', () => {
    const calls: string[] = [];
    let answer: SeriesRefreshCadenceDto | null = cadence({ nextCheckAt: new Date(Date.now() + 5 * 86_400_000).toISOString() });
    const api = {
      getSeriesRefreshCadence(id: string): Observable<SeriesRefreshCadenceDto | null> {
        calls.push(id);
        return of(answer);
      },
    };
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [{ provide: ApiService, useValue: api }] });
    const f = TestBed.createComponent(HostComponent);
    f.detectChanges();
    expect(calls).toEqual(['n1']);
    expect(f.nativeElement.textContent).toContain('Checked every 2 weeks');

    answer = null;
    f.componentInstance.id.set('n2');
    f.detectChanges();
    expect(calls).toEqual(['n1', 'n2']);
    expect(f.nativeElement.querySelector('[data-testid="series-refresh-cadence"]')).toBeNull();
  });
});
