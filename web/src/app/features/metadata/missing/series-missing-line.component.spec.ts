import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { MissingReportApiService } from './missing-report-api.service';
import { SeriesMissingLineComponent } from './series-missing-line.component';
import { gap, missingRow } from './missing.testing';

describe('SeriesMissingLineComponent', () => {
  function create(response: ReturnType<typeof missingRow> | 'error') {
    const api = { forNode: vi.fn(() => (response === 'error' ? throwError(() => ({ status: 404 })) : of(response))) };
    TestBed.configureTestingModule({
      imports: [SeriesMissingLineComponent],
      providers: [provideRouter([]), { provide: MissingReportApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(SeriesMissingLineComponent);
    fixture.componentRef.setInput('nodeId', 'series-1');
    fixture.detectChanges();
    return { api, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the volumes and chapters lines for the node', () => {
    const { api, el } = create(missingRow({ chapters: gap({ kind: 'Chapter', lowest: 61, have: 70, available: 80, behindBy: 10 }) }));
    expect(api.forNode).toHaveBeenCalledWith('series-1');
    const line = el.querySelector('[data-testid="series-missing-line"]')!;
    expect(line.textContent).toContain('You have volumes 1-7 of 10 (English)');
    expect(line.textContent).toContain('You have chapters 61-70 of 80 (English) · 10 behind');
  });

  it('renders nothing without a row or without numbers', () => {
    expect(create('error').el.querySelector('[data-testid="series-missing-line"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create(missingRow({ verdict: 'NoUnits', volumes: null })).el.querySelector('[data-testid="series-missing-line"]')).toBeNull();
  });
});
