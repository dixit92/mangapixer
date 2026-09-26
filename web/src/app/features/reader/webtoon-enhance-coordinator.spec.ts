import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';

import type { BandRenderRequest, BandRenderResult } from './anime4k-tile-renderer';
import {
  TileRendererApi, WEBTOON_TILE_RENDERER, WebtoonEnhanceCoordinator, bandRootMargin, deviceLossWindowMs,
  flingSettleMs, hiddenReleaseMs, maxConsecutiveFailures, pageRootMargin, resizeDebounceMs,
} from './webtoon-enhance-coordinator';
import { UpscaleSupportService } from './upscale.directive';

/**
 * WebtoonEnhanceCoordinator against fake IntersectionObserver / ResizeObserver
 * (callbacks captured at construction, fired by hand), fake timers and a mocked
 * tile renderer whose renders the test resolves one by one.
 *
 * Geometry used throughout: strip pages painted 400 CSS px wide at DPR 1 from a
 * 300 x 1200 source (s = 1.33, over the 1.2 gate), so 4 bands of 384 rows; each
 * band is 512 CSS px tall and a page is 1600 CSS px. With a 500 px viewport the
 * pool bound is ceil(2 * 500 / 512) + 1 = 3 canvases.
 */

class FakeIO {
  static all: FakeIO[] = [];
  readonly observed = new Set<Element>();
  constructor(readonly callback: IntersectionObserverCallback, readonly options: IntersectionObserverInit) { FakeIO.all.push(this); }
  observe(el: Element) { this.observed.add(el); }
  unobserve(el: Element) { this.observed.delete(el); }
  disconnect() { this.observed.clear(); }
  fire(targets: Element[], isIntersecting: boolean) {
    this.callback(targets.map((target) => ({ target, isIntersecting, intersectionRatio: isIntersecting ? 1 : 0 }) as unknown as IntersectionObserverEntry),
      this as unknown as IntersectionObserver);
  }
}

class FakeRO {
  static all: FakeRO[] = [];
  readonly observed = new Set<Element>();
  constructor(readonly callback: ResizeObserverCallback) { FakeRO.all.push(this); }
  observe(el: Element) { this.observed.add(el); }
  unobserve(el: Element) { this.observed.delete(el); }
  disconnect() { this.observed.clear(); }
  fire() { this.callback([], this as unknown as ResizeObserver); }
}

interface Pending { req: BandRenderRequest; resolve: (r: BandRenderResult) => void }

function define(el: object, props: Record<string, unknown>) {
  for (const [k, v] of Object.entries(props)) Object.defineProperty(el, k, { configurable: true, get: () => v });
}

describe('WebtoonEnhanceCoordinator', () => {
  let pending: Pending[];
  let loads: number;
  let engines: string[];
  let lostListener: (() => void) | null;
  let renderer: TileRendererApi & {
    renderBand: ReturnType<typeof vi.fn<(req: BandRenderRequest) => Promise<BandRenderResult>>>;
    releaseTiles: ReturnType<typeof vi.fn<() => void>>;
  };
  let scroller: HTMLDivElement;
  let imgs: HTMLImageElement[];
  let c: WebtoonEnhanceCoordinator;

  const pageIO = () => FakeIO.all.find((o) => o.options.rootMargin === pageRootMargin)!;
  const bandIO = () => FakeIO.all.find((o) => o.options.rootMargin === bandRootMargin)!;
  const sentinelsOf = (i: number) => {
    const layer = scroller.querySelector('.mp-enhance-layer')!;
    const container = layer.children[i] as HTMLElement | undefined;
    return container ? Array.from(container.children).filter((el) => el.tagName === 'DIV') : [];
  };
  const canvases = () => Array.from(scroller.querySelectorAll('canvas'));
  const flush = async (ms = 0) => { await vi.advanceTimersByTimeAsync(ms); };
  const ok = (gpuMs = 10): BandRenderResult => ({ status: 'ok', gpuMs, slices: 3 });
  const resolveNext = async (r: BandRenderResult = ok()) => { pending.shift()!.resolve(r); await flush(); };

  function page(i: number, props: Record<string, unknown> = {}) {
    const img = document.createElement('img');
    img.className = 'webtoon-page';
    define(img, {
      complete: true, naturalWidth: 300, naturalHeight: 1200, currentSrc: `/p${i}`,
      offsetLeft: 0, offsetTop: i * 1600, offsetWidth: 400, offsetHeight: 1600, ...props,
    });
    return img;
  }

  beforeEach(() => {
    localStorage.setItem('mangapixer-reader-upscaler', 'smooth'); // the default is Crisp since 1.25.0; these flows predate it
    FakeIO.all = [];
    FakeRO.all = [];
    pending = [];
    loads = 0;
    engines = [];
    lostListener = null;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date', 'performance'] });
    vi.stubGlobal('IntersectionObserver', FakeIO);
    vi.stubGlobal('ResizeObserver', FakeRO);
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(performance.now()), 16) as unknown as number);
    vi.stubGlobal('cancelAnimationFrame', (id: number) => clearTimeout(id));
    (navigator as unknown as { gpu?: unknown }).gpu = {};

    renderer = {
      renderBand: vi.fn<(req: BandRenderRequest) => Promise<BandRenderResult>>((req) => new Promise<BandRenderResult>((resolve) => pending.push({ req, resolve }))),
      releaseTiles: vi.fn<() => void>(),
      onTilesLost: (l: () => void) => { lostListener = l; return () => { lostListener = null; }; },
      createSliceBudget: () => ({ k: 4 }),
    };
    TestBed.configureTestingModule({
      providers: [
        WebtoonEnhanceCoordinator,
        { provide: WEBTOON_TILE_RENDERER, useValue: (engine: string) => { loads++; engines.push(engine); return Promise.resolve(renderer); } },
      ],
    });
    c = TestBed.inject(WebtoonEnhanceCoordinator);
    scroller = document.createElement('div');
    define(scroller, { clientHeight: 500 });
    let top = 0;
    Object.defineProperty(scroller, 'scrollTop', { configurable: true, get: () => top, set: (v: number) => { top = v; } });
    document.body.appendChild(scroller);
    imgs = [0, 1, 2].map((i) => page(i));
    for (const img of imgs) { scroller.appendChild(img); c.register(img); }
    c.attach(scroller);
  });

  afterEach(() => {
    c.destroy();
    scroller.remove();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    delete (navigator as unknown as { gpu?: unknown }).gpu;
  });

  /** Enable, bring page `i` near and report its bands `bands` as intersecting. */
  async function showBands(i: number, bands: number[]) {
    pageIO().fire([imgs[i]], true);
    bandIO().fire(bands.map((b) => sentinelsOf(i)[b]), true);
    await flush();
  }

  it('is inert (never throws, no layer, no renderer) without IntersectionObserver', () => {
    c.destroy();
    vi.stubGlobal('IntersectionObserver', undefined);
    const d = TestBed.runInInjectionContext(() => new WebtoonEnhanceCoordinator());
    const s = document.createElement('div');
    d.attach(s);
    expect(() => d.setEnabled(true)).not.toThrow();
    expect(d.isActive).toBe(false);
    expect(s.children.length).toBe(0);
    d.destroy();
  });

  it('is inert without WebGPU', () => {
    delete (navigator as unknown as { gpu?: unknown }).gpu;
    c.setEnabled(true);
    expect(c.isActive).toBe(false);
    expect(scroller.querySelector('.mp-enhance-layer')).toBeNull();
  });

  it('enable appends an inert layer as the scroller\'s last child and observes every page with the scroller as root', () => {
    c.setEnabled(true);
    const layer = scroller.lastElementChild as HTMLElement;
    expect(layer.className).toBe('mp-enhance-layer');
    expect(layer.style.pointerEvents).toBe('none');
    expect(pageIO().options.root).toBe(scroller);
    expect(bandIO().options.root).toBe(scroller);
    expect([...pageIO().observed]).toEqual(imgs);
  });

  it('a near page gets a container at its offset box and one sentinel per band', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[1]], true);
    const container = scroller.querySelector('.mp-enhance-layer')!.children[0] as HTMLElement;
    expect(container.style.top).toBe('1600px');
    expect(container.style.width).toBe('400px');
    expect(sentinelsOf(0).length).toBe(4);
    expect(bandIO().observed.size).toBe(4);
    expect((sentinelsOf(0)[1] as HTMLElement).style.top).toBe('32%'); // 384 / 1200
  });

  it('a page not decoded yet waits for its load event', async () => {
    c.setEnabled(true);
    define(imgs[0], { complete: false, naturalWidth: 0, naturalHeight: 0 });
    pageIO().fire([imgs[0]], true);
    expect(bandIO().observed.size).toBe(0);
    define(imgs[0], { complete: true, naturalWidth: 300, naturalHeight: 1200 });
    c.loaded(imgs[0]);
    expect(bandIO().observed.size).toBe(4);
  });

  it('only intersecting bands render, one job at a time, with the M chain', async () => {
    c.setEnabled(true);
    await showBands(0, [0, 1]);
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
    expect(renderer.renderBand.mock.calls[0][0].chain).toBe('m');
    await resolveNext();
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
    await resolveNext();
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
    expect(c.liveCanvasCount).toBe(2);
    const shown = canvases().filter((el) => el.style.display === 'block');
    expect(shown.length).toBe(2);
    expect(shown[1].style.top).toBe('32%');
  });

  it('priority: visible bands (most on screen, nearest the centre) first, then ahead, then behind', async () => {
    c.setEnabled(true);
    // Page 0 bands in CSS px: 0-512, 512-1024, 1024-1536, and the shifted last one 1088-1600.
    scroller.scrollTop = 520; // viewport 520-1020: band 1 visible, band 2 ahead, band 0 behind
    pageIO().fire([imgs[0]], true);
    bandIO().fire([sentinelsOf(0)[0], sentinelsOf(0)[2], sentinelsOf(0)[1]], true);
    // The first pump happens with all three queued.
    await flush();
    const order = () => renderer.renderBand.mock.calls.map((call) => (call[0] as BandRenderRequest).band.index);
    expect(order()).toEqual([1]);
    await resolveNext();
    expect(order()).toEqual([1, 2]);
    await resolveNext();
    expect(order()).toEqual([1, 2, 0]);
  });

  it('among visible bands the one most on screen goes first', async () => {
    c.setEnabled(true);
    scroller.scrollTop = 1100; // band 2 (1024-1536) 85% visible, the shifted band 3 (1088-1600) 98%
    pageIO().fire([imgs[0]], true);
    bandIO().fire([sentinelsOf(0)[2], sentinelsOf(0)[3]], true);
    await flush();
    expect((renderer.renderBand.mock.calls[0][0] as BandRenderRequest).band.index).toBe(3);
  });

  it('a band leaving the margin is dropped from the queue; an in-flight one is aborted', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0]], true);
    bandIO().fire([sentinelsOf(0)[0], sentinelsOf(0)[1]], true);
    bandIO().fire([sentinelsOf(0)[1]], false); // left before the pump
    await flush();
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
    const inFlight = pending[0].req;
    bandIO().fire([sentinelsOf(0)[0]], false);
    expect(inFlight.signal?.aborted).toBe(true);
    await resolveNext({ status: 'aborted', gpuMs: 0, slices: 1 });
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
    expect(c.queuedCount).toBe(0);
  });

  it('never holds more canvases than the pool bound; recycling takes the least recently visible', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0], imgs[1]], true);
    const all = [...sentinelsOf(0), ...sentinelsOf(1)];
    // Bands 0-2 of page 0 render and then leave in order 0, 1, 2.
    for (let b = 0; b < 3; b++) {
      bandIO().fire([all[b]], true);
      await flush();
      await resolveNext();
    }
    expect(c.canvasCount).toBe(3);
    for (let b = 0; b < 3; b++) { bandIO().fire([all[b]], false); await flush(1); }
    const leastRecent = renderer.renderBand.mock.calls[0][0].canvas;
    bandIO().fire([all[4]], true); // page 1 band 0
    await flush();
    expect(c.canvasCount).toBe(3);
    expect(renderer.renderBand.mock.calls[3][0].canvas).toBe(leastRecent);
    await resolveNext();
    for (const b of [5, 6, 7]) bandIO().fire([all[b]], true);
    for (let i = 0; i < 3; i++) { await flush(); if (pending.length) await resolveNext(); }
    expect(c.canvasCount).toBeLessThanOrEqual(3);
  });

  it('when every canvas is live a further band keeps its plain <img> (no render, no extra canvas)', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0]], true);
    bandIO().fire(sentinelsOf(0), true); // 4 visible bands, bound 3
    for (let i = 0; i < 4; i++) { await flush(); if (pending.length) await resolveNext(); }
    expect(renderer.renderBand).toHaveBeenCalledTimes(3);
    expect(c.canvasCount).toBe(3);
  });

  it('scrolling back to a rendered band reuses its canvas with no GPU work', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    bandIO().fire([sentinelsOf(0)[0]], false);
    pageIO().fire([imgs[0]], false); // page leaves the coarse margin too: container removed
    expect(scroller.querySelector('.mp-enhance-layer')!.children.length).toBe(0);
    await showBands(0, [0]);
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
    expect(canvases().length).toBe(1);
    expect(canvases()[0].style.display).toBe('block');
  });

  it('a resize / slider move only moves containers: zero renders, then new starts wait the debounce', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    renderer.renderBand.mockClear();
    define(imgs[0], { offsetWidth: 600, offsetHeight: 2400, offsetLeft: 20 }); // slider widened
    FakeRO.all[0].fire();
    await flush(16);
    const container = scroller.querySelector('.mp-enhance-layer')!.children[0] as HTMLElement;
    expect(container.style.width).toBe('600px');
    expect(container.style.left).toBe('20px');
    expect(renderer.renderBand).not.toHaveBeenCalled();
    bandIO().fire([sentinelsOf(0)[1]], true);
    await flush(resizeDebounceMs - 20);
    expect(renderer.renderBand).not.toHaveBeenCalled();
    await flush(40);
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
  });

  it('crossing the 1.2 gate down hides a page\'s canvases; crossing up queues its visible bands', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    define(imgs[0], { offsetWidth: 300, offsetHeight: 1200 }); // s = 1.0
    FakeRO.all[0].fire();
    await flush(16);
    expect(canvases().every((el) => el.style.display === 'none')).toBe(true);
    expect(sentinelsOf(0).length).toBe(0);
    define(imgs[0], { offsetWidth: 400, offsetHeight: 1600 });
    FakeRO.all[0].fire();
    await flush(16);
    expect(sentinelsOf(0).length).toBe(4);
    expect(canvases()[0].style.display).toBe('block'); // same pixels, no re-render
    bandIO().fire([sentinelsOf(0)[1]], true);
    await flush(resizeDebounceMs + 5);
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
  });

  it('no band starts during a fling; rendering resumes shortly after the scroll settles', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0]], true);
    scroller.dispatchEvent(new Event('scroll'));
    await flush(10);
    scroller.scrollTop = 800; // 800 px in 10 ms: far over 2 viewports per second
    scroller.dispatchEvent(new Event('scroll'));
    bandIO().fire([sentinelsOf(0)[1]], true);
    await flush(flingSettleMs - 20);
    expect(renderer.renderBand).not.toHaveBeenCalled();
    await flush(40);
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
  });

  it('device loss re-queues the visible bands once; a second loss within a minute pauses webtoon Enhance', async () => {
    const support = TestBed.inject(UpscaleSupportService);
    support.support.set('ready');
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    lostListener!();
    await flush();
    expect(canvases()[0].style.display).toBe('none');
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
    await resolveNext();
    await flush(deviceLossWindowMs - 1000);
    lostListener!();
    await flush();
    expect(support.webtoonPaused()).toBe(true);
    expect(support.statusText()).toContain('Enhance paused (GPU reset)');
    expect(c.isActive).toBe(false);
    expect(canvases().length).toBe(0);
    support.webtoonPaused.set(false);
  });

  it('disable zero-sizes, unconfigures and detaches every canvas and releases the tile pipelines', async () => {
    c.setEnabled(true);
    await showBands(0, [0, 1]);
    await resolveNext();
    await resolveNext();
    const els = canvases();
    for (const el of els) { el.width = 1600; el.height = 772; }
    c.setEnabled(false);
    expect(els.every((el) => el.width === 0 && el.height === 0 && !el.isConnected)).toBe(true);
    expect(scroller.querySelector('.mp-enhance-layer')).toBeNull();
    expect(renderer.releaseTiles).toHaveBeenCalledTimes(1);
    expect(c.canvasCount).toBe(0);
  });

  it('destroy releases too; a coordinator that never rendered never loads the renderer chunk', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0]], true);
    await flush();
    c.setEnabled(false);
    c.destroy();
    expect(loads).toBe(0);
    expect(renderer.releaseTiles).not.toHaveBeenCalled();
  });

  it('hidden for a minute frees the pool and pipelines; visible again re-queues the visible bands', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    let state = 'hidden';
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    document.dispatchEvent(new Event('visibilitychange'));
    await flush(hiddenReleaseMs + 10);
    expect(renderer.releaseTiles).toHaveBeenCalledTimes(1);
    expect(c.canvasCount).toBe(0);
    state = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
    delete (document as unknown as { visibilityState?: string }).visibilityState;
  });

  it('records each band\'s GPU time for the ms/band readout', async () => {
    const support = TestBed.inject(UpscaleSupportService);
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext(ok(18));
    expect(support.bandMs()).toBe(18);
  });

  it(`stops for the strip after ${maxConsecutiveFailures} consecutive failed bands (plain <img>s)`, async () => {
    c.setEnabled(true);
    await showBands(0, [0, 1, 2]);
    for (let i = 0; i < maxConsecutiveFailures; i++) await resolveNext({ status: 'failed', gpuMs: 0, slices: 0 });
    expect(c.isActive).toBe(false);
    expect(canvases().length).toBe(0);
  });

  it('an enhanced band fades in from the next frame', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    pending.shift()!.resolve(ok());
    await flush();
    const canvas = canvases()[0];
    expect(canvas.style.display).toBe('block');
    expect(canvas.style.opacity).toBe('0');
    expect(canvas.style.transition).toContain('opacity');
    await flush(16);
    expect(canvas.style.opacity).toBe('1');
  });

  it('a page settling ABOVE the near ones re-places their containers without a render', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[1]], true); // only page 1 is near: its container is the layer's first child
    bandIO().fire([sentinelsOf(0)[0]], true);
    await flush();
    await resolveNext();
    const container = scroller.querySelector('.mp-enhance-layer')!.children[0] as HTMLElement;
    expect(container.style.top).toBe('1600px');
    define(imgs[1], { offsetTop: 1700 }); // page 0 (not near) finished loading 100 px taller
    c.loaded(imgs[0]);
    await flush(16);
    expect(container.style.top).toBe('1700px');
    expect(renderer.renderBand).toHaveBeenCalledTimes(1);
  });

  it('a smaller pool bound (wider slider) trims surplus canvases on the next band', async () => {
    c.setEnabled(true);
    pageIO().fire([imgs[0]], true);
    for (let b = 0; b < 3; b++) { bandIO().fire([sentinelsOf(0)[b]], true); await flush(); await resolveNext(); }
    for (let b = 0; b < 3; b++) bandIO().fire([sentinelsOf(0)[b]], false);
    expect(c.canvasCount).toBe(3);
    define(imgs[0], { offsetWidth: 800, offsetHeight: 3200 }); // band 1024 CSS px: bound 2
    FakeRO.all[0].fire();
    await flush(16);
    bandIO().fire([sentinelsOf(0)[3]], true);
    await flush(resizeDebounceMs + 5);
    expect(c.canvasCount).toBe(2);
  });

  it('a new src (Page quality switch) invalidates the page\'s canvases: the band renders again', async () => {
    c.setEnabled(true);
    await showBands(0, [0]);
    await resolveNext();
    define(imgs[0], { currentSrc: '/p0?full' });
    c.loaded(imgs[0]);
    bandIO().fire([sentinelsOf(0)[0]], true);
    await flush();
    expect(renderer.renderBand).toHaveBeenCalledTimes(2);
  });

  /** 1.25.0: the strip renders whichever backend the Upscaling choice resolved to. */
  describe('engines (1.25.0)', () => {
    const sharpGl = { mode: 'sharp' as const, engine: 'webgl2' as const };
    const enhanceGl = { mode: 'enhance' as const, engine: 'webgl2' as const };

    it('a WebGL2 backend runs without navigator.gpu, loads the WebGL2 renderer and passes the mode', async () => {
      delete (navigator as unknown as { gpu?: unknown }).gpu;
      c.setEnabled(true, sharpGl);
      expect(c.isActive).toBe(true);
      await showBands(0, [0]);
      expect(engines).toEqual(['webgl2']);
      expect(pending[0].req.mode).toBe('sharp');
      await resolveNext();
      expect(canvases().length).toBe(1);
    });

    it('setEnabled(true) without a backend keeps the 1.24.0 meaning: Enhance on WebGPU', async () => {
      c.setEnabled(true);
      expect(c.currentBackend).toEqual({ mode: 'enhance', engine: 'webgpu' });
      await showBands(0, [0]);
      expect(engines).toEqual(['webgpu']);
      expect(pending[0].req.mode).toBe('enhance');
    });

    it('a backend change tears the layer down (fresh canvases, a canvas holds one context type) and loads that engine', async () => {
      c.setEnabled(true);
      await showBands(0, [0]);
      await resolveNext();
      const old = canvases();
      expect(old.length).toBe(1);
      const oldLayer = scroller.querySelector('.mp-enhance-layer');

      c.setEnabled(true, enhanceGl);
      expect(old[0].isConnected).toBe(false);
      expect(old[0].width).toBe(0);
      expect(renderer.releaseTiles).toHaveBeenCalledTimes(1);
      expect(scroller.querySelector('.mp-enhance-layer')).not.toBe(oldLayer);
      await showBands(0, [0]);
      expect(engines).toEqual(['webgpu', 'webgl2']);
      expect(pending[pending.length - 1].req.mode).toBe('enhance');
    });

    it('the same backend again is a no-op (no teardown, no reload)', async () => {
      c.setEnabled(true, sharpGl);
      await showBands(0, [0]);
      const layer = scroller.querySelector('.mp-enhance-layer');
      c.setEnabled(true, { ...sharpGl });
      expect(scroller.querySelector('.mp-enhance-layer')).toBe(layer);
      expect(loads).toBe(1);
    });

    it('band heights follow the engine: a wide source gets taller bands on light WebGL2 Sharp', async () => {
      const wide = page(0, { naturalWidth: 3000, naturalHeight: 3000, offsetWidth: 4000, offsetHeight: 4000 });
      c.unregister(imgs[0]);
      imgs[0].remove();
      imgs[0] = wide;
      scroller.prepend(wide);
      c.register(wide);
      c.setEnabled(true); // WebGPU M: 228 B/px -> 256-row bands
      pageIO().fire([wide], true);
      expect(sentinelsOf(0).length).toBe(Math.ceil(3000 / 256));
      c.setEnabled(true, sharpGl); // WebGL2 Sharp: 36 B/px -> the 384 default
      pageIO().fire([wide], true);
      expect(sentinelsOf(0).length).toBe(Math.ceil(3000 / 384));
    });

    it('releasing a WebGL2 band canvas never creates a webgpu context on it', async () => {
      delete (navigator as unknown as { gpu?: unknown }).gpu;
      c.setEnabled(true, sharpGl);
      await showBands(0, [0]);
      await resolveNext();
      const canvas = canvases()[0];
      const getContext = vi.spyOn(canvas, 'getContext');
      c.setEnabled(false, sharpGl);
      expect(getContext).not.toHaveBeenCalledWith('webgpu');
      expect(canvas.width).toBe(0);
    });

    it('a new engine gets a fresh chance after the old one stopped on repeated failures', async () => {
      c.setEnabled(true);
      await showBands(0, [0, 1, 2]);
      for (let i = 0; i < maxConsecutiveFailures; i++) await resolveNext({ status: 'failed', gpuMs: 0, slices: 0 });
      expect(c.isActive).toBe(false);
      c.setEnabled(true, sharpGl);
      expect(c.isActive).toBe(true);
    });

    it('a WebGL2 context loss follows the same rule: re-queue once, a second loss within a minute pauses', async () => {
      const support = TestBed.inject(UpscaleSupportService);
      c.setEnabled(true, enhanceGl);
      await showBands(0, [0]);
      await resolveNext();
      lostListener!();
      await flush();
      expect(renderer.renderBand).toHaveBeenCalledTimes(2);
      await resolveNext();
      lostListener!();
      await flush();
      expect(support.webtoonPaused()).toBe(true);
      expect(c.isActive).toBe(false);
      support.webtoonPaused.set(false);
    });
  });
});
