import {
  BUCKET_ORDER, bucketLabel, bucketToken, chipCount, chipText, chipTitle, filterParam, filterValueId, isFilterParam, locationText, roleLabel,
} from './info-labels';
import { chip } from './info.testing';

/** Info panel labels (1.45.0): the bucket order and words, the count-of-1 rule, roles, the filter parameter, places. */
describe('info labels', () => {
  it('keeps the owner\'s bucket order: creators first, About before custom tags', () => {
    expect(BUCKET_ORDER).toEqual(['Creators', 'Series', 'Magazine', 'Publisher', 'Year', 'Genres', 'Type', 'About', 'Custom']);
    expect(bucketLabel('Year')).toBe('Year started');
  });

  it('builds and reads the f parameter with lane Q\'s bucket tokens', () => {
    expect(filterParam('Creators', 'fv_1a')).toBe('creator:fv_1a');
    expect(filterParam('Genres', 'fv_2')).toBe('genre:fv_2');
    expect(bucketToken('Custom')).toBe('custom');
    expect(filterValueId('creator:fv_1a')).toBe('fv_1a');
    expect(isFilterParam('creator:fv_1a')).toBe(true);
    expect(isFilterParam('creator:')).toBe(false);
    expect(isFilterParam('creator: x')).toBe(false);
    expect(isFilterParam('fv_1a')).toBe(false);
  });

  it('shows a count only from 2: "(n)" on a work, "n here" (works below) on a container', () => {
    expect(chipCount(chip({ count: 1 }), false)).toBeNull();
    expect(chipCount(chip({ count: 9, here: 1 }), true)).toBeNull();
    expect(chipCount(chip({ count: 3 }), false)).toBe('(3)');
    expect(chipCount(chip({ count: 30, here: 12 }), true)).toBe('12 here');
  });

  it('reads the role flags', () => {
    expect(roleLabel('Writer')).toBe('Story');
    expect(roleLabel('Artist')).toBe('Art');
    expect(roleLabel('Writer, Artist')).toBe('Story & art');
    expect(roleLabel('None')).toBeNull();
    expect(roleLabel(undefined)).toBeNull();
  });

  it('names an About chip "Fan works of <series>" and spells the whole chip for a screen reader', () => {
    expect(chipText('About', chip({ display: 'Starlight' }))).toBe('Fan works of Starlight');
    expect(chipTitle('Creators', chip({ display: 'A', roles: 'Writer', count: 3 }), false)).toBe('A, Story, 3 works');
    expect(chipTitle('Genres', chip({ display: 'Comedy', here: 5 }), true)).toBe('Comedy, 5 works here');
    expect(chipTitle('Type', chip({ display: 'Manhwa', conflict: true }), false))
      .toBe('Manhwa, Conflict: the folder\'s declared value and the web record disagree');
  });

  it('writes a place as the library and its folders, display names only', () => {
    expect(locationText({ nodeId: 'f2', libraryId: 'L', libraryName: 'Manga', trail: [{ id: 'a', displayName: 'A' }, { id: 'f2', displayName: 'B' }] }))
      .toBe('Manga › A › B');
  });
});
