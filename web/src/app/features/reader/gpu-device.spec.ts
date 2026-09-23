import { vi } from 'vitest';

import { FakeGpuDevice, installNavigatorGpu, removeNavigatorGpu } from './fake-webgpu.testing';
import { acquireDevice, onDeviceLost, resetGpuDeviceForTests, trackingDevice } from './gpu-device';

describe('trackingDevice', () => {
  it('records every texture and buffer created through the proxy and destroys each exactly once', () => {
    const real = new FakeGpuDevice();
    const tracked = trackingDevice(real.asGpuDevice());
    tracked.device.createTexture({ size: [4, 4, 1], format: 'rgba8unorm', usage: 0 });
    tracked.device.createTexture({ size: [8, 8, 1], format: 'rgba16float', usage: 0 });
    tracked.device.createBuffer({ size: 4, usage: 0 });
    expect(tracked.size).toBe(3);

    tracked.dispose();
    tracked.dispose(); // idempotent
    expect(tracked.size).toBe(0);
    expect([...real.textures, ...real.buffers].map((r) => r.destroyCalls)).toEqual([1, 1, 1]);
  });

  it('does not own resources created on the real device directly', () => {
    const real = new FakeGpuDevice();
    const tracked = trackingDevice(real.asGpuDevice());
    real.createTexture({ size: [4, 4, 1], format: 'rgba8unorm', usage: 0 });
    tracked.dispose();
    expect(real.textures[0].destroyed).toBe(false);
  });

  it('forwards every other member bound to the real device', () => {
    const real = new FakeGpuDevice();
    const tracked = trackingDevice(real.asGpuDevice());
    expect(tracked.device.queue).toBe(real.queue as unknown as GPUQueue);
    expect(tracked.device.lost).toBe(real.lost);
    const receiver = vi.fn(function (this: unknown) { return this; });
    (real as unknown as { brandChecked: unknown }).brandChecked = receiver;
    const bound = (tracked.device as unknown as { brandChecked: () => unknown }).brandChecked;
    expect(bound()).toBe(real);
  });

  it('keeps disposing when one destroy() throws (e.g. device already lost)', () => {
    const real = new FakeGpuDevice();
    const tracked = trackingDevice(real.asGpuDevice());
    const a = tracked.device.createTexture({ size: [1, 1, 1], format: 'rgba8unorm', usage: 0 });
    tracked.device.createTexture({ size: [1, 1, 1], format: 'rgba8unorm', usage: 0 });
    a.destroy = () => { throw new Error('gone'); };
    expect(() => tracked.dispose()).not.toThrow();
    expect(real.textures[1].destroyed).toBe(true);
  });
});

describe('acquireDevice', () => {
  afterEach(() => {
    resetGpuDeviceForTests();
    removeNavigatorGpu();
  });

  it('resolves to null without navigator.gpu', async () => {
    removeNavigatorGpu();
    await expect(acquireDevice()).resolves.toBeNull();
  });

  it('resolves to null when there is no adapter, and never throws when requestAdapter rejects', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    await expect(acquireDevice()).resolves.toBeNull();
    resetGpuDeviceForTests();
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.reject(new Error('no')) };
    await expect(acquireDevice()).resolves.toBeNull();
  });

  it('hands out one device, and a new one after the first is lost (notifying listeners)', async () => {
    const made: FakeGpuDevice[] = [];
    installNavigatorGpu(() => { const d = new FakeGpuDevice(); made.push(d); return d; });
    const first = await acquireDevice();
    expect(await acquireDevice()).toBe(first);

    const lost = vi.fn();
    const unsubscribe = onDeviceLost(lost);
    made[0].lose();
    await vi.waitFor(() => expect(lost).toHaveBeenCalledWith(first));
    unsubscribe();

    const second = await acquireDevice();
    expect(second).not.toBe(first);
    expect(made.length).toBe(2);
  });
});
