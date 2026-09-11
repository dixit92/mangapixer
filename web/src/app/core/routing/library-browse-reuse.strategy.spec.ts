import { describe, it, expect } from 'vitest';
import { ActivatedRouteSnapshot, DetachedRouteHandle } from '@angular/router';

import { LibraryBrowseReuseStrategy } from './library-browse-reuse.strategy';

/**
 * Regression tests for the 1.6.2 reader-exit-scroll hotfix: the browse view
 * must be retained (not destroyed/re-fetched) across a round trip into the
 * reader and back to the SAME folder, but never served stale for a different
 * folder/library, and forward navigation into a new folder must load fresh.
 */
function fakeRoute(path: string | null, params: Record<string, string>): ActivatedRouteSnapshot {
  return {
    routeConfig: path === null ? null : { path },
    paramMap: { get: (key: string) => params[key] ?? null },
  } as unknown as ActivatedRouteSnapshot;
}

const BROWSE_ROOT = 'libraries/:libraryId/browse';
const BROWSE_FOLDER = 'libraries/:libraryId/browse/:nodeId';
const READER = 'reader/:itemId';

describe('LibraryBrowseReuseStrategy', () => {
  it('detaches a browse route and reattaches it for the identical folder', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const folderA = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderA' });
    const handle = {} as DetachedRouteHandle;

    expect(strategy.shouldDetach(folderA)).toBe(true);
    strategy.store(folderA, handle);

    const returningToFolderA = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderA' });
    expect(strategy.shouldAttach(returningToFolderA)).toBe(true);
    expect(strategy.retrieve(returningToFolderA)).toBe(handle);
  });

  it('does not reuse a stored view for a different folder', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const folderA = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderA' });
    strategy.store(folderA, {} as DetachedRouteHandle);

    const folderB = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderB' });
    expect(strategy.shouldAttach(folderB)).toBe(false);
    expect(strategy.retrieve(folderB)).toBeNull();
  });

  it('does not reuse a stored view for a different library', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const lib1Root = fakeRoute(BROWSE_ROOT, { libraryId: 'lib1' });
    strategy.store(lib1Root, {} as DetachedRouteHandle);

    const lib2Root = fakeRoute(BROWSE_ROOT, { libraryId: 'lib2' });
    expect(strategy.shouldAttach(lib2Root)).toBe(false);
  });

  it('is bounded to a single stored handle: detaching a second browse route evicts the first', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const folderA = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderA' });
    const folderB = fakeRoute(BROWSE_FOLDER, { libraryId: 'lib1', nodeId: 'folderB' });
    strategy.store(folderA, {} as DetachedRouteHandle);
    strategy.store(folderB, {} as DetachedRouteHandle);

    expect(strategy.shouldAttach(folderA)).toBe(false);
    expect(strategy.shouldAttach(folderB)).toBe(true);
  });

  it('never detaches or attaches non-browse routes (e.g. the reader)', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const reader = fakeRoute(READER, { itemId: 'item1' });
    expect(strategy.shouldDetach(reader)).toBe(false);
    expect(strategy.shouldAttach(reader)).toBe(false);
    expect(strategy.retrieve(reader)).toBeNull();
  });

  it('falls back to default same-route-config reuse for everything else', () => {
    const strategy = new LibraryBrowseReuseStrategy();
    const config = { path: READER };
    const curr = { routeConfig: config } as unknown as ActivatedRouteSnapshot;
    const future = { routeConfig: config } as unknown as ActivatedRouteSnapshot;
    const other = { routeConfig: { path: READER } } as unknown as ActivatedRouteSnapshot;

    expect(strategy.shouldReuseRoute(future, curr)).toBe(true);
    expect(strategy.shouldReuseRoute(other, curr)).toBe(false);
  });
});
