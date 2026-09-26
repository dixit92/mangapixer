/**
 * WebGL2 upscaler (1.25.0) - the lazy GPU half of "Rendering: Sharp" (AMD FSR 1)
 * and of "Rendering: Enhance" where WebGPU is missing (Anime4K M on WebGL2, e.g.
 * over plain `http://` on a LAN, where browsers do not expose WebGPU). Reached
 * only through a dynamic `import()` from `upscale.directive.ts` (paged pages) and
 * `webtoon-enhance-coordinator.ts` (webtoon bands), so none of this - nor the
 * ~75 kB of vendored shader text - is in the initial bundle, and nothing here
 * imports the WebGPU chunk.
 *
 * ONE shared WebGL2 context for the app, on a detached canvas: browsers cap live
 * WebGL contexts (~8-16) and silently drop the oldest, so a context per overlay
 * or per band canvas would lose pages as the strip scrolls. The result of each
 * render is copied (`drawImage`, GPU to GPU in current browsers) onto the
 * caller's canvas through a plain `2d` context, which also means an overlay never
 * presents a blank frame: a 2d canvas keeps its pixels until they are replaced
 * (the WebKit dropped-frame black box of 1.19.x cannot happen here).
 *
 * Paged: one size-keyed state (textures + framebuffers) reused while the page
 * and target sizes stay the same - within a chapter, the normal case. Webtoon:
 * up to `maxStates` states keyed by (mode, width, tile rows), least recently used
 * out, and each band's passes are spread over animation frames (`band-slicing.ts`)
 * with a fence per slice, as the WebGPU tile renderer does with
 * `onSubmittedWorkDone`.
 *
 * GPU memory: every texture and framebuffer a state creates is tracked and
 * deleted on eviction, `releasePages()` / `releaseTiles()`, and context loss;
 * after a release the context's drawing buffer is shrunk to 1x1. A lost context
 * (`webglcontextlost`: GPU reset, a backgrounded tab on some phones) drops
 * everything, tells `onTilesLost` listeners (the webtoon "twice within a minute
 * pauses vertical Enhance" rule) and the next render builds a fresh context.
 *
 * Never throws: every entry point resolves false / `'failed'` on any error and
 * the caller keeps the plain `<img>`, which is always a correct picture.
 */

import { ChainPlan, DrawPlan, outSizeUniform, planAnime4kChain, presentFragmentSource } from './anime4k-webgl';
import type { BandRenderRequest, BandRenderResult } from './anime4k-tile-renderer';
import { adaptSlice, createSliceBudget, nextAnimationFrame } from './band-slicing';
import { easuCon0, easuGlsl, fullscreenVertexGlsl, rcasGlsl, rcasSharpness } from './fsr1-glsl';
import { RenderMode, canRenderHalfFloat } from './upscale-engine';

export { createSliceBudget } from './band-slicing';

/** Band pipelines kept alive (the strip width and one odd size), as on WebGPU. */
export const maxStates = 2;
/** Give up waiting for a fence after this long (the work is still queued; only the timing is lost). */
const fenceTimeoutMs = 3000;
/** Enhance paged: add the x2 network only when the page is enlarged more than this (Anime4K mode A's rule). */
const x2Threshold = 1.2;

// --- context ------------------------------------------------------------------

interface Program {
  readonly program: WebGLProgram;
  readonly uniforms: Map<string, WebGLUniformLocation | null>;
}

interface GlHandle {
  readonly canvas: HTMLCanvasElement;
  readonly gl: WebGL2RenderingContext;
  readonly floatTargets: boolean;
  readonly maxTextureSize: number;
  readonly programs: Map<string, Program>;
  vertex: WebGLShader | null;
  lost: boolean;
}

/** Makes the shared context's canvas (a test seam; default: a detached `<canvas>`). */
export type GlCanvasFactory = () => HTMLCanvasElement | null;
const defaultCanvasFactory: GlCanvasFactory = () => (typeof document === 'undefined' ? null : document.createElement('canvas'));
let canvasFactory: GlCanvasFactory = defaultCanvasFactory;

let handle: GlHandle | null = null;
const lostListeners = new Set<() => void>();
let lastError = '';

/** Test seam: swap the canvas factory and forget the current context and every state. */
export function setGlCanvasFactoryForTests(factory: GlCanvasFactory | null): void {
  canvasFactory = factory ?? defaultCanvasFactory;
  pageState = null;
  bandStates = [];
  handle = null;
  lastError = '';
}

/** The last GL failure (compile log, incomplete framebuffer, ...) - diagnostics only, never user data. */
export function glDiagnostics(): string {
  return lastError;
}

/** Be told when the shared context is lost. Returns an unsubscribe. */
export function onTilesLost(listener: () => void): () => void {
  lostListeners.add(listener);
  return () => { lostListeners.delete(listener); };
}

function acquire(): GlHandle | null {
  if (handle && !handle.lost && !handle.gl.isContextLost()) return handle;
  handle = null;
  const canvas = canvasFactory();
  if (!canvas) return null;
  canvas.width = 1;
  canvas.height = 1;
  const gl = canvas.getContext('webgl2', {
    alpha: false, antialias: false, depth: false, stencil: false,
    premultipliedAlpha: true, preserveDrawingBuffer: false, powerPreference: 'default',
  }) as WebGL2RenderingContext | null;
  if (!gl) { lastError = 'no webgl2 context'; return null; }
  const created: GlHandle = {
    canvas, gl,
    floatTargets: canRenderHalfFloat(gl),
    maxTextureSize: Number(gl.getParameter(gl.MAX_TEXTURE_SIZE)) || 0,
    programs: new Map(),
    vertex: null,
    lost: false,
  };
  canvas.addEventListener('webglcontextlost', () => contextLost(created));
  handle = created;
  return created;
}

function contextLost(lost: GlHandle): void {
  if (lost.lost) return;
  lost.lost = true;
  if (handle === lost) handle = null;
  // Every GL object of a lost context is already gone; just unpublish the states.
  if (pageState?.gl === lost) pageState = null;
  bandStates = bandStates.filter((s) => s.gl !== lost);
  for (const listener of [...lostListeners]) {
    try { listener(); } catch { /* a listener must not block the others */ }
  }
}

function compile(gl: WebGL2RenderingContext, type: number, source: string): WebGLShader {
  const shader = gl.createShader(type);
  if (!shader) throw new Error('createShader failed');
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS) && !gl.isContextLost()) {
    const log = gl.getShaderInfoLog(shader) ?? '';
    gl.deleteShader(shader);
    throw new Error(`shader compile failed: ${log}`);
  }
  return shader;
}

/** Compile + link once per context; programs are small and survive state releases. */
function programFor(h: GlHandle, key: string, fragment: string): Program {
  const cached = h.programs.get(key);
  if (cached) return cached;
  const { gl } = h;
  h.vertex ??= compile(gl, gl.VERTEX_SHADER, fullscreenVertexGlsl);
  const frag = compile(gl, gl.FRAGMENT_SHADER, fragment);
  const program = gl.createProgram();
  if (!program) throw new Error('createProgram failed');
  gl.attachShader(program, h.vertex);
  gl.attachShader(program, frag);
  gl.linkProgram(program);
  gl.deleteShader(frag); // flagged; freed with the program
  if (!gl.getProgramParameter(program, gl.LINK_STATUS) && !gl.isContextLost()) {
    const log = gl.getProgramInfoLog(program) ?? '';
    gl.deleteProgram(program);
    throw new Error(`program link failed (${key}): ${log}`);
  }
  const entry: Program = { program, uniforms: new Map() };
  h.programs.set(key, entry);
  return entry;
}

function uniform(gl: WebGL2RenderingContext, p: Program, name: string): WebGLUniformLocation | null {
  if (!p.uniforms.has(name)) p.uniforms.set(name, gl.getUniformLocation(p.program, name));
  return p.uniforms.get(name) ?? null;
}

// --- tracked resources ----------------------------------------------------------

interface Target {
  readonly texture: WebGLTexture;
  readonly framebuffer: WebGLFramebuffer | null;
  readonly width: number;
  readonly height: number;
}

/** Textures and framebuffers owned by one state; `dispose()` deletes each exactly once. */
class GlResources {
  private textures: WebGLTexture[] = [];
  private framebuffers: WebGLFramebuffer[] = [];

  constructor(private readonly gl: WebGL2RenderingContext) {}

  get size(): number { return this.textures.length + this.framebuffers.length; }

  /** A `width x height` texture; with `renderable`, also a complete framebuffer onto it. */
  target(width: number, height: number, format: 'rgba8' | 'rgba16f', renderable: boolean): Target {
    const gl = this.gl;
    const texture = gl.createTexture();
    if (!texture) throw new Error('createTexture failed');
    this.textures.push(texture);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texStorage2D(gl.TEXTURE_2D, 1, format === 'rgba16f' ? gl.RGBA16F : gl.RGBA8, width, height);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    let framebuffer: WebGLFramebuffer | null = null;
    if (renderable) {
      framebuffer = gl.createFramebuffer();
      if (!framebuffer) throw new Error('createFramebuffer failed');
      this.framebuffers.push(framebuffer);
      gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      const status = gl.checkFramebufferStatus(gl.FRAMEBUFFER);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      if (status !== gl.FRAMEBUFFER_COMPLETE && !gl.isContextLost()) throw new Error(`framebuffer incomplete (${format})`);
    }
    return { texture, framebuffer, width, height };
  }

  dispose(): void {
    const gl = this.gl;
    const textures = this.textures;
    const framebuffers = this.framebuffers;
    this.textures = [];
    this.framebuffers = [];
    try {
      for (const fb of framebuffers) gl.deleteFramebuffer(fb);
      for (const tex of textures) gl.deleteTexture(tex);
    } catch { /* context gone */ }
  }
}

// --- chains ---------------------------------------------------------------------

/** One encodable step: issues its draw(s) on the shared context. */
type Step = () => void;

interface ChainState {
  readonly key: string;
  readonly gl: GlHandle;
  readonly resources: GlResources;
  readonly input: Target;
  /** Offscreen passes, in order (sliced for bands). */
  readonly steps: readonly Step[];
  /** Draw the final pass into the default framebuffer (`outW x outH`), rows `offset`/`scale` of the source. */
  readonly present: (outW: number, outH: number, rowOffset: number, rowScale: number) => void;
}

function drawTo(gl: WebGL2RenderingContext, framebuffer: WebGLFramebuffer | null, width: number, height: number): void {
  gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
  gl.viewport(0, 0, width, height);
  gl.drawArrays(gl.TRIANGLES, 0, 3);
}

function bindTexture(gl: WebGL2RenderingContext, p: Program, name: string, unit: number, texture: WebGLTexture): void {
  gl.activeTexture(gl.TEXTURE0 + unit);
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.uniform1i(uniform(gl, p, name), unit);
}

/** FSR 1: EASU (input -> `easuW x easuH`, RGBA8), then RCAS into the canvas. */
function buildSharp(h: GlHandle, key: string, inW: number, inH: number, easuW: number, easuH: number): ChainState {
  const { gl } = h;
  const resources = new GlResources(gl);
  try {
    const easu = programFor(h, 'fsr-easu', easuGlsl);
    const rcas = programFor(h, 'fsr-rcas', rcasGlsl);
    const input = resources.target(inW, inH, 'rgba8', false);
    const upscaled = resources.target(easuW, easuH, 'rgba8', true);
    const con0 = easuCon0(inW, inH, easuW, easuH);
    const sharpness = rcasSharpness();
    const steps: Step[] = [() => {
      gl.useProgram(easu.program);
      bindTexture(gl, easu, 'src', 0, input.texture);
      gl.uniform4f(uniform(gl, easu, 'con0'), ...con0);
      drawTo(gl, upscaled.framebuffer, easuW, easuH);
    }];
    const present = (outW: number, outH: number, rowOffset: number) => {
      gl.useProgram(rcas.program);
      bindTexture(gl, rcas, 'src', 0, upscaled.texture);
      gl.uniform1f(uniform(gl, rcas, 'sharpness'), sharpness);
      gl.uniform2i(uniform(gl, rcas, 'offset'), 0, rowOffset);
      gl.uniform1i(uniform(gl, rcas, 'outHeight'), outH);
      drawTo(gl, null, outW, outH);
    };
    return { key, gl: h, resources, input, steps, present };
  } catch (err) {
    resources.dispose();
    throw err;
  }
}

/** Anime4K M: statistics, restore, optional x2 (see `anime4k-webgl.ts`), then the clamped present. */
function buildEnhance(h: GlHandle, key: string, inW: number, inH: number, x2: boolean): ChainState {
  const { gl } = h;
  if (!h.floatTargets) throw new Error('no float render targets');
  const plan: ChainPlan = planAnime4kChain(inW, inH, x2);
  const resources = new GlResources(gl);
  try {
    const targets = plan.textures.map((t) =>
      resources.target(t.width, t.height, t.float ? 'rgba16f' : 'rgba8', t.id !== plan.input));
    const stepFor = (draw: DrawPlan): Step => {
      const p = programFor(h, draw.program, draw.source);
      const out = targets[draw.output];
      return () => {
        gl.useProgram(p.program);
        draw.inputs.forEach((input, unit) => {
          const t = targets[input.texture];
          bindTexture(gl, p, `${input.name}_raw`, unit, t.texture);
          gl.uniform2f(uniform(gl, p, `${input.name}_size`), t.width, t.height);
        });
        gl.uniform2f(uniform(gl, p, outSizeUniform), draw.width, draw.height);
        drawTo(gl, out.framebuffer, draw.width, draw.height);
      };
    };
    const steps = plan.draws.map(stepFor);
    const present = programFor(h, 'a4k-present', presentFragmentSource);
    const image = targets[plan.output];
    const stats = targets[plan.stats];
    return {
      key, gl: h, resources, input: targets[plan.input], steps,
      present: (outW, outH, rowOffset, rowScale) => {
        gl.useProgram(present.program);
        bindTexture(gl, present, 'image', 0, image.texture);
        bindTexture(gl, present, 'stats', 1, stats.texture);
        gl.uniform2f(uniform(gl, present, 'outSize'), outW, outH);
        gl.uniform2f(uniform(gl, present, 'crop'), rowOffset, rowScale);
        drawTo(gl, null, outW, outH);
      },
    };
  } catch (err) {
    resources.dispose();
    throw err;
  }
}

function upload(gl: WebGL2RenderingContext, input: Target, bitmap: ImageBitmap): void {
  gl.bindTexture(gl.TEXTURE_2D, input.texture);
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
  gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
  gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, input.width, input.height, gl.RGBA, gl.UNSIGNED_BYTE, bitmap);
}

/**
 * Wait (without blocking the main thread) until the GPU has finished everything
 * issued so far; resolves the elapsed ms. A fence's status only changes between
 * tasks, hence the timer between polls.
 */
async function gpuDone(gl: WebGL2RenderingContext): Promise<number> {
  const started = performance.now();
  const sync = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
  gl.flush();
  if (!sync) return performance.now() - started;
  try {
    while (performance.now() - started < fenceTimeoutMs && !gl.isContextLost()) {
      if (gl.getSyncParameter(sync, gl.SYNC_STATUS) === gl.SIGNALED) break;
      await new Promise((resolve) => setTimeout(resolve, 1));
    }
  } finally {
    gl.deleteSync(sync);
  }
  return performance.now() - started;
}

/** Copy the context's drawing buffer onto the caller's canvas (its `2d` context). */
function blitTo(h: GlHandle, canvas: HTMLCanvasElement, width: number, height: number): boolean {
  if (canvas.width !== width) canvas.width = width;
  if (canvas.height !== height) canvas.height = height;
  const ctx = canvas.getContext('2d');
  if (!ctx) return false;
  ctx.drawImage(h.canvas, 0, 0);
  return true;
}

function sizeDrawingBuffer(h: GlHandle, width: number, height: number): void {
  if (h.canvas.width !== width) h.canvas.width = width;
  if (h.canvas.height !== height) h.canvas.height = height;
}

function shrinkIfIdle(): void {
  if (pageState || bandStates.length > 0 || !handle) return;
  sizeDrawingBuffer(handle, 1, 1);
}

// --- paged ------------------------------------------------------------------------

export interface GlPageRequest {
  /** The already-decoded page image (naturalWidth/Height > 0). */
  readonly source: HTMLImageElement;
  /** The overlay canvas (a `2d` canvas); its backing store is set to the target. */
  readonly canvas: HTMLCanvasElement;
  /** Target size in DEVICE pixels. */
  readonly targetWidth: number;
  readonly targetHeight: number;
  readonly mode: RenderMode;
}

let pageState: ChainState | null = null;
let pageQueue: Promise<unknown> = Promise.resolve();

/**
 * Render one paged page. Serialised, so the two pages of a spread never share a
 * state mid-render. @returns true when the canvas now holds the upscaled page.
 */
export function renderPage(req: GlPageRequest): Promise<boolean> {
  const run = pageQueue.then(() => renderPageNow(req));
  pageQueue = run.catch(() => false);
  return run;
}

async function renderPageNow(req: GlPageRequest): Promise<boolean> {
  const { source, canvas, mode } = req;
  const nw = source.naturalWidth;
  const nh = source.naturalHeight;
  const tw = Math.max(1, Math.round(req.targetWidth));
  const th = Math.max(1, Math.round(req.targetHeight));
  if (!(nw > 0) || !(nh > 0)) return false;
  try {
    const h = acquire();
    if (!h) return false;
    const x2 = mode === 'enhance' && tw > x2Threshold * nw && th > x2Threshold * nh;
    const largest = Math.max(tw, th, x2 ? 2 * Math.max(nw, nh) : 0, nw, nh);
    if (h.maxTextureSize > 0 && largest > h.maxTextureSize) return false;
    const key = mode === 'sharp' ? `sharp:${nw}x${nh}->${tw}x${th}` : `enhance:${nw}x${nh}:${x2 ? 'x2' : 'x1'}`;
    if (pageState?.key !== key || pageState.gl !== h) {
      pageState?.resources.dispose();
      pageState = null;
      pageState = mode === 'sharp' ? buildSharp(h, key, nw, nh, tw, th) : buildEnhance(h, key, nw, nh, x2);
    }
    const state = pageState;
    const bitmap = await createImageBitmap(source);
    try {
      if (pageState !== state || h.lost) return false; // released or lost while decoding
      upload(h.gl, state.input, bitmap);
    } finally {
      bitmap.close();
    }
    for (const step of state.steps) step();
    sizeDrawingBuffer(h, tw, th);
    state.present(tw, th, 0, 1);
    if (!blitTo(h, canvas, tw, th)) return false;
    await gpuDone(h.gl);
    return !h.lost;
  } catch (err) {
    lastError = err instanceof Error ? err.message : String(err);
    pageState?.resources.dispose();
    pageState = null;
    return false;
  }
}

/** Drop the paged state's GPU memory (no paged overlay live any more). */
export function releasePages(): void {
  pageState?.resources.dispose();
  pageState = null;
  shrinkIfIdle();
}

// --- webtoon bands ------------------------------------------------------------

/** Most recently used LAST. */
let bandStates: ChainState[] = [];
let bandQueue: Promise<unknown> = Promise.resolve();

/** How many band states are alive (tests, diagnostics). */
export function liveStateCount(): number {
  return bandStates.length;
}

/** Render one band (see `anime4k-tile-renderer.ts` for the contract). Serialised. */
export function renderBand(req: BandRenderRequest): Promise<BandRenderResult> {
  const run = bandQueue.then(() => renderBandNow(req));
  bandQueue = run.catch(() => undefined);
  return run;
}

function bandStateFor(h: GlHandle, mode: RenderMode, width: number, tileRows: number): ChainState {
  const key = `${mode}:${width}x${tileRows}`;
  const hit = bandStates.find((s) => s.key === key && s.gl === h);
  if (hit) {
    bandStates = [...bandStates.filter((s) => s !== hit), hit];
    return hit;
  }
  // Make room first, so the peak is never maxStates + 1 states.
  while (bandStates.length >= maxStates) {
    const [evicted, ...rest] = bandStates;
    bandStates = rest;
    evicted.resources.dispose();
  }
  const state = mode === 'sharp'
    ? buildSharp(h, key, width, tileRows, 2 * width, 2 * tileRows)
    : buildEnhance(h, key, width, tileRows, true);
  bandStates = [...bandStates, state];
  return state;
}

async function renderBandNow(req: BandRenderRequest): Promise<BandRenderResult> {
  const { source, canvas, band, signal } = req;
  const mode: RenderMode = req.mode ?? 'enhance';
  const budget = req.budget ?? createSliceBudget();
  const nextFrame = req.nextFrame ?? nextAnimationFrame;
  let gpuMs = 0;
  let slices = 0;
  const result = (status: BandRenderResult['status']): BandRenderResult => ({ status, gpuMs, slices });

  const width = source.naturalWidth;
  if (!(width > 0) || !(source.naturalHeight >= band.tileY + band.tileRows) || !(band.drawRows > 0)) return result('failed');
  if (signal?.aborted) return result('aborted');

  try {
    const h = acquire();
    if (!h) return result('failed');
    if (h.maxTextureSize > 0 && (2 * width > h.maxTextureSize || 2 * band.tileRows > h.maxTextureSize)) return result('failed');
    const state = bandStateFor(h, mode, width, band.tileRows);
    const alive = () => !h.lost && bandStates.includes(state);

    const bitmap = await createImageBitmap(source, 0, band.tileY, width, band.tileRows);
    try {
      if (!alive()) return result('failed');
      if (signal?.aborted) return result('aborted');
      upload(h.gl, state.input, bitmap);
    } finally {
      bitmap.close();
    }

    let next = 0;
    const steps = state.steps;
    for (;;) {
      if (signal?.aborted) return result('aborted');
      if (!alive()) return result('failed');
      const end = Math.min(steps.length, next + Math.max(1, budget.k));
      for (let i = next; i < end; i++) steps[i]();
      next = end;
      const last = next >= steps.length;
      if (last) {
        // Never overwrite a canvas with a band it no longer shows.
        if (signal?.aborted) return result('aborted');
        const outW = 2 * width;
        const outH = 2 * band.drawRows;
        sizeDrawingBuffer(h, outW, outH);
        state.present(outW, outH, mode === 'sharp' ? 2 * band.cropY : band.cropY / band.tileRows, band.drawRows / band.tileRows);
        if (!blitTo(h, canvas, outW, outH)) return result('failed');
      }
      const elapsed = await gpuDone(h.gl);
      slices++;
      gpuMs += elapsed;
      adaptSlice(budget, elapsed);
      if (!alive()) return result('failed');
      if (last) return result('ok');
      await nextFrame();
    }
  } catch (err) {
    lastError = err instanceof Error ? err.message : String(err);
    return result('failed');
  }
}

/** Delete every band state's textures (Enhance/Sharp off, view switched, reader closed, page hidden). */
export function releaseTiles(): void {
  const old = bandStates;
  bandStates = [];
  for (const s of old) s.resources.dispose();
  shrinkIfIdle();
}

/** Diagnostics for tests: how many GL objects the live states own. */
export function ownedObjectCount(): number {
  return (pageState?.resources.size ?? 0) + bandStates.reduce((sum, s) => sum + s.resources.size, 0);
}
