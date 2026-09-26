import { Injectable, InjectionToken, NgZone, inject } from '@angular/core';

import type { BandRenderRequest, BandRenderResult, SliceBudget } from './anime4k-tile-renderer';
import { UpscaleBackend, UpscaleEngine, sameBackend } from './upscale-engine';
import { UpscaleSupportService, hasWebGpu } from './upscale.directive';
import {
  BandPlan, EnhanceChain, TileProfile, bandCssHeight, bandRowsFor, planBands, poolSize, tileProfileFor, webtoonEnhanceGate,
} from './webtoon-band-plan';
import { prefersReducedMotion } from './webtoon-nav.service';

/**
 * Webtoon Enhance coordinator (1.24.0): everything between the strip's `<img>`s
 * and the lazy tile renderer. A plain class (no template, no styles), provided by
 * `WebtoonEnhanceHostDirective` on the webtoon scroller, so it lives exactly as
 * long as the strip. It owns:
 *
 *  - the ENHANCE LAYER: one absolutely positioned, `pointer-events: none` div
 *    appended as the scroller's last child (after Angular's `@for` anchor, so the
 *    template's nodes are never disturbed). Each NEAR page gets a container in it,
 *    positioned from the img's offset box; band canvases and band sentinels sit in
 *    the container at % offsets, so a slider move or resize is 4 style writes per
 *    near page and NEVER a re-render (fixed 2x backing, see `webtoon-band-plan.ts`);
 *  - a two-level IntersectionObserver rooted at the scroller: a coarse PAGE
 *    observer (1.5 viewports behind, 2.5 ahead) that builds/tears down a page's
 *    container and sentinels, and a fine BAND observer (0.25 behind, 0.75 ahead)
 *    that queues and drops band renders;
 *  - a bounded, LRU-recycled CANVAS POOL (`poolSize`, <= 12): live, recyclable
 *    (band left the margin, pixels kept - scrolling back costs no GPU work) or
 *    free (zero-sized, unconfigured);
 *  - ONE serial render queue (visible bands first, then ahead, then behind),
 *    paused during a fling and briefly after a resize;
 *  - device-loss handling (re-queue once; a second loss within a minute pauses
 *    webtoon Enhance for the session) and the ms/band median readout.
 *
 * Engines (1.25.0): the coordinator renders whichever backend the Upscaling
 * choice resolved to - Enhance on WebGPU (`anime4k-tile-renderer.ts`), or Sharp /
 * Enhance on WebGL2 (`webgl-upscaler.ts`, one shared context whose output is
 * copied onto `2d` band canvases). A canvas holds one context type, so a backend
 * change tears the layer down and rebuilds it with fresh canvases; the band
 * height follows the engine's bytes per pixel (`tileProfileFor`).
 *
 * Every callback runs outside Angular (no change detection per scroll or
 * observer tick). Without IntersectionObserver / ResizeObserver (jsdom), or
 * without the backend's API, the coordinator is inert and never throws; the
 * plain `<img>` is always underneath, so every failure simply shows today's
 * picture.
 */

/** The lazy renderer surface the coordinator uses (a seam for tests). */
export interface TileRendererApi {
  renderBand(req: BandRenderRequest): Promise<BandRenderResult>;
  releaseTiles(): void;
  onTilesLost(listener: () => void): () => void;
  createSliceBudget(): SliceBudget;
}

/** How the coordinator loads an engine's tile renderer: a dynamic import, i.e. a lazy chunk per engine. */
export const WEBTOON_TILE_RENDERER = new InjectionToken<(engine: UpscaleEngine) => Promise<TileRendererApi>>('WEBTOON_TILE_RENDERER', {
  providedIn: 'root',
  factory: () => (engine: UpscaleEngine) => engine === 'webgl2' ? import('./webgl-upscaler') : import('./anime4k-tile-renderer'),
});

/** Webtoon always runs the light M chain (owner decision 2026-09-25; see the settings menu). */
export const webtoonChain: EnhanceChain = 'm';

/** The backend `setEnabled(true)` means when none is given: Enhance on WebGPU (the 1.24.0 behaviour). */
export const webgpuEnhance: UpscaleBackend = { mode: 'enhance', engine: 'webgpu' };

export const pageRootMargin = '150% 0px 250% 0px';
export const bandRootMargin = '25% 0px 75% 0px';
/** Scroll faster than this many viewport heights per second is a fling: start no new band. */
export const flingViewportsPerSecond = 2;
/** Resume rendering this long after the last fling-speed scroll sample. */
export const flingSettleMs = 120;
/** Slider / resize / rotation: debounce new band starts by this much. */
export const resizeDebounceMs = 150;
/** Release the pool and pipelines after the page has been hidden this long. */
export const hiddenReleaseMs = 60_000;
/** A second device loss within this window pauses webtoon Enhance for the session. */
export const deviceLossWindowMs = 60_000;
/** Consecutive failed bands after which webtoon Enhance stops for this strip. */
export const maxConsecutiveFailures = 3;
/** Canvas fade-in (instant under prefers-reduced-motion). */
export const fadeMs = 120;

interface Geometry { left: number; top: number; width: number; height: number }

interface PageState {
  readonly img: HTMLImageElement;
  near: boolean;
  /** The src the current plan / canvases were made from. */
  src: string;
  /** Bumped whenever the page's pixels change (src switch): stale canvases are ignored. */
  generation: number;
  gateOn: boolean;
  nativeWidth: number;
  nativeHeight: number;
  /** The tile profile `plan` was made for (band height depends on the engine). */
  profile: TileProfile | null;
  plan: BandPlan[];
  bands: BandState[];
  container: HTMLDivElement | null;
  geometry: Geometry | null;
}

interface BandState {
  readonly page: PageState;
  readonly plan: BandPlan;
  readonly sentinel: HTMLDivElement;
  intersecting: boolean;
}

type CanvasState = 'live' | 'recyclable' | 'free';

interface PoolEntry {
  readonly canvas: HTMLCanvasElement;
  state: CanvasState;
  page: PageState | null;
  index: number;
  generation: number;
  /** Holds finished pixels for its owner (false while rendering, after device loss, when free). */
  rendered: boolean;
  lastVisible: number;
}

interface Job { band: BandState; entry: PoolEntry; ctrl: AbortController }

@Injectable()
export class WebtoonEnhanceCoordinator {
  private readonly zone = inject(NgZone);
  private readonly support = inject(UpscaleSupportService);
  private readonly loadRenderer = inject(WEBTOON_TILE_RENDERER);

  private scroller: HTMLElement | null = null;
  private enabled = false;
  private backend: UpscaleBackend = webgpuEnhance;
  private active = false;
  private readonly pages = new Map<HTMLImageElement, PageState>();
  private readonly sentinels = new Map<Element, BandState>();
  private readonly queue = new Set<BandState>();
  private pool: PoolEntry[] = [];
  private job: Job | null = null;
  private layer: HTMLDivElement | null = null;

  private pageObserver: IntersectionObserver | null = null;
  private bandObserver: IntersectionObserver | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private layoutFrame: number | null = null;
  private pumpTimer: ReturnType<typeof setTimeout> | null = null;
  private hiddenTimer: ReturnType<typeof setTimeout> | null = null;
  private releasedWhileHidden = false;

  private renderer: TileRendererApi | null = null;
  private rendererLoad: Promise<TileRendererApi | null> | null = null;
  private offLost: (() => void) | null = null;
  private budget: SliceBudget | null = null;

  private pausedUntil = 0;
  private lastScroll: { t: number; top: number } | null = null;
  private lossTimes: number[] = [];
  private failures = 0;
  private broken = false;

  private readonly onScroll = () => this.recordScroll();
  private readonly onVisibility = () => this.visibilityChanged();

  /** Test / diagnostics view of the pool. */
  get canvasCount(): number { return this.pool.length; }
  get liveCanvasCount(): number { return this.pool.filter((e) => e.state === 'live').length; }
  get queuedCount(): number { return this.queue.size; }
  get isActive(): boolean { return this.active; }

  /** The webtoon scroller: the IntersectionObserver root and the enhance layer's parent. */
  attach(scroller: HTMLElement): void {
    this.scroller = scroller;
    if (this.enabled) this.activate();
  }

  /** The backend the strip renders with (tests, diagnostics). */
  get currentBackend(): UpscaleBackend { return this.backend; }

  /**
   * Follows the Upscaling choice: on with the backend it resolved to (Sharp or
   * Enhance, WebGPU or WebGL2), or off (Smooth / cannot run here). A backend
   * change rebuilds the layer with fresh canvases and loads that engine's renderer.
   */
  setEnabled(on: boolean, backend: UpscaleBackend = webgpuEnhance): void {
    const changed = !sameBackend(backend, this.backend);
    if (on === this.enabled && !(on && changed)) return;
    if (changed) {
      this.deactivate();
      this.offLost?.();
      this.offLost = null;
      this.renderer = null;
      this.rendererLoad = null;
      this.budget = null;
      this.backend = backend;
      // A new engine gets a fresh chance: failures and resets of the old one do not count.
      this.failures = 0;
      this.broken = false;
      this.lossTimes = [];
    }
    this.enabled = on;
    if (on) this.activate();
    else this.deactivate();
  }

  register(img: HTMLImageElement): void {
    if (this.pages.has(img)) return;
    const page: PageState = {
      img, near: false, src: '', generation: 0, gateOn: false, nativeWidth: 0, nativeHeight: 0,
      profile: null, plan: [], bands: [], container: null, geometry: null,
    };
    this.pages.set(img, page);
    this.pageObserver?.observe(img);
  }

  unregister(img: HTMLImageElement): void {
    const page = this.pages.get(img);
    if (!page) return;
    this.teardownPage(page, true);
    this.pageObserver?.unobserve(img);
    this.pages.delete(img);
  }

  /** The img fired `load`: first decode, or a new `src` (Page quality switch). */
  loaded(img: HTMLImageElement): void {
    const page = this.pages.get(img);
    if (!page || !this.active) return;
    if (!page.near) {
      // A page settling above the near ones can move them without resizing them
      // (a manifest page with no reserved aspect ratio): re-place the containers.
      this.scheduleLayout();
      return;
    }
    this.buildPage(page);
    this.schedulePump();
  }

  destroy(): void {
    this.deactivate();
    this.pages.clear();
    this.scroller = null;
  }

  // --- activation ---------------------------------------------------------------

  private activate(): void {
    if (this.active || !this.scroller || this.broken) return;
    if (typeof IntersectionObserver !== 'function' || typeof ResizeObserver !== 'function') return;
    if (this.backend.engine === 'webgpu' && !hasWebGpu()) return;
    if (this.support.webtoonPaused()) return;
    const scroller = this.scroller;
    this.active = true;
    this.zone.runOutsideAngular(() => {
      const layer = scroller.ownerDocument.createElement('div');
      layer.className = 'mp-enhance-layer';
      layer.setAttribute('aria-hidden', 'true');
      Object.assign(layer.style, {
        position: 'absolute', left: '0', top: '0', width: '0', height: '0',
        pointerEvents: 'none', zIndex: '1',
      });
      scroller.appendChild(layer);
      this.layer = layer;

      this.pageObserver = new IntersectionObserver((entries) => this.onPageEntries(entries),
        { root: scroller, rootMargin: pageRootMargin, threshold: 0 });
      this.bandObserver = new IntersectionObserver((entries) => this.onBandEntries(entries),
        { root: scroller, rootMargin: bandRootMargin, threshold: [0, 0.25, 0.5, 0.75, 1] });
      this.resizeObserver = new ResizeObserver(() => this.scheduleLayout());
      this.resizeObserver.observe(scroller);
      for (const img of this.pages.keys()) this.pageObserver.observe(img);
      scroller.addEventListener('scroll', this.onScroll, { passive: true });
      scroller.ownerDocument.addEventListener('visibilitychange', this.onVisibility);
    });
  }

  private deactivate(): void {
    if (!this.active) return;
    this.active = false;
    this.pageObserver?.disconnect();
    this.bandObserver?.disconnect();
    this.resizeObserver?.disconnect();
    this.pageObserver = this.bandObserver = this.resizeObserver = null;
    this.scroller?.removeEventListener('scroll', this.onScroll);
    this.scroller?.ownerDocument.removeEventListener('visibilitychange', this.onVisibility);
    if (this.layoutFrame !== null && typeof cancelAnimationFrame === 'function') cancelAnimationFrame(this.layoutFrame);
    this.layoutFrame = null;
    this.clearTimers();
    this.job?.ctrl.abort();
    this.job = null;
    this.queue.clear();
    for (const page of this.pages.values()) this.teardownPage(page, true);
    this.sentinels.clear();
    this.freePool();
    this.layer?.remove();
    this.layer = null;
    this.offLost?.();
    this.offLost = null;
    // Only if the chunk was ever loaded: releasing never downloads it.
    this.renderer?.releaseTiles();
    this.lastScroll = null;
  }

  private clearTimers(): void {
    if (this.pumpTimer !== null) clearTimeout(this.pumpTimer);
    if (this.hiddenTimer !== null) clearTimeout(this.hiddenTimer);
    this.pumpTimer = this.hiddenTimer = null;
  }

  // --- pages ----------------------------------------------------------------------

  private onPageEntries(entries: IntersectionObserverEntry[]): void {
    for (const entry of entries) {
      const page = this.pages.get(entry.target as HTMLImageElement);
      if (!page) continue;
      if (entry.isIntersecting) {
        if (page.near) continue;
        page.near = true;
        this.resizeObserver?.observe(page.img);
        this.buildPage(page);
      } else if (page.near) {
        this.teardownPage(page, false);
      }
    }
    this.schedulePump();
  }

  /** (Re)build a near page's container and sentinels, when it is decoded and passes the gate. */
  private buildPage(page: PageState): void {
    if (!this.active || !this.layer) return;
    const img = page.img;
    const nw = img.naturalWidth;
    const nh = img.naturalHeight;
    if (!img.complete || !(nw > 0) || !(nh > 0)) return; // `loaded()` brings us back
    const src = img.currentSrc || img.src;
    const profile = tileProfileFor(this.backend);
    if (src !== page.src || nw !== page.nativeWidth || nh !== page.nativeHeight || profile !== page.profile) {
      this.clearBands(page);
      page.src = src;
      page.generation++;
      page.nativeWidth = nw;
      page.nativeHeight = nh;
      page.profile = profile;
      page.plan = planBands(nw, nh, bandRowsFor(nw, profile, coarsePointer()));
    }
    page.geometry = readGeometry(img);
    page.gateOn = webtoonEnhanceGate(page.geometry.width, devicePixelRatioOr1(), nw);
    if (!page.gateOn) { this.clearBands(page); return; }

    if (!page.container) {
      const container = img.ownerDocument.createElement('div');
      Object.assign(container.style, { position: 'absolute', pointerEvents: 'none' });
      this.layer.appendChild(container);
      page.container = container;
    }
    applyGeometry(page.container, page.geometry);

    if (page.bands.length === 0) {
      for (const plan of page.plan) {
        const sentinel = img.ownerDocument.createElement('div');
        Object.assign(sentinel.style, {
          position: 'absolute', left: '0', width: '100%',
          top: pct(plan.bandY, nh), height: pct(plan.bandRows, nh),
        });
        page.container.appendChild(sentinel);
        const band: BandState = { page, plan, sentinel, intersecting: false };
        page.bands.push(band);
        this.sentinels.set(sentinel, band);
        this.bandObserver?.observe(sentinel);
      }
    }
    // Canvases this page still owns from a previous visit come back with no GPU work.
    for (const entry of this.pool) {
      if (entry.page === page && entry.generation === page.generation && entry.rendered) this.placeCanvas(entry, page);
    }
  }

  /** Drop a page's bands: jobs cancelled, canvases recyclable. `forget` also frees its canvases. */
  private clearBands(page: PageState, forget = false): void {
    for (const band of page.bands) {
      this.bandObserver?.unobserve(band.sentinel);
      this.sentinels.delete(band.sentinel);
      this.queue.delete(band);
      if (this.job?.band === band) this.job.ctrl.abort();
      band.sentinel.remove();
    }
    page.bands = [];
    for (const entry of this.pool) {
      if (entry.page !== page) continue;
      if (forget) this.releaseEntry(entry);
      else this.makeRecyclable(entry, true);
    }
  }

  private teardownPage(page: PageState, forget: boolean): void {
    this.clearBands(page, forget);
    page.near = false;
    this.resizeObserver?.unobserve(page.img);
    page.container?.remove();
    page.container = null;
  }

  // --- bands ----------------------------------------------------------------------

  private onBandEntries(entries: IntersectionObserverEntry[]): void {
    const now = performance.now();
    for (const e of entries) {
      const band = this.sentinels.get(e.target);
      if (!band) continue;
      band.intersecting = e.isIntersecting;
      const owned = this.ownedEntry(band);
      if (band.intersecting) {
        if (owned?.rendered) {
          owned.state = 'live';
          owned.lastVisible = now;
          this.placeCanvas(owned, band.page);
        } else {
          this.queue.add(band);
        }
      } else {
        this.queue.delete(band);
        if (this.job?.band === band) this.job.ctrl.abort();
        if (owned) this.makeRecyclable(owned, false, now);
      }
    }
    this.schedulePump();
  }

  private ownedEntry(band: BandState): PoolEntry | undefined {
    return this.pool.find((e) => e.page === band.page && e.index === band.plan.index && e.generation === band.page.generation);
  }

  // --- pool -----------------------------------------------------------------------

  /** Current pool bound: two viewports of the smallest near band, capped. */
  private poolLimit(): number {
    const vh = this.scroller?.clientHeight ?? 0;
    let smallest = Infinity;
    for (const page of this.pages.values()) {
      if (!page.near || !page.gateOn || !page.geometry || page.plan.length === 0) continue;
      const h = bandCssHeight(page.plan[0].bandRows, page.nativeWidth, page.geometry.width);
      if (h > 0) smallest = Math.min(smallest, h);
    }
    return poolSize(vh, Number.isFinite(smallest) ? smallest : 0);
  }

  /** A canvas for `band`: its own, a free one, a new one under the bound, or the LRU recyclable one. */
  private acquire(band: BandState): PoolEntry | null {
    const own = this.ownedEntry(band);
    if (own) return own;
    const limit = this.poolLimit();
    this.trimPool(limit);
    let entry = this.pool.find((e) => e.state === 'free') ?? null;
    if (!entry && this.pool.length < limit) {
      entry = { canvas: this.createCanvas(), state: 'free', page: null, index: -1, generation: -1, rendered: false, lastVisible: 0 };
      this.pool.push(entry);
    }
    if (!entry) {
      let lru: PoolEntry | null = null;
      for (const e of this.pool) {
        if (e.state === 'recyclable' && (!lru || e.lastVisible < lru.lastVisible)) lru = e;
      }
      entry = lru;
    }
    if (!entry) return null;
    entry.page = band.page;
    entry.index = band.plan.index;
    entry.generation = band.page.generation;
    entry.rendered = false;
    entry.state = 'live';
    entry.canvas.style.display = 'none';
    return entry;
  }

  /** The bound shrank (wider slider, rotation): drop free, then least recently visible, canvases above it. */
  private trimPool(limit: number): void {
    while (this.pool.length > limit) {
      const victim = this.pool.find((e) => e.state === 'free')
        ?? this.pool.filter((e) => e.state === 'recyclable').sort((a, b) => a.lastVisible - b.lastVisible)[0];
      if (!victim) return; // everything left is live
      this.releaseEntry(victim);
      this.pool = this.pool.filter((e) => e !== victim);
    }
  }

  private createCanvas(): HTMLCanvasElement {
    const canvas = (this.scroller?.ownerDocument ?? document).createElement('canvas');
    canvas.setAttribute('aria-hidden', 'true');
    Object.assign(canvas.style, {
      position: 'absolute', left: '0', width: '100%', display: 'none', pointerEvents: 'none', opacity: '0',
      transition: prefersReducedMotion() ? 'none' : `opacity ${fadeMs}ms ease-out`,
    });
    return canvas;
  }

  /** Put an owned canvas into its page's container at its band's % box and show it. */
  private placeCanvas(entry: PoolEntry, page: PageState): void {
    const plan = page.plan[entry.index];
    if (!page.container || !plan) return;
    const c = entry.canvas;
    c.style.top = pct(plan.bandY, page.nativeHeight);
    c.style.height = pct(plan.drawRows, page.nativeHeight);
    // Later bands paint over earlier ones where the seam rows overlap.
    c.style.zIndex = String(plan.index);
    if (c.parentElement !== page.container) page.container.appendChild(c);
    if (c.style.display === 'block') return;
    c.style.display = 'block';
    // Fade in from the next frame (a same-frame opacity change would not animate).
    if (c.style.transition === 'none' || typeof requestAnimationFrame !== 'function') { c.style.opacity = '1'; return; }
    c.style.opacity = '0';
    requestAnimationFrame(() => { if (c.style.display === 'block') c.style.opacity = '1'; });
  }

  private makeRecyclable(entry: PoolEntry, hide: boolean, now = performance.now()): void {
    if (entry.state === 'free') return;
    entry.state = entry.rendered ? 'recyclable' : 'free';
    entry.lastVisible = now;
    if (!entry.rendered) { entry.page = null; entry.index = -1; }
    if (hide || !entry.rendered) entry.canvas.style.display = 'none';
  }

  /** Free a canvas: unconfigure, zero-size (WebKit keeps a detached canvas's backing store), detach. */
  private releaseEntry(entry: PoolEntry): void {
    entry.state = 'free';
    entry.page = null;
    entry.index = -1;
    entry.generation = -1;
    entry.rendered = false;
    const c = entry.canvas;
    if (this.backend.engine === 'webgpu') {
      try { (c.getContext?.('webgpu') as GPUCanvasContext | null)?.unconfigure(); } catch { /* never configured */ }
    }
    c.width = 0;
    c.height = 0;
    c.style.display = 'none';
    c.style.opacity = '0';
    c.remove();
  }

  private freePool(): void {
    for (const entry of this.pool) this.releaseEntry(entry);
    this.pool = [];
  }

  // --- queue ----------------------------------------------------------------------

  private schedulePump(delay = 0): void {
    if (!this.active || this.pumpTimer !== null) return;
    this.zone.runOutsideAngular(() => {
      this.pumpTimer = setTimeout(() => { this.pumpTimer = null; this.pump(); }, delay);
    });
  }

  /** Band order: visible (most on screen, then nearest the centre), then ahead, then behind. */
  private rank(band: BandState, viewTop: number, viewBottom: number): [number, number] {
    const g = band.page.geometry;
    if (!g) return [3, 0];
    const scale = g.height / band.page.nativeHeight;
    const top = g.top + band.plan.bandY * scale;
    const bottom = top + band.plan.bandRows * scale;
    const overlap = Math.min(bottom, viewBottom) - Math.max(top, viewTop);
    if (overlap > 0) {
      const centre = Math.abs((top + bottom) / 2 - (viewTop + viewBottom) / 2);
      return [0, -overlap / (bottom - top) * 1e6 + centre];
    }
    if (top >= viewBottom) return [1, top - viewBottom];
    return [2, viewTop - bottom];
  }

  private pump(): void {
    if (!this.active || this.job || this.queue.size === 0 || this.broken) return;
    const now = performance.now();
    if (now < this.pausedUntil) { this.schedulePump(this.pausedUntil - now + 1); return; }
    const viewTop = this.scroller?.scrollTop ?? 0;
    const viewBottom = viewTop + (this.scroller?.clientHeight ?? 0);
    const ranked = [...this.queue].map((band) => ({ band, r: this.rank(band, viewTop, viewBottom) }))
      .sort((a, b) => a.r[0] - b.r[0] || a.r[1] - b.r[1]);
    for (const { band } of ranked) {
      const entry = this.acquire(band);
      if (!entry) continue; // every canvas is live: this band keeps its plain <img>
      this.queue.delete(band);
      this.start(band, entry);
      return;
    }
  }

  private start(band: BandState, entry: PoolEntry): void {
    const ctrl = new AbortController();
    const job: Job = { band, entry, ctrl };
    this.job = job;
    const page = band.page;
    const generation = page.generation;
    void (async () => {
      let result: BandRenderResult = { status: 'failed', gpuMs: 0, slices: 0 };
      try {
        const renderer = await this.ensureRenderer();
        if (renderer && !ctrl.signal.aborted && this.active) {
          this.budget ??= renderer.createSliceBudget();
          result = await renderer.renderBand({
            source: page.img, canvas: entry.canvas, band: band.plan, chain: webtoonChain, mode: this.backend.mode,
            signal: ctrl.signal, budget: this.budget,
          });
        } else if (ctrl.signal.aborted) {
          result = { status: 'aborted', gpuMs: 0, slices: 0 };
        }
      } catch {
        result = { status: 'failed', gpuMs: 0, slices: 0 };
      }
      if (this.job === job) this.job = null;
      this.finish(job, generation, result);
      this.schedulePump();
    })();
  }

  private finish(job: Job, generation: number, result: BandRenderResult): void {
    const { band, entry } = job;
    const stillOwned = entry.page === band.page && entry.index === band.plan.index && entry.generation === generation
      && band.page.generation === generation;
    if (!this.active || !stillOwned) return;
    if (result.status === 'ok') {
      this.failures = 0;
      entry.rendered = true;
      this.support.recordTiming('band', result.gpuMs);
      if (band.intersecting && band.page.bands.includes(band)) {
        entry.state = 'live';
        entry.lastVisible = performance.now();
        this.placeCanvas(entry, band.page);
      } else {
        this.makeRecyclable(entry, true);
      }
      return;
    }
    // Aborted or failed: this canvas holds nothing usable for the band.
    entry.rendered = false;
    this.makeRecyclable(entry, true);
    if (result.status === 'failed' && ++this.failures >= maxConsecutiveFailures) {
      // A GPU that keeps failing (out of memory, driver trouble): stop for this strip.
      this.broken = true;
      this.deactivate();
    } else if (result.status === 'aborted' && band.intersecting && band.page.bands.includes(band)) {
      this.queue.add(band);
    }
  }

  private ensureRenderer(): Promise<TileRendererApi | null> {
    if (this.rendererLoad) return this.rendererLoad;
    const backend = this.backend;
    const load: Promise<TileRendererApi | null> = this.loadRenderer(backend.engine).then((r) => {
      if (this.backend !== backend) return null; // the engine changed while the chunk loaded
      this.renderer = r;
      this.offLost = r.onTilesLost(() => this.zone.runOutsideAngular(() => this.deviceLost()));
      return r;
    }).catch(() => {
      if (this.rendererLoad === load) this.rendererLoad = null; // chunk failed to load: plain <img>, retry on a later band
      return null;
    });
    this.rendererLoad = load;
    return load;
  }

  // --- gating ---------------------------------------------------------------------

  private recordScroll(): void {
    const scroller = this.scroller;
    if (!scroller) return;
    const t = performance.now();
    const top = scroller.scrollTop;
    const last = this.lastScroll;
    this.lastScroll = { t, top };
    if (!last || t <= last.t) return;
    const velocity = Math.abs(top - last.top) / ((t - last.t) / 1000);
    if (velocity > flingViewportsPerSecond * scroller.clientHeight) {
      this.pausedUntil = Math.max(this.pausedUntil, t + flingSettleMs);
    }
  }

  private scheduleLayout(): void {
    if (!this.active || this.layoutFrame !== null) return;
    const run = () => { this.layoutFrame = null; this.layout(); };
    if (typeof requestAnimationFrame === 'function') this.layoutFrame = requestAnimationFrame(run);
    else run();
  }

  /**
   * Slider move, resize, rotation, zoom: move the near pages' containers (all
   * reads first, then all writes) and re-evaluate each page's gate. No band is
   * re-rendered; new starts wait `resizeDebounceMs`.
   */
  private layout(): void {
    if (!this.active) return;
    const near = [...this.pages.values()].filter((p) => p.near);
    const boxes = near.map((p) => readGeometry(p.img));
    if (near.some((p, i) => !sameGeometry(p.geometry, boxes[i]))) {
      this.pausedUntil = Math.max(this.pausedUntil, performance.now() + resizeDebounceMs);
    }
    near.forEach((page, i) => {
      page.geometry = boxes[i];
      const gate = page.nativeWidth > 0 && webtoonEnhanceGate(boxes[i].width, devicePixelRatioOr1(), page.nativeWidth);
      if (gate !== page.gateOn || (gate && !page.container)) this.buildPage(page);
      else if (page.container) applyGeometry(page.container, boxes[i]);
    });
    this.schedulePump();
  }

  // --- device loss and backgrounding ----------------------------------------------

  private deviceLost(): void {
    if (!this.active) return;
    const now = performance.now();
    this.lossTimes = [...this.lossTimes.filter((t) => now - t < deviceLossWindowMs), now];
    this.job?.ctrl.abort();
    if (this.lossTimes.length >= 2) {
      this.zone.run(() => this.support.webtoonPaused.set(true));
      this.deactivate();
      return;
    }
    // Old pixels may be blank on the new device: hide every canvas, re-queue the visible bands once.
    for (const entry of this.pool) {
      entry.rendered = false;
      this.makeRecyclable(entry, true, now); // not rendered, so this frees it for reuse
    }
    for (const band of this.sentinels.values()) if (band.intersecting) this.queue.add(band);
    this.schedulePump();
  }

  private visibilityChanged(): void {
    const doc = this.scroller?.ownerDocument;
    if (!doc || !this.active) return;
    if (doc.visibilityState === 'hidden') {
      if (this.hiddenTimer !== null) return;
      this.hiddenTimer = setTimeout(() => {
        this.hiddenTimer = null;
        this.job?.ctrl.abort();
        this.queue.clear();
        this.freePool();
        this.renderer?.releaseTiles();
        this.releasedWhileHidden = true;
      }, hiddenReleaseMs);
      return;
    }
    if (this.hiddenTimer !== null) { clearTimeout(this.hiddenTimer); this.hiddenTimer = null; }
    if (this.releasedWhileHidden) {
      this.releasedWhileHidden = false;
      for (const band of this.sentinels.values()) if (band.intersecting) this.queue.add(band);
      this.schedulePump();
    }
  }
}

function pct(rows: number, total: number): string {
  return `${(rows / total) * 100}%`;
}

function readGeometry(img: HTMLImageElement): Geometry {
  return { left: img.offsetLeft, top: img.offsetTop, width: img.offsetWidth, height: img.offsetHeight };
}

function sameGeometry(a: Geometry | null, b: Geometry): boolean {
  return !!a && a.left === b.left && a.top === b.top && a.width === b.width && a.height === b.height;
}

function applyGeometry(el: HTMLElement, g: Geometry): void {
  el.style.left = `${g.left}px`;
  el.style.top = `${g.top}px`;
  el.style.width = `${g.width}px`;
  el.style.height = `${g.height}px`;
}

function devicePixelRatioOr1(): number {
  return typeof devicePixelRatio === 'number' && devicePixelRatio > 0 ? devicePixelRatio : 1;
}

function coarsePointer(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;
}
