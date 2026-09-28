import { compactNumbers, gapDetail, haveSentence, noVerdictReason } from './missing-labels';
import { gap, missingRow } from './missing.testing';

describe('missing report labels', () => {
  it('compacts runs of numbers', () => {
    expect(compactNumbers([1, 2, 3, 7, 9, 10])).toBe('1-3, 7, 9-10');
    expect(compactNumbers([])).toBe('');
  });

  it('says what you have against which total', () => {
    expect(haveSentence(gap())).toBe('You have volumes 1-7 of 10 (English)');
    expect(haveSentence(gap({ kind: 'Chapter', lowest: 21, have: 40, available: 195, source: 'Origin' })))
      .toBe('You have chapters 21-40 of 195 (original run)');
    expect(haveSentence(gap({ lowest: 1, have: 1, available: null, source: null }))).toBe('You have volume 1; no total known');
  });

  it('lists behind and holes, capped', () => {
    expect(gapDetail(gap())).toBe('3 behind');
    expect(gapDetail(gap({ behindBy: 0, missing: [3, 4], missingCount: 2 }))).toBe('missing 3-4');
    expect(gapDetail(gap({ behindBy: 2, missing: [3], missingCount: 13 }))).toBe('2 behind · missing 3, +12 more');
    expect(gapDetail(gap({ behindBy: 0 }))).toBe('');
  });

  it('explains a missing verdict', () => {
    expect(noVerdictReason(missingRow({ verdict: 'Mixed' }))).toContain('mixed');
    expect(noVerdictReason(missingRow())).toBeNull();
  });
});
