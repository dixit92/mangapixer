import { BUCKET_ORDER, bucketLabel, bucketToken, chipCount, chipText, chipTitle, filterParam, isFilterParam } from './info-labels';
import { chip } from './info.testing';

/** Info panel labels (1.45.0): the bucket order and words, the count-of-1 rule, the filter parameter. */
describe('info labels', () => {
  it('keeps the owner\'s bucket order: creators first, About before custom tags', () => {
    expect(BUCKET_ORDER).toEqual(['Creators', 'Series', 'Magazine', 'Publisher', 'Year', 'Genres', 'Type', 'About', 'Custom']);
    expect(bucketLabel('Year')).toBe('Year started');
  });

  it('builds the f parameter with the typed-syntax word of the bucket', () => {
    expect(filterParam('Creators', 'fv_1a')).toBe('creator:fv_1a');
    expect(filterParam('Genres', 'fv_2')).toBe('genre:fv_2');
    expect(bucketToken('Year')).toBe('year');
    expect(isFilterParam('creator:fv_1a')).toBe(true);
    expect(isFilterParam('creator:')).toBe(false);
    expect(isFilterParam('creator: x')).toBe(false);
    expect(isFilterParam('fv_1a')).toBe(false);
  });

  it('shows a count only from 2, "(n)" on a work and "n here" on a container', () => {
    expect(chipCount(chip({ count: 1 }), false)).toBeNull();
    expect(chipCount(chip({ count: null }), true)).toBeNull();
    expect(chipCount(chip({ count: 3 }), false)).toBe('(3)');
    expect(chipCount(chip({ count: 12 }), true)).toBe('12 here');
  });

  it('names an About chip "Fan works of <series>" and spells the whole chip for a screen reader', () => {
    expect(chipText('About', chip({ display: 'Starlight' }))).toBe('Fan works of Starlight');
    expect(chipTitle('Creators', chip({ display: 'A', role: 'Writer', count: 3 }), false)).toBe('A, Story, 3 works');
    expect(chipTitle('Genres', chip({ display: 'Comedy', count: 5 }), true)).toBe('Comedy, 5 works here');
    expect(chipTitle('Type', chip({ display: 'Manhwa', conflict: true, sources: ['Web'] }), false))
      .toBe('Manhwa, Conflict: from the web record, the folder declares otherwise');
  });
});
