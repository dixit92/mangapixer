/**
 * Double-page pairing (1.23.0), as pure functions so the reader stays thin and every
 * rule is testable as a concrete page sequence.
 *
 * The saved layout of an archive is a sorted set S of FORCED SPREAD-START page
 * indices (shared per archive, on the server). Grouping walks the pages:
 *  - a wide page (a pre-stitched two-page spread) is always solo and restarts the
 *    cadence (unchanged since 2026-09-08);
 *  - a page in S always starts a new group, so the page before it goes solo when it
 *    would otherwise have paired across it;
 *  - otherwise pages pair.
 * With no saved layout the reader's device cover setting is the fallback: S = {1}
 * when "cover alone" is on and page 0 is not wide (exactly the old offset-cover
 * behaviour), else S = {}.
 *
 * A SEGMENT is a maximal run of non-wide pages. Its DEFAULT cadence pairs from the
 * segment's first page (s, s+1), (s+2, s+3)... A spread is "shifted" when its pairing
 * runs on the other parity; that is what the "Double page (shifted)" menu entry
 * highlights, per current spread.
 */

/** Is the page at this index wide (landscape, never paired)? */
export type WideFn = (index: number) => boolean;

/** Group page indices into spreads under the forced starts. */
export function groupSpreads(n: number, isWide: WideFn, starts: ReadonlySet<number>): number[][] {
  const groups: number[][] = [];
  let i = 0;
  while (i < n) {
    if (isWide(i)) { groups.push([i]); i += 1; continue; }
    if (i + 1 < n && !isWide(i + 1) && !starts.has(i + 1)) { groups.push([i, i + 1]); i += 2; }
    else { groups.push([i]); i += 1; }
  }
  return groups;
}

/** The device fallback used while an archive has no saved layout. */
export function fallbackSpreadStarts(n: number, isWide: WideFn, coverStandalone: boolean): number[] {
  return coverStandalone && n > 1 && !isWide(0) ? [1] : [];
}

/**
 * A server layout made safe to use: unique, sorted integers in [1, n-1]. `null` or
 * `undefined` (no saved layout) stays `null` so the device fallback applies; an
 * empty array is an explicit "no shifts" and stays empty.
 */
export function normalizeSpreadStarts(raw: readonly number[] | null | undefined, n: number): number[] | null {
  if (raw == null) return null;
  const valid = raw.filter((i) => Number.isInteger(i) && i >= 1 && i < n);
  return [...new Set(valid)].sort((a, b) => a - b);
}

/** The non-wide segment [start, end] holding `index`, or null for a wide page. */
function segmentOf(n: number, isWide: WideFn, index: number): { start: number; end: number } | null {
  if (index < 0 || index >= n || isWide(index)) return null;
  let start = index;
  while (start > 0 && !isWide(start - 1)) start -= 1;
  let end = index;
  while (end + 1 < n && !isWide(end + 1)) end += 1;
  return { start, end };
}

/**
 * Does this spread run on the shifted parity of its segment? The parity of a spread
 * is where pairing continues from it: a pair [a, a+1] continues at a; a solo page
 * that is solo because the NEXT page is forced to start continues at a+1; a solo page
 * at the segment's end has nothing after it and is judged by its own start. Wide
 * pages are never shifted.
 */
export function isShiftedSpread(n: number, isWide: WideFn, group: readonly number[]): boolean {
  if (group.length === 0) return false;
  const a = group[0];
  const seg = segmentOf(n, isWide, a);
  if (!seg) return false;
  if (group.length === 2) return (a - seg.start) % 2 === 1;
  if (a < seg.end) return (a + 1 - seg.start) % 2 === 1;
  return (a - seg.start) % 2 === 1;
}

/** Result of re-pairing: the new forced starts and the page to show (a spread start). */
export interface SpreadShift {
  starts: number[];
  anchor: number;
}

/**
 * Flip the pairing parity from the spread holding `page` onward (up to the next wide
 * page, or the next spread the reader has already forced). This is what picking the
 * other double-page entry, or the `o` key, does. On page 0 it is exactly the old
 * cover offset. Returns null when there is nothing to re-pair: a wide page, or a
 * one-page segment.
 *
 *  - pair [a, a+1]: force a+1 to start, so a goes solo and pairing continues a+1, a+2;
 *  - solo [a] before a forced a+1: drop that start, so a pairs with a+1 again
 *    (the exact inverse of the line above, so a round trip is the identity);
 *  - solo [a] at the segment's end: pair it with the page before (a-1 starts,
 *    a no longer does), the only other pairing a last page can have.
 */
export function shiftSpreadAt(n: number, isWide: WideFn, starts: readonly number[], page: number): SpreadShift | null {
  const set = new Set(starts);
  const group = groupSpreads(n, isWide, set).find((g) => g.includes(page));
  if (!group) return null;
  const a = group[0];
  const seg = segmentOf(n, isWide, a);
  if (!seg) return null;
  let anchor = a;
  if (group.length === 2) {
    set.add(a + 1);
  } else if (a < seg.end) {
    set.delete(a + 1);
  } else {
    if (a === seg.start) return null;
    set.delete(a);
    if (a - 1 >= 1) set.add(a - 1); // page 0 always starts; it is never a forced start
    anchor = a - 1;
  }
  return { starts: [...set].sort((x, y) => x - y), anchor };
}

/**
 * Entering double page at `page` (the `d` key, or a double-page menu entry picked
 * from single page): the page being read must start a spread. Returns the forced
 * starts with `page` added when it would not already start one, or null when nothing
 * needs to change. This is the owner's desktop fix flow: at a mis-paired spread press
 * `d`, step to the next page, press `d` again and pairing now starts there.
 */
export function ensureSpreadStart(n: number, isWide: WideFn, starts: readonly number[], page: number): number[] | null {
  if (page <= 0 || page >= n) return null;
  const group = groupSpreads(n, isWide, new Set(starts)).find((g) => g.includes(page));
  if (!group || group[0] === page) return null;
  return [...new Set([...starts, page])].sort((x, y) => x - y);
}
