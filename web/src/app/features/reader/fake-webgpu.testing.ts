/**
 * A fake WebGPU implementation for jsdom specs (test-only; imported by specs,
 * never by app code). Just enough surface for the REAL `anime4k-webgpu`
 * pipelines to construct and encode against it, while recording every texture
 * and buffer so a spec can assert they are all destroyed, and failing loudly if
 * a destroyed one is ever used again.
 */

export class FakeResource {
  destroyed = false;
  destroyCalls = 0;
  destroy(): void {
    this.destroyCalls++;
    this.destroyed = true;
  }
}

export class FakeTexture extends FakeResource {
  readonly width: number;
  readonly height: number;
  constructor(readonly label: string | undefined, size: GPUExtent3D) {
    super();
    const dims = Array.isArray(size) ? size : [(size as GPUExtent3DDict).width, (size as GPUExtent3DDict).height ?? 1];
    this.width = Number(dims[0]);
    this.height = Number(dims[1] ?? 1);
  }
  createView(): { texture: FakeTexture } {
    if (this.destroyed) throw new Error(`createView on destroyed texture ${this.label ?? ''}`);
    return { texture: this };
  }
}

export class FakeBuffer extends FakeResource {}

interface FakeBindGroup { resources: FakeResource[] }

/** Collect the textures/buffers a bind group entry refers to. */
function resourcesOf(entries: Iterable<GPUBindGroupEntry>): FakeResource[] {
  const out: FakeResource[] = [];
  for (const entry of entries) {
    const r = entry.resource as unknown as { texture?: FakeTexture; buffer?: FakeBuffer };
    if (r instanceof FakeResource) out.push(r);
    else if (r?.texture instanceof FakeTexture) out.push(r.texture);
    else if (r?.buffer instanceof FakeBuffer) out.push(r.buffer);
  }
  return out;
}

export class FakeGpuDevice {
  readonly textures: FakeTexture[] = [];
  readonly buffers: FakeBuffer[] = [];
  submits = 0;
  private loseDevice!: (info: GPUDeviceLostInfo) => void;
  readonly lost = new Promise<GPUDeviceLostInfo>((resolve) => { this.loseDevice = resolve; });

  /** Every resource any bind group used since the last submit refers to. */
  private pending: FakeResource[] = [];
  /**
   * Every use of a destroyed resource, recorded as well as thrown: the renderer
   * swallows all errors (its fallback is the plain <img>), so a spec must check
   * this list rather than rely on the throw surfacing.
   */
  readonly violations: string[] = [];
  private readonly assertLive = (what: string, resources: FakeResource[]) => {
    const dead = resources.find((r) => r.destroyed);
    if (!dead) return;
    const message = `${what} uses a destroyed ${dead instanceof FakeTexture ? `texture ${dead.label ?? ''}` : 'buffer'}`;
    this.violations.push(message);
    throw new Error(message);
  };

  readonly queue = {
    copyExternalImageToTexture: (_src: unknown, dst: { texture: FakeTexture }) => {
      this.assertLive('copyExternalImageToTexture', [dst.texture]);
    },
    writeBuffer: (buffer: FakeBuffer) => { this.assertLive('writeBuffer', [buffer]); },
    writeTexture: (dst: { texture: FakeTexture }) => { this.assertLive('writeTexture', [dst.texture]); },
    submit: () => {
      this.assertLive('submit', this.pending);
      this.pending = [];
      this.submits++;
    },
    onSubmittedWorkDone: () => Promise.resolve(),
  };

  createTexture(desc: GPUTextureDescriptor): FakeTexture {
    const t = new FakeTexture(desc.label, desc.size);
    this.textures.push(t);
    return t;
  }

  createBuffer(): FakeBuffer {
    const b = new FakeBuffer();
    this.buffers.push(b);
    return b;
  }

  createBindGroup(desc: GPUBindGroupDescriptor): FakeBindGroup {
    const resources = resourcesOf(desc.entries);
    this.assertLive('createBindGroup', resources);
    return { resources };
  }

  createCommandEncoder() {
    const use = (bg: FakeBindGroup) => {
      this.assertLive('setBindGroup', bg.resources);
      this.pending.push(...bg.resources);
    };
    const pass = {
      setPipeline: () => undefined,
      setBindGroup: (_i: number, bg: FakeBindGroup) => use(bg),
      dispatchWorkgroups: () => undefined,
      draw: () => undefined,
      end: () => undefined,
    };
    return {
      beginComputePass: () => pass,
      beginRenderPass: () => pass,
      copyTextureToTexture: (src: { texture: FakeTexture }, dst: { texture: FakeTexture }) => {
        this.assertLive('copyTextureToTexture', [src.texture, dst.texture]);
        this.pending.push(src.texture, dst.texture);
      },
      finish: () => ({}),
    };
  }

  // Objects with no GPU memory to own: shader modules, layouts, pipelines, samplers.
  createShaderModule() { return {}; }
  createBindGroupLayout() { return {}; }
  createPipelineLayout() { return {}; }
  createSampler() { return {}; }
  createComputePipeline() { return { getBindGroupLayout: () => ({}) }; }
  createRenderPipeline() { return { getBindGroupLayout: () => ({}) }; }

  /** Simulate a driver reset. */
  lose(): void {
    this.loseDevice({ reason: 'unknown', message: 'test' } as GPUDeviceLostInfo);
  }

  /** The device as the app sees it. */
  asGpuDevice(): GPUDevice {
    return this as unknown as GPUDevice;
  }
}

/** The WebGPU flag namespaces the library reads at construction time. */
export function stubWebGpuGlobals(stub: (name: string, value: unknown) => void): void {
  const flags = new Proxy({}, { get: () => 1 });
  stub('GPUTextureUsage', flags);
  stub('GPUBufferUsage', flags);
  stub('GPUShaderStage', flags);
  stub('GPUMapMode', flags);
}

/** Install a `navigator.gpu` whose every device request is answered by `next()`. */
export function installNavigatorGpu(next: () => FakeGpuDevice): void {
  (navigator as unknown as { gpu?: unknown }).gpu = {
    requestAdapter: () => Promise.resolve({ requestDevice: () => Promise.resolve(next().asGpuDevice()) }),
    getPreferredCanvasFormat: () => 'bgra8unorm',
  };
}

export function removeNavigatorGpu(): void {
  delete (navigator as unknown as { gpu?: unknown }).gpu;
}

/** A canvas stand-in whose `webgpu` context draws into an untracked swap-chain texture. */
export function fakeCanvas(): HTMLCanvasElement {
  const context = {
    configure: () => undefined,
    unconfigure: () => undefined,
    getCurrentTexture: () => new FakeTexture('swapchain', [1, 1, 1]),
  };
  return { width: 0, height: 0, getContext: () => context } as unknown as HTMLCanvasElement;
}

/** A decoded page image stand-in. */
export function fakeImage(width: number, height: number): HTMLImageElement {
  return { naturalWidth: width, naturalHeight: height } as unknown as HTMLImageElement;
}
