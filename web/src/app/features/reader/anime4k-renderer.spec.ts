import { vi } from 'vitest';

import { releaseUpscaler, renderUpscaled } from './anime4k-renderer';
import {
  FakeCanvasContext, FakeGpuDevice, fakeCanvas, fakeImage, installNavigatorGpu, removeNavigatorGpu, stubWebGpuGlobals,
} from './fake-webgpu.testing';
import { resetGpuDeviceForTests } from './gpu-device';

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

  function render(nw: number, nh: number, tw: number, th: number): Promise<boolean> {
    return renderUpscaled({ source: fakeImage(nw, nh), canvas: fakeCanvas(), targetWidth: tw, targetHeight: th });
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
