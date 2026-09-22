import { DownscaleFilter } from '@app/core/reading/downscale-filters';

/**
 * Display-sized page variant targeting (1.19.0, Lane B "SCALE-CLIENT").
 *
 * The reader used to ask the server for the FULL-SIZE transcode of every page,
 * whatever the screen it was going to be squeezed onto. On a phone that is a
 * 2400x3600 scan pushed down a mobile link and then resampled by the browser's
 * (cheap, box-ish) downscale filter — slow AND softer than it needs to be.
 *
 * 1.19.0 adds `?maxDim=<int>` to the page endpoint: the server snaps the request
 * UP to a bucket on a fixed ladder and returns a Lanczos-downscaled transcode of
 * that longest edge. Fewer bytes, and a visibly crisper result than the browser's
 * own downscale. This module holds the CLIENT half of that contract: the pure
 * rule that turns "what box is this page about to be painted into" into the
 * bucket to ask for, with no DOM and no Angular in sight so it can be unit
 * tested exhaustively.
 *
 * Deliberately simple and deterministic:
 *  - one number out, computed from the layout box the fit mode implies;
 *  - snapped UP (never down) to a ladder, so the page is never asked for at
 *    fewer pixels than the screen will paint;
 *  - `0` means "no `maxDim` param at all" — i.e. the full-size transcode — which
 *    is the answer for `original` fit, for an unknown viewport, and for any box
 *    that needs more than the top bucket.
 *
 * It is never a QUALITY downgrade: the bucket always covers the device-pixel box
 * the image occupies, and the server only ever downscales a page that is bigger
 * than the bucket (it serves the full transcode when the source is already
 * smaller). The user can still force the old behaviour with the "Page quality:
 * Full" preference — see `ReaderPreferencesService.pageQuality`.
 */

/**
 * Longest-edge buckets, in CSS-independent PIXELS. This mirrors the server's
 * default ladder for `?maxDim=` (`[1080, 1440, 2160]`); the server snaps up to
 * the same rungs, so asking for a value already on the ladder is a no-op there
 * and the URL stays stable (and therefore cacheable) across small viewport
 * jitter. Keep the two in step: a client value BETWEEN rungs would still work
 * (the server snaps up) but would fragment the cache, so we snap here too.
 */
export const variantLadder: readonly number[] = [1080, 1440, 2160];

/**
 * The fit modes the rule understands. `'screen' | 'width' | 'height' |
 * 'original'` are the reader's `FitMode` values; `'webtoon'` is passed instead
 * of a fit mode when the vertical (continuous-scroll) view is on screen, where
 * the page width is a percentage of the viewport rather than a fit mode.
 *
 * Kept as its own union rather than importing `FitMode` so this module stays
 * dependency-free (and so the reader's settings surface can keep owning
 * `FitMode` without this file pulling the whole settings component in).
 */
export type VariantFitMode = 'screen' | 'width' | 'height' | 'original' | 'webtoon';

/** The top rung: a box needing more than this gets the full-size transcode. */
const topBucket = variantLadder[variantLadder.length - 1];

/** jsdom and a few embedded webviews report no/garbage devicePixelRatio. */
function safeDpr(dpr: number): number {
  return Number.isFinite(dpr) && dpr > 0 ? dpr : 1;
}

/**
 * The bucket (longest edge, px) to request for a page about to be painted into
 * the current reader layout, or `0` for "ask for the full-size transcode".
 *
 * Box per fit mode (the `<img>`'s layout box, in CSS px):
 *  - `screen` / `height` — the viewport box (`viewportW x viewportH`); a paired
 *    double-page spread gets HALF the width, since two pages share the row.
 *  - `width` — the viewport width (halved when paired); the height is unbounded
 *    (the page scrolls), so it is derived from the page's own aspect ratio.
 *  - `webtoon` — `webtoonWidthPct` percent of the viewport width; height again
 *    from the page aspect.
 *  - `original` — always `0`. "Original size" means exactly that: the user asked
 *    for native pixels, so never ask the server for fewer.
 *
 * The needed longest edge is then `ceil(max(boxW, boxH) * dpr)`, snapped UP to
 * the ladder. Using the FULL viewport box for `screen`/`height` slightly
 * over-asks for a portrait page on a landscape screen (the page is letterboxed,
 * so its painted width is less than the viewport's) — deliberately: the error is
 * always in the safe direction (never blurrier than the screen can show) and it
 * keeps the rule independent of per-page measurement, so every page of a chapter
 * lands on the same bucket and the same URLs stay cacheable.
 *
 * @param viewportW        reader viewport width in CSS px (0/unknown -> full)
 * @param viewportH        reader viewport height in CSS px (0/unknown -> full)
 * @param dpr              `window.devicePixelRatio` (non-finite/<=0 reads as 1)
 * @param fitMode          current fit mode, or `'webtoon'` for the vertical view
 * @param paired           true when two pages share the row (double-page spread)
 * @param webtoonWidthPct  webtoon page width, 1..100 (ignored outside webtoon)
 * @param pageAspect       page height / width from the manifest; 0 = unknown,
 *                         in which case an unbounded height is treated as square
 */
export function targetMaxDim(
  viewportW: number,
  viewportH: number,
  dpr: number,
  fitMode: VariantFitMode,
  paired: boolean,
  webtoonWidthPct: number,
  pageAspect = 0,
): number {
  // "Original size" is an explicit request for native pixels — never downscale.
  if (fitMode === 'original') return 0;

  const w = Number.isFinite(viewportW) ? viewportW : 0;
  const h = Number.isFinite(viewportH) ? viewportH : 0;
  // No measured viewport yet (pre-layout, or a headless/jsdom host): fall back to
  // the full-size transcode rather than guessing a box and under-asking.
  if (w <= 0 || h <= 0) return 0;

  const ratio = Number.isFinite(pageAspect) && pageAspect > 0 ? pageAspect : 0;

  let boxW: number;
  let boxH: number;
  if (fitMode === 'webtoon') {
    const pct = Number.isFinite(webtoonWidthPct) ? Math.min(100, Math.max(1, webtoonWidthPct)) : 100;
    boxW = (w * pct) / 100;
    boxH = ratio > 0 ? boxW * ratio : boxW;
  } else {
    boxW = paired ? w / 2 : w;
    // fit-width leaves the height unbounded (the page scrolls vertically), so the
    // painted height follows from the page's own aspect ratio, not the viewport.
    boxH = fitMode === 'width' ? (ratio > 0 ? boxW * ratio : boxW) : h;
  }

  const needed = Math.ceil(Math.max(boxW, boxH) * safeDpr(dpr));
  if (needed <= 0) return 0;
  for (const bucket of variantLadder) {
    if (needed <= bucket) return bucket;
  }
  // Bigger than the top rung: the screen genuinely wants more pixels than the
  // ladder offers, so take the full-size transcode.
  return needed > topBucket ? 0 : topBucket;
}

// DownscaleFilter re-exported from downscale-filters.ts (single source of truth)
export { DownscaleFilter };

/**
 * Append `?maxDim=<n>` (and, when a filter is given, `&filter=<f>`) to a page
 * URL, or return it untouched when `n` is 0 (the full-size transcode) — the
 * single place the query shape is decided, shared by the reader's `<img>`
 * sources and its prefetch warm-up so both produce the exact same string and
 * the prefetched response is a browser cache HIT.
 *
 * The `filter` is only ever appended alongside a real `maxDim` bucket: for
 * `maxDim <= 0` the URL is returned exactly as it was in 1.19.x (no `filter=`
 * param), since the server ignores it on the full-size transcode anyway and an
 * unchanged URL keeps existing caches (and `withMaxDim` call sites that predate
 * the filter preference) a byte-for-byte HIT.
 */
export function withMaxDim(url: string, maxDim: number, filter?: DownscaleFilter): string {
  if (!url || !Number.isFinite(maxDim) || maxDim <= 0) return url;
  const base = `${url}?maxDim=${Math.round(maxDim)}`;
  return filter ? `${base}&filter=${filter}` : base;
}
