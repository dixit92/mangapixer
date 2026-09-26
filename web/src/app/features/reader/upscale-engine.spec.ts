import { vi } from 'vitest';

import { createFakeGl } from './fake-webgl.testing';
import {
  GpuCaps, WebGlCaps, availabilityFor, backendFor, canRenderHalfFloat, capsSummary, effectiveUpscaler, nextUpscaler, noWebGl,
  probeWebGl, reasons, sameBackend,
} from './upscale-engine';

/**
 * Rendering engines (1.25.0): every state the Rendering menu can be in, from the
 * device's capabilities - which engine each choice runs on, or the one-line
 * reason it cannot - plus the `e` cycle and the WebGL2 probe.
 */
const gl = (floatTargets = true): WebGlCaps => ({ status: 'ready', floatTargets, maxTextureSize: 8192 });
const caps = (over: Partial<GpuCaps> = {}): GpuCaps => ({ webgpu: 'unavailable', webgl: gl(), secure: true, ...over });

describe('availabilityFor', () => {
  it('Smooth is always ready and needs no GPU', () => {
    expect(availabilityFor('smooth', caps({ webgl: noWebGl }))).toEqual({ state: 'ready', engine: null, note: 'Browser scaling' });
  });

  it('Sharp runs on WebGL2 - also without float targets and over plain HTTP', () => {
    expect(availabilityFor('sharp', caps({ webgl: gl(false), secure: false }))).toEqual({ state: 'ready', engine: 'webgl2', note: 'WebGL2' });
  });

  it('Sharp names why it cannot run: no WebGL2 at all, or no context (graphics chip)', () => {
    expect(availabilityFor('sharp', caps({ webgl: noWebGl }))).toEqual({ state: 'unavailable', reason: reasons.noWebGl2 });
    expect(availabilityFor('sharp', caps({ webgl: { status: 'unavailable', floatTargets: false, maxTextureSize: 0 } })))
      .toEqual({ state: 'unavailable', reason: reasons.chip });
  });

  it('Enhance prefers WebGPU whenever it is ready', () => {
    expect(availabilityFor('enhance', caps({ webgpu: 'ready' }))).toEqual({ state: 'ready', engine: 'webgpu', note: 'WebGPU' });
    expect(availabilityFor('enhance', caps({ webgpu: 'ready', webgl: noWebGl }))).toEqual({ state: 'ready', engine: 'webgpu', note: 'WebGPU' });
  });

  it('Enhance waits for the WebGPU probe instead of flashing a WebGL2 line', () => {
    expect(availabilityFor('enhance', caps({ webgpu: 'checking' }))).toEqual({ state: 'checking' });
  });

  it('Enhance falls back to WebGL2 with float targets, and says why WebGPU is not used', () => {
    expect(availabilityFor('enhance', caps({ secure: false }))).toEqual({ state: 'ready', engine: 'webgl2', note: 'WebGL2 - WebGPU needs HTTPS' });
    expect(availabilityFor('enhance', caps({ secure: true }))).toEqual({ state: 'ready', engine: 'webgl2', note: 'WebGL2 - WebGPU unavailable here' });
  });

  it('Enhance without WebGPU or WebGL2 float targets names the cause', () => {
    // Plain HTTP: HTTPS is the fix (it brings WebGPU).
    expect(availabilityFor('enhance', caps({ secure: false, webgl: gl(false) }))).toEqual({ state: 'unavailable', reason: reasons.https });
    expect(availabilityFor('enhance', caps({ secure: false, webgl: noWebGl }))).toEqual({ state: 'unavailable', reason: reasons.https });
    // Secure but no usable GPU path: the graphics chip.
    expect(availabilityFor('enhance', caps({ webgl: gl(false) }))).toEqual({ state: 'unavailable', reason: reasons.chipFloat });
    expect(availabilityFor('enhance', caps({ webgl: { status: 'unavailable', floatTargets: false, maxTextureSize: 0 } })))
      .toEqual({ state: 'unavailable', reason: reasons.chip });
    expect(availabilityFor('enhance', caps({ webgl: noWebGl }))).toEqual({ state: 'unavailable', reason: reasons.noWebGl2 });
  });

  it('every reason fits on one short line', () => {
    for (const reason of Object.values(reasons)) expect(reason.length).toBeLessThanOrEqual(42);
  });
});

describe('backends', () => {
  it('backendFor resolves a stored preference to what runs here, or null (Smooth / cannot run)', () => {
    expect(backendFor('smooth', caps())).toBeNull();
    expect(backendFor('sharp', caps())).toEqual({ mode: 'sharp', engine: 'webgl2' });
    expect(backendFor('enhance', caps({ webgpu: 'ready' }))).toEqual({ mode: 'enhance', engine: 'webgpu' });
    expect(backendFor('enhance', caps({ secure: false }))).toEqual({ mode: 'enhance', engine: 'webgl2' });
    expect(backendFor('enhance', caps({ webgpu: 'checking' }))).toBeNull();
    expect(backendFor('sharp', caps({ webgl: noWebGl }))).toBeNull();
  });

  it('sameBackend compares by value', () => {
    expect(sameBackend(null, null)).toBe(true);
    expect(sameBackend({ mode: 'sharp', engine: 'webgl2' }, { mode: 'sharp', engine: 'webgl2' })).toBe(true);
    expect(sameBackend({ mode: 'enhance', engine: 'webgl2' }, { mode: 'enhance', engine: 'webgpu' })).toBe(false);
    expect(sameBackend(null, { mode: 'sharp', engine: 'webgl2' })).toBe(false);
  });

  it('the choice on screen is Smooth when the saved one cannot run (still checking counts as running)', () => {
    expect(effectiveUpscaler('enhance', caps({ secure: false, webgl: gl(false) }))).toBe('smooth');
    expect(effectiveUpscaler('enhance', caps({ webgpu: 'checking' }))).toBe('enhance');
    expect(effectiveUpscaler('sharp', caps())).toBe('sharp');
  });
});

describe('nextUpscaler (the e shortcut)', () => {
  it('cycles Smooth -> Sharp -> Enhance -> Smooth when all three can run', () => {
    const c = caps({ webgpu: 'ready' });
    expect(nextUpscaler('smooth', c)).toBe('sharp');
    expect(nextUpscaler('sharp', c)).toBe('enhance');
    expect(nextUpscaler('enhance', c)).toBe('smooth');
  });

  it('skips what cannot run: Smooth <-> Sharp over HTTP without float targets', () => {
    const c = caps({ secure: false, webgl: gl(false) });
    expect(nextUpscaler('smooth', c)).toBe('sharp');
    expect(nextUpscaler('sharp', c)).toBe('smooth');
  });

  it('Smooth <-> Enhance where only WebGPU exists (no WebGL2)', () => {
    const c = caps({ webgpu: 'ready', webgl: noWebGl });
    expect(nextUpscaler('smooth', c)).toBe('enhance');
    expect(nextUpscaler('enhance', c)).toBe('smooth');
  });

  it('a saved choice that cannot run counts as Smooth; with nothing to run it stays Smooth', () => {
    expect(nextUpscaler('enhance', caps({ secure: false, webgl: gl(false) }))).toBe('sharp');
    expect(nextUpscaler('smooth', caps({ webgl: noWebGl }))).toBe('smooth');
  });

  it('skips Enhance while WebGPU is still being probed', () => {
    expect(nextUpscaler('sharp', caps({ webgpu: 'checking' }))).toBe('smooth');
  });
});

describe('capsSummary', () => {
  it('reads as the device verification line', () => {
    expect(capsSummary(caps({ webgpu: 'ready' }))).toBe('WebGPU ready, WebGL2 ready');
    expect(capsSummary(caps({ secure: false }))).toBe('WebGPU needs HTTPS, WebGL2 ready');
    expect(capsSummary(caps({ webgl: gl(false) }))).toBe('WebGPU unavailable, WebGL2 ready (Sharp only)');
    expect(capsSummary(caps({ webgpu: 'checking', webgl: noWebGl }))).toBe('checking WebGPU…, no WebGL2');
  });
});

describe('probeWebGl', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('is "unsupported" without a WebGL2RenderingContext global (jsdom) and touches no canvas', () => {
    const create = vi.spyOn(document, 'createElement');
    expect(probeWebGl()).toEqual(noWebGl);
    expect(create).not.toHaveBeenCalled();
  });

  it('reads float targets and the texture limit from a throwaway context, then gives it back', () => {
    vi.stubGlobal('WebGL2RenderingContext', function WebGL2RenderingContext() { /* marker */ });
    const fake = createFakeGl(null, { maxTextureSize: 4096 });
    const lose = vi.fn();
    const getExtension = fake.gl.getExtension.bind(fake.gl);
    fake.gl.getExtension = ((name: string) => (name === 'WEBGL_lose_context' ? { loseContext: lose } : getExtension(name))) as typeof fake.gl.getExtension;
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(fake.gl as unknown as GPUCanvasContext);
    expect(probeWebGl()).toEqual({ status: 'ready', floatTargets: true, maxTextureSize: 4096 });
    expect(lose).toHaveBeenCalledTimes(1);
    expect(fake.live()).toBe(0); // the RGBA16F test framebuffer was deleted
  });

  it('is "unavailable" when WebGL2 exists but no context can be created', () => {
    vi.stubGlobal('WebGL2RenderingContext', function WebGL2RenderingContext() { /* marker */ });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    expect(probeWebGl().status).toBe('unavailable');
  });

  it('canRenderHalfFloat needs the extension AND a complete RGBA16F framebuffer', () => {
    expect(canRenderHalfFloat(createFakeGl(null, { floatTargets: false }).gl)).toBe(false);
    expect(canRenderHalfFloat(createFakeGl(null).gl)).toBe(true);
  });
});
