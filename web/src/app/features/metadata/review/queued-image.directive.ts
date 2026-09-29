import { Directive, ElementRef, HostListener, inject, Injectable, input, OnChanges, OnDestroy, output } from '@angular/core';

/**
 * A small first-come slot queue for provider images (1.29.0). Every candidate cover is one MangaUpdates
 * request through the server's image limiter, which refuses a burst beyond its short queue with
 * `provider_busy`; the review list showed a broken image for most rows when all of them asked at once
 * (1.28.0 regression). Holding the browser to a few images in flight keeps the server's queue from
 * overflowing.
 */
@Injectable({ providedIn: 'root' })
export class ProviderImageQueue {
  /** Images in flight at once (the server's image limiter allows 5/s with a queue of 10). */
  static readonly MaxInFlight = 3;

  private inFlight = 0;
  private readonly waiting: (() => void)[] = [];

  /**
   * Runs `start` now when a slot is free, otherwise when one frees up. Returns a cancel function for a
   * request still waiting; the caller must call `release()` once its image has loaded or failed.
   */
  request(start: () => void): () => void {
    if (this.inFlight < ProviderImageQueue.MaxInFlight) {
      this.inFlight += 1;
      start();
      return () => undefined;
    }
    const entry = (): void => {
      this.inFlight += 1;
      start();
    };
    this.waiting.push(entry);
    return () => {
      const i = this.waiting.indexOf(entry);
      if (i >= 0) this.waiting.splice(i, 1);
    };
  }

  release(): void {
    this.inFlight = Math.max(0, this.inFlight - 1);
    this.waiting.shift()?.();
  }
}

/** Where a queued image is: waiting / loading (a neutral frame shows), loaded, or given up. */
export type QueuedImageState = 'loading' | 'loaded' | 'failed';

/**
 * `<img [appQueuedImage]="url">` loads a provider image through {@link ProviderImageQueue}: the image stays
 * hidden (the frame behind it shows) until it has decoded, a refused or failed load is retried with backoff,
 * and after the last attempt the host is told (`queuedImageState` = failed) so it can show "No cover" with a
 * retry instead of a broken-image icon. {@link retry} starts over.
 */
@Directive({
  selector: 'img[appQueuedImage]',
  standalone: true,
  exportAs: 'queuedImage',
})
export class QueuedImageDirective implements OnChanges, OnDestroy {
  /** Backoff (ms) before each retry of a refused / failed load. */
  static readonly Backoff = [1500, 4000, 10000];

  readonly appQueuedImage = input<string | null>(null);
  readonly queuedImageState = output<QueuedImageState>();

  private readonly el = inject<ElementRef<HTMLImageElement>>(ElementRef);
  private readonly queue = inject(ProviderImageQueue);
  private attempts = 0;
  private holdsSlot = false;
  private cancelWait: (() => void) | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private observer: IntersectionObserver | null = null;

  ngOnChanges(): void {
    this.start();
  }

  /** Starts over (the "tap to retry" of a cover that gave up). */
  retry(): void {
    this.start();
  }

  @HostListener('load')
  onLoad(): void {
    if (!this.holdsSlot) return;
    this.releaseSlot();
    const img = this.el.nativeElement;
    if (img.naturalWidth > 0) {
      img.style.visibility = 'visible';
      this.queuedImageState.emit('loaded');
    } else {
      this.failedAttempt();
    }
  }

  @HostListener('error')
  onError(): void {
    if (!this.holdsSlot) return;
    this.releaseSlot();
    this.failedAttempt();
  }

  ngOnDestroy(): void {
    this.reset();
  }

  private start(): void {
    this.reset();
    this.attempts = 0;
    const img = this.el.nativeElement;
    img.style.visibility = 'hidden';
    if (!this.appQueuedImage()) {
      img.removeAttribute('src');
      return;
    }
    this.queuedImageState.emit('loading');
    this.whenNearViewport(() => this.enqueue());
  }

  /**
   * Like `loading="lazy"`: a row far below the fold asks for nothing (each image is a provider request) and
   * never holds a queue slot it cannot use. Without IntersectionObserver (tests, very old browsers) at once.
   */
  private whenNearViewport(go: () => void): void {
    if (typeof IntersectionObserver === 'undefined') {
      go();
      return;
    }
    this.observer = new IntersectionObserver((entries) => {
      if (!entries.some((e) => e.isIntersecting)) return;
      this.observer?.disconnect();
      this.observer = null;
      go();
    }, { rootMargin: '300px' });
    this.observer.observe(this.el.nativeElement);
  }

  private enqueue(): void {
    const url = this.appQueuedImage();
    if (!url) return;
    this.cancelWait = this.queue.request(() => {
      this.cancelWait = null;
      this.holdsSlot = true;
      // A retry asks again past any cached refusal.
      this.el.nativeElement.src = this.attempts === 0 ? url : `${url}${url.includes('?') ? '&' : '?'}r=${this.attempts}`;
    });
  }

  private failedAttempt(): void {
    const img = this.el.nativeElement;
    img.style.visibility = 'hidden';
    img.removeAttribute('src'); // never leave a broken-image icon behind
    if (this.attempts >= QueuedImageDirective.Backoff.length) {
      this.queuedImageState.emit('failed');
      return;
    }
    const delay = QueuedImageDirective.Backoff[this.attempts];
    this.attempts += 1;
    this.timer = setTimeout(() => {
      this.timer = null;
      this.enqueue();
    }, delay);
  }

  private releaseSlot(): void {
    if (!this.holdsSlot) return;
    this.holdsSlot = false;
    this.queue.release();
  }

  private reset(): void {
    this.observer?.disconnect();
    this.observer = null;
    this.cancelWait?.();
    this.cancelWait = null;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.releaseSlot();
  }
}
