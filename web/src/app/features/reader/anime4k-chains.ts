/**
 * Anime4K chain builders shared by the two lazy Enhance renderers (paged
 * `anime4k-renderer.ts`, webtoon `anime4k-tile-renderer.ts`). Only those lazy
 * modules import this file, so `anime4k-webgpu` stays in a shared LAZY chunk.
 *
 * Two chain families, read from `anime4k-webgpu` 1.0.0:
 *  - `vl` - the package's own `ModeA`: `ClampHighlights` -> `CNNVL` restore ->
 *    `CNNx2VL` (the heavy "VL" chain, 372 B per native pixel of textures). This is
 *    what paged Enhance shipped with in 1.19.0-1.23.x; it is now the paged
 *    "Max quality" choice.
 *  - `m` - the same `ModeA` shape built from the package's lighter exported
 *    classes: `ClampHighlights` -> `CNNM` -> `CNNx2M` (228 B/px, roughly 3-4x fewer
 *    multiply-adds: one 4-channel conv per layer over 4 inputs instead of two over
 *    8). The default for paged and the only chain webtoon uses (owner decision
 *    2026-09-25: design for less capable devices, don't eat battery for minor gains).
 */

import {
  Anime4KPipeline, Anime4KPresetPipelineDescriptor, CNNM, CNNVL, CNNx2M, CNNx2VL, ClampHighlights, Downscale, ModeA,
} from 'anime4k-webgpu';

import type { EnhanceChain } from './webtoon-band-plan';

/**
 * `ModeA`'s exact scale logic with the M family: restore at native size, x2 when
 * the target is > 1.2x native, then by scale `s` a `Downscale` to the target
 * (1.2 < s < 2), or a `Downscale` to half the target plus a further `CNNx2M`
 * (2.4 < s < 4), or just the further `CNNx2M` (s >= 4). Mirrors the package's
 * `ModeA` constructor line by line so paged M and paged VL differ ONLY in the
 * CNN weights, never in geometry.
 */
class ModeAM implements Anime4KPipeline {
  readonly pipelines: Anime4KPipeline[] = [];
  private readonly outputTexture: GPUTexture;

  constructor({ device, inputTexture, nativeDimensions: native, targetDimensions: target }: Anime4KPresetPipelineDescriptor) {
    let w = native.width;
    let h = native.height;
    let tex = inputTexture;
    const push = (p: Anime4KPipeline) => { this.pipelines.push(p); tex = p.getOutputTexture(); };

    push(new ClampHighlights({ device, inputTexture: tex }));
    push(new CNNM({ device, inputTexture: tex }));
    if (target.width > 1.2 * w && target.height > 1.2 * h) {
      push(new CNNx2M({ device, inputTexture: tex }));
      w *= 2; h *= 2;
    }
    if (target.width > 1.2 * native.width && target.height > 1.2 * native.height
      && target.width < 2 * native.width && target.height < 2 * native.height) {
      push(new Downscale({ device, inputTexture: tex, targetDimensions: target }));
      w = target.width; h = target.height;
    }
    if (target.width > 2.4 * native.width && target.height > 2.4 * native.height
      && target.width < 4 * native.width && target.height < 4 * native.height) {
      const half = { width: Math.ceil(target.width / 2), height: Math.ceil(target.height / 2) };
      push(new Downscale({ device, inputTexture: tex, targetDimensions: half }));
      w = half.width; h = half.height;
    }
    if (target.width > 1.2 * w && target.height > 1.2 * h) {
      push(new CNNx2M({ device, inputTexture: tex }));
    }
    this.outputTexture = tex;
  }

  updateParam(): void { throw new Error('Preset has no param'); }
  pass(encoder: GPUCommandEncoder): void { for (const p of this.pipelines) p.pass(encoder); }
  getOutputTexture(): GPUTexture { return this.outputTexture; }
}

/** The paged preset: restore + upscale to (about) the target box. */
export function buildPagedChain(chain: EnhanceChain, desc: Anime4KPresetPipelineDescriptor): Anime4KPipeline {
  return chain === 'vl' ? new ModeA(desc) : new ModeAM(desc);
}

/**
 * The webtoon tile chain: restore + exactly one x2, no `Downscale`. The fixed 2x
 * output is the band canvas's backing store as is (see `webtoon-band-plan.ts`).
 */
export function buildTileChain(chain: EnhanceChain, device: GPUDevice, inputTexture: GPUTexture): Anime4KPipeline[] {
  const clamp = new ClampHighlights({ device, inputTexture });
  const restore = chain === 'vl'
    ? new CNNVL({ device, inputTexture: clamp.getOutputTexture() })
    : new CNNM({ device, inputTexture: clamp.getOutputTexture() });
  const x2 = chain === 'vl'
    ? new CNNx2VL({ device, inputTexture: restore.getOutputTexture() })
    : new CNNx2M({ device, inputTexture: restore.getOutputTexture() });
  return [clamp, restore, x2];
}

/** One encodable step of a chain. */
export type EncodeStep = (encoder: GPUCommandEncoder) => void;

/**
 * Flatten a chain into its leaf passes, in order, so a tile can be encoded a few
 * passes at a time across frames. Composite classes (`CNNM`, `CNNx2VL`, presets)
 * expose a public `pipelines` ARRAY; a leaf either has none or, like
 * `ClampHighlights`, an object of raw `GPUComputePipeline`s (its own `pass()`
 * encodes all three of them).
 */
export function flattenPasses(pipelines: readonly Anime4KPipeline[]): EncodeStep[] {
  const steps: EncodeStep[] = [];
  const visit = (p: Anime4KPipeline) => {
    const children = (p as { pipelines?: unknown }).pipelines;
    if (Array.isArray(children)) { for (const child of children as Anime4KPipeline[]) visit(child); return; }
    steps.push((encoder) => p.pass(encoder));
  };
  for (const p of pipelines) visit(p);
  return steps;
}
