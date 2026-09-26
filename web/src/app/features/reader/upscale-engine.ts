import type { Upscaler } from '../../core/reading/reader-preferences.service';

/**
 * Rendering engines (1.25.0): which GPU path runs each Rendering choice on THIS
 * device, and - when a choice cannot run - the one-line reason the settings UI
 * shows. Pure (the WebGL2 probe aside), so every state the menu can be in is
 * unit-tested here without a GPU.
 *
 *  - Smooth  - the browser's own scaling. No GPU work of ours, always available.
 *  - Crisp   - AMD FSR 1 (EASU + RCAS) on WebGL2. Needs only a WebGL2 context:
 *              no secure context, no float render targets.
 *  - Enhance - Anime4K. WebGPU when the browser hands out an adapter; WebGPU is
 *              `[SecureContext]`, so over plain `http://<LAN IP>` it does not
 *              exist and Enhance runs on WebGL2 instead (Efficient chain only),
 *              which needs renderable half-float targets.
 *
 * Nothing falls back silently: the engine in use is shown under the selected
 * option ("WebGL2 - WebGPU needs HTTPS"), an option that cannot run is disabled
 * with its reason, and a SAVED choice that cannot run is announced once per
 * session by the reader.
 */

export type RenderMode = 'sharp' | 'enhance';
export type UpscaleEngine = 'webgpu' | 'webgl2';

/** A runnable Rendering choice: what to draw and on which API. */
export interface UpscaleBackend {
  readonly mode: RenderMode;
  readonly engine: UpscaleEngine;
}

/** WebGPU adapter probe (`UpscaleSupportService.support`). */
export type WebGpuStatus = 'checking' | 'ready' | 'unavailable';

/**
 * What a WebGL2 probe found:
 *  - `unsupported` - the browser has no WebGL2 at all;
 *  - `unavailable` - WebGL2 exists but no context could be created (GPU
 *    blocklisted, driver trouble, too many contexts);
 *  - `ready` - a context works; `floatTargets` says whether `RGBA16F` can be
 *    rendered to (needed by Enhance's convolution layers, not by Crisp).
 */
export interface WebGlCaps {
  readonly status: 'unsupported' | 'unavailable' | 'ready';
  readonly floatTargets: boolean;
  readonly maxTextureSize: number;
  /**
   * The browser renders WebGL in software (a blocklisted GPU: a context exists, but not
   * with `failIfMajorPerformanceCaveat`). Crisp is cheap enough to keep; Enhance would
   * take seconds per page, so it is offered disabled instead (owner, 2026-09-26).
   */
  readonly software?: boolean;
}

export interface GpuCaps {
  readonly webgpu: WebGpuStatus;
  readonly webgl: WebGlCaps;
  /** `window.isSecureContext`: WebGPU exists only in a secure context. */
  readonly secure: boolean;
}

export type Availability =
  | { readonly state: 'checking' }
  /** `engine` is null for Smooth (the browser scales; no GPU work of ours). */
  | { readonly state: 'ready'; readonly engine: UpscaleEngine | null; readonly note: string }
  | { readonly state: 'unavailable'; readonly reason: string };

export const noWebGl: WebGlCaps = { status: 'unsupported', floatTargets: false, maxTextureSize: 0 };

/** Reasons, one line each (they sit under a menu item and in the phone sheet). */
export const reasons = {
  noWebGl2: 'Needs WebGL2, which this browser lacks',
  chip: 'Graphics chip unavailable',
  chipFloat: 'Graphics chip lacks float render targets',
  https: 'Needs a secure connection (HTTPS)',
} as const;

/** Can this Rendering choice run here, and on which engine? */
export function availabilityFor(mode: Upscaler, caps: GpuCaps): Availability {
  if (mode === 'smooth') return { state: 'ready', engine: null, note: 'Browser scaling' };
  const gl = caps.webgl;
  if (mode === 'sharp') {
    if (gl.status === 'ready') return { state: 'ready', engine: 'webgl2', note: 'WebGL2' };
    return { state: 'unavailable', reason: gl.status === 'unsupported' ? reasons.noWebGl2 : reasons.chip };
  }
  if (caps.webgpu === 'ready') return { state: 'ready', engine: 'webgpu', note: 'WebGPU' };
  // Wait for the adapter probe (a few ms) rather than flash a WebGL2 line first.
  if (caps.webgpu === 'checking') return { state: 'checking' };
  if (gl.status === 'ready' && gl.floatTargets && !gl.software) {
    return {
      state: 'ready', engine: 'webgl2',
      note: caps.secure ? 'WebGL2 - WebGPU unavailable here' : 'WebGL2 - WebGPU needs HTTPS',
    };
  }
  // Software WebGL: the graphics chip is not available to the browser; HTTPS would not help.
  if (gl.status === 'ready' && gl.software) return { state: 'unavailable', reason: reasons.chip };
  // Over plain HTTP the fix is the connection: HTTPS brings WebGPU.
  if (!caps.secure) return { state: 'unavailable', reason: reasons.https };
  if (gl.status === 'ready') return { state: 'unavailable', reason: reasons.chipFloat };
  return { state: 'unavailable', reason: gl.status === 'unsupported' ? reasons.noWebGl2 : reasons.chip };
}

/** The backend a stored preference runs on here, or null for "plain `<img>`" (Smooth, or cannot run). */
export function backendFor(pref: Upscaler, caps: GpuCaps): UpscaleBackend | null {
  if (pref === 'smooth') return null;
  const a = availabilityFor(pref, caps);
  return a.state === 'ready' && a.engine ? { mode: pref, engine: a.engine } : null;
}

/** Same backend? (`null` = no GPU rendering.) */
export function sameBackend(a: UpscaleBackend | null, b: UpscaleBackend | null): boolean {
  return a === b || (!!a && !!b && a.mode === b.mode && a.engine === b.engine);
}

/** The Rendering choice actually on screen: the preference, or Smooth when it cannot run. */
export function effectiveUpscaler(pref: Upscaler, caps: GpuCaps): Upscaler {
  return availabilityFor(pref, caps).state === 'unavailable' ? 'smooth' : pref;
}

/** Rendering order for the menu and the `e` shortcut. */
export const upscalerOrder: readonly Upscaler[] = ['smooth', 'sharp', 'enhance'];

/** `e`: the next choice after `current` that can run right now (Smooth always can). */
export function nextUpscaler(current: Upscaler, caps: GpuCaps): Upscaler {
  const start = upscalerOrder.indexOf(effectiveUpscaler(current, caps));
  for (let step = 1; step <= upscalerOrder.length; step++) {
    const candidate = upscalerOrder[(start + step) % upscalerOrder.length];
    if (availabilityFor(candidate, caps).state === 'ready') return candidate;
  }
  return 'smooth';
}

export const upscalerLabels: Readonly<Record<Upscaler, string>> = { smooth: 'Smooth', sharp: 'Crisp', enhance: 'Enhance' };

/** Short engine summary for the status line when Smooth is selected (capabilities at a glance). */
export function capsSummary(caps: GpuCaps): string {
  const webgpu = caps.webgpu === 'ready' ? 'WebGPU ready'
    : caps.webgpu === 'checking' ? 'checking WebGPU…'
      : caps.secure ? 'WebGPU unavailable' : 'WebGPU needs HTTPS';
  const gl = caps.webgl;
  const webgl = gl.status !== 'ready' ? 'no WebGL2'
    : gl.software ? 'WebGL2 in software (Crisp only)'
      : gl.floatTargets ? 'WebGL2 ready' : 'WebGL2 ready (Crisp only)';
  return `${webgpu}, ${webgl}`;
}

/**
 * Probe WebGL2 once: create a throwaway context, check `RGBA16F` renderability,
 * read the texture limit, and give the context back immediately
 * (`WEBGL_lose_context`), so the probe never holds one of the browser's few
 * live-context slots. Never throws. Returns `unsupported` without a
 * `WebGL2RenderingContext` global (jsdom), so no canvas is ever touched there.
 */
export function probeWebGl(doc: Document | null = typeof document === 'undefined' ? null : document): WebGlCaps {
  if (!doc || typeof WebGL2RenderingContext === 'undefined') return noWebGl;
  try {
    const canvas = doc.createElement('canvas');
    canvas.width = 1;
    canvas.height = 1;
    const options: WebGLContextAttributes = { antialias: false, depth: false, stencil: false };
    // A hardware context first; if only a "major performance caveat" (software) context
    // exists, Crisp still runs but Enhance is offered disabled.
    let software = false;
    let gl = canvas.getContext('webgl2', { ...options, failIfMajorPerformanceCaveat: true });
    if (!gl) {
      gl = doc.createElement('canvas').getContext('webgl2', options);
      software = !!gl;
    }
    if (!gl) return { status: 'unavailable', floatTargets: false, maxTextureSize: 0 };
    const maxTextureSize = Number(gl.getParameter(gl.MAX_TEXTURE_SIZE)) || 0;
    const floatTargets = canRenderHalfFloat(gl);
    gl.getExtension('WEBGL_lose_context')?.loseContext();
    return { status: 'ready', floatTargets, maxTextureSize, software };
  } catch {
    return { status: 'unavailable', floatTargets: false, maxTextureSize: 0 };
  }
}

/**
 * Enable a float colour-buffer extension and prove it with a real `RGBA16F`
 * framebuffer (an advertised extension is not always a complete framebuffer on
 * mobile drivers). Shared with the lazy WebGL2 renderer.
 */
export function canRenderHalfFloat(gl: WebGL2RenderingContext): boolean {
  if (!gl.getExtension('EXT_color_buffer_float') && !gl.getExtension('EXT_color_buffer_half_float')) return false;
  const tex = gl.createTexture();
  const fb = gl.createFramebuffer();
  try {
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RGBA16F, 4, 4);
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
    return gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
  } catch {
    return false;
  } finally {
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.bindTexture(gl.TEXTURE_2D, null);
    gl.deleteFramebuffer(fb);
    gl.deleteTexture(tex);
  }
}
