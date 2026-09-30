import { TestBed } from '@angular/core/testing';

import { VolumeViewDto } from '../../core/api/api-types';
import { VolumeSeriesStatusComponent, languageName, seriesStatusLine } from './volume-series-status.component';

/** The series status line of the Volumes view (1.29.0 RC): status + what is missing against the preferred language. */
describe('series status line', () => {
  function view(over: Partial<VolumeViewDto> = {}): VolumeViewDto {
    return {
      nodeId: 'f1', available: true, active: true, consolidated: false, stackCount: 0, hasSeriesStatus: true,
      seriesStatus: 'Ongoing', missingVolumes: 0, missingChapters: 0, releaseKnown: true, language: 'en', ...over,
    };
  }

  it('says up to date only when what is released is known', () => {
    expect(seriesStatusLine(view())).toBe('Ongoing · up to date');
    expect(seriesStatusLine(view({ seriesStatus: 'Complete' }))).toBe('Complete · up to date');
    expect(seriesStatusLine(view({ releaseKnown: false }))).toBe('Ongoing');
  });

  it('counts missing volumes and chapters', () => {
    expect(seriesStatusLine(view({ missingVolumes: 2, missingChapters: 3 }))).toBe('Ongoing · 2 volumes, 3 chapters missing');
    expect(seriesStatusLine(view({ missingVolumes: 1, releaseKnown: false }))).toBe('Ongoing · 1 volume missing');
    expect(seriesStatusLine(view({ seriesStatus: 'Hiatus', missingChapters: 1 }))).toBe('On hiatus · 1 chapter missing');
  });

  it('shows nothing without a series link, and no status word for an unknown status', () => {
    expect(seriesStatusLine(view({ hasSeriesStatus: false }))).toBeNull();
    expect(seriesStatusLine(null)).toBeNull();
    expect(seriesStatusLine(view({ seriesStatus: 'Unknown' }))).toBe('up to date');
    expect(seriesStatusLine(view({ seriesStatus: null, releaseKnown: false }))).toBeNull();
  });

  it('names the country of origin and what is out in the preferred language', () => {
    expect(seriesStatusLine(view({ seriesStatus: 'Complete', origin: 'Japan', originVolumes: 14, releasedVolumes: 12, missingVolumes: 2 })))
      .toBe('Complete (Japan) · English: 12 of 14 volumes · 2 volumes missing');
    expect(seriesStatusLine(view({ seriesStatus: 'Complete', origin: 'Japan', originVolumes: 14, releasedVolumes: 14 })))
      .toBe('Complete (Japan) · English: complete · up to date');
    expect(seriesStatusLine(view({ origin: 'Korea', releasedVolumes: 3 }))).toBe('Ongoing (Korea) · English: 3 volumes · up to date');
    // Not licensed: MangaUpdates' scanlation status, else "not licensed".
    expect(seriesStatusLine(view({ seriesStatus: 'Complete', origin: 'Japan', licensed: false, scanlationComplete: false, missingChapters: 1 })))
      .toBe('Complete (Japan) · English scanlation: ongoing · 1 chapter missing');
    expect(seriesStatusLine(view({ origin: 'Japan', licensed: false, releaseKnown: false }))).toBe('Ongoing (Japan) · English: not licensed');
    // Another language: the chapters MangaDex lists as released in it; an "other" origin names no place.
    expect(seriesStatusLine(view({ language: 'fr', origin: 'Other', releasedChapter: 87 }))).toBe('Ongoing · French: up to chapter 87 · up to date');
  });

  it('names the preferred language in the hint', () => {
    expect(languageName('fr')).toBe('French');
    expect(languageName(null)).toBe('');
    const fixture = TestBed.createComponent(VolumeSeriesStatusComponent);
    fixture.componentRef.setInput('view', view({ language: 'fr', missingVolumes: 1 }));
    fixture.detectChanges();
    const line = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="series-status"]')!;
    expect(line.textContent).toContain('Ongoing · 1 volume missing');
    expect(line.getAttribute('title')).toBe('Missing means released in French, your preferred language.');
    expect(line.classList.contains('missing')).toBe(true);
  });

  it('1.30.0: shows the two progress lines and the completion icon when the server sends the progress', () => {
    const fixture = TestBed.createComponent(VolumeSeriesStatusComponent);
    fixture.componentRef.setInput('view', view({
      progress: {
        trackers: { language: 'en', origin: 'Japan', originStatus: 'Complete', originVolumes: 14, officialPublisher: 'Viz Media', officialVolumes: 14,
          officialStatus: 'Complete' },
        reach: { volumeFiles: [{ from: 1, to: 14 }], chapters: [], overlapChapters: 0, resolution: 'FileNames' },
        missingVolumes: 0, missingChapters: 0, releaseKnown: true, upgradeVolumes: [], upgradeCount: 0,
        completion: 'CompleteCollection', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 14,
      },
    }));
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="series-trackers"]')!.textContent).toBe('Complete (Japan): 14 volumes · English (Viz Media): 14 volumes, complete');
    expect(el.querySelector('[data-testid="series-folder"]')!.textContent).toBe('You have volumes 1-14 · Complete collection');
    expect(el.querySelector('mat-icon')!.textContent).toBe('workspace_premium');
    expect(el.querySelector('.complete')).not.toBeNull();
  });
});
