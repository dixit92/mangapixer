import { vi } from 'vitest';

import {
  BandRenderRequest, adaptSlice, createSliceBudget, liveStateCount, onTilesLost, releaseTiles, renderBand, sliceBudgetMs,
} from './anime4k-tile-renderer';
import { FakeCanvasContext, FakeGpuDevice, fakeCanvas, fakeImage, installNavigatorGpu, removeNavigatorGpu, stubWebGpuGlobals } from './fake-webgpu.testing';
import { SourceRows, probeBytes, setSourceSamplerForTests } from './blank-guard';
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

  it('M tile: input + Clamp 3 + CNNM 9 + CNNx2M 10 + blank probe textures, one crop uniform and one probe readback', async () => {
    const r = await renderBand(req(800, 3000, 1));
    expect(r.status).toBe('ok');
    const [d] = devices;
    expect(d.textures.length).toBe(24);
    expect(d.buffers.length).toBe(2);
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
    expect(d.textures.length).toBe(24);
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
    expect(b.length).toBe(26);
    expect(b.every((r) => r.destroyCalls === 1)).toBe(true);
  });

  it('slices the chain across frames and encodes every pass exactly once, then the probe, then the blit', async () => {
    const nextFrame = vi.fn(() => Promise.resolve());
    const r = await renderBand(req(800, 3000, 1, { nextFrame, budget: { k: 2 } }));
    const [d] = devices;
    expect(r.slices).toBeGreaterThan(1);
    // 1.43.0: the chain slices (the last one carries the blank guard's probe), then one present submission.
    expect(d.submits).toBe(r.slices + 1);
    expect(nextFrame).toHaveBeenCalledTimes(r.slices - 1);
    // M: Clamp 3 + CNNM 8 convs + CNNx2M 8 convs + DepthToSpace = 20 compute passes;
    // CNNM and CNNx2M overlays + the probe blit + the crop blit = 4 render passes.
    expect(d.computePasses).toBe(20);
    expect(d.renderPasses).toBe(4);
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
    expect(live(d)).toBe(26);
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

/**
 * Blank-output guard (1.43.0): WebKit's WebGPU now and then hands back an all-black chain output; the blit forces
 * alpha 1, so it would paint an opaque black block over a band that is not black. The last slice reads back a tiny
 * probe of the band's blit; near black over a lit source = `blank`, and the canvas is never touched.
 */
describe('anime4k-tile-renderer blank guard', () => {
  let devices: FakeGpuDevice[];
  let output: () => readonly [number, number, number, number];
  let sourceRows: SourceRows[];
  let sourceLit: boolean;

  const req = (w: number, h: number, i = 0, extra: Partial<BandRenderRequest> = {}): BandRenderRequest => ({
    source: fakeImage(w, h), canvas: fakeCanvas(), band: planBands(w, h)[i], chain: 'm',
    nextFrame: () => Promise.resolve(), ...extra,
  });
  const configs = (canvas: HTMLCanvasElement) => (canvas.getContext('webgpu') as unknown as FakeCanvasContext).configs;

  beforeEach(() => {
    stubWebGpuGlobals(vi.stubGlobal);
    vi.stubGlobal('createImageBitmap', () => Promise.resolve({ close: () => undefined }));
    output = () => [0, 0, 0, 255];
    sourceRows = [];
    sourceLit = true;
    setSourceSamplerForTests((_img, rows) => {
      sourceRows.push(rows);
      return new Uint8ClampedArray(probeBytes).fill(sourceLit ? 255 : 0);
    });
    devices = [];
    installNavigatorGpu(() => { const d = new FakeGpuDevice(); d.output = () => output(); devices.push(d); return d; });
    resetGpuDeviceForTests();
    releaseTiles();
  });

  afterEach(() => {
    expect(devices.flatMap((d) => d.violations)).toEqual([]);
    setSourceSamplerForTests(null);
    releaseTiles();
    resetGpuDeviceForTests();
    removeNavigatorGpu();
    vi.unstubAllGlobals();
  });

  it('a black output over a lit band is blank: the canvas is never sized, configured or drawn', async () => {
    const r = req(800, 3000, 1, { budget: { k: 4 } });
    const res = await renderBand(r);
    expect(res.status).toBe('blank');
    const [d] = devices;
    expect(d.submits).toBe(res.slices); // the chain slices only: no present submission
    expect(configs(r.canvas).length).toBe(0);
    expect([r.canvas.width, r.canvas.height]).toEqual([0, 0]);
  });

  it('samples the source rows the band SHOWS (tile offset + crop), from the page <img>', async () => {
    const r = req(800, 3000, 2);
    await renderBand(r);
    expect(sourceRows).toEqual([{ y: r.band.tileY + r.band.cropY, height: r.band.drawRows }]);
  });

  it('a black output over a black band is shown (a genuinely black page is no false positive)', async () => {
    sourceLit = false;
    const r = req(800, 3000, 1);
    expect((await renderBand(r)).status).toBe('ok');
    expect(configs(r.canvas).length).toBe(1);
    expect(r.canvas.width).toBe(1600);
  });

  it('a normal output is shown as before and never samples the source (no CPU cost)', async () => {
    output = () => [180, 170, 160, 255];
    const r = req(800, 3000, 1);
    const res = await renderBand(r);
    expect(res.status).toBe('ok');
    expect(sourceRows.length).toBe(0);
    expect(configs(r.canvas).length).toBe(1);
  });

  it('costs one 4 KB readback per band (a 256-byte-aligned copy) and one small present submission', async () => {
    output = () => [180, 170, 160, 255];
    const res = await renderBand(req(800, 3000, 1, { budget: { k: 2 } }));
    const [d] = devices;
    expect(d.copiesToBuffer).toBe(1);
    expect(d.bytesReadBack).toBe(4096);
    expect(d.submits).toBe(res.slices + 1);
  });

  it('a blank band and then a good one: the next render of the same band is shown', async () => {
    let n = 0;
    output = () => (n++ === 0 ? [0, 0, 0, 255] : [200, 200, 200, 255]);
    expect((await renderBand(req(800, 3000, 1))).status).toBe('blank');
    const again = req(800, 3000, 1);
    expect((await renderBand(again)).status).toBe('ok');
    expect(configs(again.canvas).length).toBe(1);
  });

  it('a probe that could not run (validation error) is unknown, never blank: the band shows as before', async () => {
    installNavigatorGpu(() => {
      const d = new FakeGpuDevice(); d.output = () => output(); d.failNextScope = true; devices.push(d); return d;
    });
    resetGpuDeviceForTests();
    releaseTiles();
    const r = req(800, 3000, 1);
    expect((await renderBand(r)).status).toBe('ok');
    expect(sourceRows.length).toBe(0);
  });

  it('aborted during the chain stays aborted (no readback verdict, canvas untouched)', async () => {
    const ctrl = new AbortController();
    const r = req(800, 3000, 1, { signal: ctrl.signal, budget: { k: 2 }, nextFrame: () => { ctrl.abort(); return Promise.resolve(); } });
    expect((await renderBand(r)).status).toBe('aborted');
    expect(devices[0].copiesToBuffer).toBe(0);
    expect(configs(r.canvas).length).toBe(0);
  });

  it('released while the probe is read back: failed, and nothing destroyed is touched', async () => {
    let release = false;
    installNavigatorGpu(() => {
      const d = new FakeGpuDevice();
      d.output = () => output();
      const done = d.queue.onSubmittedWorkDone;
      d.queue.onSubmittedWorkDone = () => { if (release && d.copiesToBuffer > 0) releaseTiles(); return done(); };
      devices.push(d);
      return d;
    });
    resetGpuDeviceForTests();
    releaseTiles();
    release = true;
    const r = req(800, 3000, 1);
    expect((await renderBand(r)).status).toBe('failed');
    expect(configs(r.canvas).length).toBe(0);
  });
});
