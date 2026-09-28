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
    // Holes: count what is there instead of a range that overstates it.
    expect(haveSentence(gap({ lowest: 1, have: 16, unitCount: 2, available: 18, missing: [2, 3], missingCount: 14 }))).toBe('You have 2 volumes (up to 16) of 18 (English)');
    expect(haveSentence(gap({ lowest: 7, have: 8, unitCount: 2, available: 14, missing: [1, 2], missingCount: 6 }))).toBe('You have 2 volumes (up to 8) of 14 (English)');
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
