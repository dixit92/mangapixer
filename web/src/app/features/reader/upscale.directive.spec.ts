import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import { FakeGl, createFake2d, createFakeGl } from './fake-webgl.testing';
import { UpscaleDirective, UpscaleSupportService, hasWebGpu } from './upscale.directive';

/**
 * The directive's whole contract in a jsdom world is "do nothing, loudly never".
 * There is no WebGPU here (and no image decoding), so every path must end in a
 * silent no-op: no canvas in the DOM, no dynamic import of the Anime4K chunk, no
 * thrown error. The GPU path itself cannot be exercised headlessly - see the
 * owner device-verification steps in the feature note.
 */
@Component({
  standalone: true,
  imports: [UpscaleDirective],
  template: `<div class="row"><img [appUpscale]="on()" src="/api/v1/items/i/pages/p0" alt="Page" /></div>`,
})
class HostComponent {
  readonly on = signal(true);
}

/** Give the <img> a natural size and a layout box, which jsdom never computes. */
function fakeLayout(img: HTMLImageElement, natural: number, box: number): void {
  Object.defineProperty(img, 'naturalWidth', { value: natural, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: natural * 2, configurable: true });
  Object.defineProperty(img, 'complete', { value: true, configurable: true });
  Object.defineProperty(img, 'offsetWidth', { value: box, configurable: true });
  Object.defineProperty(img, 'offsetHeight', { value: box * 2, configurable: true });
  Object.defineProperty(img, 'offsetLeft', { value: 0, configurable: true });
  Object.defineProperty(img, 'offsetTop', { value: 0, configurable: true });
}

describe('UpscaleDirective', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    // The reader binds [appUpscale] from this preference (Sharp or Enhance).
    TestBed.inject(ReaderPreferencesService).setUpscaler('enhance');
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, img: host.querySelector('img') as HTMLImageElement };
  }

  afterEach(() => {
    delete (navigator as unknown as { gpu?: unknown }).gpu;
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('adds no canvas and throws nothing without WebGPU', async () => {
    const { fixture, host, img } = create();
    fakeLayout(img, 400, 1200); // a 3x upscale: it WOULD want the GPU
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(hasWebGpu()).toBe(false);
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the page is NOT being upscaled, even with WebGPU present', async () => {
    // Pretend the platform has WebGPU so the only reason to bail is the scale.
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fakeLayout(img, 2000, 1000); // rendered at HALF its natural size: a downscale
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the page is rendered at exactly its natural size', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fakeLayout(img, 1000, 1000);
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the preference is off', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fixture.componentInstance.on.set(false);
    fixture.detectChanges();
    fakeLayout(img, 400, 1200);
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('survives an image that never decodes (naturalWidth 0)', async () => {
    const { fixture, host, img } = create();
    Object.defineProperty(img, 'complete', { value: true, configurable: true });
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    expect(() => fixture.detectChanges()).not.toThrow();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('cleans up without error when the host is destroyed', () => {
    const { fixture } = create();
    expect(() => fixture.destroy()).not.toThrow();
  });

  describe('device-pixel gate (devicePixelRatio)', () => {
    function setDpr(value: unknown): void {
      Object.defineProperty(window, 'devicePixelRatio', { value, configurable: true });
    }

    let originalDpr: number;

    beforeEach(() => {
      originalDpr = window.devicePixelRatio;
    });

    afterEach(() => {
      setDpr(originalDpr);
    });

    it('treats a CSS-px downscale as an upscale once DPR 3 device pixels are counted', async () => {
      setDpr(3);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve({}) };
      const { fixture, host, img } = create();
      // Painted at 390 CSS px against a 780px-wide source: 0.5x in CSS px (would
      // read as a downscale) but 390 * 3 / 780 = 1.5x in device px: an upscale.
      fakeLayout(img, 780, 390);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).not.toBeNull();
    });

    it('stays a downscale at DPR 1 for the same painted/natural widths', async () => {
      setDpr(1);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).toBeNull();
    });

    it('is an upscale for the iPad spread case: DPR 2, 1366 CSS px painted, 1415 natural', async () => {
      setDpr(2);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve({}) };
      const { fixture, host, img } = create();
      // 1366 * 2 = 2732 device px against a 1415px-wide source: ~1.93x, an upscale.
      fakeLayout(img, 1415, 1366);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).not.toBeNull();
    });

    it('treats a missing devicePixelRatio as 1 without throwing', async () => {
      setDpr(undefined);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390); // downscale at DPR 1
      img.dispatchEvent(new Event('load'));
      await expect(new Promise((r) => setTimeout(r, 40))).resolves.not.toThrow();
      expect(() => fixture.detectChanges()).not.toThrow();
      expect(host.querySelector('canvas')).toBeNull();
    });

    it('treats a zero devicePixelRatio as 1 without throwing', async () => {
      setDpr(0);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390); // downscale at DPR 1
      img.dispatchEvent(new Event('load'));
      await expect(new Promise((r) => setTimeout(r, 40))).resolves.not.toThrow();
      expect(() => fixture.detectChanges()).not.toThrow();
      expect(host.querySelector('canvas')).toBeNull();
    });
  });
});

describe('hasWebGpu', () => {
  afterEach(() => { delete (navigator as unknown as { gpu?: unknown }).gpu; });

  it('is false in jsdom (no navigator.gpu)', () => {
    expect(hasWebGpu()).toBe(false);
  });

  it('is true once a gpu entry point exists', () => {
    (navigator as unknown as { gpu?: unknown }).gpu = {};
    expect(hasWebGpu()).toBe(true);
  });
});

describe('UpscaleSupportService', () => {
  afterEach(() => { delete (navigator as unknown as { gpu?: unknown }).gpu; });

  const service = () => TestBed.inject(UpscaleSupportService);

  it('reports unavailable with no navigator.gpu, with a readable status', () => {
    const svc = service();
    expect(svc.support()).toBe('unavailable');
    // jsdom: a secure context (localhost), no WebGL2.
    expect(svc.statusText()).toBe('GPU: WebGPU unavailable, no WebGL2');
  });

  it('reports ready when an adapter is handed out', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve({}) };
    const svc = service();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('ready');
    expect(svc.statusText()).toBe('GPU: WebGPU ready, no WebGL2');
  });

  it('reports unavailable when the adapter request resolves to null', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const svc = service();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });

  it('reports unavailable when the adapter request rejects', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.reject(new Error('no')) };
    const svc = service();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });

  it('reports unavailable when requestAdapter throws synchronously', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = {
      requestAdapter: () => { throw new Error('boom'); },
    };
    const svc = service();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });
});

/** 1.25.0: engine resolution for the stored choice, and the once-per-session notice. */
describe('UpscaleSupportService engines (1.25.0)', () => {
  const webgl = (floatTargets = true) => ({ status: 'ready' as const, floatTargets, maxTextureSize: 8192 });
  const setup = () => {
    const svc = TestBed.inject(UpscaleSupportService);
    const prefs = TestBed.inject(ReaderPreferencesService);
    return { svc, prefs };
  };
  afterEach(() => {
    localStorage.clear();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('probes WebGL2 at start-up (a real context, given back at once)', () => {
    vi.stubGlobal('WebGL2RenderingContext', function WebGL2RenderingContext() { /* marker */ });
    const fake = createFakeGl(null, { floatTargets: false, maxTextureSize: 4096 });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(fake.gl as unknown as GPUCanvasContext);
    const { svc } = setup();
    expect(svc.webgl()).toEqual({ status: 'ready', floatTargets: false, maxTextureSize: 4096, software: false });
    expect(svc.sharp()).toEqual({ state: 'ready', engine: 'webgl2', note: 'WebGL2' });
  });

  it('backend and effective follow the stored choice and the device', () => {
    const { svc, prefs } = setup();
    expect(svc.backend()).toBeNull(); // Smooth
    prefs.setUpscaler('sharp');
    expect(svc.backend()).toBeNull(); // no WebGL2 in jsdom
    expect(svc.effective()).toBe('smooth');
    svc.webgl.set(webgl());
    expect(svc.backend()).toEqual({ mode: 'sharp', engine: 'webgl2' });
    expect(svc.effective()).toBe('sharp');
    prefs.setUpscaler('enhance');
    expect(svc.backend()).toEqual({ mode: 'enhance', engine: 'webgl2' });
    svc.support.set('ready');
    expect(svc.backend()).toEqual({ mode: 'enhance', engine: 'webgpu' });
    expect(svc.enhanceEngine()).toBe('webgpu');
  });

  it('announces a saved choice that cannot run here once per session, and nothing that runs', () => {
    const { svc, prefs } = setup();
    expect(svc.pendingNotice()).toBeNull(); // Smooth
    prefs.setUpscaler('enhance');
    expect(svc.pendingNotice()).toBe("Enhance isn't available here - showing Smooth.");
    svc.markNoticeShown();
    expect(svc.pendingNotice()).toBeNull();
    prefs.setUpscaler('sharp');
    expect(svc.pendingNotice()).toBe("Crisp isn't available here - showing Smooth.");
    svc.markNoticeShown();
    prefs.setUpscaler('enhance');
    expect(svc.pendingNotice()).toBeNull(); // already said this session
  });

  it('the unsaved Crisp default that cannot run here is never announced; once saved, it is (1.25.0)', () => {
    localStorage.clear();
    const { svc, prefs } = setup();
    expect(prefs.upscaler()).toBe('sharp'); // the default, not chosen
    expect(prefs.upscalerChosen()).toBe(false);
    expect(svc.pendingNotice()).toBeNull(); // jsdom has no WebGL2: Smooth shows, quietly
    prefs.setUpscaler('sharp');
    expect(svc.pendingNotice()).toBe("Crisp isn't available here - showing Smooth.");
  });

  it('Enhance falling back to WebGL2 is not a notice (it runs; the menu names the engine)', () => {
    const { svc, prefs } = setup();
    svc.secure.set(false);
    svc.webgl.set(webgl());
    prefs.setUpscaler('enhance');
    expect(svc.pendingNotice()).toBeNull();
    expect(svc.statusText()).toBe('GPU: Enhance on WebGL2 - WebGPU needs HTTPS');
  });

  it('says nothing while WebGPU is still being probed', () => {
    const { svc, prefs } = setup();
    svc.support.set('checking');
    prefs.setUpscaler('enhance');
    expect(svc.pendingNotice()).toBeNull();
  });
});

/**
 * 1.25.0: the paged overlay on the WebGL2 engine - the real directive and the real
 * lazy `webgl-upscaler.ts` against a fake WebGL2 context: Sharp renders into a
 * `2d` overlay canvas; switching to WebGPU Enhance re-creates the canvas (a
 * canvas can hold only one context type).
 */
describe('UpscaleDirective on WebGL2 (1.25.0)', () => {
  let fakes: FakeGl[];
  let gl: typeof import('./webgl-upscaler');

  beforeEach(async () => {
    fakes = [];
    Object.defineProperty(window, 'devicePixelRatio', { value: 1, configurable: true });
    vi.stubGlobal('createImageBitmap', () => Promise.resolve({ close: () => undefined }));
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (this: HTMLCanvasElement, type: string) {
      if (type !== '2d') return null;
      const self = this as HTMLCanvasElement & { __2d?: ReturnType<typeof createFake2d> };
      self.__2d ??= createFake2d(this);
      return self.__2d as unknown as CanvasRenderingContext2D;
    } as typeof HTMLCanvasElement.prototype.getContext);
    gl = await import('./webgl-upscaler');
    gl.setGlCanvasFactoryForTests(() => {
      const canvas = document.createElement('canvas');
      const fake = createFakeGl(canvas);
      fakes.push(fake);
      canvas.getContext = ((type: string) => (type === 'webgl2' ? fake.gl : null)) as typeof canvas.getContext;
      return canvas;
    });
  });

  afterEach(() => {
    for (const f of fakes) expect(f.violations).toEqual([]);
    gl.setGlCanvasFactoryForTests(null);
    localStorage.clear();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function create() {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const support = TestBed.inject(UpscaleSupportService);
    support.webgl.set({ status: 'ready', floatTargets: true, maxTextureSize: 8192 });
    TestBed.inject(ReaderPreferencesService).setUpscaler('sharp');
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, support, img: host.querySelector('img') as HTMLImageElement };
  }

  it('Sharp renders the page through FSR 1 into a 2d overlay canvas at the device-pixel target', async () => {
    const { host, img } = create();
    fakeLayout(img, 400, 1000); // 2.5x
    img.dispatchEvent(new Event('load'));
    await vi.waitFor(() => expect((host.querySelector('canvas') as HTMLCanvasElement | null)?.style.display).toBe('block'));
    const canvas = host.querySelector('canvas') as HTMLCanvasElement;
    expect([canvas.width, canvas.height]).toEqual([1000, 2000]);
    expect(fakes[0].draws.map((d) => d.pass)).toEqual(['easu', 'rcas']);
  });

  it('switching Sharp -> WebGPU Enhance re-creates the overlay canvas (one context type per canvas)', async () => {
    const { fixture, host, img, support } = create();
    fakeLayout(img, 400, 1000);
    img.dispatchEvent(new Event('load'));
    await vi.waitFor(() => expect((host.querySelector('canvas') as HTMLCanvasElement | null)?.style.display).toBe('block'));
    const first = host.querySelector('canvas') as HTMLCanvasElement;
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    support.support.set('ready');
    TestBed.inject(ReaderPreferencesService).setUpscaler('enhance');
    fixture.detectChanges();
    await vi.waitFor(() => expect(first.isConnected).toBe(false));
    expect(first.width).toBe(0);
    expect(host.querySelectorAll('canvas').length).toBe(1);
    delete (navigator as unknown as { gpu?: unknown }).gpu;
  });

  it('a choice that cannot run here draws nothing (the reader announces it; the menu says why)', async () => {
    const { fixture, host, img, support } = create();
    support.webgl.set({ status: 'unsupported', floatTargets: false, maxTextureSize: 0 });
    fixture.detectChanges();
    fakeLayout(img, 400, 1000);
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    expect(host.querySelector('canvas')).toBeNull();
    expect(fakes.length).toBe(0);
  });
});
