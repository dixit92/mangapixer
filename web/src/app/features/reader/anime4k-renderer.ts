/**
 * Anime4K (WebGPU) line-art upscale renderer — the HEAVY half of the "Upscaling:
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
 * Chain (1.24.0): `anime4k-chains.ts` builds the `ModeA` shape with either the
 * light M family (`ClampHighlights` -> `CNNM` -> `CNNx2M`, the DEFAULT) or the
 * package's own `ModeA`, which in `anime4k-webgpu` 1.0.0 is the HEAVY *VL* chain
 * (`CNNVL` -> `CNNx2VL`; the paged "Max quality" choice). Both follow `ModeA`'s
 * geometry: restore at native size -> x2 (when the target is > 1.2x native) ->
 * then, by scale `s`, a `Downscale` to the target box (1.2 < s < 2), or a
 * `Downscale` to half the target followed by `CNNx2M` (2.4 < s < 4), or a
 * further `CNNx2M` (s >= 4). For 2 <= s <= 2.4 the chain ends at 2x native. The
 * output is therefore close to, but not always exactly, the target box; the
 * blit below samples it linearly onto the canvas at display resolution either
 * way. That is a few dozen full-size `rgba16float` intermediate textures per
 * pipeline, which is why every one of them is tracked and destroyed (see
 * `gpu-device.ts` and `RenderState` below).
 *
 * Scope: paged / double-spread pages. The webtoon (continuous scroll) view has
 * its own banded renderer, `anime4k-tile-renderer.ts`, because one pipeline per
 * visible strip page of its own size is a very different resource problem from
 * "the one or two pages on screen". Both share the device (`gpu-device.ts`).
 *
 * Everything is best-effort: every entry point resolves to a boolean and never
 * throws, because the fallback ("let the browser paint the <img> as it always
 * did") is always correct.
 */

import { buildPagedChain } from './anime4k-chains';
import { TrackingDevice, acquireDevice, onDeviceLost, trackingDevice } from './gpu-device';
import type { EnhanceChain } from './webtoon-band-plan';

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

// Alpha is forced to 1: the canvas is 'premultiplied' (see configure below), so
// every presented pixel must be fully opaque to look exactly as before.
@fragment
fn fs(@location(0) uv: vec2<f32>) -> @location(0) vec4<f32> {
  return vec4<f32>(textureSample(tex, samp, uv).rgb, 1.0);
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
  /** Anime4K chain family: `m` (default, light) or `vl` (paged "Max quality"). */
  readonly chain?: EnhanceChain;
}

/** Per-device objects: cheap, no GPU memory to destroy, rebuilt on a new device. */
interface DeviceState {
  device: GPUDevice;
  canvasFormat: GPUTextureFormat;
  blit: GPURenderPipeline;
  sampler: GPUSampler;
}

/**
 * One reusable size-keyed pipeline. Creating an Anime4K pipeline allocates a
 * long chain of intermediate textures, so we keep exactly ONE alive and reuse it
 * while the source and target dimensions are unchanged - which, within a
 * chapter, is the normal case (pages of a scan share a size). Everything it
 * allocated (the input texture AND every texture/buffer the chain created) went
 * through `resources`, so a dimension change, `releaseUpscaler()` or a lost
 * device destroys all of it and GPU memory does not grow with the page count.
 */
interface RenderState {
  key: string;
  resources: TrackingDevice;
  inputTexture: GPUTexture;
  pipeline: { pass(encoder: GPUCommandEncoder): void; getOutputTexture(): GPUTexture };
  bindGroup: GPUBindGroup;
}

let deviceState: DeviceState | null = null;
let state: RenderState | null = null;
/** Renders run one at a time, so no render can use a state another one disposed. */
let queue: Promise<unknown> = Promise.resolve();

onDeviceLost((device) => {
  if (deviceState?.device !== device) return;
  disposeCached();
  deviceState = null;
});

function disposeCached(): void {
  const old = state;
  // Unpublish BEFORE destroying so nothing can pick up a destroyed resource.
  state = null;
  old?.resources.dispose();
}

/**
 * Upscale `source` into `canvas` at the requested target size.
 *
 * @returns true when the canvas now holds an upscaled frame, false when the
 *          caller should fall back to the plain `<img>` (no WebGPU, device
 *          acquisition failed, a shader/pipeline error, …). Never throws.
 */
export function renderUpscaled(req: UpscaleRequest): Promise<boolean> {
  // Two pages of a spread (possibly of different sizes, so different keys) must
  // not interleave across the awaits below: the second would dispose the
  // pipeline the first is about to use.
  const run = queue.then(() => renderNow(req));
  queue = run.catch(() => false);
  return run;
}

async function renderNow(req: UpscaleRequest): Promise<boolean> {
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

    if (!deviceState || deviceState.device !== device) {
      disposeCached();
      const module = device.createShaderModule({ code: blitWGSL, label: 'mp-upscale-blit' });
      deviceState = {
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
      };
    }
    const shared = deviceState;

    const chain: EnhanceChain = req.chain ?? 'm';
    const key = `${chain}:${nativeWidth}x${nativeHeight}->${targetWidth}x${targetHeight}`;
    if (state?.key !== key) {
      disposeCached();
      const resources = trackingDevice(device);
      try {
        const inputTexture = resources.device.createTexture({
          label: 'mp-upscale-src',
          size: [nativeWidth, nativeHeight, 1],
          format: 'rgba8unorm',
          usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
        });
        const pipeline = buildPagedChain(chain, {
          device: resources.device,
          inputTexture,
          nativeDimensions: { width: nativeWidth, height: nativeHeight },
          targetDimensions: { width: targetWidth, height: targetHeight },
        });
        state = {
          key,
          resources,
          inputTexture,
          pipeline,
          bindGroup: device.createBindGroup({
            layout: shared.blit.getBindGroupLayout(0),
            entries: [
              { binding: 0, resource: shared.sampler },
              { binding: 1, resource: pipeline.getOutputTexture().createView() },
            ],
          }),
        };
      } catch (err) {
        // A half-built chain (e.g. out of memory on texture 30 of 40) still owns
        // everything it allocated so far.
        resources.dispose();
        throw err;
      }
    }
    const current = state;

    // `copyExternalImageToTexture` accepts an HTMLImageElement, but only a fully
    // decoded one; an ImageBitmap is the portable way to guarantee that and keeps
    // the decode off the main thread.
    const bitmap = await createImageBitmap(source);
    try {
      // Released (reader closed / Enhance off) or device lost while decoding:
      // the textures are destroyed, so do not touch them.
      if (state !== current || deviceState !== shared) return false;
      device.queue.copyExternalImageToTexture(
        { source: bitmap },
        { texture: current.inputTexture },
        [nativeWidth, nativeHeight],
      );
    } finally {
      bitmap.close();
    }

    if (canvas.width !== targetWidth) canvas.width = targetWidth;
    if (canvas.height !== targetHeight) canvas.height = targetHeight;
    // 'premultiplied', not 'opaque': the blit writes alpha 1, so a presented
    // frame looks identical, but a frame the compositor drops (WebKit, seen on
    // iPad after a Slide page turn) is transparent and the <img> underneath
    // shows through. With 'opaque' it painted a black page-shaped box.
    context.configure({ device, format: canvasFormat, alphaMode: 'premultiplied' });

    const encoder = device.createCommandEncoder({ label: 'mp-upscale' });
    current.pipeline.pass(encoder);
    const pass = encoder.beginRenderPass({
      colorAttachments: [{
        view: context.getCurrentTexture().createView(),
        clearValue: { r: 0, g: 0, b: 0, a: 1 },
        loadOp: 'clear',
        storeOp: 'store',
      }],
    });
    pass.setPipeline(shared.blit);
    pass.setBindGroup(0, current.bindGroup);
    pass.draw(3);
    pass.end();
    // Destroying a texture after this submit is safe: WebGPU defers the free
    // until the submitted work that uses it has finished.
    device.queue.submit([encoder.finish()]);
    await device.queue.onSubmittedWorkDone();
    return true;
  } catch {
    // Any failure at all — unsupported format, out of memory, a driver quirk —
    // is answered by "show the plain <img>", which is always a valid reader.
    return false;
  }
}

/**
 * Destroy every GPU texture and buffer the renderer owns and drop the cached
 * pipeline objects (reader torn down / Enhance switched off; called by
 * `upscale.directive.ts` once no Enhance overlay is live). The device itself
 * stays with `gpu-device.ts` for the app session: re-acquiring it is cheap but
 * not free, and it holds no page-sized memory. The next render rebuilds lazily.
 */
export function releaseUpscaler(): void {
  disposeCached();
  deviceState = null;
}
