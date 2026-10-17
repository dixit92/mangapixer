import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';

import { TrackerSummaryDto } from '../../core/api/api-types';
import { SeriesTrackingButtonComponent } from './series-tracking-button.component';
import { TrackerStateService } from './tracker-state.service';
import { seriesRow, summary, trackerAccount } from './trackers.testing';

/**
 * The series header's "Tracking" button and popover (1.43.0 owner redesign): hidden without a connected tracker and for a folder
 * that is no linked series; otherwise per tracker the state chip, MangaPixer against the tracker, the dates and "Open in Tracking"
 * to the series' row. Reads the stored summary only.
 */
describe('SeriesTrackingButtonComponent', () => {
  function create(trackers: TrackerSummaryDto[] | null, nodeId = 'n1') {
    TestBed.configureTestingModule({
      imports: [SeriesTrackingButtonComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    if (trackers) TestBed.inject(TrackerStateService).set({ trackers });
    const fixture = TestBed.createComponent(SeriesTrackingButtonComponent);
    fixture.componentRef.setInput('nodeId', nodeId);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  const overlay = () => document.querySelector('.cdk-overlay-container') as HTMLElement;
  const q = (root: ParentNode | null, id: string) => root?.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('is hidden, and asks nothing, without a connected tracker', () => {
    const { el, http } = create([summary()]);
    http.expectNone('/api/v1/trackers/nodes/n1/summary');
    expect(q(el, 'series-tracking-button')).toBeNull();
  });

  it('is hidden for a folder that is not a linked series', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: null, rows: [] });
    fixture.detectChanges();
    expect(q(el, 'series-tracking-button')).toBeNull();
  });

  it('opens a popover with the state, both sides, the dates and "Open in Tracking" to the series row', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    const row = seriesRow({
      bucket: 'LeftAsIs', state: 'LeftAsIs', remoteStatus: 'Completed', allChapters: 45, chapters: 45, allVolumes: null, volumes: null,
      remoteChapters: 51, remoteVolumes: 10,
    });
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [row] });
    fixture.detectChanges();
    const button = q(el, 'series-tracking-button') as HTMLButtonElement;
    expect(button.textContent).toContain('Tracking');
    expect(button.getAttribute('aria-expanded')).toBe('false');

    button.click();
    // Opening re-reads the summary (a sync may have run since).
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [row] });
    fixture.detectChanges();
    expect(button.getAttribute('aria-expanded')).toBe('true');
    const pop = q(overlay(), 'series-tracking-popover')!;
    expect(pop.getAttribute('role')).toBe('dialog');
    expect(pop.textContent).toContain('MyAnimeList');
    expect(q(pop, 'series-tracking-state-mal')!.textContent).toContain('Left as is · Completed on MyAnimeList');
    expect(q(pop, 'series-tracking-here-mal')!.textContent).toContain('45 of 51 ch');
    expect(q(pop, 'series-tracking-remote-mal')!.textContent).toContain('51 ch · 10 vol · Completed');
    expect(q(pop, 'series-tracking-dates-mal')!.textContent).toContain('Start Oct 1, 2026 · Finish —');
    expect(q(pop, 'series-tracking-ring-legend')!.textContent).toContain('every tracker this series syncs to');
    const open = q(pop, 'series-tracking-open-mal') as HTMLAnchorElement;
    expect(open.getAttribute('href')).toBe('/tracking?tracker=mal&series=s1');
    expect(open.getAttribute('href')).not.toContain('Synthetic');
  });

  it('1.44.0: a folder whose reading is not synced says why (not "nothing read") and turns sync on in place', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    const row = seriesRow({ allChapters: null, chapters: null, allVolumes: null, volumes: null, remoteChapters: 60, remoteVolumes: 10 });
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [row], notSynced: 'PrivateLibrary' });
    fixture.detectChanges();
    (q(el, 'series-tracking-button') as HTMLButtonElement).click();
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [row], notSynced: 'PrivateLibrary' });
    fixture.detectChanges();
    const pop = q(overlay(), 'series-tracking-popover')!;
    expect(q(pop, 'series-tracking-here-mal')!.textContent!.trim()).toBe('not synced');
    expect(q(pop, 'series-tracking-not-synced')!.textContent).toContain('this folder is in one of your Private libraries');

    (q(pop, 'series-tracking-sync-on') as HTMLButtonElement).click();
    const put = http.expectOne('/api/v1/trackers/sync-overrides');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ nodeIds: ['n1'], enabled: true });
    put.flush([]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [row], notSynced: null });
    fixture.detectChanges();
    expect(q(pop, 'series-tracking-not-synced')).toBeNull();
    expect(q(pop, 'series-tracking-sync-on-done')!.textContent).toContain('the next time you read here');
    expect(q(pop, 'series-tracking-here-mal')!.textContent!.trim()).toBe('nothing read');
  });

  it('shows one block per connected tracker and no dates for MangaUpdates', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() }),
      summary({ slug: 'mangaupdates', name: 'MangaUpdates', account: trackerAccount({ tracker: 'mangaupdates' }) })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow(),
      seriesRow({ tracker: 'mangaupdates', dates: { ...seriesRow().dates, supported: false } })] });
    fixture.detectChanges();
    (q(el, 'series-tracking-button') as HTMLButtonElement).click();
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow(),
      seriesRow({ tracker: 'mangaupdates', dates: { ...seriesRow().dates, supported: false } })] });
    fixture.detectChanges();
    expect(q(overlay(), 'series-tracking-row-mal')).not.toBeNull();
    expect(q(overlay(), 'series-tracking-row-mangaupdates')).not.toBeNull();
    expect(q(overlay(), 'series-tracking-dates-mangaupdates')).toBeNull();
  });

  it('1.43.2: the tracker\'s numbers open the series on its site - a new tab, no referrer; none without an id', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() }),
      summary({ slug: 'mangaupdates', name: 'MangaUpdates', account: trackerAccount({ tracker: 'mangaupdates' }) })]);
    const rows = [seriesRow({ remoteId: '2' }), seriesRow({ tracker: 'mangaupdates', remoteId: null, bucket: 'NoId', state: 'NoId',
      dates: { ...seriesRow().dates, supported: false } })];
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows });
    fixture.detectChanges();
    (q(el, 'series-tracking-button') as HTMLButtonElement).click();
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows });
    fixture.detectChanges();
    const link = q(overlay(), 'series-tracking-remote-link-mal') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://myanimelist.net/manga/2');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('aria-label')).toContain('Open on MyAnimeList');
    expect(link.textContent).toContain('12 ch · 2 vol');
    expect(q(overlay(), 'series-tracking-remote-link-mangaupdates')).toBeNull();
    expect(q(overlay(), 'series-tracking-remote-mangaupdates')!.textContent).toContain('12 ch');
  });

  it('another folder drops the previous folder\'s summary until its own arrives', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    expect(q(el, 'series-tracking-button')).not.toBeNull();
    fixture.componentRef.setInput('nodeId', 'n2');
    fixture.detectChanges();
    expect(q(el, 'series-tracking-button')).toBeNull();
    http.expectOne('/api/v1/trackers/nodes/n2/summary').flush({ seriesId: null, rows: [] });
    fixture.detectChanges();
    expect(q(el, 'series-tracking-button')).toBeNull();
  });

  function openOn(fixture: ReturnType<typeof create>['fixture'], el: HTMLElement, http: HttpTestingController, id: string) {
    (q(el, 'series-tracking-button') as HTMLButtonElement).click();
    http.expectOne(`/api/v1/trackers/nodes/${id}/summary`).flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    expect(q(overlay(), 'series-tracking-popover')).not.toBeNull();
  }

  it('closes the popover when the folder changes: the next series\' button starts closed (owner review, iPad)', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    openOn(fixture, el, http, 'n1');
    fixture.componentRef.setInput('nodeId', 'n2');
    fixture.detectChanges();
    http.expectOne('/api/v1/trackers/nodes/n2/summary').flush({ seriesId: 's2', rows: [seriesRow({ seriesId: 's2' })] });
    fixture.detectChanges();
    const button = q(el, 'series-tracking-button') as HTMLButtonElement;
    expect(button).not.toBeNull();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(q(overlay(), 'series-tracking-popover')).toBeNull();
  });

  it('closes the popover on any navigation, and when the button goes away', async () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    openOn(fixture, el, http, 'n1');
    await TestBed.inject(Router).navigateByUrl('/somewhere-else').catch(() => undefined);
    fixture.detectChanges();
    expect(q(overlay(), 'series-tracking-popover')).toBeNull();

    openOn(fixture, el, http, 'n1');
    fixture.destroy();
    expect(q(overlay(), 'series-tracking-popover')).toBeNull();
  });

  it('UX5: opening moves the focus into the popover, a named dialog; Escape closes it and gives the focus back to the button', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    const button = q(el, 'series-tracking-button') as HTMLButtonElement;
    button.focus();
    openOn(fixture, el, http, 'n1');
    const pop = q(overlay(), 'series-tracking-popover')!;
    expect(pop.contains(document.activeElement)).toBe(true);
    expect(document.activeElement).toBe(q(pop, 'series-tracking-open-mal'));
    expect(pop.getAttribute('role')).toBe('dialog');
    expect(document.getElementById(pop.getAttribute('aria-labelledby') ?? '')?.textContent).toBe('Tracking for this series');

    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', keyCode: 27, bubbles: true } as KeyboardEventInit));
    fixture.detectChanges();
    expect(q(overlay(), 'series-tracking-popover')).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  it('UX10: a press beside the open popover lands on a transparent backdrop: it only closes the popover (focus back on the button)', () => {
    const { fixture, el, http } = create([summary({ account: trackerAccount() })]);
    http.expectOne('/api/v1/trackers/nodes/n1/summary').flush({ seriesId: 's1', rows: [seriesRow()] });
    fixture.detectChanges();
    openOn(fixture, el, http, 'n1');
    const backdrop = overlay().querySelector('.cdk-overlay-backdrop') as HTMLElement | null;
    expect(backdrop).not.toBeNull();
    expect(backdrop!.classList).toContain('cdk-overlay-transparent-backdrop');
    backdrop!.click();
    fixture.detectChanges();
    const button = q(el, 'series-tracking-button') as HTMLButtonElement;
    expect(q(overlay(), 'series-tracking-popover')).toBeNull();
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button);
    // The button opens it again.
    openOn(fixture, el, http, 'n1');
  });
});
