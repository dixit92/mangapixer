import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { vi } from 'vitest';

import { TrackerEntryDto, TrackerSeriesRowDto } from '../../core/api/api-types';
import { TrackingSeriesListComponent } from './tracking-series-list.component';
import { listCounts, seriesPage, seriesRow } from './trackers.testing';

const BASE = '/api/v1/trackers/mal';

function entry(o: Partial<TrackerEntryDto> = {}): TrackerEntryDto {
  return {
    seriesId: 's1', title: 'Synthetic Saga', nodeId: 'n1', tracker: 'mal', remoteId: '2', remoteIdSource: 'MangaDex', state: 'Ok',
    errorCode: null, dontSync: false, localChapters: 12, localVolumes: 2, heldChapters: null, heldVolumes: null,
    lastSentChapters: 12, lastSentVolumes: 2, lastSentAt: '2026-10-13T00:00:00Z', remoteChapters: 12, remoteVolumes: 2,
    remoteStatus: 'Reading', remoteMissing: false, remoteSeenAt: '2026-10-13T00:00:00Z', updatedAt: '2026-10-13T00:00:00Z', ...o,
  };
}

/** A MyAnimeList row MyAnimeList is ahead on (51 ch, Completed there; 45 here): only "from MyAnimeList" can move anything. */
const AHEAD = (o: Partial<TrackerSeriesRowDto> = {}) => seriesRow({
  bucket: 'TrackerAhead', state: 'TrackerAhead', allChapters: 45, chapters: 45, allVolumes: null, volumes: null, totalVolumes: null,
  remoteChapters: 51, remoteVolumes: 10, remoteStatus: 'Reading',
  fromTracker: { possible: true, block: 'None' }, toTracker: { possible: false, block: 'NotAhead' }, ...o,
});

/**
 * The Tracking page's series list (1.43.0 owner redesign): server paging (50, Load more), chips by state with counts, title search,
 * rows with both sides' numbers and dates, the two "Set progress" actions (the one that cannot move anything disabled with its
 * reason), the from-tracker confirm and its counts, the "⋯" menu (Sync now, dates, Set to N behind its warning, Don't sync, open),
 * the first-connect preview and Send all. Mocked HTTP backend: every request is to this server.
 */
describe('TrackingSeriesListComponent', () => {
  function create(first: ReturnType<typeof seriesPage>, opts: { canSync?: boolean; focus?: string | null; search?: string } = {}) {
    TestBed.configureTestingModule({
      imports: [TrackingSeriesListComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(TrackingSeriesListComponent);
    fixture.componentRef.setInput('slug', 'mal');
    fixture.componentRef.setInput('name', 'MyAnimeList');
    fixture.componentRef.setInput('canSync', opts.canSync ?? true);
    fixture.componentRef.setInput('focusSeries', opts.focus ?? null);
    fixture.componentRef.setInput('initialSearch', opts.search ?? '');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const req = http.expectOne((r) => r.url === `${BASE}/series`);
    req.flush(first);
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance, firstReq: req };
  }

  const q = (el: ParentNode, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;
  const params = (req: TestRequest) => Object.fromEntries(req.request.params.keys().map((k) => [k, req.request.params.get(k)]));

  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('marks a date "(auto)" only when MangaPixer would send it - the tracker keeps none (owner review 2026-10-15)', () => {
    const { el } = create(seriesPage([
      seriesRow({ dates: { supported: true, startedOn: '2025-10-05', startedOnOverridden: false, finishedOn: '2025-10-17',
        finishedOnOverridden: false, remoteStartedOn: '2025-10-05', remoteFinishedOn: '2025-10-17', pending: false } }),
      seriesRow({ seriesId: 's2', title: 'Second Story', dates: { supported: true, startedOn: '2026-10-15', startedOnOverridden: false,
        finishedOn: null, finishedOnOverridden: false, remoteStartedOn: null, remoteFinishedOn: null, pending: false } }),
    ]));
    const onTracker = q(el, 'tracking-dates-s1').textContent!;
    expect(onTracker).toContain('Start Oct 5, 2025');
    expect(onTracker).toContain('Finish Oct 17, 2025');
    expect(onTracker).not.toContain('(auto)');
    expect(q(el, 'tracking-dates-s2').textContent).toContain('Start Oct 15, 2026 (auto)');
  });

  it('shows the rows with both sides, the chips with counts and the paging line', () => {
    const { el, firstReq } = create(seriesPage([AHEAD(), seriesRow({ seriesId: 's2', title: 'Second Story' })], {
      totalCount: 120, counts: listCounts({ inSync: 100, trackerAhead: 20 }),
    }));
    expect(params(firstReq)).toEqual({ page: '1' });
    const row = q(el, 'tracking-row-s1');
    expect(row.getAttribute('data-bucket')).toBe('TrackerAhead');
    expect(q(el, 'tracking-state-s1').textContent).toContain('MyAnimeList ahead');
    expect(q(el, 'tracking-here-s1').textContent).toContain('45 of 51 ch');
    expect(q(el, 'tracking-remote-s1').textContent).toContain('51 ch · 10 vol');
    expect(row.textContent).toContain('Reading');
    expect(q(el, 'tracking-dates-s1').textContent).toContain('Start Oct 1, 2026');
    // The tracker keeps that start: no note (the column is the tracker's dates).
    expect(q(el, 'tracking-dates-s1').textContent).not.toContain('(auto)');
    expect((row.querySelector('a.title') as HTMLAnchorElement).getAttribute('href')).toBe('/series/n1');
    const chips = [...el.querySelectorAll('.chip-btn')].map((b) => b.textContent!.trim());
    expect(chips).toEqual([
      'All · 120', 'MangaPixer ahead · 0', 'MyAnimeList ahead · 20', 'Left as is · 0', 'Waiting for you · 0', 'No MyAnimeList id · 0',
      'Not synced · 0', 'In sync · 100',
    ]);
    expect(q(el, 'tracking-chip-All').getAttribute('aria-pressed')).toBe('true');
    expect(q(el, 'tracking-shown-mal').textContent).toContain('Showing 1–2 of 120');
  });

  it('the action that cannot move anything is disabled with its reason; the other is on', () => {
    const { el } = create(seriesPage([AHEAD()]));
    const from = q(el, 'tracking-from-s1') as HTMLButtonElement;
    const to = q(el, 'tracking-to-s1') as HTMLButtonElement;
    expect(from.textContent).toContain('Set progress from MyAnimeList to MangaPixer');
    expect(to.textContent).toContain('Set progress from MangaPixer to MyAnimeList');
    expect(from.getAttribute('aria-disabled')).not.toBe('true');
    expect(to.getAttribute('aria-disabled')).toBe('true');
  });

  it('a chip and the search ask the server for page 1 with the filter and the title', async () => {
    vi.useFakeTimers();
    try {
      const { fixture, http, el } = create(seriesPage([seriesRow()], { counts: listCounts({ inSync: 1, trackerAhead: 3 }) }));
      q(el, 'tracking-chip-TrackerAhead').click();
      const byChip = http.expectOne((r) => r.url === `${BASE}/series`);
      expect(params(byChip)).toEqual({ page: '1', filter: 'TrackerAhead' });
      byChip.flush(seriesPage([AHEAD()], { counts: listCounts({ inSync: 1, trackerAhead: 3 }), totalCount: 1 }));
      fixture.detectChanges();
      expect(q(el, 'tracking-chip-TrackerAhead').getAttribute('aria-pressed')).toBe('true');

      fixture.componentInstance.onSearch('Synth');
      http.expectNone((r) => r.url === `${BASE}/series`);
      vi.advanceTimersByTime(350);
      const bySearch = http.expectOne((r) => r.url === `${BASE}/series`);
      expect(params(bySearch)).toEqual({ page: '1', filter: 'TrackerAhead', q: 'Synth' });
      bySearch.flush(seriesPage([], { counts: listCounts({ inSync: 1, trackerAhead: 3 }), totalCount: 0 }));
      fixture.detectChanges();
      expect(q(el, 'tracking-empty-mal').textContent).toContain('No series matches this search');
    } finally {
      vi.useRealTimers();
    }
  });

  it('Load more asks for the next page and appends it; the button stops at the total', () => {
    const page1 = Array.from({ length: 50 }, (_, i) => seriesRow({ seriesId: `a${i}`, title: `Series ${i}` }));
    const page2 = Array.from({ length: 10 }, (_, i) => seriesRow({ seriesId: `b${i}`, title: `More ${i}` }));
    const { fixture, http, el } = create(seriesPage(page1, { totalCount: 60 }));
    expect(el.querySelectorAll('.row:not(.header)')).toHaveLength(50);
    q(el, 'tracking-load-more-mal').click();
    const req = http.expectOne((r) => r.url === `${BASE}/series`);
    expect(params(req)).toEqual({ page: '2' });
    req.flush(seriesPage(page2, { page: 2, totalCount: 60 }));
    fixture.detectChanges();
    expect(el.querySelectorAll('.row:not(.header)')).toHaveLength(60);
    expect((q(el, 'tracking-load-more-mal') as HTMLButtonElement).disabled).toBe(true);
    expect(q(el, 'tracking-shown-mal').textContent).toContain('Showing 1–60 of 60');
  });

  it('"Set progress from MyAnimeList to MangaPixer" asks first, marks read here and shows the counts', () => {
    const { fixture, http, el } = create(seriesPage([AHEAD()]));
    q(el, 'tracking-from-s1').click();
    fixture.detectChanges();
    expect(q(el, 'tracking-from-confirm-s1').textContent).toContain('Mark chapters 1-51 and volumes 1-10 read in MangaPixer?');
    http.expectNone(`${BASE}/entries/s1/from-tracker`);
    q(el, 'tracking-from-yes-s1').click();
    const req = http.expectOne(`${BASE}/entries/s1/from-tracker`);
    expect(req.request.method).toBe('POST');
    req.flush({ marked: 6, alreadyRead: 45, remoteChapters: 51, remoteVolumes: 10,
      entry: seriesRow({ allChapters: 51, chapters: 51, remoteChapters: 51 }) });
    fixture.detectChanges();
    expect(q(el, 'tracking-notice-mal').textContent).toContain('marked 6 files read in MangaPixer (45 files already read)');
    expect(q(el, 'tracking-from-confirm-s1')).toBeNull();
    expect(q(el, 'tracking-state-s1').textContent).toContain('In sync');
  });

  it('"Set progress from MangaPixer to MyAnimeList" sends and re-reads the row', () => {
    const ahead = seriesRow({ bucket: 'Held', state: 'Held', heldChapters: 40, allChapters: 40,
      toTracker: { possible: true, block: 'None' }, fromTracker: { possible: false, block: 'NotAhead' } });
    const { fixture, http, el } = create(seriesPage([ahead], { counts: listCounts({ held: 1 }) }));
    expect(q(el, 'tracking-row-s1').textContent).toContain('Waiting for you: 40 ch');
    q(el, 'tracking-to-s1').click();
    http.expectOne(`${BASE}/entries/s1/send`).flush(entry({ localChapters: 40, lastSentChapters: 40, remoteChapters: 40 }));
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow({ allChapters: 40, remoteChapters: 40 })] });
    fixture.detectChanges();
    expect(q(el, 'tracking-notice-mal').textContent).toContain('Synthetic Saga: up to date.');
    expect(q(el, 'tracking-remote-s1').textContent).toContain('40 ch');
  });

  it('an account that cannot sync keeps both actions and the preview off', () => {
    const { el } = create(seriesPage([AHEAD()]), { canSync: false });
    expect(q(el, 'tracking-from-s1').getAttribute('aria-disabled')).toBe('true');
    expect(q(el, 'tracking-to-s1').getAttribute('aria-disabled')).toBe('true');
    expect((q(el, 'tracker-preview-mal') as HTMLButtonElement).disabled).toBe(true);
  });

  it('the "⋯" menu: Sync now, Set dates, "Set MyAnimeList to a number… (can lower it)", Don\'t sync, Open the series', () => {
    const { fixture, el } = create(seriesPage([AHEAD()]));
    q(el, 'tracking-more-s1').click();
    fixture.detectChanges();
    const overlay = document.querySelector('.cdk-overlay-container')!;
    // The label without its icon's ligature text.
    const label = (i: Element) => i.textContent!.replace(i.querySelector('mat-icon')?.textContent ?? '', '').replace(/\s+/g, ' ').trim();
    const items = [...overlay.querySelectorAll('[mat-menu-item]')].map(label);
    expect(items).toEqual([
      'Sync now', 'Set dates on MyAnimeList…', 'Set MyAnimeList to a number… (can lower it)', "Don't sync this series", 'Open the series',
    ]);
    expect(q(overlay, 'tracking-menu-set').classList).toContain('warn-item');
  });

  it('"Set to N" warns that it may lower the tracker before it sends, and refuses a bad number', () => {
    const { fixture, http, el, c } = create(seriesPage([AHEAD()]));
    c.openSet(AHEAD());
    fixture.detectChanges();
    expect(q(el, 'tracker-set-form-s1').textContent).toContain('also when MyAnimeList has MORE');
    c.setChapters.set(1.5);
    fixture.detectChanges();
    expect((q(el, 'tracker-set-yes-s1') as HTMLButtonElement).disabled).toBe(true);
    c.setChapters.set(10);
    c.setVolumes.set(null);
    fixture.detectChanges();
    expect(q(el, 'tracker-set-yes-s1').textContent).toContain('Set MyAnimeList to 10 ch');
    q(el, 'tracker-set-yes-s1').click();
    const req = http.expectOne(`${BASE}/entries/s1/set`);
    expect(req.request.body).toEqual({ chapters: 10, volumes: null });
    req.flush(entry({ remoteChapters: 10 }));
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow({ remoteChapters: 10 })] });
    fixture.detectChanges();
    expect(q(el, 'tracker-set-form-s1')).toBeNull();
  });

  it('offers no "Set to" and no actions for a series left as is or without an id', () => {
    const left = seriesRow({ bucket: 'LeftAsIs', state: 'LeftAsIs', remoteStatus: 'Dropped',
      toTracker: { possible: false, block: 'LeftAsIs' }, fromTracker: { possible: false, block: 'LeftAsIs' } });
    const noId = seriesRow({ seriesId: 's2', bucket: 'NoId', state: 'NoId', remoteId: null,
      toTracker: { possible: false, block: 'NoId' }, fromTracker: { possible: false, block: 'NoId' } });
    const { el, c } = create(seriesPage([left, noId]));
    expect(q(el, 'tracking-state-s1').textContent).toContain('Left as is · Dropped on MyAnimeList');
    expect(c.canSetTo(left)).toBe(false);
    expect(c.canAct(left)).toBe(true);
    expect(c.canAct(noId)).toBe(false);
    expect(c.toReason(noId)).toContain('No MyAnimeList id');
    expect(c.fromReason(left)).toContain('Left as is on MyAnimeList');
  });

  it("Don't sync and Sync again", () => {
    const { fixture, http, el, c } = create(seriesPage([seriesRow()]));
    c.toggleDontSync(seriesRow());
    const req = http.expectOne(`${BASE}/entries/s1/dont-sync`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ dontSync: true });
    req.flush(entry({ dontSync: true, state: 'DontSync' }));
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow({ dontSync: true, bucket: 'NotSynced' })] });
    fixture.detectChanges();
    expect(q(el, 'tracking-notice-mal').textContent).toContain('is not synced any more');
    expect(q(el, 'tracking-state-s1').textContent).toContain('Not synced (your choice)');
  });

  it('Set dates opens the dates dialog and shows the saved row', async () => {
    const saved = seriesRow({ dates: { ...seriesRow().dates, startedOn: '2026-09-01', startedOnOverridden: true, pending: true } });
    const { fixture, el, c } = create(seriesPage([seriesRow()]));
    const open = vi.spyOn(TestBed.inject(MatDialog), 'open').mockReturnValue({ afterClosed: () => of(saved) } as never);
    await c.openDates(seriesRow());
    fixture.detectChanges();
    expect(open).toHaveBeenCalled();
    expect(q(el, 'tracking-dates-s1').textContent).toContain('Start Sep 1, 2026');
    expect(q(el, 'tracking-dates-s1').textContent).toContain('(yours)');
    expect(q(el, 'tracking-dates-s1').textContent).toContain('Goes with the next update');
  });

  it('the preview reads 50 at a time, says nothing changed on the tracker and reloads the list', () => {
    const { fixture, http, el } = create(seriesPage([]));
    expect(q(el, 'tracking-empty-mal').textContent).toContain('No series yet');
    q(el, 'tracker-preview-mal').click();
    http.expectOne(`${BASE}/preview`).flush({ entries: [entry({ state: 'Held', heldChapters: 20 })], remaining: 70, capReached: false });
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([seriesRow({ bucket: 'Held' })], { counts: listCounts({ held: 1 }) }));
    fixture.detectChanges();
    expect(q(el, 'tracking-notice-mal').textContent).toContain('Nothing was changed on MyAnimeList');
    expect(q(el, 'tracker-preview-mal').textContent).toContain('Check 50 more (70 left)');
    expect(q(el, 'tracking-row-s1')).not.toBeNull();
  });

  it('Send all asks first, then reports the counts and reloads', () => {
    const { fixture, http, el } = create(seriesPage([seriesRow({ bucket: 'Held', heldChapters: 40 })], { counts: listCounts({ held: 1 }) }));
    q(el, 'tracker-send-all-mal').click();
    fixture.detectChanges();
    expect(q(el, 'tracker-send-all-confirm-mal').textContent).toContain('never lowered');
    http.expectNone(`${BASE}/entries/send-all`);
    q(el, 'tracker-send-all-yes-mal').click();
    http.expectOne(`${BASE}/entries/send-all`).flush({ sent: 1, skipped: 0, failed: 0, capReached: false });
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([seriesRow()]));
    fixture.detectChanges();
    expect(q(el, 'tracking-notice-mal').textContent).toContain('Sent 1');
  });

  it('a failed action is said in plain words with the tracker named', () => {
    const { fixture, http, el } = create(seriesPage([AHEAD()]));
    q(el, 'tracking-from-s1').click();
    fixture.detectChanges();
    q(el, 'tracking-from-yes-s1').click();
    http.expectOne(`${BASE}/entries/s1/from-tracker`).flush({ error: 'unavailable', message: 'x' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(q(el, 'tracking-error-mal').textContent).toContain('MyAnimeList could not be reached from this server');
  });

  it('"Open in Tracking": the series title seeds the search and the series row is highlighted', () => {
    const { el, firstReq } = create(seriesPage([AHEAD()]), { focus: 's1', search: 'Synthetic Saga' });
    expect(params(firstReq)).toEqual({ page: '1', q: 'Synthetic Saga' });
    expect(q(el, 'tracking-row-s1').classList).toContain('focused');
  });
});
