import { vi } from 'vitest';

import {
  BandRenderRequest, adaptSlice, createSliceBudget, liveStateCount, onTilesLost, releaseTiles, renderBand, sliceBudgetMs,
} from './anime4k-tile-renderer';
import { FakeCanvasContext, FakeGpuDevice, fakeCanvas, fakeImage, installNavigatorGpu, removeNavigatorGpu, stubWebGpuGlobals } from './fake-webgpu.testing';
import { resetGpuDeviceForTests } from './gpu-device';
import { BandPlan, planBands } from './webtoon-band-plan';

/**
 * The webtoon tile renderer builds the REAL `anime4k-webgpu` M / VL tile chains
 * against the fake device, so texture counts are what the library allocates, and
 * the fake device records any use of a destroyed texture or buffer.
 */

/** Counts the compute / render passes each submitted command buffer carried. */
class CountingDevice extends FakeGpuDevice {
  computePasses = 0;
  renderPasses = 0;
  override createCommandEncoder() {
    const encoder = super.createCommandEncoder();
    return {
      ...encoder,
      beginComputePass: () => { this.computePasses++; return encoder.beginComputePass(); },
      beginRenderPass: () => { this.renderPasses++; return encoder.beginRenderPass(); },
    };
  }
}

describe('anime4k-tile-renderer', () => {
  let devices: CountingDevice[];
  let bitmapCalls: unknown[][];

  const live = (d: FakeGpuDevice) => [...d.textures, ...d.buffers].filter((r) => !r.destroyed).length;
  const band = (w: number, h: number, i = 0): BandPlan => planBands(w, h)[i];
  const req = (w: number, h: number, i = 0, extra: Partial<BandRenderRequest> = {}): BandRenderRequest => ({
    source: fakeImage(w, h), canvas: fakeCanvas(), band: band(w, h, i), chain: 'm',
    nextFrame: () => Promise.resolve(), ...extra,
  });

  beforeEach(() => {
    stubWebGpuGlobals(vi.stubGlobal);
    bitmapCalls = [];
    vi.stubGlobal('createImageBitmap', (...args: unknown[]) => {
      bitmapCalls.push(args);
      return Promise.resolve({ close: () => undefined });
    });
    devices = [];
    installNavigatorGpu(() => { const d = new CountingDevice(); devices.push(d); return d; });
    resetGpuDeviceForTests();
    releaseTiles();
  });

  afterEach(() => {
    expect(devices.flatMap((d) => d.violations)).toEqual([]);
    releaseTiles();
    resetGpuDeviceForTests();
    removeNavigatorGpu();
    vi.unstubAllGlobals();
  });

  it('M tile: input + Clamp 3 + CNNM 9 + CNNx2M 10 textures and one crop uniform', async () => {
    const r = await renderBand(req(800, 3000, 1));
    expect(r.status).toBe('ok');
    const [d] = devices;
    expect(d.textures.length).toBe(23);
    expect(d.buffers.length).toBe(1);
    // The 2x output (DepthToSpace / Overlay of CNNx2M) is exactly twice the tile.
    expect(d.textures.some((t) => t.width === 1600 && t.height === 2 * 432)).toBe(true);
  });

  it('VL tile allocates more (CNNVL + CNNx2VL) than M for the same band', async () => {
    await renderBand(req(800, 3000, 1, { chain: 'vl' }));
    expect(devices[0].textures.length).toBeGreaterThan(23 + 10);
  });

  it('crops the band tile off the page and sizes the canvas to the fixed 2x backing', async () => {
    const r = req(800, 3000, 2);
    await renderBand(r);
    const b = r.band;
    expect(bitmapCalls[0]).toEqual([r.source, 0, b.tileY, 800, b.tileRows]);
    expect(r.canvas.width).toBe(1600);
    expect(r.canvas.height).toBe(2 * b.drawRows);
    const { configs } = r.canvas.getContext('webgpu') as unknown as FakeCanvasContext;
    expect(configs.every((c) => c.alphaMode === 'premultiplied')).toBe(true);
  });

  it('writes the crop uniform (offset, scale) for the band rows inside the tile', async () => {
    const writes: Float32Array[] = [];
    const r = req(800, 3000, 2);
    await renderBand({ ...r, nextFrame: () => Promise.resolve() });
    // Re-render with a spy on the live device's writeBuffer.
    const [d] = devices;
    const orig = d.queue.writeBuffer;
    d.queue.writeBuffer = ((buffer: never, _o: number, data: Float32Array) => { writes.push(data); return orig(buffer); }) as never;
    await renderBand(req(800, 3000, 2));
    const b = r.band;
    expect(Array.from(writes[0])).toEqual([b.cropY / b.tileRows, b.drawRows / b.tileRows, 0, 0].map(Math.fround));
  });

  it('reuses the pipeline for every full band of the strip (one key per width)', async () => {
    for (let i = 0; i < 5; i++) expect((await renderBand(req(800, 3000, i))).status).toBe('ok');
    const [d] = devices;
    expect(d.textures.length).toBe(23);
    expect(liveStateCount()).toBe(1);
  });

  it('keeps at most 2 pipelines: a third key evicts the least recently used, destroying all of it once', async () => {
    await renderBand(req(800, 3000));          // A
    const [d] = devices;
    const snapshot = () => ({ t: d.textures.length, b: d.buffers.length });
    const afterA = snapshot();
    const a = [...d.textures, ...d.buffers];
    await renderBand(req(800, 300));           // B: short page, whole-page tile
    const afterB = snapshot();
    await renderBand(req(800, 3000, 1));       // A again: now most recent
    await renderBand(req(1080, 3000));         // C evicts B
    expect(liveStateCount()).toBe(2);
    expect(a.every((r) => !r.destroyed)).toBe(true);
    const b = [...d.textures.slice(afterA.t, afterB.t), ...d.buffers.slice(afterA.b, afterB.b)];
    expect(b.length).toBe(24);
    expect(b.every((r) => r.destroyCalls === 1)).toBe(true);
  });

  it('slices the chain across frames and encodes every pass exactly once, then the blit', async () => {
    const nextFrame = vi.fn(() => Promise.resolve());
    const r = await renderBand(req(800, 3000, 1, { nextFrame, budget: { k: 2 } }));
    const [d] = devices;
    expect(r.slices).toBeGreaterThan(1);
    expect(d.submits).toBe(r.slices);
    expect(nextFrame).toHaveBeenCalledTimes(r.slices - 1);
    // M: Clamp 3 + CNNM 8 convs + CNNx2M 8 convs + DepthToSpace = 20 compute passes;
    // CNNM and CNNx2M overlays + the crop blit = 3 render passes.
    expect(d.computePasses).toBe(20);
    expect(d.renderPasses).toBe(3);
  });

  it('AIMD: the slice grows by one pass under budget and halves over it', () => {
    const b = createSliceBudget();
    expect(b.k).toBe(4);
    adaptSlice(b, sliceBudgetMs - 1);
    expect(b.k).toBe(5);
    adaptSlice(b, sliceBudgetMs + 10);
    expect(b.k).toBe(2);
    adaptSlice(b, 100); adaptSlice(b, 100);
    expect(b.k).toBe(1);
  });

  it('an aborted job stops after its current slice and never touches the canvas', async () => {
    const ctrl = new AbortController();
    const r = req(800, 3000, 1, { signal: ctrl.signal, budget: { k: 2 }, nextFrame: () => { ctrl.abort(); return Promise.resolve(); } });
    const res = await renderBand(r);
    expect(res.status).toBe('aborted');
    expect(res.slices).toBe(1);
    expect(devices[0].submits).toBe(1);
    expect((r.canvas.getContext('webgpu') as unknown as FakeCanvasContext).configs.length).toBe(0);
  });

  it('a job aborted before it starts allocates nothing', async () => {
    const ctrl = new AbortController();
    ctrl.abort();
    expect((await renderBand(req(800, 3000, 0, { signal: ctrl.signal }))).status).toBe('aborted');
    expect(devices.length).toBe(0);
  });

  it('releaseTiles() destroys everything exactly once; the next band rebuilds', async () => {
    await renderBand(req(800, 3000));
    await renderBand(req(800, 300));
    const [d] = devices;
    const all = [...d.textures, ...d.buffers];
    releaseTiles();
    releaseTiles();
    expect(all.every((r) => r.destroyCalls === 1)).toBe(true);
    expect(live(d)).toBe(0);
    expect((await renderBand(req(800, 3000))).status).toBe('ok');
    expect(live(d)).toBe(24);
  });

  it('a release during the tile decode does not touch the destroyed textures', async () => {
    let finish!: () => void;
    vi.stubGlobal('createImageBitmap', () => new Promise((resolve) => { finish = () => resolve({ close: () => undefined }); }));
    const pending = renderBand(req(800, 3000));
    await vi.waitFor(() => expect(finish).toBeTypeOf('function'));
    releaseTiles();
    finish();
    expect((await pending).status).toBe('failed');
  });

  it('device loss frees the pipelines and notifies listeners; the next band uses a new device', async () => {
    const lost = vi.fn();
    const off = onTilesLost(lost);
    await renderBand(req(800, 3000));
    const [d] = devices;
    d.lose();
    await vi.waitFor(() => expect(lost).toHaveBeenCalledTimes(1));
    expect(live(d)).toBe(0);
    expect((await renderBand(req(800, 3000))).status).toBe('ok');
    expect(devices.length).toBe(2);
    off();
  });

  it('fails (plain <img>) without WebGPU, for a source too wide for 2x, or a band outside the image', async () => {
    const tooWide = fakeImage(5000, 3000);
    expect((await renderBand({ ...req(5000, 3000), source: tooWide })).status).toBe('failed');
    expect((await renderBand({ ...req(800, 3000), source: fakeImage(800, 100) })).status).toBe('failed');
    removeNavigatorGpu();
    resetGpuDeviceForTests();
    releaseTiles();
    const before = devices.length;
    expect((await renderBand(req(800, 3000))).status).toBe('failed');
    expect(devices.length).toBe(before);
  });
});
