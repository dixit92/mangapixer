/**
 * Adaptive slicing of a band's GPU passes across animation frames (1.24.0; split
 * out of `anime4k-tile-renderer.ts` in 1.25.0 so the WebGL2 renderer shares it
 * without importing - and so downloading - the WebGPU chunk).
 *
 * A 30-100 ms monolithic submission would starve the compositor on the tiled
 * GPUs of a phone or iPad and make scrolling stutter, so a band's passes are
 * encoded a few at a time, one submission per frame. The slice size adapts
 * (AIMD) to keep each slice's GPU time under `sliceBudgetMs`.
 */

/** Target GPU time per submitted slice (ms). */
export const sliceBudgetMs = 6;

/** Adaptive slice size, shared across bands so it converges once per session. */
export interface SliceBudget {
  /** Passes per slice. */
  k: number;
}

export function createSliceBudget(): SliceBudget {
  return { k: 4 };
}

/** AIMD: +1 pass while a slice stays under budget, halve when it goes over. */
export function adaptSlice(budget: SliceBudget, sliceMs: number, maxK = 64): void {
  budget.k = sliceMs <= sliceBudgetMs ? Math.min(maxK, budget.k + 1) : Math.max(1, Math.floor(budget.k / 2));
}

/** Resolves on the next animation frame (a timer where there is none). */
export function nextAnimationFrame(): Promise<void> {
  return new Promise((resolve) => {
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(() => resolve());
    else setTimeout(resolve, 16);
  });
}
