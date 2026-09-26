import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { MetadataMatchEstimateDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { estimate } from '../metadata-admin.testing';
import { LibraryMatchPanelComponent } from './library-match-panel.component';

/** "Match this library now" (decision 1): local estimate, review-first on a first run, Start queues only. */
describe('LibraryMatchPanelComponent', () => {
  function create(first: MetadataMatchEstimateDto | Error = estimate()) {
    const api = {
      getMatchEstimate: vi.fn((_id: string, retry: boolean) =>
        first instanceof Error ? throwError(() => ({ message: first.message })) : of({ ...first, candidates: retry ? 130 : first.candidates })),
      matchLibrary: vi.fn(() => of({ runId: 'r1', libraryName: 'Library One' })),
    };
    TestBed.configureTestingModule({
      imports: [LibraryMatchPanelComponent],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(LibraryMatchPanelComponent);
    fixture.componentRef.setInput('libraryId', 'lib1');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, c: fixture.componentInstance, api, el };
  }

  it('shows candidates, requests, days at the budget and already linked', () => {
    const { el, api } = create();
    expect(api.getMatchEstimate).toHaveBeenCalledWith('lib1', false);
    const text = el.querySelector('[data-testid="match-estimate"]')!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('120 folders to match');
    expect(text).toContain('about 360 requests');
    expect(text).toContain('within a day at 5,000 a day');
    expect(text).toContain('15 already linked');
  });

  it('offers "review everything once" on a first run and sends it with Start', () => {
    const { c, el, api } = create();
    expect(el.querySelector('[data-testid="match-review-first"]')).not.toBeNull();
    const started: string[] = [];
    c.started.subscribe((r) => started.push(r.runId));
    c.reviewFirst.set(true);
    c.start();
    expect(api.matchLibrary).toHaveBeenCalledWith('lib1', { reviewFirst: true, retryUnmatched: false });
    expect(started).toEqual(['r1']);
  });

  it('re-estimates with retry unmatched, and never sends reviewFirst after the first run', () => {
    const { c, api } = create(estimate({ firstRun: false }));
    c.reviewFirst.set(true);
    c.setRetry(true);
    expect(api.getMatchEstimate).toHaveBeenLastCalledWith('lib1', true);
    expect(c.estimate()!.candidates).toBe(130);
    c.start();
    expect(api.matchLibrary).toHaveBeenCalledWith('lib1', { reviewFirst: false, retryUnmatched: true });
  });

  it('explains why automatic matching is unavailable and disables Start', () => {
    const { el, fixture } = create(estimate({ automaticAvailable: false, unavailableCode: 'automatic_off' }));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="match-unavailable"]')!.textContent).toContain('Automatic matching is off.');
    expect((el.querySelector('[data-testid="match-start"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows an estimate failure', () => {
    const { c } = create(new Error('Not implemented yet.'));
    expect(c.error()).toBe('Not implemented yet.');
  });
});
