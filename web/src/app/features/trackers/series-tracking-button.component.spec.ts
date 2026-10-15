import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

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
    const open = q(pop, 'series-tracking-open-mal') as HTMLAnchorElement;
    expect(open.getAttribute('href')).toBe('/tracking?tracker=mal&series=s1');
    expect(open.getAttribute('href')).not.toContain('Synthetic');
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
});
