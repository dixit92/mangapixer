import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { MetadataMatchRunsDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { MetadataReviewStateService } from '../../metadata-review-state.service';
import { run } from '../metadata-admin.testing';
import { MetadataRunsComponent, RUNS_POLL_MS } from './metadata-runs.component';

/** Runs tab (stage 2): worker status, live run + Cancel, history, the local "changed by an admin" counter, polling. */
describe('MetadataRunsComponent', () => {
  const page = (overrides: Partial<MetadataMatchRunsDto> = {}): MetadataMatchRunsDto => ({
    status: { enabled: true, active: false, waitingCode: 'budget_exhausted', waitingUntil: null, pending: 42 },
    items: [run({ runId: 'r2', status: 'Running', processed: 10, queued: 40, autoChangedByAdmin: 1 }), run()],
    ...overrides,
  });

  function create(first: MetadataMatchRunsDto | 'error' = page()) {
    const api = {
      getRuns: vi.fn(() => (first === 'error' ? throwError(() => ({ status: 501 })) : of(first))),
      cancelRun: vi.fn((runId: string) => of(run({ runId, status: 'Cancelled' }))),
    };
    const reviewState = { refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [MetadataRunsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: MetadataReviewStateService, useValue: reviewState },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(MetadataRunsComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, c: fixture.componentInstance, api, reviewState };
  }

  afterEach(() => vi.useRealTimers());

  it('shows the status with why it waits, the live run\'s progress, and the history counters', () => {
    const { el, c } = create();
    const status = el.querySelector('[data-testid="runs-status"]')!.textContent!;
    expect(status).toContain('Automatic matching is waiting');
    expect(status).toContain('budget is spent');
    expect(status).toContain('42 folders queued');
    expect(el.querySelector('[data-testid="runs-changed"]')!.textContent).toBe('3');
    const live = el.querySelector('[data-testid="run-live"]')!.textContent!;
    expect(live).toContain('10 of 40 done');
    expect(c.progress(c.live()[0])).toBe(25);
    expect(el.querySelectorAll('[data-testid="runs-history"] tbody tr')).toHaveLength(2);
  });

  it('cancels the live run', () => {
    const { el, api, c, fixture, reviewState } = create();
    (el.querySelector('[data-testid="run-cancel"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.cancelRun).toHaveBeenCalledWith('r2');
    expect(c.live()).toHaveLength(0);
    expect(reviewState.refresh).toHaveBeenCalled();
  });

  it('polls only while a run is live', () => {
    vi.useFakeTimers();
    const live = create();
    vi.advanceTimersByTime(RUNS_POLL_MS + 10);
    expect(live.api.getRuns).toHaveBeenCalledTimes(2);
    TestBed.resetTestingModule();
    const idle = create(page({ items: [run()] }));
    vi.advanceTimersByTime(RUNS_POLL_MS * 3);
    expect(idle.api.getRuns).toHaveBeenCalledTimes(1);
  });

  it('explains a server without runs (501)', () => {
    expect(create('error').c.error()).toContain('not available on this server yet');
  });
});
