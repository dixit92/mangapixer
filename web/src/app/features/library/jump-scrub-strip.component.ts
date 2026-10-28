import { ChangeDetectionStrategy, Component, HostListener, computed, input, output, signal } from '@angular/core';

import { JumpIndexBucketDto } from '../../core/api/api-types';
import { bubbleTitle, bucketIndexAt, isLongLevel, labelStride, markText } from './jump-scrub';

/** Space (px) kept between the strip and the sticky top bar above it, and the screen's bottom edge below it. */
const GAP_TOP = 8;
const GAP_BOTTOM = 16;

/**
 * The phone scrub strip (1.49.0, owner decision 12 c): a thin index on the right edge of a LONG level (50 rows or more) that a thumb
 * drags through the rail's chips. A bubble beside the finger names the current chip; letting go jumps there - the same jump as a tap
 * on the chip (the host's `jumpToBucket`: scroll in place, or reload from the bucket's cursor). The rail's picker button stays on every
 * level as the accessible path, so the strip is `aria-hidden` and not focusable. Phones only (the rail's own breakpoint); the
 * 44 px wide hit area carries `touch-action: none` so a drag never scrolls the page - nothing else on the page does.
 */
@Component({
  selector: 'app-jump-scrub-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (long()) {
      <div class="scrub" aria-hidden="true" [class.dragging]="dragging()"
           [style.top.px]="top() + gapTop" [style.bottom.px]="gapBottom"
           (pointerdown)="onDown($event)" (pointermove)="onMove($event)" (pointerup)="onUp($event)"
           (pointercancel)="onCancel($event)" (lostpointercapture)="onCancel($event)" (contextmenu)="$event.preventDefault()">
        <div class="track">
          @for (bucket of buckets(); track $index) {
            <span class="mark" [class.on]="highlight() === $index" [class.dot]="markAt(bucket, $index) === null">{{ markAt(bucket, $index) }}</span>
          }
        </div>
        @if (dragging() && picked(); as bucket) {
          <div class="bubble" [style.top.px]="bubbleY()">
            <span class="bubble-label">{{ title(bucket) }}</span>
            <span class="bubble-count">{{ bucket.count }}</span>
          </div>
        }
      </div>
    }
  `,
  styles: [`
    :host { display: contents; }
    /* Phones only: the rail's own picker breakpoint. The whole strip is the 44 px hit area; the visible part is the thin track. */
    .scrub { display: none; }
    @media (max-width: 599.98px) {
      .scrub {
        display: flex; justify-content: flex-end; align-items: stretch;
        position: fixed; right: 0; width: 44px; z-index: 15;
        touch-action: none; user-select: none; -webkit-user-select: none; -webkit-touch-callout: none;
        -webkit-tap-highlight-color: transparent;
      }
    }
    .track {
      display: flex; flex-direction: column; width: 20px; margin-right: 2px;
      background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.12); border-radius: 10px;
      overflow: hidden;
    }
    .scrub.dragging .track { border-color: rgb(var(--mp-accent-strong-rgb) / 0.6); }
    /* Equal slices: the slice a finger is in is the slice drawn highlighted. */
    .mark {
      flex: 1 1 0; min-height: 0; display: flex; align-items: center; justify-content: center;
      font-size: 10px; line-height: 1; font-weight: 700; font-variant-numeric: tabular-nums;
      color: var(--mp-text-muted); overflow: hidden;
    }
    .mark.dot::before { content: ''; width: 3px; height: 3px; border-radius: 50%; background: currentColor; }
    .mark.on { background: var(--mp-accent-strong); color: var(--mp-on-accent); }
    .bubble {
      position: absolute; right: 52px; transform: translateY(-50%); pointer-events: none;
      display: flex; align-items: baseline; gap: 8px; max-width: 60vw;
      padding: 10px 14px; border-radius: 12px;
      background: var(--mp-accent-strong); color: var(--mp-on-accent); box-shadow: 0 2px 10px rgb(var(--mp-shade-rgb) / 0.4);
    }
    .bubble-label { font-size: 22px; font-weight: 700; line-height: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .bubble-count { font-size: 12px; opacity: 0.8; }
  `],
})
export class JumpScrubStripComponent {
  /** The rail's buckets in display order. */
  readonly buckets = input.required<JumpIndexBucketDto[]>();
  /** The bucket of the topmost visible card (scroll-spy), or null. */
  readonly active = input<JumpIndexBucketDto | null>(null);
  /** The measured height of the browse top bar (the strip starts below it). */
  readonly top = input(0);
  /** Rows from which a level counts as long (default `LONG_LEVEL_ROWS`). */
  readonly minRows = input<number | undefined>(undefined);
  /** The finger was lifted on a bucket. */
  readonly jump = output<JumpIndexBucketDto>();

  protected readonly gapTop = GAP_TOP;
  protected readonly gapBottom = GAP_BOTTOM;

  private readonly viewportHeight = signal(typeof window === 'undefined' ? 0 : window.innerHeight);
  /** Pointer id of the drag in progress, null when none. */
  private pointerId: number | null = null;
  private rect: { top: number; height: number } | null = null;

  protected readonly dragging = signal(false);
  /** Index under the finger while scrubbing. */
  private readonly scrubIndex = signal(-1);
  protected readonly bubbleY = signal(0);

  readonly long = computed(() => isLongLevel(this.buckets(), this.minRows()));
  /** The strip's height as laid out (the viewport below the top bar, minus the gaps); drives the label thinning. */
  readonly trackHeight = computed(() => Math.max(0, this.viewportHeight() - this.top() - GAP_TOP - GAP_BOTTOM));
  readonly stride = computed(() => labelStride(this.buckets().length, this.trackHeight()));
  protected readonly picked = computed(() => this.buckets()[this.scrubIndex()] ?? null);
  /** The slice drawn highlighted: the finger's while scrubbing, else the scroll-spy's chip. */
  protected readonly highlight = computed(() => (this.dragging() ? this.scrubIndex() : this.active() ? this.buckets().indexOf(this.active()!) : -1));

  protected markAt(bucket: JumpIndexBucketDto, index: number): string | null {
    return markText(bucket, index, this.stride());
  }

  protected title(bucket: JumpIndexBucketDto): string {
    return bubbleTitle(bucket);
  }

  @HostListener('window:resize')
  protected onResize(): void {
    this.viewportHeight.set(window.innerHeight);
  }

  protected onDown(event: PointerEvent): void {
    if (this.dragging() || (event.pointerType === 'mouse' && event.button !== 0)) return;
    const strip = event.currentTarget as HTMLElement;
    const box = strip.getBoundingClientRect();
    this.rect = { top: box.top, height: box.height };
    this.pointerId = event.pointerId ?? 0;
    event.preventDefault();
    try { strip.setPointerCapture?.(event.pointerId); } catch { /* capture is a nicety: the strip's own box still gets the moves */ }
    this.dragging.set(true);
    this.moveTo(event.clientY);
  }

  protected onMove(event: PointerEvent): void {
    if (!this.dragging() || (event.pointerId ?? 0) !== this.pointerId) return;
    event.preventDefault();
    this.moveTo(event.clientY);
  }

  protected onUp(event: PointerEvent): void {
    if (!this.dragging() || (event.pointerId ?? 0) !== this.pointerId) return;
    this.moveTo(event.clientY);
    const bucket = this.picked();
    this.end(event);
    if (bucket) this.jump.emit(bucket);
  }

  /** The system took the gesture (a call, a swipe from the edge) or capture was lost: nothing is jumped to. */
  protected onCancel(event: PointerEvent): void {
    if (!this.dragging() || (event.pointerId ?? 0) !== this.pointerId) return;
    this.end(event);
  }

  private moveTo(clientY: number): void {
    const rect = this.rect;
    if (!rect) return;
    const index = bucketIndexAt(clientY, rect.top, rect.height, this.buckets().length);
    this.scrubIndex.set(index);
    // The bubble rides the finger, kept inside the strip.
    this.bubbleY.set(Math.min(rect.height, Math.max(0, clientY - rect.top)));
  }

  private end(event: PointerEvent): void {
    this.dragging.set(false);
    this.scrubIndex.set(-1);
    this.pointerId = null;
    this.rect = null;
    try { (event.currentTarget as HTMLElement | null)?.releasePointerCapture?.(event.pointerId); } catch { /* already released */ }
  }
}
