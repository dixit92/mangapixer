import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';

import { TrackerBatchResultDto, TrackerSelectableSeriesDto, TrackerSeriesRowDto } from '../../core/api/api-types';
import { TrackingSeriesListComponent } from './tracking-series-list.component';
import { listCounts, seriesPage, seriesRow } from './trackers.testing';

const BASE = '/api/v1/trackers/mal';

/** MyAnimeList ahead (only "from MyAnimeList" applies), MangaPixer ahead (only "to MyAnimeList"), in sync, and not synced. */
const FROM = (o: Partial<TrackerSeriesRowDto> = {}) => seriesRow({ seriesId: 'a', title: 'Alpha', bucket: 'TrackerAhead', state: 'TrackerAhead',
  remoteChapters: 51, fromTracker: { possible: true, block: 'None' }, ...o });
const TO = (o: Partial<TrackerSeriesRowDto> = {}) => seriesRow({ seriesId: 'b', title: 'Beta', bucket: 'MangaPixerAhead', allChapters: 60,
  toTracker: { possible: true, block: 'None' }, ...o });
const SYNCED = (o: Partial<TrackerSeriesRowDto> = {}) => seriesRow({ seriesId: 'c', title: 'Gamma', ...o });
const OFF = (o: Partial<TrackerSeriesRowDto> = {}) => seriesRow({ seriesId: 'd', title: 'Delta', bucket: 'NotSynced', state: 'DontSync',
  dontSync: true, fromTracker: { possible: false, block: 'DontSync' }, toTracker: { possible: false, block: 'DontSync' }, ...o });

function selectable(id: string, o: Partial<TrackerSelectableSeriesDto> = {}): TrackerSelectableSeriesDto {
  return { seriesId: id, fromTracker: { possible: true, block: 'None' }, toTracker: { possible: false, block: 'NotAhead' }, canSync: true,
    dontSync: false, ...o };
}

function result(o: Partial<TrackerBatchResultDto> = {}): TrackerBatchResultDto {
  return { done: 0, skipped: 0, failed: 0, capReached: false, notReached: 0, marked: 0, alreadyRead: 0, stoppedCode: null, skips: [], rows: [], ...o };
}

/** Lets an awaited request in `applyBatch` settle so the next chunk goes out. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/**
 * The Tracking list's Select mode (1.43.2, owner 2026-10-16; it replaces Send all): one shared multi-select for every chip - a tick per
 * row (Shift-click range), "Select all N in this chip" from the server's ids route, the bar's actions with how many of the selected each
 * applies to (the chip's natural one first; one that applies to none is disabled with a visible reason), ONE confirm with the counts, the
 * chunked apply with "N of M…" and Cancel, the summary, the reload; and the link to the series on the tracker's site. Mocked HTTP backend.
 */
describe('TrackingSeriesListComponent - Select mode (1.43.2)', () => {
  function create(first: ReturnType<typeof seriesPage>, opts: { canSync?: boolean; addStartedSeries?: boolean; slug?: string } = {}) {
    TestBed.configureTestingModule({
      imports: [TrackingSeriesListComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(TrackingSeriesListComponent);
    const slug = opts.slug ?? 'mal';
    fixture.componentRef.setInput('slug', slug);
    fixture.componentRef.setInput('name', slug === 'mal' ? 'MyAnimeList' : 'MangaUpdates');
    fixture.componentRef.setInput('canSync', opts.canSync ?? true);
    if (opts.addStartedSeries !== undefined) fixture.componentRef.setInput('addStartedSeries', opts.addStartedSeries);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === `/api/v1/trackers/${slug}/series`).flush(first);
    fixture.detectChanges();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    return { fixture, http, el: fixture.nativeElement as HTMLElement, changed };
  }

  const q = (el: ParentNode, id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement;
  const text = (el: ParentNode, id: string) => (q(el, id)?.textContent ?? '').replace(/\s+/g, ' ').trim();
  const actionOrder = (el: HTMLElement) => [...el.querySelectorAll('.sel-act')].map((b) => b.getAttribute('data-testid')!.replace('tracking-batch-', ''));
  const tick = (el: HTMLElement, id: string, shiftKey = false) =>
    q(el, `tracking-pick-box-${id}`).dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, shiftKey }));

  afterEach(() => {
    vi.useRealTimers();
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('1.44.0: "Set MyAnimeList\'s volumes from its chapters" joins the bar only while a selected series offers it', () => {
    const { fixture, el } = create(seriesPage([FROM({ volumesFromChapters: 27 }), SYNCED()], { counts: listCounts({ trackerAhead: 1, inSync: 1 }) }));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    tick(el, 'c');
    fixture.detectChanges();
    expect(actionOrder(el)).not.toContain('VolumesFromChapters');
    expect(q(el, 'tracking-batch-why-VolumesFromChapters')).toBeNull();

    tick(el, 'a');
    fixture.detectChanges();
    expect(actionOrder(el).at(-1)).toBe('VolumesFromChapters');
    expect(q(el, 'tracking-batch-VolumesFromChapters').getAttribute('aria-label')).toBe("Set MyAnimeList's volumes from its chapters (1)");
  });

  it('Select shows a tick per row and the bar; each action says how many of the selected it applies to, and why one applies to none', () => {
    const { fixture, el } = create(seriesPage([FROM(), TO(), SYNCED(), OFF()], { counts: listCounts({ trackerAhead: 1, mangaPixerAhead: 1, inSync: 1, notSynced: 1 }) }));
    expect(q(el, 'tracking-pick-a')).toBeNull();
    expect(q(el, 'tracking-select-bar-mal')).toBeNull();

    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    expect(q(el, 'tracking-select-mal').getAttribute('aria-pressed')).toBe('true');
    expect(el.querySelectorAll('[data-testid^="tracking-pick-box-"]').length).toBe(4);
    expect(text(el, 'tracking-selected-count')).toBe('0 selected');
    expect(text(el, 'tracking-batch-why')).toContain('Tick series with the boxes');
    // 1.44.0: the volumes action shows only while a selected series offers it (none here).
    expect(actionOrder(el)).toEqual(['FromTracker', 'ToTracker', 'SyncNow', 'DontSync', 'SyncAgain']);
    for (const a of actionOrder(el)) expect((q(el, `tracking-batch-${a}`) as HTMLButtonElement).disabled).toBe(true);

    for (const id of ['a', 'b', 'c']) tick(el, id);
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('3 selected');
    expect((q(el, 'tracking-pick-box-a') as HTMLInputElement).checked).toBe(true);
    const count = (a: string) => q(el, `tracking-batch-${a}`).querySelector('.n')!.textContent!.trim();
    expect(['FromTracker', 'ToTracker', 'SyncNow', 'DontSync', 'SyncAgain'].map(count)).toEqual(['1', '1', '3', '3', '0']);
    expect(q(el, 'tracking-batch-FromTracker').getAttribute('aria-label')).toBe('Set progress from MyAnimeList to MangaPixer (1)');
    expect((q(el, 'tracking-batch-SyncAgain') as HTMLButtonElement).disabled).toBe(true);
    expect(text(el, 'tracking-batch-why-SyncAgain')).toBe('Sync again: None of the selected is turned off');
    expect(q(el, 'tracking-batch-why-FromTracker')).toBeNull();

    // Only the in-sync one: both Set-progress actions apply to none, each with the rows' own reason.
    tick(el, 'a');
    tick(el, 'b');
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-why-FromTracker')).toContain('None of the selected (MyAnimeList is not ahead of MangaPixer - nothing to bring in)');
    expect(text(el, 'tracking-batch-why-ToTracker')).toContain('MangaPixer is not ahead of MyAnimeList - nothing to send');

    // 1.44.0 (owner): on a phone the reason lines fold behind one toggle (the CSS hides them below 600 px until it is opened).
    const toggle = q(el, 'tracking-batch-why-toggle') as HTMLButtonElement;
    expect(toggle.textContent).toMatch(/Why \d+ actions are off/);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(q(el, 'tracking-batch-why').classList).not.toContain('open');
    toggle.click();
    fixture.detectChanges();
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(q(el, 'tracking-batch-why').classList).toContain('open');

    // Clear empties it; Done leaves Select mode.
    q(el, 'tracking-select-clear').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('0 selected');
    q(el, 'tracking-select-done').click();
    fixture.detectChanges();
    expect(q(el, 'tracking-select-bar-mal')).toBeNull();
    expect(q(el, 'tracking-pick-a')).toBeNull();
  });

  it('a Shift-click ticks every loaded row from the last tick to this one', () => {
    const rows = ['r0', 'r1', 'r2', 'r3', 'r4'].map((id, i) => seriesRow({ seriesId: id, title: `Row ${i}` }));
    const { fixture, el } = create(seriesPage(rows));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    tick(el, 'r1');
    tick(el, 'r3', true);
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('3 selected');
    expect(['r0', 'r1', 'r2', 'r3', 'r4'].map((id) => (q(el, `tracking-pick-box-${id}`) as HTMLInputElement).checked))
      .toEqual([false, true, true, true, false]);
    // A Shift-click upwards keeps the ticked one ticked and adds the range.
    tick(el, 'r0', true);
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('4 selected');
  });

  it('the bar follows the chip: its natural action comes first, and another chip or a search clears the selection', () => {
    const { fixture, http, el } = create(seriesPage([FROM(), TO()], { counts: listCounts({ trackerAhead: 1, mangaPixerAhead: 1, held: 1, notSynced: 1 }) }));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    tick(el, 'a');
    fixture.detectChanges();
    const counts = listCounts({ trackerAhead: 1, mangaPixerAhead: 1, held: 1, notSynced: 1 });
    const cases: [string, TrackerSeriesRowDto, string][] = [
      ['TrackerAhead', FROM(), 'FromTracker'],
      ['Held', TO({ bucket: 'Held' }), 'ToTracker'],
      ['MangaPixerAhead', TO(), 'ToTracker'],
      ['NotSynced', OFF(), 'SyncAgain'],
      ['All', FROM(), 'FromTracker'],
    ];
    for (const [chip, row, first] of cases) {
      q(el, `tracking-chip-${chip}`).click();
      http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([row], { counts }));
      fixture.detectChanges();
      expect(text(el, 'tracking-selected-count')).toBe('0 selected');
      expect(actionOrder(el)[0]).toBe(first);
      expect(q(el, `tracking-batch-${first}`).classList.contains('primary')).toBe(true);
    }
  });

  it('"Select all N in this chip" selects every matching series from the server\'s ids, also beyond the loaded page', () => {
    const rows = Array.from({ length: 50 }, (_, i) => FROM({ seriesId: `t${i}`, title: `Ahead ${String(i).padStart(2, '0')}` }));
    const { fixture, http, el } = create(seriesPage(rows, { totalCount: 51, counts: listCounts({ trackerAhead: 51 }) }));
    q(el, 'tracking-chip-TrackerAhead').click();
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage(rows, { totalCount: 51, counts: listCounts({ trackerAhead: 51 }) }));
    fixture.detectChanges();
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-select-all')).toBe('Select all 51 in this chip');
    q(el, 'tracking-select-all').click();
    const req = http.expectOne((r) => r.url === `${BASE}/series/ids`);
    expect(req.request.params.get('filter')).toBe('TrackerAhead');
    expect(req.request.params.has('q')).toBe(false);
    req.flush({ items: [...rows.map((r) => selectable(r.seriesId)), selectable('t50', { fromTracker: { possible: false, block: 'NotOnList' } })],
      totalCount: 51, truncated: false });
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('51 selected');
    expect(q(el, 'tracking-select-all')).toBeNull();
    expect(q(el, 'tracking-batch-FromTracker').querySelector('.n')!.textContent!.trim()).toBe('50');
  });

  it('a truncated "Select all" says it selected the first part; a search goes with the ids request', () => {
    const { fixture, http, el } = create(seriesPage([FROM()], { totalCount: 2500, counts: listCounts({ trackerAhead: 2500 }) }));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-select-all')).toBe('Select all 2500');
    q(el, 'tracking-select-all').click();
    const req = http.expectOne((r) => r.url === `${BASE}/series/ids`);
    expect(req.request.params.keys()).toEqual([]);
    req.flush({ items: Array.from({ length: 2000 }, (_, i) => selectable(`x${i}`)), totalCount: 2500, truncated: true });
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('2000 selected');
    expect(text(el, 'tracking-notice-mal')).toContain('Selected the first 2000 of 2500 series');
  });

  it('applies in chunks with "N of M…", Cancel stops after the current chunk, one summary, then reload, badge and a cleared selection', async () => {
    const rows = [FROM()];
    const { fixture, http, el, changed } = create(seriesPage(rows, { totalCount: 25, counts: listCounts({ trackerAhead: 25 }) }));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    q(el, 'tracking-select-all').click();
    const ids = Array.from({ length: 25 }, (_, i) => `p${String(i).padStart(2, '0')}`);
    http.expectOne((r) => r.url === `${BASE}/series/ids`).flush({ items: ids.map((id) => selectable(id)), totalCount: 25, truncated: false });
    fixture.detectChanges();

    q(el, 'tracking-batch-FromTracker').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-confirm')).toContain('Mark read in MangaPixer what MyAnimeList counts, for 25 series? Nothing is unmarked.');
    http.expectNone(`${BASE}/entries/batch`);
    q(el, 'tracking-batch-yes').click();

    const first = http.expectOne(`${BASE}/entries/batch`);
    expect(first.request.method).toBe('POST');
    expect(first.request.body).toEqual({ action: 'FromTracker', seriesIds: ids.slice(0, 10) });
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-progress')).toContain('0 of 25…');
    first.flush(result({ done: 9, skipped: 1, marked: 80, alreadyRead: 2, skips: [{ reason: 'LeftAsIs', count: 1 }] }));
    await settle();
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-progress')).toContain('10 of 25…');
    const second = http.expectOne(`${BASE}/entries/batch`);
    expect(second.request.body.seriesIds).toEqual(ids.slice(10, 20));
    q(el, 'tracking-batch-cancel').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-cancel')).toContain('Stopping after this part');
    second.flush(result({ done: 10, marked: 100, alreadyRead: 1 }));
    await settle();
    http.expectNone(`${BASE}/entries/batch`);
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([FROM({ bucket: 'InSync' })], { counts: listCounts({ inSync: 1 }) }));
    fixture.detectChanges();
    expect(text(el, 'tracking-notice-mal')).toBe(
      'Set progress from MyAnimeList for 20 series: 180 files marked, 3 already read, 1 skipped (left as is). Cancelled - 5 series not done.');
    expect(text(el, 'tracking-selected-count')).toBe('0 selected');
    expect(q(el, 'tracking-batch-progress')).toBeNull();
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('stops at the daily cap and says how many were not done', async () => {
    const rows = Array.from({ length: 12 }, (_, i) => TO({ seriesId: `h${i}`, title: `Held ${String(i).padStart(2, '0')}`, bucket: 'Held' }));
    const { fixture, http, el } = create(seriesPage(rows, { counts: listCounts({ held: 12 }) }));
    q(el, 'tracking-chip-Held').click();
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage(rows, { counts: listCounts({ held: 12 }) }));
    fixture.detectChanges();
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    // Everything is loaded: Select all takes the rows without a request.
    q(el, 'tracking-select-all').click();
    http.expectNone((r) => r.url === `${BASE}/series/ids`);
    fixture.detectChanges();
    expect(text(el, 'tracking-selected-count')).toBe('12 selected');
    q(el, 'tracking-batch-ToTracker').click();
    fixture.detectChanges();
    q(el, 'tracking-batch-yes').click();
    http.expectOne(`${BASE}/entries/batch`).flush(result({ done: 4, capReached: true, notReached: 6 }));
    await settle();
    http.expectNone(`${BASE}/entries/batch`);
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage(rows, { counts: listCounts({ held: 8 }) }));
    fixture.detectChanges();
    expect(text(el, 'tracking-notice-mal')).toBe('Set progress from MangaPixer to MyAnimeList for 4 series: 4 sent. '
      + 'Today\'s request limit for MyAnimeList is reached - 8 series not done; try again tomorrow.');
  });

  for (const add of [true, false]) {
    it(`the confirm words the add part from "Add series I start reading" (${add ? 'on' : 'off'}), and counts the selected it leaves as they are`, () => {
      const { fixture, el } = create(seriesPage([TO(), SYNCED()]), { addStartedSeries: add });
      q(el, 'tracking-select-mal').click();
      fixture.detectChanges();
      tick(el, 'b');
      tick(el, 'c');
      fixture.detectChanges();
      q(el, 'tracking-batch-ToTracker').click();
      fixture.detectChanges();
      expect(text(el, 'tracking-batch-confirm')).toContain(add
        ? 'Send MangaPixer\'s progress to MyAnimeList for 1 series? Never lowers; series not on your list are added (Add series I start reading is on). 1 of the selected is left as it is.'
        : 'series not on your list are left out (Add series I start reading is off).');
      q(el, 'tracking-batch-no').click();
      fixture.detectChanges();
      expect(q(el, 'tracking-batch-confirm')).toBeNull();
    });
  }

  it('an account that cannot sync: the tracker actions are off with the reason; the switches still work (no request to the tracker)', async () => {
    const { fixture, http, el } = create(seriesPage([FROM(), SYNCED()]), { canSync: false });
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    tick(el, 'a');
    tick(el, 'c');
    fixture.detectChanges();
    for (const a of ['FromTracker', 'ToTracker', 'SyncNow']) {
      expect((q(el, `tracking-batch-${a}`) as HTMLButtonElement).disabled).toBe(true);
      expect(text(el, `tracking-batch-why-${a}`)).toContain('Your account cannot sync right now');
    }
    q(el, 'tracking-batch-DontSync').click();
    fixture.detectChanges();
    expect(text(el, 'tracking-batch-confirm')).toContain('Stop syncing 2 series with MyAnimeList?');
    q(el, 'tracking-batch-yes').click();
    const req = http.expectOne(`${BASE}/entries/batch`);
    expect(req.request.body).toEqual({ action: 'DontSync', seriesIds: ['a', 'c'] });
    req.flush(result({ done: 2, rows: [OFF({ seriesId: 'a' }), OFF({ seriesId: 'c' })] }));
    await settle();
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([OFF({ seriesId: 'a' }), OFF({ seriesId: 'c' })], { counts: listCounts({ notSynced: 2 }) }));
    fixture.detectChanges();
    expect(text(el, 'tracking-notice-mal')).toBe('Turned sync off for 2 series: 2 turned off.');
  });

  it('a refused chunk stops the apply and says why; what was done before is summed up', async () => {
    const { fixture, http, el } = create(seriesPage([SYNCED()]));
    q(el, 'tracking-select-mal').click();
    fixture.detectChanges();
    tick(el, 'c');
    fixture.detectChanges();
    q(el, 'tracking-batch-SyncNow').click();
    fixture.detectChanges();
    q(el, 'tracking-batch-yes').click();
    http.expectOne(`${BASE}/entries/batch`).flush({ error: 'needs_reconnect', message: 'x' }, { status: 409, statusText: 'Conflict' });
    await settle();
    http.expectOne((r) => r.url === `${BASE}/series`).flush(seriesPage([SYNCED()]));
    fixture.detectChanges();
    expect(text(el, 'tracking-error-mal')).toBe('Connect your account again.');
    expect(q(el, 'tracking-notice-mal')).toBeNull();
  });

  it('the tracker column opens the series on the tracker\'s site (new tab, no referrer), also from "⋯"; none without an id', () => {
    const { fixture, el } = create(seriesPage([SYNCED({ remoteId: '2' }), SYNCED({ seriesId: 'n', title: 'No id', remoteId: null, bucket: 'NoId', state: 'NoId' })]));
    const link = q(el, 'tracking-remote-link-c') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://myanimelist.net/manga/2');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('aria-label')).toBe('Open on MyAnimeList: 12 ch · 2 vol');
    expect(link.querySelector('mat-icon')!.textContent).toContain('open_in_new');
    expect(q(el, 'tracking-remote-c').textContent).toContain('12 ch · 2 vol');
    expect(q(el, 'tracking-remote-link-n')).toBeNull();
    expect(q(el, 'tracking-remote-n')).not.toBeNull();

    q(el, 'tracking-more-c').click();
    fixture.detectChanges();
    const item = document.querySelector('[data-testid="tracking-menu-open-tracker"]') as HTMLAnchorElement;
    expect(item.getAttribute('href')).toBe('https://myanimelist.net/manga/2');
    expect(item.getAttribute('rel')).toBe('noopener noreferrer');
    expect(item.getAttribute('target')).toBe('_blank');
    expect(item.textContent).toContain('Open on MyAnimeList');
  });

  it('MangaUpdates links use the site\'s base36 series address', () => {
    const { el } = create(seriesPage([SYNCED({ tracker: 'mangaupdates', remoteId: '51239621230' })]), { slug: 'mangaupdates' });
    expect(q(el, 'tracking-remote-link-c').getAttribute('href')).toBe(`https://www.mangaupdates.com/series/${(51239621230).toString(36)}`);
  });
});
