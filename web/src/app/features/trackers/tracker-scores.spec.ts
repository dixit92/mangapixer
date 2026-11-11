import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { TrackerScoreImportResultDto, TrackerSummaryDto } from '../../core/api/api-types';
import { TrackerAccountPanelComponent } from './tracker-account-panel.component';
import { TrackerNavigationService } from './tracker-navigation.service';
import { TrackerScoreImportComponent } from './tracker-score-import.component';
import { summary, trackerAccount } from './trackers.testing';

const BASE = '/api/v1/trackers';

/**
 * 1.53.0 (lane T) tracker scores on the Tracking page's account card: the switch "Keep my score in step with my MangaPixer rating"
 * (saved with the other settings; turning it on offers the import once), and "Take my scores from <tracker>" - the summary and the
 * per-row "Use <tracker>'s" for a kept, differing rating. Mocked HTTP backend.
 */
describe('Tracker scores (account card)', () => {
  const q = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  function panel(tracker: TrackerSummaryDto) {
    TestBed.configureTestingModule({
      imports: [TrackerAccountPanelComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: TrackerNavigationService, useValue: { openSignIn: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(TrackerAccountPanelComponent);
    fixture.componentRef.setInput('tracker', tracker);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  function importer(slug = 'mal', name = 'MyAnimeList') {
    TestBed.configureTestingModule({
      imports: [TrackerScoreImportComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(TrackerScoreImportComponent);
    fixture.componentRef.setInput('slug', slug);
    fixture.componentRef.setInput('name', name);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  const result = (o: Partial<TrackerScoreImportResultDto> = {}): TrackerScoreImportResultDto => ({
    scored: 9, matched: 5, filled: 2, same: 1, requests: 1, capReached: false,
    differ: [
      { seriesId: 's1', title: 'Synthetic Saga', nodeId: 'n1', myScore: 4, trackerScore: 8 },
      { seriesId: 's2', title: 'Another Saga', nodeId: 'n2', myScore: 9, trackerScore: 7 },
    ],
    ...o,
  });

  it('shows the switch, off by default, on a connected card only - with what it does', () => {
    const { el } = panel(summary({ account: trackerAccount() }));
    const toggle = q(el, 'tracker-sync-scores-mal')!;
    expect(toggle.textContent).toContain('Keep my score in step with my MangaPixer rating');
    expect(toggle.querySelector('button[role="switch"]')!.getAttribute('aria-checked')).toBe('false');
    // "Take my scores" sits next to Disconnect; the hint and the offer wait for the switch.
    expect(q(el, 'tracker-import-scores-mal')!.textContent).toContain('Take my scores from MyAnimeList');
    expect(q(el, 'tracker-import-scores-mal')!.parentElement).toBe(q(el, 'tracker-disconnect-mal')!.parentElement);
    expect(q(el, 'tracker-sync-scores-hint-mal')).toBeNull();
    expect(q(el, 'tracker-score-offer-mal')).toBeNull();
  });

  it('says what the switch does once it is on', () => {
    const { el } = panel(summary({ account: trackerAccount({ syncScores: true }) }));
    expect(q(el, 'tracker-sync-scores-mal')!.querySelector('button[role="switch"]')!.getAttribute('aria-checked')).toBe('true');
    expect(q(el, 'tracker-sync-scores-hint-mal')!.textContent).toContain('only for series already on your MyAnimeList list');
  });

  it('the card\'s "Take my scores" runs the import and shows its summary in the card', () => {
    const { fixture, http, el } = panel(summary({ account: trackerAccount() }));
    q(el, 'tracker-import-scores-mal')!.click();
    fixture.detectChanges();
    expect((q(el, 'tracker-import-scores-mal') as HTMLButtonElement).disabled).toBe(true);
    http.expectOne(`${BASE}/mal/scores/import`).flush(result({ differ: [] }));
    fixture.detectChanges();
    expect(q(el, 'tracker-score-summary-mal')!.textContent).toContain('2 unrated series now have your MyAnimeList score');
    expect((q(el, 'tracker-import-scores-mal') as HTMLButtonElement).disabled).toBe(false);
    http.verify();
  });

  it('has neither the switch nor the import before connecting, or while the account must connect again', () => {
    for (const t of [summary(), summary({ account: trackerAccount({ consentRenewalNeeded: true }) }),
      summary({ account: trackerAccount({ state: 'NeedsReconnect' }) })]) {
      TestBed.resetTestingModule();
      const { el } = panel(t);
      expect(q(el, 'tracker-sync-scores-mal')).toBeNull();
      expect(q(el, 'tracker-import-scores-mal')).toBeNull();
    }
  });

  it('saves the switch as a per-user setting and offers the import once it is on', () => {
    const { fixture, http, el } = panel(summary({ account: trackerAccount() }));
    (q(el, 'tracker-sync-scores-mal')!.querySelector('button[role="switch"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const req = http.expectOne(`${BASE}/mal/settings`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ syncScores: true });
    req.flush(trackerAccount({ syncScores: true }));
    fixture.detectChanges();
    expect(q(el, 'tracker-score-offer-mal')!.textContent).toContain('your MyAnimeList scores too');
    http.verify();
  });

  it('puts the switch back when the save fails, with no offer', () => {
    const { fixture, http, el } = panel(summary({ account: trackerAccount() }));
    const button = q(el, 'tracker-sync-scores-mal')!.querySelector('button[role="switch"]') as HTMLButtonElement;
    button.click();
    fixture.detectChanges();
    http.expectOne(`${BASE}/mal/settings`).flush({ error: 'not_connected', message: 'x' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(button.getAttribute('aria-checked')).toBe('false');
    expect(q(el, 'tracker-score-offer-mal')).toBeNull();
    expect(q(el, 'tracker-error-mal')).not.toBeNull();
  });

  it('takes the scores on click and sums up: filled, the same, and the kept differences listed with "Use <tracker>\'s"', () => {
    const { fixture, http, el } = importer();
    q(el, 'tracker-import-scores-mal')!.click();
    fixture.detectChanges();
    const req = http.expectOne(`${BASE}/mal/scores/import`);
    expect(req.request.method).toBe('POST');
    req.flush(result());
    fixture.detectChanges();

    expect(q(el, 'tracker-score-summary-mal')!.textContent)
      .toContain('2 unrated series now have your MyAnimeList score; 1 already the same; 2 differ - kept your MangaPixer ratings');
    const row = q(el, 'tracker-score-row-s1')!;
    expect(row.textContent).toContain('Synthetic Saga');
    // Stars: 4 = 2 stars here, 8 = 4 stars on MyAnimeList.
    expect(row.textContent).toContain('Yours 2 ★ · MyAnimeList 4 ★');
    expect(row.querySelector('a')!.getAttribute('href')).toBe('/series/n1');
    expect(q(el, 'tracker-score-use-s1')!.textContent).toContain("Use MyAnimeList's");
    expect(q(el, 'tracker-score-use-s2')).not.toBeNull();
    http.verify();
  });

  it('"Use <tracker>\'s" sets that one rating only, then shows it as taken', () => {
    const { fixture, http, el } = importer();
    q(el, 'tracker-import-scores-mal')!.click();
    http.expectOne(`${BASE}/mal/scores/import`).flush(result());
    fixture.detectChanges();

    q(el, 'tracker-score-use-s1')!.click();
    fixture.detectChanges();
    const req = http.expectOne(`${BASE}/mal/scores/s1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ score: 8 });
    req.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(q(el, 'tracker-score-use-s1')).toBeNull();
    expect(q(el, 'tracker-score-row-s1')!.textContent).toContain('Now 4 ★ (from MyAnimeList)');
    // The other difference is untouched and still offered.
    expect(q(el, 'tracker-score-use-s2')).not.toBeNull();
    http.verify();
  });

  it('says when nothing matched, when the day\'s requests are used up, and shows a failure', () => {
    const { fixture, http, el } = importer('mangaupdates', 'MangaUpdates');
    q(el, 'tracker-import-scores-mangaupdates')!.click();
    http.expectOne(`${BASE}/mangaupdates/scores/import`).flush(result({ matched: 0, filled: 0, same: 0, differ: [], scored: 3 }));
    fixture.detectChanges();
    expect(q(el, 'tracker-score-summary-mangaupdates')!.textContent).toContain('None of your 3 scored series on MangaUpdates are here.');
    expect(q(el, 'tracker-score-differ-mangaupdates')).toBeNull();

    q(el, 'tracker-import-scores-mangaupdates')!.click();
    http.expectOne(`${BASE}/mangaupdates/scores/import`).flush(result({ capReached: true, requests: 0, filled: 0, same: 0, differ: [] }));
    fixture.detectChanges();
    expect(q(el, 'tracker-score-summary-mangaupdates')!.textContent).toContain("Today's MangaUpdates requests are used up");

    q(el, 'tracker-import-scores-mangaupdates')!.click();
    http.expectOne(`${BASE}/mangaupdates/scores/import`)
      .flush({ error: 'needs_reconnect', message: 'x' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(q(el, 'tracker-score-error-mangaupdates')).not.toBeNull();
    http.verify();
  });
});
