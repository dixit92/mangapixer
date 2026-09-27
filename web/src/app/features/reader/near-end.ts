/**
 * "Near the end counts as finished" (1.27.0) - the client copy of the server's rule
 * (`NearEndRule` in MangaPixer.Core; keep the two in step). The server decides
 * completion, the read-mark and where a read archive reopens; the reader only needs it
 * to know when a save WOULD finish the archive (see `saveProgress`'s landed-at-the-end
 * guard).
 *
 * A zero-based position is at the end when at most n pages follow it,
 * n = min(5, max(1, floor(pageCount / 20))), and never the first page of an archive
 * with two or more pages.
 */
export const NEAR_END_MAX_PAGES_AFTER = 5;

/** How many pages may follow a position that still counts as the end. */
export function nearEndPagesAfter(pageCount: number): number {
  if (pageCount <= 0) return 0;
  const n = Math.min(NEAR_END_MAX_PAGES_AFTER, Math.max(1, Math.floor(pageCount / 20)));
  return Math.max(0, Math.min(n, pageCount - 2));
}

/** True when the zero-based `pageIndex` counts as the end of a `pageCount`-page archive. */
export function isNearEnd(pageIndex: number, pageCount: number): boolean {
  return pageCount > 0 && pageIndex >= pageCount - 1 - nearEndPagesAfter(pageCount);
}
