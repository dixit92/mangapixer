import {
  Directive, ElementRef, Injectable, NgZone, OnDestroy, effect, inject, input, signal,
} from '@angular/core';

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
 * deliberately NOT covered this cycle: it keeps dozens of images live at once, so
 * one GPU pipeline per visible strip page is a different resource problem that
 * deserves its own design.
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

/** Is a WebGPU entry point even present on this platform? Cheap + synchronous. */
export function hasWebGpu(): boolean {
  try {
    return typeof navigator !== 'undefined' && !!(navigator as Navigator & { gpu?: unknown }).gpu;
  } catch {
    return false;
  }
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
  readonly support = signal<UpscaleSupport>('checking');
  private probed = false;

  constructor() {
    this.probe();
  }

  /** A short human-readable status for a tooltip/hint. */
  statusText(): string {
    switch (this.support()) {
      case 'ready': return 'GPU: WebGPU ready';
      case 'unavailable': return 'GPU: WebGPU unavailable';
      default: return 'GPU: checking WebGPU…';
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
      if (!on) { this.hide(); return; }
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
    this.canvas?.remove();
    this.canvas = null;
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
      const { renderUpscaled } = await import('./anime4k-renderer');
      if (this.destroyed || token !== this.token) return;
      const ok = await renderUpscaled({ source: img, canvas, targetWidth, targetHeight });
      if (this.destroyed || token !== this.token) return;
      if (!ok) { canvas.style.display = 'none'; return; }
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
