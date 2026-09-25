import { seriesInfo } from './series-info.testing';
import { ageLabel, creditGroups, hasSeriesContent, itemLine, metaLine, precedenceLabel, roleLabel } from './series-info-labels';

/** Pure label helpers for the series-metadata surfaces (1.24.0). */
describe('series-info-labels', () => {
  it('builds the facts line and skips empty parts', () => {
    const info = seriesInfo({ origin: 'Japan', format: 'Comic', startYear: 1989, originStatus: 'Ongoing', originVolumes: 43 });
    expect(metaLine(info)).toBe('Japan · 1989 · Ongoing, 43 vols');
    expect(metaLine(seriesInfo())).toBe('');
  });

  it('names non-comic formats and the webtoon flag', () => {
    const info = seriesInfo({ origin: 'Korea', format: 'Novel', webtoon: true, originVolumes: 1 });
    expect(metaLine(info)).toBe('Korea · Novel · 1 vol · Webtoon');
  });

  it('describes the effective precedence and its source', () => {
    expect(precedenceLabel({ precedence: 'ComicInfoFirst', precedenceSource: 'Folder' })).toBe('ComicInfo first (set on a folder)');
    expect(precedenceLabel({ precedence: 'WebFirst', precedenceSource: 'Default' })).toBe('Web first (default)');
  });

  it('formats an item line from ComicInfo numbers', () => {
    expect(itemLine({ volume: 3, number: '12.5', year: 2020 })).toBe('Vol 3 · #12.5 · 2020');
    expect(itemLine({ volume: 0 })).toBe('Vol 0');
    expect(itemLine(null)).toBe('');
  });

  it('turns a fetch time into a relative age', () => {
    const now = Date.parse('2026-09-25T12:00:00Z');
    expect(ageLabel('2026-09-25T08:00:00Z', now)).toBe('today');
    expect(ageLabel('2026-09-24T08:00:00Z', now)).toBe('1 day ago');
    expect(ageLabel('2026-09-20T12:00:00Z', now)).toBe('5 days ago');
    expect(ageLabel(null, now)).toBe('');
    expect(ageLabel('not a date', now)).toBe('');
  });

  it('knows which states carry content', () => {
    expect(hasSeriesContent(seriesInfo({ state: 'Web' }))).toBe(true);
    expect(hasSeriesContent(seriesInfo({ state: 'Mixed' }))).toBe(true);
    expect(hasSeriesContent(seriesInfo({ state: 'None' }))).toBe(false);
    expect(hasSeriesContent(seriesInfo({ state: 'DontMatch' }))).toBe(false);
    expect(hasSeriesContent(null)).toBe(false);
  });

  it('groups credits story-first, de-duplicating names', () => {
    const groups = creditGroups([
      { name: 'Artist A', role: 'penciller' },
      { name: 'Writer A', role: 'writer' },
      { name: 'Writer A', role: 'writer' },
      { name: 'Inker A', role: 'inker' },
    ]);
    expect(groups).toEqual([
      { label: 'Story', names: ['Writer A'] },
      { label: 'Art', names: ['Artist A'] },
      { label: 'Inks', names: ['Inker A'] },
    ]);
    expect(creditGroups(null)).toEqual([]);
  });

  it('labels creator roles', () => {
    expect(roleLabel('writer')).toBe('Story');
    expect(roleLabel('penciller')).toBe('Art');
    expect(roleLabel('something-new')).toBe('Other');
  });
});
