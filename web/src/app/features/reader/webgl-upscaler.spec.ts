import { vi } from 'vitest';

import { FakeGl, FakeGlOptions, createFake2d, createFakeGl } from './fake-webgl.testing';
import {
  glDiagnostics, liveStateCount, maxStates, onTilesLost, ownedObjectCount, releasePages, releaseTiles, renderBand, renderPage,
  setGlCanvasFactoryForTests,
} from './webgl-upscaler';
import { planBands } from './webtoon-band-plan';

/**
 * The WebGL2 upscaler (Sharp = FSR 1, Enhance = Anime4K M) against a recording
 * fake context: which passes run, what they allocate, that everything is deleted
 * on release / eviction, that no draw is a feedback loop or touches a deleted
 * object, the shared-context and context-loss rules. Shader COMPILATION and the
 * pictures themselves are checked in a real browser (Playwright, see the note).
 */

function page(width: number, height: number): HTMLImageElement {
  const img = document.createElement('img');
  Object.defineProperty(img, 'naturalWidth', { value: width, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: height, configurable: true });
  return img;
}

describe('webgl-upscaler', () => {
  let contexts: FakeGl[];
  let glCanvases: HTMLCanvasElement[];
  let options: FakeGlOptions;
  let bitmaps: unknown[][];
  const gl = () => contexts[contexts.length - 1];

  beforeEach(() => {
    contexts = [];
    glCanvases = [];
    options = {};
    bitmaps = [];
    vi.stubGlobal('createImageBitmap', (...args: unknown[]) => { bitmaps.push(args); return Promise.resolve({ close: () => undefined }); });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement, type: string) {
      if (type === '2d') {
        const self = this as HTMLCanvasElement & { __2d?: ReturnType<typeof createFake2d> };
        self.__2d ??= createFake2d(this);
        return self.__2d as unknown as CanvasRenderingContext2D;
      }
      return null;
    } as typeof HTMLCanvasElement.prototype.getContext);
    setGlCanvasFactoryForTests(() => {
      const canvas = document.createElement('canvas');
      const fake = createFakeGl(canvas, options);
      contexts.push(fake);
      glCanvases.push(canvas);
      canvas.getContext = ((type: string) => (type === 'webgl2' ? fake.gl : null)) as typeof canvas.getContext;
      return canvas;
    });
  });

  afterEach(() => {
    for (const c of contexts) expect(c.violations).toEqual([]);
    releasePages();
    releaseTiles();
    setGlCanvasFactoryForTests(null);
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  const drawnOnto = (canvas: HTMLCanvasElement) => (canvas as HTMLCanvasElement & { __2d?: ReturnType<typeof createFake2d> }).__2d?.drawn ?? [];

  describe('paged Sharp (FSR 1)', () => {
    it('runs EASU into an RGBA8 target at the output size, then RCAS into the drawing buffer, and copies it onto the overlay', async () => {
      const overlay = document.createElement('canvas');
      const ok = await renderPage({ source: page(400, 600), canvas: overlay, targetWidth: 1000, targetHeight: 1500, mode: 'sharp' });
      expect(ok).toBe(true);
      expect(gl().draws.map((d) => d.pass)).toEqual(['easu', 'rcas']);
      const [easu, rcas] = gl().draws;
      expect([easu.width, easu.height]).toEqual([1000, 1500]);
      expect(easu.target?.format).toBe(0x8058); // RGBA8: Sharp needs no float targets
      expect(easu.uniforms.get('con0')).toEqual([0.4, 0.4, 0.5 * 0.4 - 0.5, 0.5 * 0.4 - 0.5]);
      expect(rcas.target).toBeNull();
      expect([rcas.width, rcas.height]).toEqual([1000, 1500]);
      expect(rcas.uniforms.get('offset')).toEqual([0, 0]);
      expect(rcas.uniforms.get('outHeight')).toBe(1500);
      expect(rcas.uniforms.get('sharpness')).toBeCloseTo(Math.pow(2, -0.2));
      expect([overlay.width, overlay.height]).toEqual([1000, 1500]);
      expect(drawnOnto(overlay).map((d) => d.source)).toEqual([glCanvases[0]]);
      expect([glCanvases[0].width, glCanvases[0].height]).toEqual([1000, 1500]);
      // Input texture + EASU target + its framebuffer.
      expect(ownedObjectCount()).toBe(3);
    });

    it('works without float render targets (any WebGL2 device)', async () => {
      options = { floatTargets: false };
      const ok = await renderPage({ source: page(400, 600), canvas: document.createElement('canvas'), targetWidth: 800, targetHeight: 1200, mode: 'sharp' });
      expect(ok).toBe(true);
    });

    it('reuses the state while page and target sizes stay, rebuilds (deleting the old) when they change', async () => {
      const req = { source: page(400, 600), canvas: document.createElement('canvas'), targetWidth: 1000, targetHeight: 1500, mode: 'sharp' as const };
      await renderPage(req);
      const allocated = gl().textures.length; // (includes the context's one-off float-target probe)
      await renderPage({ ...req, source: page(400, 600) });
      expect(gl().textures.length).toBe(allocated);
      expect(gl().textures.filter((t) => t.uploads === 2).length).toBe(1); // the input, uploaded again
      await renderPage({ ...req, targetWidth: 800, targetHeight: 1200 });
      expect(gl().textures.length).toBe(allocated + 2);
      expect(gl().live()).toBe(3);
      expect(contexts.length).toBe(1); // ONE shared context
    });
  });

  describe('paged Enhance (Anime4K M on WebGL2)', () => {
    it('runs 2 statistics + 8 restore + 9 x2 passes, then the clamped present, when the page is enlarged > 1.2x', async () => {
      const overlay = document.createElement('canvas');
      const ok = await renderPage({ source: page(300, 400), canvas: overlay, targetWidth: 750, targetHeight: 1000, mode: 'enhance' });
      expect(ok).toBe(true);
      const passes = gl().draws.map((d) => d.pass);
      expect(passes.length).toBe(20);
      expect(passes.slice(0, 2).every((p) => p.includes('De-Ring-Compute-Statistics'))).toBe(true);
      expect(passes.slice(2, 10).every((p) => p.includes('Restore-CNN-(M)'))).toBe(true);
      expect(passes.slice(10, 19).every((p) => p.includes('Upscale-CNN-x2-(M)'))).toBe(true);
      expect(passes[18]).toContain('Depth-to-Space');
      expect(passes[19]).toBe('present');
      const depthToSpace = gl().draws[18];
      expect([depthToSpace.width, depthToSpace.height]).toEqual([600, 800]);
      const present = gl().draws[19];
      expect(present.target).toBeNull();
      expect(present.uniforms.get('outSize')).toEqual([750, 1000]);
      expect(present.uniforms.get('crop')).toEqual([0, 1]);
      expect([overlay.width, overlay.height]).toEqual([750, 1000]);
      // Every intermediate is RGBA16F; only the input is RGBA8.
      expect(gl().textures.filter((t) => t.format === 0x8058).length).toBe(1);
    });

    it('restores only (no x2) when the page is enlarged by 1.2x or less', async () => {
      await renderPage({ source: page(1000, 1400), canvas: document.createElement('canvas'), targetWidth: 1150, targetHeight: 1610, mode: 'enhance' });
      const passes = gl().draws.map((d) => d.pass);
      expect(passes.length).toBe(11);
      expect(passes.some((p) => p.includes('x2'))).toBe(false);
    });

    it('is refused without float render targets (the menu disables it there)', async () => {
      options = { floatTargets: false };
      const ok = await renderPage({ source: page(300, 400), canvas: document.createElement('canvas'), targetWidth: 750, targetHeight: 1000, mode: 'enhance' });
      expect(ok).toBe(false);
      expect(glDiagnostics()).toContain('float');
      expect(ownedObjectCount()).toBe(0);
    });

    it('refuses a page whose 2x intermediate exceeds MAX_TEXTURE_SIZE, allocating nothing', async () => {
      options = { maxTextureSize: 4096 };
      const ok = await renderPage({ source: page(2400, 3000), canvas: document.createElement('canvas'), targetWidth: 3000, targetHeight: 3750, mode: 'enhance' });
      expect(ok).toBe(false);
      expect(gl().live()).toBe(0);
      expect(gl().draws).toEqual([]);
    });

    it('a shader that fails to compile resolves false, frees what it allocated, and says why (diagnostics only)', async () => {
      options = { failCompile: /Restore-CNN/ };
      const ok = await renderPage({ source: page(300, 400), canvas: document.createElement('canvas'), targetWidth: 750, targetHeight: 1000, mode: 'enhance' });
      expect(ok).toBe(false);
      expect(glDiagnostics()).toContain('shader compile failed');
      expect(gl().live()).toBe(0);
    });

    it('releasePages() deletes every texture and framebuffer and shrinks the drawing buffer', async () => {
      await renderPage({ source: page(300, 400), canvas: document.createElement('canvas'), targetWidth: 750, targetHeight: 1000, mode: 'enhance' });
      expect(gl().live()).toBeGreaterThan(20);
      releasePages();
      expect(gl().live()).toBe(0);
      expect([glCanvases[0].width, glCanvases[0].height]).toEqual([1, 1]);
    });
  });

  describe('webtoon bands', () => {
    const tall = () => page(600, 2000);
    const plan = () => planBands(600, 2000, 384);
    const now = () => Promise.resolve();

    it('Sharp: EASU the whole tile at 2x, RCAS only the band rows (offset by 2 * cropY) onto a 2x band canvas', async () => {
      const band = plan()[1];
      const canvas = document.createElement('canvas');
      const r = await renderBand({ source: tall(), canvas, band, chain: 'm', mode: 'sharp', nextFrame: now });
      expect(r.status).toBe('ok');
      expect(bitmaps[0].slice(1)).toEqual([0, band.tileY, 600, band.tileRows]);
      const [easu, rcas] = gl().draws;
      expect([easu.width, easu.height]).toEqual([1200, 2 * band.tileRows]);
      expect(rcas.uniforms.get('offset')).toEqual([0, 2 * band.cropY]);
      expect([canvas.width, canvas.height]).toEqual([1200, 2 * band.drawRows]);
      expect(drawnOnto(canvas).length).toBe(1);
    });

    it('Enhance: 19 passes sliced across frames (AIMD), the present crops the band rows', async () => {
      const band = plan()[2];
      const canvas = document.createElement('canvas');
      const frames = vi.fn(() => Promise.resolve());
      const budget = { k: 4 };
      const r = await renderBand({ source: tall(), canvas, band, chain: 'm', mode: 'enhance', budget, nextFrame: frames });
      expect(r.status).toBe('ok');
      expect(gl().draws.length).toBe(20);
      // k grows 4, 5, 6 ... while slices stay under budget: 4 + 5 + 6 + 4 remaining = 19 steps in 4 slices.
      expect(r.slices).toBe(4);
      expect(frames).toHaveBeenCalledTimes(3);
      const present = gl().draws[19];
      expect(present.uniforms.get('crop')).toEqual([band.cropY / band.tileRows, band.drawRows / band.tileRows]);
      expect([canvas.width, canvas.height]).toEqual([1200, 2 * band.drawRows]);
    });

    it('keeps at most maxStates band states, least recently used out, deleting the evicted one', async () => {
      const canvas = document.createElement('canvas');
      await renderBand({ source: page(600, 2000), canvas, band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'sharp', nextFrame: now });
      await renderBand({ source: page(500, 2000), canvas, band: planBands(500, 2000, 384)[0], chain: 'm', mode: 'sharp', nextFrame: now });
      expect(liveStateCount()).toBe(maxStates);
      const before = gl().live();
      await renderBand({ source: page(400, 2000), canvas, band: planBands(400, 2000, 384)[0], chain: 'm', mode: 'sharp', nextFrame: now });
      expect(liveStateCount()).toBe(maxStates);
      expect(gl().live()).toBe(before); // one evicted (deleted), one built
    });

    it('an aborted job stops between slices and never touches the canvas', async () => {
      const canvas = document.createElement('canvas');
      const ctrl = new AbortController();
      const frames = () => { ctrl.abort(); return Promise.resolve(); };
      const r = await renderBand({ source: tall(), canvas, band: plan()[0], chain: 'm', mode: 'enhance', signal: ctrl.signal, budget: { k: 2 }, nextFrame: frames });
      expect(r.status).toBe('aborted');
      expect(drawnOnto(canvas)).toEqual([]);
      expect(gl().draws.some((d) => d.pass === 'present')).toBe(false);
    });

    it('releaseTiles() deletes every band texture and framebuffer', async () => {
      const canvas = document.createElement('canvas');
      await renderBand({ source: tall(), canvas, band: plan()[0], chain: 'm', mode: 'enhance', nextFrame: now });
      expect(gl().live()).toBeGreaterThan(0);
      releaseTiles();
      expect(gl().live()).toBe(0);
      expect(liveStateCount()).toBe(0);
    });

    it('rejects a tile wider than half MAX_TEXTURE_SIZE (its 2x would not fit)', async () => {
      options = { maxTextureSize: 1024 };
      const r = await renderBand({ source: tall(), canvas: document.createElement('canvas'), band: plan()[0], chain: 'm', mode: 'sharp', nextFrame: now });
      expect(r.status).toBe('failed');
    });
  });

  describe('the shared context', () => {
    it('paged pages and webtoon bands share ONE context', async () => {
      await renderPage({ source: page(400, 600), canvas: document.createElement('canvas'), targetWidth: 1000, targetHeight: 1500, mode: 'sharp' });
      await renderBand({ source: page(600, 2000), canvas: document.createElement('canvas'), band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'sharp', nextFrame: () => Promise.resolve() });
      expect(contexts.length).toBe(1);
    });

    it('context loss tells the listeners, drops every state, and the next render builds a fresh context', async () => {
      const lost = vi.fn();
      const off = onTilesLost(lost);
      const canvas = document.createElement('canvas');
      await renderBand({ source: page(600, 2000), canvas, band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'enhance', nextFrame: () => Promise.resolve() });
      expect(liveStateCount()).toBe(1);
      contexts[0].loseContext();
      expect(lost).toHaveBeenCalledTimes(1);
      expect(liveStateCount()).toBe(0);
      const r = await renderBand({ source: page(600, 2000), canvas, band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'enhance', nextFrame: () => Promise.resolve() });
      expect(r.status).toBe('ok');
      expect(contexts.length).toBe(2);
      off();
    });

    it('a band in flight when the context is lost fails instead of drawing', async () => {
      const canvas = document.createElement('canvas');
      const frames = () => { contexts[0].loseContext(); return Promise.resolve(); };
      const r = await renderBand({ source: page(600, 2000), canvas, band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'enhance', budget: { k: 2 }, nextFrame: frames });
      expect(r.status).toBe('failed');
      expect(drawnOnto(canvas)).toEqual([]);
    });

    it('no context (WebGL2 refused) resolves false / failed without throwing', async () => {
      setGlCanvasFactoryForTests(() => null);
      expect(await renderPage({ source: page(400, 600), canvas: document.createElement('canvas'), targetWidth: 1000, targetHeight: 1500, mode: 'sharp' })).toBe(false);
      const r = await renderBand({ source: page(600, 2000), canvas: document.createElement('canvas'), band: planBands(600, 2000, 384)[0], chain: 'm', mode: 'sharp' });
      expect(r.status).toBe('failed');
    });
  });
});
