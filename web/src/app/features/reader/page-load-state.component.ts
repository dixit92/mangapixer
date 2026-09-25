import {
  AfterContentInit, ChangeDetectionStrategy, Component, ElementRef, OnDestroy, effect, inject, input, signal,
} from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Reader page loading feedback (1.24.0 follow-up). When the library drive is
 * spinning up, a page can take seconds to arrive; the reader used to be plain
 * black (vertical) or show a small purple spinner (paged). Two pieces:
 *
 *  - `app-page-load-indicator`: a light ring on a dark pill (readable on the
 *    black reader) that adds the text "Loading…" once the wait passes
 *    `slowLoadDelayMs`. It is a polite live region, so only the text is announced,
 *    and the ring stops spinning under `prefers-reduced-motion`. The paged view
 *    shows it while `pageLoading()` is true.
 *  - `app-webtoon-page`: wraps one vertical-strip `<img>` (content projection) and
 *    lays a veil over its already sized box until the img fires `load`: the
 *    indicator while loading, a "tap to retry" button on `error`.
 *
 * Why a wrapper and not a positioned overlay: the Enhance coordinator and the
 * reader's scroll tracking read `img.offsetTop` / `offsetLeft`, which are relative
 * to the scroller only while no ancestor between them is positioned. The host is
 * therefore a STATIC one-cell grid (the img and the veil share the cell), and
 * only the veil's content is `position: sticky` (which positions nothing but its
 * own children). The veil has `contain: size`, so it never adds to the box's
 * height (no layout shift), and it is gone once the page has loaded - it never
 * overlaps an enhanced page, whose band canvases sit in the scroller's own layer
 * above everything else (`webtoon-enhance-coordinator.ts`). No per-frame work:
 * one shared IntersectionObserver arms the "Loading…" timer only for pages that
 * are actually on screen (a lazy page far below is not being waited on yet).
 */

/** After this long without the page, the indicator adds "Loading…" (owner: ~3 s). */
export const slowLoadDelayMs = 3000;

@Component({
  selector: 'app-page-load-indicator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'status', 'aria-live': 'polite' },
  template: `
    <span class="ring" aria-hidden="true"></span>
    @if (slow()) {
      <span class="text">Loading…</span>
    }
  `,
  styles: `
    :host {
      display: inline-flex; align-items: center; gap: 10px;
      padding: 8px; border-radius: 999px;
      background: rgba(0, 0, 0, 0.6); color: rgba(255, 255, 255, 0.92);
      font-size: 14px; line-height: 20px; pointer-events: none;
    }
    .ring {
      width: 20px; height: 20px; box-sizing: border-box; flex: none; border-radius: 50%;
      border: 3px solid rgba(255, 255, 255, 0.28); border-top-color: rgba(255, 255, 255, 0.92);
      animation: mp-page-load-spin 0.9s linear infinite;
    }
    .text { padding-right: 6px; }
    @keyframes mp-page-load-spin { to { transform: rotate(360deg); } }
    @media (prefers-reduced-motion: reduce) {
      .ring { animation: none; }
    }
  `,
})
export class PageLoadIndicatorComponent {
  /** The "Loading…" timer runs only while armed (the vertical strip: while on screen). */
  readonly armed = input(true);
  readonly delayMs = input(slowLoadDelayMs);
  /** True once the wait has passed `delayMs`; stays true until the indicator goes away. */
  readonly slow = signal(false);

  constructor() {
    effect((onCleanup) => {
      if (!this.armed() || this.slow()) return;
      const timer = setTimeout(() => this.slow.set(true), this.delayMs());
      onCleanup(() => clearTimeout(timer));
    });
  }
}

export type PageLoadState = 'loading' | 'loaded' | 'error';

/**
 * One IntersectionObserver (viewport root; the scroller's clipping is taken into
 * account) for every strip page that is still loading.
 */
const visibilityListeners = new Map<Element, (visible: boolean) => void>();
let visibilityObserver: IntersectionObserver | null = null;

function observeVisibility(el: Element, listener: (visible: boolean) => void): () => void {
  if (typeof IntersectionObserver !== 'function') {
    listener(true); // no observer (jsdom, very old browsers): treat as on screen
    return () => undefined;
  }
  visibilityObserver ??= new IntersectionObserver((entries) => {
    for (const e of entries) visibilityListeners.get(e.target)?.(e.isIntersecting);
  });
  visibilityListeners.set(el, listener);
  visibilityObserver.observe(el);
  return () => {
    if (!visibilityListeners.delete(el)) return;
    visibilityObserver?.unobserve(el);
    if (visibilityListeners.size === 0) {
      visibilityObserver?.disconnect();
      visibilityObserver = null;
    }
  };
}

@Component({
  selector: 'app-webtoon-page',
  imports: [MatIconModule, PageLoadIndicatorComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ng-content />
    @if (state() !== 'loaded') {
      <div class="veil">
        @if (state() === 'loading') {
          <app-page-load-indicator class="pin" [armed]="visible()" />
        } @else {
          <div class="pin" role="status" aria-live="polite">
            <button type="button" class="retry" (click)="retry()" [attr.aria-label]="'Page ' + pageNumber() + ' did not load. Retry'">
              <mat-icon aria-hidden="true">refresh</mat-icon>
              <span>Page {{ pageNumber() }} did not load<br /><span class="hint">Tap to retry</span></span>
            </button>
          </div>
        }
      </div>
    }
  `,
  styles: `
    /* STATIC one-cell grid: see the file comment (offsetTop must stay scroller-relative). */
    :host { display: grid; grid-template-columns: minmax(0, 1fr); justify-items: center; width: 100%; }
    :host > ::ng-deep img { grid-area: 1 / 1; }
    .veil {
      grid-area: 1 / 1; justify-self: stretch; align-self: stretch;
      contain: size; overflow: clip; pointer-events: none;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
    }
    /* Sticky keeps the indicator inside the visible part of a tall page box. */
    .pin { position: sticky; top: 40%; bottom: 40%; margin: 12px 0; }
    .retry {
      pointer-events: auto; cursor: pointer; font: inherit; font-size: 14px; line-height: 20px; text-align: left;
      display: inline-flex; align-items: center; gap: 10px; padding: 10px 16px;
      border-radius: 12px; border: 1px solid rgba(255, 255, 255, 0.35);
      background: rgba(0, 0, 0, 0.7); color: rgba(255, 255, 255, 0.92);
    }
    .retry:focus-visible { outline: 2px solid #b39dff; outline-offset: 2px; }
    .retry mat-icon { flex: none; }
    .hint { opacity: 0.75; }
  `,
})
export class WebtoonPageComponent implements AfterContentInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** One-based, for the failed-state text. */
  readonly pageNumber = input.required<number>();
  readonly state = signal<PageLoadState>('loading');
  /** On screen (arms the "Loading…" timer). */
  readonly visible = signal(false);

  private img: HTMLImageElement | null = null;
  private stopObserving: (() => void) | null = null;
  private readonly onLoad = () => this.setState('loaded');
  private readonly onError = () => this.setState('error');

  ngAfterContentInit(): void {
    const img = this.host.nativeElement.querySelector<HTMLImageElement>(':scope > img');
    if (!img) { this.state.set('loaded'); return; }
    this.img = img;
    img.addEventListener('load', this.onLoad);
    img.addEventListener('error', this.onError);
    // A cached page may already be decoded before the listeners exist.
    this.setState(img.complete && img.naturalWidth > 0 ? 'loaded' : 'loading');
  }

  /** Request the page again (the button in the failed state). */
  retry(): void {
    const img = this.img;
    const src = img?.getAttribute('src');
    if (!img || !src) return;
    this.setState('loading');
    // Remove + set forces a fresh request even for the same URL (a failed
    // response is not kept in the image cache).
    img.removeAttribute('src');
    img.setAttribute('src', src);
  }

  ngOnDestroy(): void {
    this.img?.removeEventListener('load', this.onLoad);
    this.img?.removeEventListener('error', this.onError);
    this.unobserve();
  }

  private setState(state: PageLoadState): void {
    this.state.set(state);
    if (state === 'loading') {
      this.stopObserving ??= observeVisibility(this.host.nativeElement, (v) => this.visible.set(v));
    } else {
      this.unobserve();
    }
  }

  private unobserve(): void {
    this.stopObserving?.();
    this.stopObserving = null;
    this.visible.set(false);
  }
}
