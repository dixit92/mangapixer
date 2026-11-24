/**
 * The Home rows and the user's order of them (1.54.0, owner decision 3: per user, synced in the `library-preferences` blob; reordered in
 * Settings > Home). The ids are the server's (`HomeRows` in MangaPixer.Core); `normalizeHomeRowOrder` mirrors its rule so the page draws
 * every row exactly once whatever it is given: unknown and repeated ids dropped, the missing ones appended in the default order.
 */
export type HomeRowId = 'continue' | 'recommended' | 'favorites' | 'recent' | 'libraries';

/** The order of a user who never reordered Home (Home's order before 1.54.0). */
export const DEFAULT_HOME_ROW_ORDER: readonly HomeRowId[] = ['continue', 'recommended', 'favorites', 'recent', 'libraries'];

/** Each row's name as its heading on Home says it. */
export const HOME_ROW_LABELS: Readonly<Record<HomeRowId, string>> = {
  continue: 'Continue reading',
  recommended: 'Recommended for you',
  favorites: 'Favorites',
  recent: 'New chapters',
  libraries: 'Libraries',
};

function isHomeRowId(id: unknown): id is HomeRowId {
  return typeof id === 'string' && (DEFAULT_HOME_ROW_ORDER as readonly string[]).includes(id);
}

/** The full order `order` stands for (null / absent / empty = the default order). */
export function normalizeHomeRowOrder(order: readonly unknown[] | null | undefined): HomeRowId[] {
  const result: HomeRowId[] = [];
  for (const id of order ?? []) {
    if (isHomeRowId(id) && !result.includes(id)) result.push(id);
  }
  for (const id of DEFAULT_HOME_ROW_ORDER) {
    if (!result.includes(id)) result.push(id);
  }
  return result;
}

/** `order` with the row at `index` moved by `delta` places (-1 up, +1 down); the same order when it cannot move. */
export function moveHomeRow(order: readonly HomeRowId[], index: number, delta: -1 | 1): HomeRowId[] {
  const target = index + delta;
  if (index < 0 || index >= order.length || target < 0 || target >= order.length) return [...order];
  const next = [...order];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}
