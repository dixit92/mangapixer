import { compactNumbers, gapDetail, haveSentence, noVerdictReason, totalTooltip } from './missing-labels';
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
    expect(noVerdictReason(missingRow({ verdict: 'Restarts' }))).toContain('starts again');
    expect(noVerdictReason(missingRow({ verdict: 'NoUnits' }))).toContain('chapter 0 alone');
    expect(noVerdictReason(missingRow())).toBeNull();
  });
});

describe('missing labels, preferred language (1.29.0 RC)', () => {
  const base = { kind: 'Volume' as const, archiveCount: 3, unitCount: 3, lowest: 1, have: 3, behindBy: 0, missing: [], missingCount: 0 };

  it('shows the origin total as context when no total is known in the preferred language', () => {
    expect(haveSentence({ ...base, available: null, source: null, originTotal: 14 })).toBe('You have volumes 1-3; no total known (original run: 14)');
  });

  it('names a released-in-your-language total and explains it in the tooltip', () => {
    const gap = { ...base, kind: 'Chapter' as const, available: 5, source: 'Released' as const, confidence: 'Medium' as const, behindBy: 2 };
    expect(haveSentence(gap)).toBe('You have chapters 1-3 of 5 (released in your language)');
    expect(totalTooltip(gap)).toContain('released in your preferred language');
    expect(totalTooltip({ ...gap, source: 'English', confidence: 'High' })).toBe('Total from the English publisher total');
  });
});
