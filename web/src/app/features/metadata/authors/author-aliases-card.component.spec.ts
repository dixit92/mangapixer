import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { AuthorAliasStatusDto } from '../../../core/api/api-types';
import { AuthorAliasesApiService } from './author-aliases-api.service';
import { aliasStatus } from './author-aliases.testing';
import {
  AUTHOR_ALIAS_POLL_MS, AuthorAliasesCardComponent, blockedLabel, lookupDuration, outcomeLabel,
} from './author-aliases-card.component';

/**
 * Metadata Manager "Artists' other names" card (1.38.0): the counts line, "Look up the rest" with its request count and time, the
 * reason it cannot start, progress + Cancel while it runs (polled), the last result. Mocked API - never the network.
 */
describe('AuthorAliasesCardComponent', () => {
  function create(first: AuthorAliasStatusDto, api: Partial<Record<'status' | 'start' | 'cancel', ReturnType<typeof vi.fn>>> = {}) {
    const mock = {
      status: api.status ?? vi.fn(() => of(first)),
      start: api.start ?? vi.fn(() => of(first)),
      cancel: api.cancel ?? vi.fn(() => of(first)),
    };
    TestBed.configureTestingModule({
      imports: [AuthorAliasesCardComponent],
      providers: [provideNoopAnimations(), { provide: AuthorAliasesApiService, useValue: mock }],
    });
    const fixture = TestBed.createComponent(AuthorAliasesCardComponent);
    fixture.detectChanges();
    TestBed.tick();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
    const text = (id: string) => (q(id)?.textContent ?? '').replace(/\s+/g, ' ').trim();
    return { fixture, el, q, text, api: mock };
  }

  afterEach(() => vi.useRealTimers());

  it('shows the counts and "Look up the rest" with the number of requests and the time', () => {
    const { q, text, el } = create(aliasStatus({ eligible: 1512, fetched: 12, toFetch: 1500, withOtherNames: 9 }));
    expect(text('md-authors-status')).toBe('12 of 1512 known authors fetched · 9 with other names');
    expect(q('md-authors-start')).not.toBeNull();
    expect(el.textContent).toContain('1500 requests, about 25 min.');
    expect(q('md-authors-blocked')).toBeNull();
  });

  it('starts the look-up and polls the progress until it finishes; Cancel stops it', () => {
    vi.useFakeTimers();
    const running = aliasStatus({
      eligible: 3, toFetch: 3,
      running: { startedAt: '2026-10-09T06:00:00Z', total: 3, requests: 1, stored: 1, notFound: 0, failed: 0, outcome: 'running' },
    });
    const done = aliasStatus({
      eligible: 3, fetched: 3, withOtherNames: 2,
      lastRun: { startedAt: '2026-10-09T06:00:00Z', finishedAt: '2026-10-09T06:00:03Z', total: 3, requests: 3, stored: 2, notFound: 1,
        failed: 0, outcome: 'completed' },
    });
    const statuses = [aliasStatus({ eligible: 3, toFetch: 3 }), done];
    const status = vi.fn(() => of(statuses.shift() ?? done));
    const { q, text, fixture, api } = create(statuses[0], { status, start: vi.fn(() => of(running)) });

    q('md-authors-start')!.click();
    fixture.detectChanges();
    expect(api.start).toHaveBeenCalledTimes(1);
    expect(text('md-authors-progress')).toBe('Looking up… 1 of 3');
    expect(q('md-authors-cancel')).not.toBeNull();

    vi.advanceTimersByTime(AUTHOR_ALIAS_POLL_MS);
    fixture.detectChanges();
    expect(q('md-authors-progress')).toBeNull();
    expect(text('md-authors-last')).toBe('Last look-up finished: 2 found, 1 not on MangaUpdates (3 requests).');
    // Finished: no more polling.
    const calls = status.mock.calls.length;
    vi.advanceTimersByTime(AUTHOR_ALIAS_POLL_MS * 3);
    expect(status.mock.calls.length).toBe(calls);
  });

  it('Cancel asks the server to stop', () => {
    const cancelled = aliasStatus({
      eligible: 3, fetched: 1, toFetch: 2,
      lastRun: { startedAt: '2026-10-09T06:00:00Z', total: 3, requests: 1, stored: 1, notFound: 0, failed: 0, outcome: 'cancelled' },
    });
    const running = aliasStatus({
      eligible: 3, toFetch: 3,
      running: { startedAt: '2026-10-09T06:00:00Z', total: 3, requests: 1, stored: 1, notFound: 0, failed: 0, outcome: 'running' },
    });
    const { q, text, fixture, api } = create(running, { cancel: vi.fn(() => of(cancelled)) });

    q('md-authors-cancel')!.click();
    fixture.detectChanges();
    expect(api.cancel).toHaveBeenCalledTimes(1);
    expect(text('md-authors-last')).toContain('cancelled - the rest stay to look up');
    expect(q('md-authors-start')).not.toBeNull();
  });

  it('says why it cannot start instead of offering the button', () => {
    const { q, text } = create(aliasStatus({ eligible: 3, toFetch: 3, blockedReason: 'metadata_disabled' }));
    expect(q('md-authors-start')).toBeNull();
    expect(text('md-authors-blocked')).toContain('Turn on "Fetch from the web"');
  });

  it('shows a refusal message and reloads the status', () => {
    const start = vi.fn(() => throwError(() => ({ error: 'budget_exhausted', message: "Today's metadata request budget is used up." })));
    const pending = new Subject<AuthorAliasStatusDto>();
    const status = vi.fn()
      .mockReturnValueOnce(of(aliasStatus({ eligible: 3, toFetch: 3 })))
      .mockReturnValueOnce(pending);
    const { q, text, fixture } = create(aliasStatus(), { status, start });

    q('md-authors-start')!.click();
    fixture.detectChanges();
    expect(text('md-authors-error')).toContain('budget is used up');
    expect(status).toHaveBeenCalledTimes(2);
  });

  it('labels', () => {
    expect(lookupDuration(25, 1)).toBe('about 25 s');
    expect(lookupDuration(1512, 1)).toBe('about 25 min');
    expect(lookupDuration(3900, 1)).toBe('about 1 h 5 min');
    expect(blockedLabel(null)).toBeNull();
    expect(blockedLabel('provider_not_allowed')).toContain('allowed sites');
    expect(blockedLabel('library_metadata_disabled')).toContain('No library');
    expect(outcomeLabel({ startedAt: '', total: 1, requests: 1, stored: 0, notFound: 0, failed: 0, outcome: 'provider_backoff' }))
      .toContain('slow down');
  });
});
