import { DEFAULT_HOME_ROW_ORDER, HOME_ROW_LABELS, moveHomeRow, normalizeHomeRowOrder } from './home-rows';

/** 1.54.0: the Home rows' ids, labels and the order rule (mirrors the server's HomeRows). */
describe('home-rows', () => {
  it('the default order is Home before 1.54.0, with a label per row', () => {
    expect([...DEFAULT_HOME_ROW_ORDER]).toEqual(['continue', 'recommended', 'favorites', 'recent', 'libraries']);
    expect(DEFAULT_HOME_ROW_ORDER.map((r) => HOME_ROW_LABELS[r]))
      .toEqual(['Continue reading', 'Recommended for you', 'Favorites', 'New chapters', 'Libraries']);
  });

  it('normalizes: unknown, repeated and non-string ids dropped; the missing ones appended in the default order', () => {
    expect(normalizeHomeRowOrder(null)).toEqual([...DEFAULT_HOME_ROW_ORDER]);
    expect(normalizeHomeRowOrder([])).toEqual([...DEFAULT_HOME_ROW_ORDER]);
    expect(normalizeHomeRowOrder(['recent', 'bogus', 7, 'recent', 'libraries']))
      .toEqual(['recent', 'libraries', 'continue', 'recommended', 'favorites']);
  });

  it('moves a row one place, and not past an end', () => {
    const order = [...DEFAULT_HOME_ROW_ORDER];
    expect(moveHomeRow(order, 4, -1)).toEqual(['continue', 'recommended', 'favorites', 'libraries', 'recent']);
    expect(moveHomeRow(order, 0, 1)).toEqual(['recommended', 'continue', 'favorites', 'recent', 'libraries']);
    expect(moveHomeRow(order, 0, -1)).toEqual(order);
    expect(moveHomeRow(order, 4, 1)).toEqual(order);
    expect(order).toEqual([...DEFAULT_HOME_ROW_ORDER]); // never mutates its input
  });
});
