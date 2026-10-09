import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { DeclaredFactsApiService } from '../declared/declared-facts-api.service';
import { MissingReportApiService } from './missing-report-api.service';
import { SeriesMissingLineComponent } from './series-missing-line.component';
import { gap, missingRow, ownerProgress } from './missing.testing';

describe('SeriesMissingLineComponent', () => {
  function create(response: ReturnType<typeof missingRow> | 'error', admin = false) {
    const api = { forNodeViewer: vi.fn(() => (response === 'error' ? throwError(() => ({ status: 404 })) : of(response))) };
    TestBed.configureTestingModule({
      imports: [SeriesMissingLineComponent],
      providers: [
        provideRouter([]),
        { provide: MissingReportApiService, useValue: api },
        { provide: DeclaredFactsApiService, useValue: { version: signal(0) } },
      ],
    });
    const fixture = TestBed.createComponent(SeriesMissingLineComponent);
    fixture.componentRef.setInput('nodeId', 'series-1');
    fixture.componentRef.setInput('showReportLink', admin);
    fixture.detectChanges();
    return { api, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the volumes and chapters lines for the node', () => {
    const { api, el } = create(missingRow({ chapters: gap({ kind: 'Chapter', lowest: 61, have: 70, available: 80, behindBy: 10 }) }));
    expect(api.forNodeViewer).toHaveBeenCalledWith('series-1'); // the viewer endpoint: readers see it too
    const line = el.querySelector('[data-testid="series-missing-line"]')!;
    expect(line.textContent).toContain('You have volumes 1-7 of 10 (English)');
    expect(line.textContent).toContain('You have chapters 61-70 of 80 (English) · 10 behind');
  });

  it('links to the admin report only for admins', () => {
    const row = missingRow({});
    expect(create(row).el.querySelector('a.more')).toBeNull();
    TestBed.resetTestingModule();
    expect(create(row, true).el.querySelector('a.more')?.textContent).toContain('Missing report');
  });

  it('renders nothing without a row or without numbers', () => {
    expect(create('error').el.querySelector('[data-testid="series-missing-line"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create(missingRow({ verdict: 'NoUnits', volumes: null })).el.querySelector('[data-testid="series-missing-line"]')).toBeNull();
  });

  it('1.30.0: shows the progress lines and the completion mark when the row carries the progress', () => {
    const { el } = create(missingRow({ progress: ownerProgress({ upgradeVolumes: [], upgradeCount: 0, missingVolumes: 1, completion: 'FinishedNotHeld',
      completionBasis: 'OfficialVolumes', completionTarget: 15, completionHeld: 14 }) }));
    expect(el.querySelector('[data-testid="series-line-trackers"]')!.textContent).toContain('English (Synthetic Press): 15 volumes, ongoing');
    expect(el.querySelector('[data-testid="series-line-folder"]')!.textContent)
      .toBe('You have volumes 1-14 + chapters 43-57 · 1 volume missing · finished - missing some (Official, 14 of 15)');
    expect(el.querySelector('[data-testid="completion-mark"]')!.textContent).toContain('Finished - missing some (Official, 14 of 15)');
  });

  it('1.31.0: names duplicated chapter numbers under the progress or gap lines, and shows them alone', () => {
    const dup = [{ kind: 'Chapter' as const, number: '1', files: 2 }, { kind: 'Volume' as const, number: '3', files: 2 }];
    const withGap = create(missingRow({ duplicates: dup, duplicateCount: 2 })).el;
    expect(withGap.querySelector('[data-testid="series-line-duplicates"]')!.textContent)
      .toBe('1 duplicate chapter, 1 duplicate volume: Chapter 1: 2 files, Volume 3: 2 files');
    TestBed.resetTestingModule();
    // No gap to report (no volumes, no chapters) but a duplicate: the line still shows.
    const alone = create(missingRow({ verdict: 'UpToDate', volumes: null, chapters: null, duplicates: dup.slice(0, 1), duplicateCount: 1 })).el;
    expect(alone.querySelector('[data-testid="series-line-duplicates"]')!.textContent).toBe('1 duplicate chapter: Chapter 1: 2 files');
    TestBed.resetTestingModule();
    expect(create(missingRow({ duplicates: [], duplicateCount: 0 })).el.querySelector('[data-testid="series-line-duplicates"]')).toBeNull();
  });
});
