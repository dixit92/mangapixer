import { vi } from 'vitest';

import { releaseUpscaler, renderUpscaled } from './anime4k-renderer';
import {
  FakeCanvasContext, FakeGpuDevice, fakeCanvas, fakeImage, installNavigatorGpu, removeNavigatorGpu, stubWebGpuGlobals,
} from './fake-webgpu.testing';
import { resetGpuDeviceForTests } from './gpu-device';
import type { EnhanceChain } from './webtoon-band-plan';

/**
 * GPU memory ownership of the paged Enhance renderer (Phase 0 of the Webtoon
 * Enhance design). The REAL `anime4k-webgpu` `ModeA` is constructed against a
 * fake device, so these counts are what the shipped library actually allocates:
 * every texture/buffer it creates must be destroyed when the size-keyed state is
 * replaced and on `releaseUpscaler()`, exactly once, and nothing destroyed may
 * ever be used again (the fake device throws if it is).
 */
describe('anime4k-renderer GPU disposal', () => {
  let devices: FakeGpuDevice[];

  // These disposal tests predate 1.24.0 and exercise the heavy VL chain (paged
  // "Max quality"); the M default is covered in its own describe below.
  function render(nw: number, nh: number, tw: number, th: number, chain: EnhanceChain = 'vl'): Promise<boolean> {
    return renderUpscaled({ source: fakeImage(nw, nh), canvas: fakeCanvas(), targetWidth: tw, targetHeight: th, chain });
  }

  const live = (d: FakeGpuDevice) =>
    [...d.textures, ...d.buffers].filter((r) => !r.destroyed).length;

  beforeEach(() => {
    stubWebGpuGlobals(vi.stubGlobal);
    vi.stubGlobal('createImageBitmap', () => Promise.resolve({ close: () => undefined }));
    devices = [];
    installNavigatorGpu(() => {
      const d = new FakeGpuDevice();
      devices.push(d);
      return d;
    });
    resetGpuDeviceForTests();
    releaseUpscaler();
  });

  afterEach(() => {
    // No texture/buffer was ever used after being destroyed, in any test.
    expect(devices.flatMap((d) => d.violations)).toEqual([]);
    releaseUpscaler();
    resetGpuDeviceForTests();
    removeNavigatorGpu();
    vi.unstubAllGlobals();
  });

  it('builds the heavy VL chain: dozens of textures for one page, all live while cached', async () => {
    expect(await render(800, 1200, 1400, 2100)).toBe(true); // s = 1.75: Clamp + CNNVL + CNNx2VL + Downscale
    const [d] = devices;
    expect(d.textures.length).toBeGreaterThan(30);
    expect(live(d)).toBe(d.textures.length + d.buffers.length);
    expect(d.submits).toBe(1);
  });

  it('reuses the cached pipeline for a same-size page without allocating', async () => {
    await render(800, 1200, 1400, 2100);
    const [d] = devices;
    const allocated = d.textures.length;
    expect(await render(800, 1200, 1400, 2100)).toBe(true);
    expect(d.textures.length).toBe(allocated);
    expect(d.textures.every((t) => !t.destroyed)).toBe(true);
  });

  it('destroys EVERY texture and buffer of the old state when the size key changes', async () => {
    await render(800, 1200, 1400, 2100);
    const [d] = devices;
    const first = [...d.textures, ...d.buffers];
    // A different page size (and a scale that adds the CNNx2M tail).
    expect(await render(600, 900, 1800, 2700)).toBe(true);
    const second = [...d.textures, ...d.buffers].slice(first.length);

    expect(first.every((r) => r.destroyCalls === 1)).toBe(true);
    expect(second.length).toBeGreaterThan(30);
    expect(second.every((r) => !r.destroyed)).toBe(true);
    expect(live(d)).toBe(second.length);
    expect(d.submits).toBe(2);
  });

  it('memory stays flat across many size changes (no growth with page count)', async () => {
    const sizes: [number, number][] = [[800, 1200], [810, 1190], [790, 1210], [800, 1150], [820, 1230]];
    // Same 1.75x scale for every page, so every pipeline has the same footprint.
    await render(800, 1200, 1400, 2100);
    const perPipeline = live(devices[0]);
    for (let i = 1; i < 30; i++) {
      const [w, h] = sizes[i % sizes.length];
      expect(await render(w, h, Math.round(w * 1.75), Math.round(h * 1.75))).toBe(true);
      expect(live(devices[0])).toBe(perPipeline);
    }
    expect(devices[0].textures.length).toBeGreaterThan(perPipeline * 20);
    expect([...devices[0].textures, ...devices[0].buffers].every((r) => r.destroyCalls <= 1)).toBe(true);
  });

  it('releaseUpscaler() destroys everything exactly once, and a later render rebuilds cleanly', async () => {
    await render(800, 1200, 1400, 2100);
    const [d] = devices;
    const before = d.textures.length;
    releaseUpscaler();
    releaseUpscaler(); // idempotent
    expect(live(d)).toBe(0);
    expect(d.textures.every((t) => t.destroyCalls === 1)).toBe(true);

    // Same size again: must NOT reuse the destroyed state (the fake throws if it did).
    expect(await render(800, 1200, 1400, 2100)).toBe(true);
    expect(d.textures.length).toBe(before * 2);
    expect(live(d)).toBe(before);
  });

  it('a release while a render is decoding does not touch the destroyed textures', async () => {
    let decoded!: (b: { close(): void }) => void;
    vi.stubGlobal('createImageBitmap', () => new Promise((r) => { decoded = r; }));
    const pending = render(800, 1200, 1400, 2100);
    await vi.waitFor(() => expect(decoded).toBeTypeOf('function'));
    releaseUpscaler();
    decoded({ close: () => undefined });
    expect(await pending).toBe(false); // falls back to the <img>; the fake would have thrown on reuse
    expect(live(devices[0])).toBe(0);
    expect(devices[0].submits).toBe(0);
  });

  it('serialises a two-page spread of different sizes so neither uses the other’s disposed state', async () => {
    const [a, b] = await Promise.all([render(800, 1200, 1400, 2100), render(700, 1100, 1225, 1925)]);
    expect(a).toBe(true);
    expect(b).toBe(true);
    expect(devices[0].submits).toBe(2);
  });

  it('a lost device destroys the old state and the next render builds on a new device', async () => {
    await render(800, 1200, 1400, 2100);
    const [lost] = devices;
    lost.lose();
    await vi.waitFor(() => expect(live(lost)).toBe(0));
    expect(await render(800, 1200, 1400, 2100)).toBe(true);
    expect(devices.length).toBe(2);
    expect(live(devices[1])).toBeGreaterThan(30);
  });

  it('a failure half-way through construction frees what was already allocated', async () => {
    await render(800, 1200, 1400, 2100);
    const [d] = devices;
    const original = d.createTexture.bind(d);
    let n = 0;
    d.createTexture = (desc) => {
      if (++n === 10) throw new Error('out of memory');
      return original(desc);
    };
    expect(await render(600, 900, 1050, 1575)).toBe(false);
    expect(live(d)).toBe(0);
  });

  it('presents through a premultiplied canvas with alpha forced to 1, so a dropped frame shows the <img>', async () => {
    // WebKit can drop a WebGPU canvas's presented frame when the compositing
    // layers around it change (iPad, Slide page turn). An 'opaque' canvas then
    // paints a black box over the page; a premultiplied one is transparent, and
    // the blit writing alpha 1 keeps every presented frame looking identical.
    const canvas = fakeCanvas();
    expect(await renderUpscaled({ source: fakeImage(800, 1200), canvas, targetWidth: 1400, targetHeight: 2100 })).toBe(true);
    const { configs } = canvas.getContext('webgpu') as unknown as FakeCanvasContext;
    expect(configs.length).toBeGreaterThan(0);
    expect(configs.every((c) => c.alphaMode === 'premultiplied')).toBe(true);
    const blit = devices[0].shaderCode.find((code) => code.includes('fn fs('));
    expect(blit).toContain('.rgb, 1.0)');
  });

  it('returns false without WebGPU and allocates nothing', async () => {
    removeNavigatorGpu();
    resetGpuDeviceForTests();
    expect(await render(800, 1200, 1400, 2100)).toBe(false);
    expect(devices.length).toBe(0);
  });
});

/**
 * 1.24.0 owner decision: paged Enhance defaults to the light M chain
 * (`ClampHighlights` -> `CNNM` -> `CNNx2M`, built by `anime4k-chains.ts`), with
 * the package's VL `ModeA` kept as the "Max quality" choice. Counts are what the
 * REAL `anime4k-webgpu` classes allocate against the fake device.
 */
describe('anime4k-renderer chain choice (M default, VL max quality)', () => {
  let devices: FakeGpuDevice[];

  const textureBytes = (d: FakeGpuDevice) =>
    d.textures.filter((t) => !t.destroyed && t.label !== 'swapchain').reduce((sum, t) => sum + t.width * t.height * 8, 0);

  beforeEach(() => {
    stubWebGpuGlobals(vi.stubGlobal);
    vi.stubGlobal('createImageBitmap', () => Promise.resolve({ close: () => undefined }));
    devices = [];
    installNavigatorGpu(() => { const d = new FakeGpuDevice(); devices.push(d); return d; });
    resetGpuDeviceForTests();
    releaseUpscaler();
  });

  afterEach(() => {
    expect(devices.flatMap((d) => d.violations)).toEqual([]);
    releaseUpscaler();
    resetGpuDeviceForTests();
    removeNavigatorGpu();
    vi.unstubAllGlobals();
  });

  function render(chain: EnhanceChain | undefined, tw = 1400, th = 2100): Promise<boolean> {
    return renderUpscaled({ source: fakeImage(800, 1200), canvas: fakeCanvas(), targetWidth: tw, targetHeight: th, chain });
  }

  it('no chain means M: input + Clamp 3 + CNNM 9 + CNNx2M 10 + Downscale 1 = 24 textures', async () => {
    expect(await render(undefined)).toBe(true);
    expect(devices[0].textures.length).toBe(24);
  });

  it('M allocates well under the VL chain for the same page (the reason it is the default)', async () => {
    await render('m');
    const m = textureBytes(devices[0]);
    releaseUpscaler();
    await render('vl');
    const vl = textureBytes(devices[0]);
    expect(m).toBeGreaterThan(0);
    expect(m / vl).toBeLessThan(0.7);
  });

  it('switching chain at the same page size is a key change: every old resource is destroyed once', async () => {
    await render('m');
    const [d] = devices;
    const first = [...d.textures, ...d.buffers];
    expect(await render('vl')).toBe(true);
    expect(first.every((r) => r.destroyCalls === 1)).toBe(true);
    const n = d.textures.length;
    expect(await render('vl')).toBe(true); // same chain + size: reused
    expect(d.textures.length).toBe(n);
  });

  it('M follows ModeA geometry: s 3 adds Downscale-to-half + a second CNNx2M, s 4.5 a second CNNx2M', async () => {
    await render('m', 2400, 3600); // s = 3
    // input 1 + Clamp 3 + CNNM 9 + CNNx2M 10 + Downscale 1 + CNNx2M 10
    expect(devices[0].textures.length).toBe(34);
    releaseUpscaler();
    const before = devices[0].textures.length;
    await render('m', 3600, 5400); // s = 4.5: no Downscale
    expect(devices[0].textures.length - before).toBe(33);
  });
});
