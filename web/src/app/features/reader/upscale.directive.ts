import {
  Directive, ElementRef, Injectable, NgZone, OnDestroy, computed, effect, inject, input, signal,
} from '@angular/core';

import { EnhanceQuality, ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import type { EnhanceChain } from './webtoon-band-plan';

/**
 * Display upscaling ("Rendering: Enhance", 1.19.0).
 *
 * Small manga scans blown up to a modern display are the one case the server
 * cannot fix: `?maxDim=` (see `page-variant.ts`) makes a DOWNSCALE sharp, but an
 * UPSCALE is invented detail, and the browser's bilinear smoothing turns line art
 * into mush. This directive offers the alternative: when the reader has opted in
 * AND the page is genuinely being painted larger than its natural size, it lays a
 * `<canvas>` over the `<img>`'s painted rect and renders the page through
 * Anime4K on the GPU instead.
 *
 * Architecture — why an overlay rather than replacing the `<img>`:
 *  - the `<img>` remains the LAYOUT element. Every fit-mode rule, the paired
 *    double-spread sizing, the swipe transform, the page-turn animation, the
 *    overflow measurement and the reader's existing test suite all keep working
 *    untouched, because nothing about the element changed.
 *  - the canvas is inert (`pointer-events: none`), absolutely positioned inside
 *    the already-`position: relative` `.spread-row`, and sized to the image's
 *    PAINTED rect (the `object-fit: contain` box, not the element box — paged
 *    fit-screen gives the `<img>` the whole viewport and letterboxes the page
 *    inside it, so using the element box would stretch the upscale).
 *  - if anything at all goes wrong the canvas is simply hidden and the `<img>`
 *    underneath is what the reader sees — i.e. exactly today's behaviour.
 *
 * Scope: paged and double-spread pages. The webtoon (vertical scroll) view is
 * covered by `webtoon-upscale.directive.ts` instead (1.24.0): it keeps dozens of
 * images live at once and every strip page has its own size, so it renders fixed
 * bands through its own tile renderer rather than one pipeline per page.
 *
 * Chain: the paged views honour the Enhance quality preference - the light M
 * chain ("Balanced", the default) or the heavy VL chain ("Max quality").
 *
 * GPU memory: the renderer keeps one Anime4K pipeline (hundreds of MB for a
 * large page) alive between pages. This directive is the only thing that knows
 * when no Enhance overlay is live any more (reader closed, view switched to
 * webtoon, preference turned off), so it owns the `releaseUpscaler()` call: once
 * the last live overlay has been gone for `upscaleReleaseDelayMs`, every GPU
 * texture and buffer is destroyed. The delay absorbs page turns, which re-create
 * the `<img>` (and so this directive) on every turn.
 *
 * Feature detection: with no `navigator.gpu`, or when adapter/device acquisition
 * fails, the directive is a silent no-op and `UpscaleSupportService` reports the
 * option as unavailable so the settings UI can disable it with a reason. Under
 * jsdom (`navigator.gpu` absent, images never decode) it does nothing and never
 * throws, so no test needs to know it exists.
 *
 * The heavy Anime4K code is reached through a dynamic `import()` of
 * `anime4k-renderer.ts`, keeping it out of the initial bundle entirely.
 */

/** Below this display/native scale the page is not being upscaled — do nothing. */
const upscaleThreshold = 1.02;

/** How long no Enhance overlay may be live before the renderer's GPU memory is released. */
export const upscaleReleaseDelayMs = 1500;

/** The lazy renderer module, once some overlay has loaded it (never loaded here just to release). */
let renderer: typeof import('./anime4k-renderer') | null = null;
/** Directives whose preference is on: each may be showing (or about to show) GPU output. */
const liveOverlays = new Set<object>();
let releaseTimer: ReturnType<typeof setTimeout> | null = null;

function retainUpscaler(owner: object): void {
  liveOverlays.add(owner);
  if (releaseTimer !== null) { clearTimeout(releaseTimer); releaseTimer = null; }
}

function relinquishUpscaler(owner: object): void {
  if (!liveOverlays.delete(owner) || liveOverlays.size > 0 || !renderer) return;
  if (releaseTimer !== null) clearTimeout(releaseTimer);
  releaseTimer = setTimeout(() => {
    releaseTimer = null;
    if (liveOverlays.size === 0) renderer?.releaseUpscaler();
  }, upscaleReleaseDelayMs);
}

/** Is a WebGPU entry point even present on this platform? Cheap + synchronous. */
export function hasWebGpu(): boolean {
  try {
    return typeof navigator !== 'undefined' && !!(navigator as Navigator & { gpu?: unknown }).gpu;
  } catch {
    return false;
  }
}

/** The Anime4K chain the PAGED views run for an Enhance quality choice. */
export function pagedChainFor(quality: EnhanceQuality): EnhanceChain {
  return quality === 'max' ? 'vl' : 'm';
}

/** Rolling window for the median render-time readout. */
const timingWindow = 15;
/** Taps on the Rendering status line, within `statsTapWindowMs`, that toggle the timing readout. */
export const statsTapCount = 5;
const statsTapWindowMs = 3000;

function median(values: readonly number[]): number | null {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = sorted.length >> 1;
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

/** What the settings UI shows about GPU upscaling availability. */
export type UpscaleSupport = 'checking' | 'ready' | 'unavailable';

/**
 * Probes once per app session whether a WebGPU device can actually be acquired
 * (a `navigator.gpu` that then fails to hand out an adapter is common on older
 * Linux/Android drivers), so the settings surface can say "needs WebGPU" instead
 * of offering an option that silently does nothing. The probe touches no Anime4K
 * code, so asking the question never downloads the lazy chunk.
 */
@Injectable({ providedIn: 'root' })
export class UpscaleSupportService {
  static readonly StatsKey = 'mangapixer-reader-gpu-stats';

  readonly support = signal<UpscaleSupport>('checking');
  private probed = false;

  /**
   * Webtoon Enhance paused for this app session after a second GPU reset within
   * a minute (1.24.0); the plain `<img>`s show and the status line says so.
   */
  readonly webtoonPaused = signal(false);

  /**
   * Device-verification readout (1.24.0), OFF by default: the median ms per paged
   * page / webtoon band, appended to the status line. Toggled per device by
   * tapping the Rendering status line `statsTapCount` times; nothing leaves the
   * device.
   */
  readonly statsVisible = signal(this.loadStats());
  private readonly pageTimes = signal<readonly number[]>([]);
  private readonly bandTimes = signal<readonly number[]>([]);
  readonly pageMs = computed(() => median(this.pageTimes()));
  readonly bandMs = computed(() => median(this.bandTimes()));
  private taps: number[] = [];

  constructor() {
    this.probe();
  }

  /** A short human-readable status for a tooltip/hint. */
  statusText(): string {
    let text: string;
    switch (this.support()) {
      case 'ready': text = 'GPU: WebGPU ready'; break;
      case 'unavailable': return 'GPU: WebGPU unavailable';
      default: return 'GPU: checking WebGPU…';
    }
    if (this.webtoonPaused()) text += ' - Enhance paused (GPU reset)';
    if (this.statsVisible()) {
      const page = this.pageMs();
      const band = this.bandMs();
      if (page !== null) text += ` - ${Math.round(page)} ms/page`;
      if (band !== null) text += ` - ${Math.round(band)} ms/band`;
      if (page === null && band === null) text += ' - no renders yet';
    }
    return text;
  }

  /** Record one finished render (paged page or webtoon band) for the median readout. */
  recordTiming(kind: 'page' | 'band', ms: number): void {
    if (!Number.isFinite(ms) || ms < 0) return;
    const target = kind === 'page' ? this.pageTimes : this.bandTimes;
    target.update((values) => [...values, ms].slice(-timingWindow));
  }

  /** One tap on the status line; the `statsTapCount`-th tap within 3 s toggles the readout. */
  tapStats(now = Date.now()): void {
    this.taps = [...this.taps.filter((t) => now - t < statsTapWindowMs), now];
    if (this.taps.length < statsTapCount) return;
    this.taps = [];
    const on = !this.statsVisible();
    this.statsVisible.set(on);
    try {
      if (on) localStorage.setItem(UpscaleSupportService.StatsKey, '1');
      else localStorage.removeItem(UpscaleSupportService.StatsKey);
    } catch {
      /* storage unavailable - session-only */
    }
  }

  private loadStats(): boolean {
    try {
      return localStorage.getItem(UpscaleSupportService.StatsKey) === '1';
    } catch {
      return false;
    }
  }

  probe(): void {
    if (this.probed) return;
    this.probed = true;
    if (!hasWebGpu()) { this.support.set('unavailable'); return; }
    const gpu = (navigator as Navigator & { gpu?: GPU }).gpu;
    try {
      // requestAdapter() may resolve to null (no compatible adapter) or reject.
      Promise.resolve(gpu!.requestAdapter())
        .then((adapter) => this.support.set(adapter ? 'ready' : 'unavailable'))
        .catch(() => this.support.set('unavailable'));
    } catch {
      this.support.set('unavailable');
    }
  }
}

@Directive({
  selector: 'img[appUpscale]',
  standalone: true,
  host: {
    '(load)': 'schedule()',
    '(error)': 'hide()',
  },
})
export class UpscaleDirective implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLImageElement>>(ElementRef);
  private readonly zone = inject(NgZone);
  private readonly prefs = inject(ReaderPreferencesService);
  private readonly support = inject(UpscaleSupportService);

  /** True when the reader's "Rendering" preference is `enhance`. */
  readonly appUpscale = input(false);

  private canvas: HTMLCanvasElement | null = null;
  private observer: ResizeObserver | null = null;
  private frame: number | null = null;
  /** Bumped on every (re)schedule so a slow async render can detect it is stale. */
  private token = 0;
  private destroyed = false;

  constructor() {
    // React to the preference flipping while a page is on screen.
    effect(() => {
      const on = this.appUpscale();
      this.prefs.enhanceQuality(); // a Balanced <-> Max quality switch re-renders too
      if (!on) {
        this.hide();
        this.zone.runOutsideAngular(() => relinquishUpscaler(this));
        return;
      }
      retainUpscaler(this);
      this.schedule();
    });
    // The painted rect changes with fit mode, rotation, window resize and the
    // paired/single switch — all of which show up as a size change on the <img>.
    if (typeof ResizeObserver === 'function') {
      this.observer = new ResizeObserver(() => this.schedule());
      // Outside Angular: a resize must not spin change detection.
      this.zone.runOutsideAngular(() => this.observer?.observe(this.host.nativeElement));
    }
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.token++;
    this.observer?.disconnect();
    this.observer = null;
    if (this.frame !== null && typeof cancelAnimationFrame === 'function') cancelAnimationFrame(this.frame);
    this.frame = null;
    if (this.canvas) {
      // WebKit keeps a detached canvas's backing store until it is resized.
      this.canvas.width = 0;
      this.canvas.height = 0;
      this.canvas.remove();
    }
    this.canvas = null;
    this.zone.runOutsideAngular(() => relinquishUpscaler(this));
  }

  /** Coalesce the (load / resize / preference) triggers into one render a frame. */
  schedule(): void {
    if (this.destroyed) return;
    const run = () => { this.frame = null; void this.apply(); };
    if (typeof requestAnimationFrame !== 'function') { run(); return; }
    if (this.frame !== null) cancelAnimationFrame(this.frame);
    this.zone.runOutsideAngular(() => { this.frame = requestAnimationFrame(run); });
  }

  /** Hide (but keep) the overlay so the plain `<img>` is what shows. */
  hide(): void {
    this.token++;
    if (this.canvas) this.canvas.style.display = 'none';
  }

  /**
   * The rect the `<img>` actually PAINTS its pixels into, in CSS px relative to
   * the offset parent (`.spread-row`). Every reader fit rule either preserves the
   * page aspect in the element box or applies `object-fit: contain`, so the
   * contain box is correct in all of them: scale by the smaller axis and centre.
   * Returns null when there is nothing (yet) to paint.
   */
  private paintedRect(): { left: number; top: number; width: number; height: number } | null {
    const img = this.host.nativeElement;
    const nw = img.naturalWidth;
    const nh = img.naturalHeight;
    const boxW = img.offsetWidth;
    const boxH = img.offsetHeight;
    if (nw <= 0 || nh <= 0 || boxW <= 0 || boxH <= 0) return null;
    const scale = Math.min(boxW / nw, boxH / nh);
    const width = nw * scale;
    const height = nh * scale;
    return {
      left: img.offsetLeft + (boxW - width) / 2,
      top: img.offsetTop + (boxH - height) / 2,
      width,
      height,
    };
  }

  private ensureCanvas(): HTMLCanvasElement | null {
    if (this.canvas) return this.canvas;
    const img = this.host.nativeElement;
    const parent = img.parentElement;
    if (!parent) return null;
    const canvas = img.ownerDocument.createElement('canvas');
    canvas.setAttribute('aria-hidden', 'true');
    canvas.style.position = 'absolute';
    canvas.style.pointerEvents = 'none';
    canvas.style.zIndex = '1';
    canvas.style.display = 'none';
    parent.insertBefore(canvas, img.nextSibling);
    this.canvas = canvas;
    return canvas;
  }

  /** The whole decision, in one place; never throws. */
  private async apply(): Promise<void> {
    if (this.destroyed || !this.appUpscale()) { this.hide(); return; }
    const img = this.host.nativeElement;
    if (!img.complete) return; // a (load) event will bring us back
    const rect = this.paintedRect();
    if (!rect) { this.hide(); return; }

    // Not an upscale: compare in DEVICE pixels, not CSS pixels. The canvas below
    // is rasterized at `rect.width * dpr`, and that is what actually lands on the
    // screen — on a high-DPR phone/tablet a CSS-px comparison alone reads a page
    // painted well past its native resolution as a "downscale" (DPR 3 divides the
    // painted width down before it ever reaches naturalWidth), so the overlay
    // would stay hidden exactly where it is needed most.
    const dpr = typeof devicePixelRatio === 'number' && devicePixelRatio > 0 ? devicePixelRatio : 1;
    const scale = (rect.width * dpr) / img.naturalWidth;
    if (!(scale > upscaleThreshold)) { this.hide(); return; }
    if (!hasWebGpu()) { this.hide(); return; }

    const canvas = this.ensureCanvas();
    if (!canvas) { this.hide(); return; }

    const targetWidth = Math.round(rect.width * dpr);
    const targetHeight = Math.round(rect.height * dpr);
    // Never ask the GPU for more than the page could ever fill at a sane cost.
    if (targetWidth <= 0 || targetHeight <= 0) { this.hide(); return; }

    const token = ++this.token;
    try {
      renderer = await import('./anime4k-renderer');
      if (this.destroyed || token !== this.token) return;
      const chain = pagedChainFor(this.prefs.enhanceQuality());
      const started = performance.now();
      const ok = await renderer.renderUpscaled({ source: img, canvas, targetWidth, targetHeight, chain });
      if (this.destroyed || token !== this.token) return;
      if (!ok) { canvas.style.display = 'none'; return; }
      this.support.recordTiming('page', performance.now() - started);
      canvas.style.left = `${rect.left}px`;
      canvas.style.top = `${rect.top}px`;
      canvas.style.width = `${rect.width}px`;
      canvas.style.height = `${rect.height}px`;
      canvas.style.display = 'block';
    } catch {
      // Chunk failed to load, WebGPU threw, anything: stay on the <img>.
      if (this.canvas) this.canvas.style.display = 'none';
    }
  }
}
