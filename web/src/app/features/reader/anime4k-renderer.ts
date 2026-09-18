/**
 * Anime4K (WebGPU) line-art upscale renderer — the HEAVY half of the "Rendering:
 * Enhance" option (1.19.0). It is deliberately the only module in the app that
 * statically imports `anime4k-webgpu`, so the bundler parks the package (and its
 * ~3.5 MB of embedded WGSL convolution weights) in its own LAZY chunk that is
 * fetched the first time a reader actually turns the option on. Nothing here is
 * reachable from the initial bundle; `upscale.directive.ts` reaches it through a
 * dynamic `import()`.
 *
 * Library: `anime4k-webgpu` 1.0.0, MIT (verified in `node_modules/.../LICENSE.md`
 * and `package.json`), a WebGPU port of the Anime4K shader family. It is BUNDLED
 * from npm, makes no network calls of its own, and is attributed through the
 * Angular build's generated `3rdpartylicenses.txt` like every other npm
 * dependency — no vendored source, so `THIRD-PARTY-NOTICES.md` is untouched.
 *
 * Preset: `ModeA` — Anime4K's "Restore + Upscale (CNN)" chain at the *M* (medium)
 * size, the cheapest preset in the family that visibly cleans line art rather
 * than just resampling it. The heavier VL/UL/GAN variants exist in the package
 * but cost several times the GPU work for a difference that a manga page, read
 * statically rather than at 60 fps, does not repay. `ModeA` also ends with a
 * downscale to the exact target box, so the pipeline output drops straight onto
 * the canvas at display resolution whatever the 2x CNN produced.
 *
 * Scope: paged / double-spread pages only. The webtoon (continuous scroll) view
 * is out of scope this cycle — it can have dozens of simultaneously-live images,
 * and one GPU pipeline per visible strip page is a very different resource
 * problem from "the one or two pages on screen".
 *
 * Everything is best-effort: every entry point resolves to a boolean and never
 * throws, because the fallback ("let the browser paint the <img> as it always
 * did") is always correct.
 */

import { ModeA } from 'anime4k-webgpu';

/**
 * Blit shader: draws the pipeline's output texture over the canvas with a single
 * full-screen triangle. Ours, not the library's — `anime4k-webgpu`'s own
 * `render()` helper is built around `requestVideoFrameCallback` on a
 * `<video>`, which is the wrong shape for a still page (we want exactly one
 * frame per page, not a render loop).
 */
const blitWGSL = /* wgsl */ `
struct VSOut {
  @builtin(position) pos: vec4<f32>,
  @location(0) uv: vec2<f32>,
};

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
  // Clip space is y-up, texture space is y-down.
  out.uv = vec2<f32>((p.x + 1.0) * 0.5, 1.0 - (p.y + 1.0) * 0.5);
  return out;
}

@group(0) @binding(0) var samp: sampler;
@group(0) @binding(1) var tex: texture_2d<f32>;

@fragment
fn fs(@location(0) uv: vec2<f32>) -> @location(0) vec4<f32> {
  return textureSample(tex, samp, uv);
}
`;

export interface UpscaleRequest {
  /** The already-decoded page image (naturalWidth/Height > 0). */
  readonly source: HTMLImageElement;
  /** The overlay canvas; its width/height are set to the target device pixels. */
  readonly canvas: HTMLCanvasElement;
  /** Target size in DEVICE pixels (the img's CSS box times devicePixelRatio). */
  readonly targetWidth: number;
  readonly targetHeight: number;
}

/**
 * One reusable GPU context. Creating an Anime4K pipeline allocates a long chain
 * of intermediate textures, so we keep exactly ONE alive and reuse it while the
 * source and target dimensions are unchanged — which, within a chapter, is the
 * normal case (pages of a scan share a size). A dimension change tears the old
 * one down first so GPU memory does not grow with the page count.
 */
interface RenderState {
  device: GPUDevice;
  canvasFormat: GPUTextureFormat;
  blit: GPURenderPipeline;
  sampler: GPUSampler;
  // Cached per source/target size.
  key: string;
  inputTexture: GPUTexture;
  pipeline: { pass(encoder: GPUCommandEncoder): void; getOutputTexture(): GPUTexture };
  bindGroup: GPUBindGroup;
}

let state: RenderState | null = null;
let devicePromise: Promise<GPUDevice | null> | null = null;

/** Acquire (once) a WebGPU device, or null when the platform cannot give us one. */
async function acquireDevice(): Promise<GPUDevice | null> {
  if (devicePromise) return devicePromise;
  devicePromise = (async () => {
    try {
      const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
      if (!gpu) return null;
      const adapter = await gpu.requestAdapter();
      if (!adapter) return null;
      const device = await adapter.requestDevice();
      // A lost device (driver reset, tab backgrounded on some platforms) must not
      // strand every later page on a dead pipeline: drop everything and let the
      // next request rebuild from scratch.
      device.lost.then(() => { state = null; devicePromise = null; }).catch(() => { /* ignore */ });
      return device;
    } catch {
      return null;
    }
  })();
  return devicePromise;
}

function disposeCached(): void {
  if (!state) return;
  try { state.inputTexture.destroy(); } catch { /* already gone */ }
  state = { ...state, key: '', inputTexture: null as unknown as GPUTexture } as RenderState;
}

/**
 * Upscale `source` into `canvas` at the requested target size.
 *
 * @returns true when the canvas now holds an upscaled frame, false when the
 *          caller should fall back to the plain `<img>` (no WebGPU, device
 *          acquisition failed, a shader/pipeline error, …). Never throws.
 */
export async function renderUpscaled(req: UpscaleRequest): Promise<boolean> {
  const { source, canvas } = req;
  const nativeWidth = source.naturalWidth;
  const nativeHeight = source.naturalHeight;
  const targetWidth = Math.max(1, Math.round(req.targetWidth));
  const targetHeight = Math.max(1, Math.round(req.targetHeight));
  if (nativeWidth <= 0 || nativeHeight <= 0) return false;

  try {
    const device = await acquireDevice();
    if (!device) return false;

    const context = canvas.getContext('webgpu') as GPUCanvasContext | null;
    if (!context) return false;

    const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
    const canvasFormat = gpu?.getPreferredCanvasFormat?.() ?? 'bgra8unorm';

    if (!state || state.device !== device) {
      const module = device.createShaderModule({ code: blitWGSL, label: 'mp-upscale-blit' });
      state = {
        device,
        canvasFormat,
        blit: device.createRenderPipeline({
          label: 'mp-upscale-blit',
          layout: 'auto',
          vertex: { module, entryPoint: 'vs' },
          fragment: { module, entryPoint: 'fs', targets: [{ format: canvasFormat }] },
          primitive: { topology: 'triangle-list' },
        }),
        sampler: device.createSampler({ magFilter: 'linear', minFilter: 'linear' }),
        key: '',
        inputTexture: null as unknown as GPUTexture,
        pipeline: null as unknown as RenderState['pipeline'],
        bindGroup: null as unknown as GPUBindGroup,
      };
    }

    const key = `${nativeWidth}x${nativeHeight}->${targetWidth}x${targetHeight}`;
    if (state.key !== key) {
      disposeCached();
      const inputTexture = device.createTexture({
        label: 'mp-upscale-src',
        size: [nativeWidth, nativeHeight, 1],
        format: 'rgba8unorm',
        usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
      });
      const pipeline = new ModeA({
        device,
        inputTexture,
        nativeDimensions: { width: nativeWidth, height: nativeHeight },
        targetDimensions: { width: targetWidth, height: targetHeight },
      });
      state = {
        ...state,
        key,
        inputTexture,
        pipeline,
        bindGroup: device.createBindGroup({
          layout: state.blit.getBindGroupLayout(0),
          entries: [
            { binding: 0, resource: state.sampler },
            { binding: 1, resource: pipeline.getOutputTexture().createView() },
          ],
        }),
      };
    }

    // `copyExternalImageToTexture` accepts an HTMLImageElement, but only a fully
    // decoded one; an ImageBitmap is the portable way to guarantee that and keeps
    // the decode off the main thread.
    const bitmap = await createImageBitmap(source);
    try {
      device.queue.copyExternalImageToTexture(
        { source: bitmap },
        { texture: state.inputTexture },
        [nativeWidth, nativeHeight],
      );
    } finally {
      bitmap.close();
    }

    if (canvas.width !== targetWidth) canvas.width = targetWidth;
    if (canvas.height !== targetHeight) canvas.height = targetHeight;
    context.configure({ device, format: canvasFormat, alphaMode: 'opaque' });

    const encoder = device.createCommandEncoder({ label: 'mp-upscale' });
    state.pipeline.pass(encoder);
    const pass = encoder.beginRenderPass({
      colorAttachments: [{
        view: context.getCurrentTexture().createView(),
        clearValue: { r: 0, g: 0, b: 0, a: 1 },
        loadOp: 'clear',
        storeOp: 'store',
      }],
    });
    pass.setPipeline(state.blit);
    pass.setBindGroup(0, state.bindGroup);
    pass.draw(3);
    pass.end();
    device.queue.submit([encoder.finish()]);
    await device.queue.onSubmittedWorkDone();
    return true;
  } catch {
    // Any failure at all — unsupported format, out of memory, a driver quirk —
    // is answered by "show the plain <img>", which is always a valid reader.
    return false;
  }
}

/** Drop every cached GPU object (reader torn down / option switched off). */
export function releaseUpscaler(): void {
  disposeCached();
  state = null;
}
