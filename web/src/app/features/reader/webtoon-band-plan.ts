/**
 * Webtoon Enhance (1.24.0): the PURE half - band tiling, the per-page gate, pool
 * sizing and the memory estimate. No DOM and no GPU, like `page-variant.ts`, so
 * every number the coordinator and the tile renderer act on is unit-tested here.
 *
 * Why bands: a webtoon strip page can be anywhere from ~1280 to 12000+ native
 * rows tall and every page has its own height. Rendering whole pages would
 * rebuild the Anime4K pipeline for almost every page (its textures are sized to
 * the input) and would blow past WebGPU's 8192 px texture limit on tall slices.
 * Instead each page is cut into horizontal BANDS of `bandRows` native rows, and
 * each band is processed as a TILE of `bandRows + 2 * halo` rows whose window is
 * clamped inside the page. Every tile of a page of width `Wn` that is at least
 * one tile tall therefore has the same input size, and one pipeline (keyed by
 * `Wn x tileRows`) serves the whole strip.
 *
 * Why a halo: Anime4K's receptive field is ~23 native px (ClampHighlights radius
 * 2, 7-8 layers of 3x3 convs per CNN, a trailing x2), so 24 extra rows above and
 * below a band make the band's own rows identical to a whole-page render and
 * neighbouring bands join invisibly.
 *
 * Why a fixed 2x output: every band canvas is backed by exactly `2 * Wn` x
 * `2 * rows` device pixels (the CNNx2 output as is) and CSS-sized to the band's
 * painted box, so the width slider, rotation, window resize and browser zoom only
 * move CSS boxes and never re-render.
 */

/** Anime4K chain: `m` = CNNM + CNNx2M (light), `vl` = CNNVL + CNNx2VL (heavy, paged "max quality"). */
export type EnhanceChain = 'm' | 'vl';

/**
 * Webtoon enhances a page only when it is painted more than this much larger than
 * its delivered natural width, in DEVICE pixels. Below it the fixed-2x design
 * would pay for a full x2 only to be downscaled again (paged keeps its 1.02).
 */
export const webtoonUpscaleThreshold = 1.2;

/** Default band height in native rows (the design's memory tables use it). */
export const defaultBandRows = 384;
/** Rows of context above and below a band: covers the ~23 px receptive field. */
export const haloRows = 24;
/** Band heights snap to this so tiles stay GPU-friendly (workgroups are 8x8). */
export const bandRowStep = 32;
/** Smallest / largest band the budget formula may pick. */
export const minBandRows = 256;
export const maxBandRows = 768;
/**
 * A band canvas extends this many native rows into the next band (drawn from its
 * halo), hiding hairline anti-aliasing gaps at fractional device-pixel joins. The
 * later band is later in the DOM and wins where they overlap.
 */
export const seamRows = 2;
/** Hard cap on band canvases app-wide (compositor layers and memory). */
export const poolCap = 12;

/**
 * Pipeline bytes per native tile pixel, from the texture counts in
 * `anime4k-webgpu` 1.0.0 (`rgba16float` = 8 B/px, the `rgba8unorm` input = 4):
 *  - VL: 4 + ClampHighlights 3*8 + CNNVL 18*8 + CNNx2VL (17*8 + DepthToSpace and
 *    Overlay at 4x area, 2*4*8) = 372
 *  - M:  4 + 3*8 + CNNM 9*8 + CNNx2M (8*8 + 2*4*8) = 228
 */
export const bytesPerTilePixel: Readonly<Record<EnhanceChain, number>> = { m: 228, vl: 372 };

/** Pipeline memory budget for one tile: tighter on touch (coarse-pointer) devices. */
export const pipelineBudgetBytes = { coarse: 96e6, fine: 160e6 } as const;

/** One band of a page, all values in NATIVE rows of that page. */
export interface BandPlan {
  /** Index of the band within its page (top to bottom). */
  readonly index: number;
  /** First page row this band owns. */
  readonly bandY: number;
  /** Rows this band owns (the last band is shifted up, so it may overlap its predecessor). */
  readonly bandRows: number;
  /** First page row of the tile fed to the GPU (band plus halo, clamped inside the page). */
  readonly tileY: number;
  /** Rows of the tile; equal for every band of a page taller than one tile. */
  readonly tileRows: number;
  /** Offset of the band's first row inside the tile. */
  readonly cropY: number;
  /** Rows drawn into the band canvas: `bandRows` plus the seam overlap where the page continues. */
  readonly drawRows: number;
}

/**
 * Split a `width x height` native page into bands of `bandRows` (+ `halo` each
 * side). Bands start every `bandRows` rows; the last one is shifted up to end at
 * the page bottom (so every canvas of a page has the same size). A page no taller
 * than one tile is a single whole-page band/tile. Returns [] for a degenerate page.
 */
export function planBands(width: number, height: number, bandRows = defaultBandRows, halo = haloRows): BandPlan[] {
  const w = Math.floor(width);
  const h = Math.floor(height);
  const bn = Math.floor(bandRows);
  const hr = Math.max(0, Math.floor(halo));
  if (!(w > 0) || !(h > 0) || !(bn > 0)) return [];
  const tileRows = bn + 2 * hr;
  if (h <= tileRows) {
    return [{ index: 0, bandY: 0, bandRows: h, tileY: 0, tileRows: h, cropY: 0, drawRows: h }];
  }
  const count = Math.ceil(h / bn);
  const bands: BandPlan[] = [];
  for (let i = 0; i < count; i++) {
    const bandY = Math.min(i * bn, h - bn);
    const tileY = Math.min(Math.max(bandY - hr, 0), h - tileRows);
    const cropY = bandY - tileY;
    // Never draw past the tile or the page; the seam rows come from the halo below.
    const drawRows = Math.min(bn + seamRows, h - bandY, tileRows - cropY);
    bands.push({ index: i, bandY, bandRows: bn, tileY, tileRows, cropY, drawRows });
  }
  return bands;
}

/** Device-pixel scale of a painted page: > 1 means the browser is enlarging it. */
export function deviceScale(cssWidth: number, dpr: number, naturalWidth: number): number {
  const ratio = Number.isFinite(dpr) && dpr > 0 ? dpr : 1;
  if (!(cssWidth > 0) || !(naturalWidth > 0)) return 0;
  return (cssWidth * ratio) / naturalWidth;
}

/**
 * Should this page be enhanced? Per page, from its OWN delivered natural width
 * (never a chapter-wide representative), in device pixels (the 1.19.1 lesson).
 */
export function webtoonEnhanceGate(cssWidth: number, dpr: number, naturalWidth: number): boolean {
  return deviceScale(cssWidth, dpr, naturalWidth) > webtoonUpscaleThreshold;
}

/**
 * Band height for a `width`-wide source under a pipeline memory budget:
 * `tileRows = floor(budget / (bytesPerPx * width))`, then
 * `bandRows = floor((tileRows - 2 * halo) / 32) * 32`, clamped to [256, 768].
 */
export function bandHeightFor(width: number, bytesPerPx: number, budgetBytes: number, halo = haloRows): number {
  if (!(width > 0) || !(bytesPerPx > 0) || !(budgetBytes > 0)) return minBandRows;
  const tileRows = Math.floor(budgetBytes / (bytesPerPx * width));
  const rows = Math.floor((tileRows - 2 * halo) / bandRowStep) * bandRowStep;
  return Math.min(maxBandRows, Math.max(minBandRows, rows));
}

/**
 * The band height the coordinator actually uses: the design default (384), made
 * smaller for wide sources so one tile stays inside the pipeline budget.
 */
export function bandRowsFor(width: number, chain: EnhanceChain, coarsePointer: boolean): number {
  const budget = coarsePointer ? pipelineBudgetBytes.coarse : pipelineBudgetBytes.fine;
  return Math.min(defaultBandRows, bandHeightFor(width, bytesPerTilePixel[chain], budget));
}

/** CSS height of one band of a page painted `cssWidth` wide from a `nativeWidth` source. */
export function bandCssHeight(bandRows: number, nativeWidth: number, cssWidth: number): number {
  if (!(nativeWidth > 0) || !(cssWidth > 0)) return 0;
  return (bandRows * cssWidth) / nativeWidth;
}

/**
 * How many band canvases may exist: the visible viewport plus one viewport of
 * margin, `ceil(2 * viewport / band) + 1`, capped at `cap`.
 */
export function poolSize(viewportCssHeight: number, bandCssH: number, cap = poolCap): number {
  if (!(viewportCssHeight > 0) || !(bandCssH > 0)) return cap;
  return Math.max(1, Math.min(cap, Math.ceil((2 * viewportCssHeight) / bandCssH) + 1));
}

/** Bytes of one tile pipeline (all intermediate textures) for a `width x tileRows` input. */
export function estimatePipelineBytes(width: number, tileRows: number, chain: EnhanceChain): number {
  return bytesPerTilePixel[chain] * width * tileRows;
}

/** Bytes of one band canvas backing store (fixed 2x, 4 B/px), for `swaps` swap images. */
export function estimateCanvasBytes(width: number, bandRows: number, swaps = 1): number {
  return 2 * width * 2 * bandRows * 4 * swaps;
}

export interface MemoryEstimate {
  readonly pipeline: number;
  readonly canvasesMin: number;
  readonly canvasesMax: number;
  readonly totalMin: number;
  readonly totalMax: number;
  readonly bands: number;
}

/**
 * Peak GPU memory for a device: one pipeline plus a pool of band canvases (1x to
 * 2x swap images). Reproduces the design note's device table.
 */
export function estimateBytes(opts: {
  nativeWidth: number; bandRows: number; chain: EnhanceChain; viewportCssHeight: number; cssWidth: number;
}): MemoryEstimate {
  const { nativeWidth, bandRows, chain, viewportCssHeight, cssWidth } = opts;
  const pipeline = estimatePipelineBytes(nativeWidth, bandRows + 2 * haloRows, chain);
  const bands = poolSize(viewportCssHeight, bandCssHeight(bandRows, nativeWidth, cssWidth));
  const one = estimateCanvasBytes(nativeWidth, bandRows);
  return {
    pipeline,
    bands,
    canvasesMin: bands * one,
    canvasesMax: bands * one * 2,
    totalMin: pipeline + bands * one,
    totalMax: pipeline + bands * one * 2,
  };
}
