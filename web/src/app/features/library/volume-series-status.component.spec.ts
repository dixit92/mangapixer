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
    expect(seriesStatusLine(view())).toBe('Ongoing - up to date');
    expect(seriesStatusLine(view({ seriesStatus: 'Complete' }))).toBe('Complete - up to date');
    expect(seriesStatusLine(view({ releaseKnown: false }))).toBe('Ongoing');
  });

  it('counts missing volumes and chapters', () => {
    expect(seriesStatusLine(view({ missingVolumes: 2, missingChapters: 3 }))).toBe('Ongoing - 2 volumes, 3 chapters missing');
    expect(seriesStatusLine(view({ missingVolumes: 1, releaseKnown: false }))).toBe('Ongoing - 1 volume missing');
    expect(seriesStatusLine(view({ seriesStatus: 'Hiatus', missingChapters: 1 }))).toBe('On hiatus - 1 chapter missing');
  });

  it('shows nothing without a series link, and no status word for an unknown status', () => {
    expect(seriesStatusLine(view({ hasSeriesStatus: false }))).toBeNull();
    expect(seriesStatusLine(null)).toBeNull();
    expect(seriesStatusLine(view({ seriesStatus: 'Unknown' }))).toBe('up to date');
    expect(seriesStatusLine(view({ seriesStatus: null, releaseKnown: false }))).toBeNull();
  });

  it('names the preferred language in the hint', () => {
    expect(languageName('fr')).toBe('French');
    expect(languageName(null)).toBe('');
    const fixture = TestBed.createComponent(VolumeSeriesStatusComponent);
    fixture.componentRef.setInput('view', view({ language: 'fr', missingVolumes: 1 }));
    fixture.detectChanges();
    const line = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="series-status"]')!;
    expect(line.textContent).toContain('Ongoing - 1 volume missing');
    expect(line.getAttribute('title')).toBe('Missing means released in French, your preferred language.');
    expect(line.classList.contains('missing')).toBe(true);
  });
});
