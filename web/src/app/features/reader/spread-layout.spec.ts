import {
  groupSpreads, fallbackSpreadStarts, normalizeSpreadStarts, isShiftedSpread, shiftSpreadAt, ensureSpreadStart,
} from './spread-layout';

/**
 * 1.23.0 shifted pairing, as concrete page sequences. Pages are zero-based; `W`
 * marks a wide (stitched two-page) page. Groups are written the way a reader sees
 * the screens, e.g. [0],[1,2],[3,4].
 */

/** A wide-page predicate from a list of wide indices. */
const wideAt = (...wide: number[]) => (i: number) => wide.includes(i);
const noWide = wideAt();
const group = (n: number, isWide: (i: number) => boolean, starts: number[]) => groupSpreads(n, isWide, new Set(starts));
/** Shifted flag for every screen, in order. */
const shiftedFlags = (n: number, isWide: (i: number) => boolean, starts: number[]) =>
  group(n, isWide, starts).map((g) => isShiftedSpread(n, isWide, g));

describe('groupSpreads', () => {
  it('no forced starts pairs from the first page; an odd last page is alone', () => {
    expect(group(7, noWide, [])).toEqual([[0, 1], [2, 3], [4, 5], [6]]);
  });

  it('S = {1} is the old "offset cover": cover alone, then 1-2, 3-4', () => {
    expect(group(6, noWide, [1])).toEqual([[0], [1, 2], [3, 4], [5]]);
  });

  it('a wide page is solo and restarts the cadence (the pre-1.23 bug: both modes then pair alike)', () => {
    // Pages 0..9 with page 4 wide.
    expect(group(10, wideAt(4), [])).toEqual([[0, 1], [2, 3], [4], [5, 6], [7, 8], [9]]);
    expect(group(10, wideAt(4), [1])).toEqual([[0], [1, 2], [3], [4], [5, 6], [7, 8], [9]]);
  });

  it('a mid-archive forced start fixes pairing after a wide page (a credits page inserted at 5)', () => {
    // Pages 5.. were off by one after the stitched spread at 4: force 6 to start.
    expect(group(10, wideAt(4), [6])).toEqual([[0, 1], [2, 3], [4], [5], [6, 7], [8, 9]]);
  });

  it('a forced start right after a wide page is redundant (the wide page already restarts)', () => {
    expect(group(8, wideAt(3), [4])).toEqual(group(8, wideAt(3), []));
  });

  it('a forced start right before a wide page leaves both of its neighbours solo', () => {
    expect(group(8, wideAt(4), [3])).toEqual([[0, 1], [2], [3], [4], [5, 6], [7]]);
  });

  it('a forced start at the last page puts the last page alone', () => {
    expect(group(6, noWide, [5])).toEqual([[0, 1], [2, 3], [4], [5]]);
  });

  it('a wide cover is solo whatever S says; the device fallback then adds nothing', () => {
    expect(fallbackSpreadStarts(5, wideAt(0), true)).toEqual([]);
    expect(group(5, wideAt(0), [])).toEqual([[0], [1, 2], [3, 4]]);
  });

  it('empty and one-page archives', () => {
    expect(group(0, noWide, [])).toEqual([]);
    expect(group(1, noWide, [1])).toEqual([[0]]);
    expect(fallbackSpreadStarts(1, noWide, true)).toEqual([]);
  });
});

describe('fallbackSpreadStarts / normalizeSpreadStarts', () => {
  it('device fallback: cover alone on -> {1}, off -> {}', () => {
    expect(fallbackSpreadStarts(6, noWide, true)).toEqual([1]);
    expect(fallbackSpreadStarts(6, noWide, false)).toEqual([]);
  });

  it('no saved layout stays null (fallback applies); [] stays an explicit "no shifts"', () => {
    expect(normalizeSpreadStarts(null, 10)).toBeNull();
    expect(normalizeSpreadStarts(undefined, 10)).toBeNull();
    expect(normalizeSpreadStarts([], 10)).toEqual([]);
  });

  it('drops out-of-range, duplicate and non-integer entries and sorts', () => {
    expect(normalizeSpreadStarts([9, 0, 3, 3, 12, -1, 2.5, 1], 10)).toEqual([1, 3, 9]);
  });
});

describe('isShiftedSpread (which double-page entry is highlighted)', () => {
  it('default pairing is never shifted; the old cover offset is shifted on the first run only', () => {
    expect(shiftedFlags(6, noWide, [])).toEqual([false, false, false]);
    expect(shiftedFlags(6, noWide, [1])).toEqual([true, true, true, true]);
    // After a wide page the offset no longer applies: 5-6, 7-8 are default pairs.
    expect(shiftedFlags(9, wideAt(4), [1])).toEqual([true, true, true, false, false, false]);
  });

  it('a mid-archive shift marks only the re-paired spreads', () => {
    // [0,1],[2],[3,4],[5,6],[7]
    expect(shiftedFlags(8, noWide, [3])).toEqual([false, true, true, true, true]);
  });

  it('wide pages are never shifted', () => {
    expect(isShiftedSpread(6, wideAt(2), [2])).toBe(false);
  });
});

describe('shiftSpreadAt (the other double-page entry / the o key)', () => {
  it('on page 0 it is exactly the old cover offset, both ways', () => {
    const on = shiftSpreadAt(6, noWide, [], 0)!;
    expect(on).toEqual({ starts: [1], anchor: 0 });
    expect(group(6, noWide, on.starts)).toEqual([[0], [1, 2], [3, 4], [5]]);
    expect(shiftSpreadAt(6, noWide, [1], 0)).toEqual({ starts: [], anchor: 0 });
  });

  it('after a spread it re-pairs from the current spread to the next wide page (the reported bug)', () => {
    // 0..11, wide at 4. Reading [7,8] which should be [8,9]: shift there.
    const n = 12, w = wideAt(4);
    expect(group(n, w, [])).toEqual([[0, 1], [2, 3], [4], [5, 6], [7, 8], [9, 10], [11]]);
    const r = shiftSpreadAt(n, w, [], 7)!;
    expect(r).toEqual({ starts: [8], anchor: 7 });
    expect(group(n, w, r.starts)).toEqual([[0, 1], [2, 3], [4], [5, 6], [7], [8, 9], [10, 11]]);
    // The highlighted entry flips to "(shifted)" for the re-paired spreads, not before.
    expect(shiftedFlags(n, w, r.starts)).toEqual([false, false, false, false, true, true, true]);
  });

  it('stops at the next wide page', () => {
    // 0..9, wide at 6: shifting at [2,3] re-pairs 2..5 only.
    const r = shiftSpreadAt(10, wideAt(6), [], 2)!;
    expect(group(10, wideAt(6), r.starts)).toEqual([[0, 1], [2], [3, 4], [5], [6], [7, 8], [9]]);
  });

  it('works from the second page of a pair too (resume lands on a spread\'s last index)', () => {
    expect(shiftSpreadAt(6, noWide, [], 3)).toEqual({ starts: [3], anchor: 2 });
  });

  it('round trip is the identity (o, o)', () => {
    const cases: [number, (i: number) => boolean, number[], number][] = [
      [6, noWide, [], 0],
      [6, noWide, [1], 0],
      [12, wideAt(4), [], 7],
      [12, wideAt(4), [8], 7],
      [10, noWide, [3, 7], 4],        // a later forced start (7) survives both flips
      [10, wideAt(5), [1], 2],        // adjacent to a wide page
    ];
    for (const [n, w, starts, page] of cases) {
      const once = shiftSpreadAt(n, w, starts, page)!;
      const twice = shiftSpreadAt(n, w, once.starts, once.anchor)!;
      expect(twice.starts).toEqual(starts);
      expect(group(n, w, twice.starts)).toEqual(group(n, w, starts));
      expect(isShiftedSpread(n, w, group(n, w, once.starts).find((g) => g.includes(once.anchor))!))
        .toBe(!isShiftedSpread(n, w, group(n, w, starts).find((g) => g.includes(page))!));
    }
  });

  it('a solo last page pairs with the page before it', () => {
    // [0,1],[2,3],[4] -> [0,1],[2],[3,4]
    const r = shiftSpreadAt(5, noWide, [], 4)!;
    expect(r).toEqual({ starts: [3], anchor: 3 });
    expect(group(5, noWide, r.starts)).toEqual([[0, 1], [2], [3, 4]]);
  });

  it('a solo page right before a wide page pairs with the page before it', () => {
    // [0,1],[2],[W3] -> [0],[1,2],[W3]
    const r = shiftSpreadAt(6, wideAt(3), [], 2)!;
    expect(group(6, wideAt(3), r.starts)).toEqual([[0], [1, 2], [3], [4, 5]]);
  });

  it('nothing to re-pair on a wide page or a lone page between two wide pages', () => {
    expect(shiftSpreadAt(6, wideAt(2), [], 2)).toBeNull();
    expect(shiftSpreadAt(6, wideAt(1, 3), [], 2)).toBeNull();
    expect(shiftSpreadAt(0, noWide, [], 0)).toBeNull();
  });
});

describe('ensureSpreadStart (d into double page: the page being read starts a spread)', () => {
  it('the owner\'s d-next-d flow: at a mis-paired [4,5], d, next page, d -> pairing starts at 5', () => {
    const n = 10;
    // Mis-paired: [4,5] should be [5,6]. `d` to single page lands on 4, next page is 5.
    const next = ensureSpreadStart(n, noWide, [], 5)!;
    expect(next).toEqual([5]);
    expect(group(n, noWide, next)).toEqual([[0, 1], [2, 3], [4], [5, 6], [7, 8], [9]]);
  });

  it('keeps the saved layout and changes nothing when the page already starts a spread', () => {
    expect(ensureSpreadStart(10, noWide, [3], 3)).toBeNull();
    expect(ensureSpreadStart(10, noWide, [3], 5)).toBeNull(); // [3,4],[5,6]
    expect(ensureSpreadStart(10, noWide, [], 0)).toBeNull();
    expect(ensureSpreadStart(10, wideAt(6), [], 6)).toBeNull();
  });

  it('adds to an existing layout, sorted', () => {
    expect(ensureSpreadStart(12, noWide, [1, 9], 4)).toEqual([1, 4, 9]);
  });

  it('works at the last page', () => {
    expect(ensureSpreadStart(6, noWide, [], 5)).toEqual([5]);
    expect(group(6, noWide, [5])).toEqual([[0, 1], [2, 3], [4], [5]]);
  });
});
