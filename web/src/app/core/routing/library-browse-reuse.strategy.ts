import { Injectable } from '@angular/core';
import { ActivatedRouteSnapshot, DetachedRouteHandle, RouteReuseStrategy } from '@angular/router';

/**
 * Route paths that render `LibraryBrowseComponent`. Kept in sync with
 * `app.routes.ts` — any route this strategy should retain must be listed here.
 */
const BROWSE_ROUTE_PATHS = new Set([
  'libraries/:libraryId',
  'libraries/:libraryId/browse',
  'libraries/:libraryId/browse/:nodeId',
]);

/** Identifies a specific folder view, or null when the route isn't a browse route. */
function browseKey(route: ActivatedRouteSnapshot): string | null {
  const path = route.routeConfig?.path;
  if (!path || !BROWSE_ROUTE_PATHS.has(path)) return null;
  const libraryId = route.paramMap.get('libraryId') ?? '';
  const nodeId = route.paramMap.get('nodeId') ?? '';
  return `${libraryId}::${nodeId}`;
}

/**
 * Retains the `LibraryBrowseComponent` instance (DOM, loaded nodes, scroll
 * position) across a round trip into the reader and back, instead of letting
 * the default strategy destroy it on navigate-away and re-create + re-fetch it
 * on `Location.back()`. That destroy/re-fetch cycle is what caused the reader
 * exit bug: a blank frame while the fresh instance re-loaded, landing at the
 * top of the folder instead of the prior scroll position.
 *
 * Only ONE browse view is ever retained (the last one detached) — bounded
 * memory, no leak across a session of folder-hopping. A stored view is reused
 * only when the outgoing and incoming route resolve to the exact same
 * `libraryId`/`nodeId` pair, so navigating into a different folder or library
 * always loads fresh content. Every other route keeps Angular's default
 * same-route-config reuse behavior (e.g. paginating within one browse route).
 */
@Injectable()
export class LibraryBrowseReuseStrategy implements RouteReuseStrategy {
  private stored: { key: string; handle: DetachedRouteHandle } | null = null;

  shouldDetach(route: ActivatedRouteSnapshot): boolean {
    return browseKey(route) !== null;
  }

  store(route: ActivatedRouteSnapshot, handle: DetachedRouteHandle | null): void {
    const key = browseKey(route);
    if (key === null) return;
    if (handle) {
      this.stored = { key, handle };
    } else if (this.stored?.key === key) {
      this.stored = null;
    }
  }

  shouldAttach(route: ActivatedRouteSnapshot): boolean {
    const key = browseKey(route);
    return key !== null && this.stored?.key === key;
  }

  retrieve(route: ActivatedRouteSnapshot): DetachedRouteHandle | null {
    const key = browseKey(route);
    if (key === null || this.stored?.key !== key) return null;
    return this.stored.handle;
  }

  shouldReuseRoute(future: ActivatedRouteSnapshot, curr: ActivatedRouteSnapshot): boolean {
    return future.routeConfig === curr.routeConfig;
  }
}
