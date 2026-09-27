import { describe, expect, it } from 'vitest';

import { isNearEnd, nearEndPagesAfter } from './near-end';

/** Same boundary table as the server's NearEndRuleTests (1.27.0). */
describe('near-end rule (client copy of the server rule)', () => {
  it.each([
    [0, 0], [1, 0], [2, 0], [3, 1], [20, 1], [21, 1], [39, 1], [40, 2], [99, 4], [100, 5], [200, 5], [5000, 5],
  ])('%i pages allow %i pages after the end position', (pageCount, expected) => {
    expect(nearEndPagesAfter(pageCount)).toBe(expected);
  });

  it.each([
    [0, 1, true], [0, 2, false], [1, 2, true], [0, 3, false], [1, 3, true],
    [17, 20, false], [18, 20, true], [19, 20, true],
    [18, 21, false], [19, 21, true],
    [93, 100, false], [94, 100, true],
    [189, 200, false], [193, 200, false], [194, 200, true], [199, 200, true],
    [0, 0, false], [5, 0, false],
  ])('index %i of %i pages: at the end = %s', (pageIndex, pageCount, expected) => {
    expect(isNearEnd(pageIndex, pageCount)).toBe(expected);
  });
});
