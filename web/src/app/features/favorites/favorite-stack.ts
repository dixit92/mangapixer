import { CatalogNodeDto } from '../../core/api/api-types';

/**
 * Favorites stacking (1.27.0), shared by the Favorites page and the Home Favorites row.
 * `GET /favorites` returns a STACK as the folder node with `favoriteStackCount` set: at
 * least two of the user's starred archives are direct children of that folder. Tapping
 * a stack opens the folder's browse view with a TRANSIENT `?favorites=1`, which turns on
 * browse's "Favorites only" filter for that view without saving it anywhere.
 */

/** True when the favorites item stands for a stack of starred archives in one folder. */
export function isFavoriteStack(node: CatalogNodeDto): boolean {
  return (node.favoriteStackCount ?? 0) > 1;
}

/** Router link for a favorites item: folders (and stacks) open browse, archives the reader. */
export function favoriteLink(node: CatalogNodeDto): string[] {
  if (node.kind === 'Folder') return ['/libraries', node.libraryId, 'browse', node.id];
  return ['/reader', node.id];
}

/** Query params for a favorites item link: a stack opens browse filtered to favorites. */
export function favoriteQueryParams(node: CatalogNodeDto): Record<string, string> | null {
  return isFavoriteStack(node) ? { favorites: '1' } : null;
}

/**
 * `@for` track key. A starred folder and a stack of its starred children can both be
 * listed, with the same node id, so a stack gets its own key.
 */
export function favoriteTrackKey(node: CatalogNodeDto): string {
  return isFavoriteStack(node) ? `stack:${node.id}` : node.id;
}

/** "N favorites" caption for a stack card. */
export function favoriteStackLabel(node: CatalogNodeDto): string {
  return `${node.favoriteStackCount} favorites`;
}
