import { IdentifyPreviewDto } from '../../../core/api/api-types';
import { candidateLine, previewLine, retryLabel, scorePercent, tallStripsLabel } from './identify-labels';

/** Identify dialog display helpers (1.24.0, lane B2). */
describe('identify labels', () => {
  it('builds a candidate line from type, a distinct origin and the year', () => {
    expect(candidateLine({ providerType: 'Manga', origin: 'Japan', year: 1989 })).toBe('Manga · Japan · 1989');
    expect(candidateLine({ providerType: 'Novel', origin: null, year: null })).toBe('Novel');
    expect(candidateLine({})).toBe('');
  });

  it('builds a preview line with volumes and status', () => {
    const p = { format: 'Comic', origin: 'Korea', startYear: 2018, originVolumes: 15, originStatus: 'Complete' } as IdentifyPreviewDto;
    expect(previewLine(p)).toBe('Comic · Korea · 2018 · 15 vols, complete');
    expect(previewLine({ ...p, originVolumes: 1, originStatus: null })).toBe('Comic · Korea · 2018 · 1 vol');
    expect(previewLine({ ...p, originVolumes: null, originStatus: 'Ongoing' })).toBe('Comic · Korea · 2018 · ongoing');
  });

  it('describes the local tall-strip signal', () => {
    expect(tallStripsLabel(true)).toContain('tall strips');
    expect(tallStripsLabel(false)).toBe('regular pages');
    expect(tallStripsLabel(null)).toBe('not measured yet');
  });

  it('formats a retry time and clamps scores', () => {
    expect(retryLabel(null)).toBe('');
    expect(retryLabel('not a date')).toBe('');
    expect(retryLabel('2026-09-25T14:05:00Z')).toMatch(/^Try again after /);
    expect(scorePercent(0.934)).toBe(93);
    expect(scorePercent(1.4)).toBe(100);
    expect(scorePercent(-1)).toBe(0);
  });
});
