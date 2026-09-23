/**
 * Shared WebGPU device + GPU-memory ownership helpers for the lazy Enhance
 * renderers. Imported only by `anime4k-renderer.ts` (itself reached through a
 * dynamic `import()`), so none of this lands in the initial bundle.
 *
 * Why a tracking device: `anime4k-webgpu` 1.0.0 has no `destroy()` on any of its
 * pipeline classes, yet a single `ModeA` allocates dozens of full-size
 * `rgba16float` textures. Dropping the JS object leaves those to garbage
 * collection, and GC does not see GPU memory pressure, so the textures linger
 * long after they are unreachable. Handing the library a `Proxy` of the device
 * that records every `createTexture` / `createBuffer` lets us own, and
 * explicitly `destroy()`, everything it allocated. Pipelines, bind groups,
 * samplers and shader modules have no destroy method in WebGPU and are left to
 * GC.
 */

/** A device wrapper that records the GPU memory allocated through it. */
export interface TrackingDevice {
  /** Pass this to code whose allocations we must own. */
  readonly device: GPUDevice;
  /** How many textures + buffers are currently owned (0 after `dispose()`). */
  readonly size: number;
  /** `destroy()` every recorded texture and buffer exactly once. Idempotent; never throws. */
  dispose(): void;
}

interface Destroyable { destroy(): void }

/**
 * Wrap `device` so every texture and buffer created through the wrapper is
 * recorded for `dispose()`. Every other member is forwarded, with methods bound
 * to the REAL device: WebGPU objects brand-check `this`, so calling e.g.
 * `createBindGroup` with the Proxy as receiver would throw.
 */
export function trackingDevice(device: GPUDevice): TrackingDevice {
  let owned: Destroyable[] = [];
  const recording = <A extends unknown[], R extends Destroyable>(create: (...args: A) => R) =>
    (...args: A): R => {
      const resource = create.apply(device, args);
      owned.push(resource);
      return resource;
    };
  const createTexture = recording(device.createTexture);
  const createBuffer = recording(device.createBuffer);

  const proxy = new Proxy(device, {
    get(target, prop) {
      if (prop === 'createTexture') return createTexture;
      if (prop === 'createBuffer') return createBuffer;
      const value = Reflect.get(target, prop, target) as unknown;
      return typeof value === 'function' ? (value as (...a: unknown[]) => unknown).bind(target) : value;
    },
  });

  return {
    device: proxy,
    get size() { return owned.length; },
    dispose() {
      const resources = owned;
      owned = [];
      for (const resource of resources) {
        try { resource.destroy(); } catch { /* already gone (device lost) */ }
      }
    },
  };
}

let devicePromise: Promise<GPUDevice | null> | null = null;
const lostListeners = new Set<(device: GPUDevice) => void>();

/**
 * Acquire (once per device lifetime) a WebGPU device, or null when the platform
 * cannot give us one. Never throws. A lost device (driver reset, a backgrounded
 * PWA on some platforms) resets the singleton and notifies `onDeviceLost`
 * listeners, so the next request rebuilds from scratch rather than stranding
 * every later page on a dead device.
 */
export function acquireDevice(): Promise<GPUDevice | null> {
  if (devicePromise) return devicePromise;
  const attempt: Promise<GPUDevice | null> = (async () => {
    try {
      const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
      if (!gpu) return null;
      const adapter = await gpu.requestAdapter();
      if (!adapter) return null;
      const device = await adapter.requestDevice();
      device.lost.then(() => {
        if (devicePromise === attempt) devicePromise = null;
        for (const listener of [...lostListeners]) {
          try { listener(device); } catch { /* a listener must not block the others */ }
        }
      }).catch(() => { /* ignore */ });
      return device;
    } catch {
      return null;
    }
  })();
  devicePromise = attempt;
  return attempt;
}

/** Be told when a device handed out by `acquireDevice()` is lost. Returns an unsubscribe. */
export function onDeviceLost(listener: (device: GPUDevice) => void): () => void {
  lostListeners.add(listener);
  return () => { lostListeners.delete(listener); };
}

/**
 * Test seam: forget the cached device so the next `acquireDevice()` probes
 * again. Listeners stay subscribed (they belong to long-lived lazy modules).
 */
export function resetGpuDeviceForTests(): void {
  devicePromise = null;
}
