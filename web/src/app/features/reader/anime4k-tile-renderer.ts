/**
 * Webtoon Enhance tile renderer (1.24.0) - the lazy GPU half of webtoon Enhance.
 * Reached only through a dynamic `import()` from `webtoon-enhance-coordinator.ts`;
 * it and `anime4k-renderer.ts` are the only static importers of `anime4k-webgpu`
 * (via `anime4k-chains.ts`), so the package stays in a shared LAZY chunk.
 *
 * One call renders one BAND of one strip page (see `webtoon-band-plan.ts`):
 *  1. `createImageBitmap(img, 0, tileY, Wn, tileRows)` crops the band's tile (band
 *     plus halo) off the main thread; the transient size is one tile, never the
 *     whole tall page;
 *  2. the tile is copied into the input texture of a pipeline keyed by
 *     `(chain, Wn, tileRows)` - every full tile of a page has the same size, so one
 *     pipeline serves the whole strip. At most `maxStates` (2) pipelines are kept,
 *     least recently used first out, every texture and buffer destroyed
 *     (`gpu-device.ts` tracking);
 *  3. the chain's leaf passes (~45 for M) are encoded a few at a time, ONE submit
 *     per animation frame, the next slice waiting for `onSubmittedWorkDone`. A
 *     30-100 ms monolithic submission would starve the compositor on the tiled
 *     GPUs of a phone or iPad and make scrolling stutter. The slice size adapts
 *     (AIMD) to keep each slice's GPU time under `sliceBudgetMs`;
 *  4. the last slice also draws the band's blit into the blank guard's tiny probe
 *     target and reads it back (`blank-guard.ts`, 1.43.0); a near-black output over
 *     a band whose source is not black is answered `'blank'` and the canvas is
 *     never touched;
 *  5. otherwise one more small submission blits the band's own rows (`cropY`,
 *     `drawRows`) of the fixed 2x output onto the band canvas, whose backing store
 *     is exactly `2 * Wn x 2 * drawRows` device pixels.
 *
 * Never throws: every failure resolves `'failed'` and the caller keeps the plain
 * `<img>`, which is always a correct picture.
 */

import { buildTileChain, EncodeStep, flattenPasses } from './anime4k-chains';
import { SliceBudget, adaptSlice, createSliceBudget, nextAnimationFrame } from './band-slicing';
import {
  OutputProbe, createOutputProbe, encodeOutputProbe, isBlankOutput, popProbeScope, pushProbeScope, readOutputProbe,
} from './blank-guard';
import { TrackingDevice, acquireDevice, onDeviceLost, trackingDevice } from './gpu-device';
import type { RenderMode } from './upscale-engine';
import type { BandPlan, EnhanceChain } from './webtoon-band-plan';

// Re-exported: the slicing helpers lived here until 1.25.0 (specs and the coordinator use them).
export { adaptSlice, createSliceBudget, sliceBudgetMs } from './band-slicing';
export type { SliceBudget } from './band-slicing';

/** Pipelines kept alive: the strip width and one odd size (credits, a wider cover). */
export const maxStates = 2;

/**
 * Crop blit: one full-screen triangle sampling the band's rows of the 2x output.
 * `crop.offset` / `crop.scale` map the canvas's 0..1 v onto the output texture.
 */
const blitWGSL = /* wgsl */ `
struct VSOut {
  @builtin(position) pos: vec4<f32>,
  @location(0) uv: vec2<f32>,
};

struct Crop { offset: f32, scale: f32, pad0: f32, pad1: f32 };

@vertex
fn vs(@builtin(vertex_index) idx: u32) -> VSOut {
  var corners = array<vec2<f32>, 3>(
    vec2<f32>(-1.0, -1.0),
    vec2<f32>( 3.0, -1.0),
    vec2<f32>(-1.0,  3.0),
  );
  let p = corners[idx];
  var out: VSOut;
  out.pos = vec4<f32>(p, 0.0, 1.0);
  out.uv = vec2<f32>((p.x + 1.0) * 0.5, 1.0 - (p.y + 1.0) * 0.5);
  return out;
}

@group(0) @binding(0) var samp: sampler;
@group(0) @binding(1) var tex: texture_2d<f32>;
@group(0) @binding(2) var<uniform> crop: Crop;

// Alpha forced to 1 on a premultiplied canvas: a presented frame is opaque, a
// frame the compositor drops is transparent and the <img> underneath shows.
@fragment
fn fs(@location(0) uv: vec2<f32>) -> @location(0) vec4<f32> {
  let v = crop.offset + uv.y * crop.scale;
  return vec4<f32>(textureSample(tex, samp, vec2<f32>(uv.x, v)).rgb, 1.0);
}
`;

export interface BandRenderRequest {
  /** The decoded strip page (`naturalWidth` is `Wn`). */
  readonly source: HTMLImageElement;
  /** The band canvas; its backing store is set to `2 * Wn x 2 * band.drawRows`. */
  readonly canvas: HTMLCanvasElement;
  readonly band: BandPlan;
  readonly chain: EnhanceChain;
  /**
   * What to draw (1.25.0). The WebGPU renderer only does `enhance`; the WebGL2
   * renderer (`webgl-upscaler.ts`) does both. Default `enhance`.
   */
  readonly mode?: RenderMode;
  /** Checked between slices: an aborted job finishes its current slice and stops. */
  readonly signal?: AbortSignal;
  readonly budget?: SliceBudget;
  /** Resolves on the next animation frame (test seam). */
  readonly nextFrame?: () => Promise<void>;
}

export interface BandRenderResult {
  /**
   * `blank` (1.43.0): the chain output came back near black over a band whose source is not black
   * (`blank-guard.ts`); the canvas was not touched - keep the plain `<img>`.
   */
  readonly status: 'ok' | 'aborted' | 'failed' | 'blank';
  /** Sum of the slices' submit-to-done times (the ms/band readout). */
  readonly gpuMs: number;
  /** Slices submitted for this band. */
  readonly slices: number;
}

interface DeviceState {
  device: GPUDevice;
  canvasFormat: GPUTextureFormat;
  blit: GPURenderPipeline;
  sampler: GPUSampler;
}

interface RenderState {
  key: string;
  resources: TrackingDevice;
  inputTexture: GPUTexture;
  steps: EncodeStep[];
  uniform: GPUBuffer;
  bindGroup: GPUBindGroup;
  probe: OutputProbe;
}

let deviceState: DeviceState | null = null;
/** Most recently used LAST. */
let states: RenderState[] = [];
let queue: Promise<unknown> = Promise.resolve();
const lostListeners = new Set<() => void>();

onDeviceLost((device) => {
  if (deviceState?.device !== device) return;
  disposeStates();
  deviceState = null;
  for (const listener of [...lostListeners]) {
    try { listener(); } catch { /* a listener must not block the others */ }
  }
});

/** Be told when the GPU device the tiles were rendered on is lost. Returns an unsubscribe. */
export function onTilesLost(listener: () => void): () => void {
  lostListeners.add(listener);
  return () => { lostListeners.delete(listener); };
}

function disposeStates(): void {
  const old = states;
  states = [];
  for (const s of old) s.resources.dispose();
}

/** How many pipelines are alive (tests, diagnostics). */
export function liveStateCount(): number {
  return states.length;
}

/** Render one band. Serialised: bands never interleave on the shared pipeline textures. */
export function renderBand(req: BandRenderRequest): Promise<BandRenderResult> {
  const run = queue.then(() => renderNow(req));
  queue = run.catch(() => undefined);
  return run;
}

function ensureDeviceState(device: GPUDevice): DeviceState {
  if (deviceState?.device === device) return deviceState;
  disposeStates();
  const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
  const canvasFormat = gpu?.getPreferredCanvasFormat?.() ?? 'bgra8unorm';
  const module = device.createShaderModule({ code: blitWGSL, label: 'mp-tile-blit' });
  deviceState = {
    device,
    canvasFormat,
    blit: device.createRenderPipeline({
      label: 'mp-tile-blit',
      layout: 'auto',
      vertex: { module, entryPoint: 'vs' },
      fragment: { module, entryPoint: 'fs', targets: [{ format: canvasFormat }] },
      primitive: { topology: 'triangle-list' },
    }),
    sampler: device.createSampler({ magFilter: 'linear', minFilter: 'linear' }),
  };
  return deviceState;
}

function stateFor(device: GPUDevice, shared: DeviceState, chain: EnhanceChain, width: number, tileRows: number): RenderState {
  const key = `${chain}:${width}x${tileRows}`;
  const hit = states.find((s) => s.key === key);
  if (hit) {
    states = [...states.filter((s) => s !== hit), hit];
    return hit;
  }
  // Make room first, so the peak is never maxStates + 1 pipelines.
  while (states.length >= maxStates) {
    const [evicted, ...rest] = states;
    states = rest;
    evicted.resources.dispose();
  }
  const resources = trackingDevice(device);
  try {
    const inputTexture = resources.device.createTexture({
      label: 'mp-tile-src',
      size: [width, tileRows, 1],
      format: 'rgba8unorm',
      usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
    });
    const chainPipelines = buildTileChain(chain, resources.device, inputTexture);
    const output = chainPipelines[chainPipelines.length - 1].getOutputTexture();
    const uniform = resources.device.createBuffer({
      label: 'mp-tile-crop',
      size: 16,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });
    const state: RenderState = {
      key,
      resources,
      inputTexture,
      steps: flattenPasses(chainPipelines),
      uniform,
      probe: createOutputProbe(resources.device, shared.canvasFormat),
      bindGroup: device.createBindGroup({
        layout: shared.blit.getBindGroupLayout(0),
        entries: [
          { binding: 0, resource: shared.sampler },
          { binding: 1, resource: output.createView() },
          { binding: 2, resource: { buffer: uniform } },
        ],
      }),
    };
    states = [...states, state];
    return state;
  } catch (err) {
    resources.dispose(); // a half-built chain still owns what it allocated
    throw err;
  }
}

async function renderNow(req: BandRenderRequest): Promise<BandRenderResult> {
  const { source, canvas, band, chain, signal } = req;
  const budget = req.budget ?? createSliceBudget();
  const nextFrame = req.nextFrame ?? nextAnimationFrame;
  let gpuMs = 0;
  let slices = 0;
  const result = (status: BandRenderResult['status']): BandRenderResult => ({ status, gpuMs, slices });

  const width = source.naturalWidth;
  if (!(width > 0) || !(source.naturalHeight >= band.tileY + band.tileRows) || !(band.drawRows > 0)) return result('failed');
  if (signal?.aborted) return result('aborted');

  try {
    const device = await acquireDevice();
    if (!device) return result('failed');
    const maxDim = device.limits?.maxTextureDimension2D ?? 8192;
    if (2 * width > maxDim || 2 * band.tileRows > maxDim) return result('failed');

    const context = canvas.getContext('webgpu') as GPUCanvasContext | null;
    if (!context) return result('failed');

    const shared = ensureDeviceState(device);
    const state = stateFor(device, shared, chain, width, band.tileRows);
    const alive = () => deviceState === shared && states.includes(state);

    const bitmap = await createImageBitmap(source, 0, band.tileY, width, band.tileRows);
    try {
      if (!alive()) return result('failed');
      if (signal?.aborted) return result('aborted');
      device.queue.copyExternalImageToTexture({ source: bitmap }, { texture: state.inputTexture }, [width, band.tileRows]);
    } finally {
      bitmap.close();
    }

    let next = 0;
    const steps = state.steps;
    let probeRan = false;
    while (next < steps.length) {
      if (signal?.aborted) return result('aborted');
      if (!alive()) return result('failed');
      const encoder = device.createCommandEncoder({ label: 'mp-tile' });
      const end = Math.min(steps.length, next + Math.max(1, budget.k));
      for (let i = next; i < end; i++) steps[i](encoder);
      next = end;
      const last = next >= steps.length;

      if (last) {
        // Last slice: the blank guard's probe (the band's blit into a tiny private target, read back below).
        device.queue.writeBuffer(state.uniform, 0,
          new Float32Array([band.cropY / band.tileRows, band.drawRows / band.tileRows, 0, 0]));
        encodeOutputProbe(encoder, state.probe, shared.blit, state.bindGroup);
        pushProbeScope(device);
      }

      const started = performance.now();
      device.queue.submit([encoder.finish()]);
      slices++;
      const scope = last ? popProbeScope(device) : null;
      await device.queue.onSubmittedWorkDone();
      if (scope) probeRan = await scope;
      const elapsed = performance.now() - started;
      gpuMs += elapsed;
      adaptSlice(budget, elapsed);
      if (next < steps.length) await nextFrame();
    }

    const output = probeRan ? await readOutputProbe(state.probe) : null;
    // Dropped or released meanwhile: never overwrite a canvas with a band it no longer shows.
    if (signal?.aborted) return result('aborted');
    if (!alive()) return result('failed');
    if (isBlankOutput(output, source, { y: band.tileY + band.cropY, height: band.drawRows })) return result('blank');

    // Present: blit the band's rows onto the canvas (a tiny pass; the chain output is already done).
    const targetW = 2 * width;
    const targetH = 2 * band.drawRows;
    if (canvas.width !== targetW) canvas.width = targetW;
    if (canvas.height !== targetH) canvas.height = targetH;
    context.configure({ device, format: shared.canvasFormat, alphaMode: 'premultiplied' });
    const encoder = device.createCommandEncoder({ label: 'mp-tile-present' });
    const pass = encoder.beginRenderPass({
      colorAttachments: [{
        view: context.getCurrentTexture().createView(),
        // theme-exempt: the upscaler's own render target, cleared before the page is drawn into it - part of the processed image, not the UI.
        clearValue: { r: 0, g: 0, b: 0, a: 1 },
        loadOp: 'clear',
        storeOp: 'store',
      }],
    });
    pass.setPipeline(shared.blit);
    pass.setBindGroup(0, state.bindGroup);
    pass.draw(3);
    pass.end();
    const started = performance.now();
    device.queue.submit([encoder.finish()]);
    await device.queue.onSubmittedWorkDone();
    gpuMs += performance.now() - started;
    return result('ok');
  } catch {
    return result('failed');
  }
}

/**
 * Destroy every tile pipeline's textures and buffers (Enhance off, view switched
 * to paged, reader closed, page hidden for a minute). The device stays with
 * `gpu-device.ts`; the next band rebuilds lazily.
 */
export function releaseTiles(): void {
  disposeStates();
  deviceState = null;
}
