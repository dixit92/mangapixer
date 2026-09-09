import { Directive, ElementRef, HostListener, inject, OnDestroy } from '@angular/core';

/**
 * Cover-image auto-retry (1.2.0). Durable thumbnails are pre-generated at scan
 * time, but during the initial scan/analysis (or the one-time backfill) a cover
 * endpoint returns a "pending" (202) response that an <img> can't decode — the
 * element fires `error`. Rather than leaving the fallback icon showing until a
 * manual reload, this directive hides the image (revealing the fallback behind
 * it) and retries the load a few times with backoff. When the thumbnail becomes
 * available the image loads and is shown — no reload needed.
 *
 * After the attempts are exhausted the image stays hidden (the fallback icon
 * remains), so a genuinely missing/broken cover degrades exactly as before.
 *
 * Uses `visibility` (not `display`) so the card layout never shifts.
 */
@Directive({
  selector: 'img[appCover]',
  standalone: true,
})
export class CoverImageDirective implements OnDestroy {
  private readonly el = inject<ElementRef<HTMLImageElement>>(ElementRef);
  private attempts = 0;
  private timer: ReturnType<typeof setTimeout> | null = null;

  /** Backoff schedule (ms) for pending-thumbnail retries. */
  private static readonly Backoff = [2500, 6000, 15000];

  @HostListener('error')
  onError(): void {
    const img = this.el.nativeElement;
    img.style.visibility = 'hidden'; // reveal the fallback icon behind it
    if (this.attempts >= CoverImageDirective.Backoff.length) return;

    const delay = CoverImageDirective.Backoff[this.attempts];
    this.attempts += 1;
    this.clearTimer();
    this.timer = setTimeout(() => {
      // Cache-bust so a previously-pending URL is actually re-requested.
      const base = img.src.split('?')[0];
      img.src = `${base}?r=${this.attempts}`;
    }, delay);
  }

  @HostListener('load')
  onLoad(): void {
    const img = this.el.nativeElement;
    // Only reveal on a real decoded image (guards against a 0-byte/again-pending body).
    if (img.naturalWidth > 0) img.style.visibility = 'visible';
  }

  ngOnDestroy(): void {
    this.clearTimer();
  }

  private clearTimer(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
