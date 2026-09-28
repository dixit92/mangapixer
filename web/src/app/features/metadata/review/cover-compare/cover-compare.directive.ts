import { Overlay, OverlayRef } from '@angular/cdk/overlay';
import { ComponentPortal } from '@angular/cdk/portal';
import { Directive, ElementRef, OnDestroy, inject, input } from '@angular/core';

import { CoverComparePopoverComponent } from './cover-compare-popover.component';

/** Open delay on a mouse hover: sweeping the pointer down the list opens (and fetches) nothing. */
export const COVER_COMPARE_OPEN_DELAY_MS = 400;

/**
 * Review-row thumbnail -> a large side-by-side preview of the node's own cover and the series record's
 * cover (1.28.0, owner). Mouse: opens after a short rest, closes when the pointer leaves. Touch / pen:
 * a tap on the thumbnail toggles it; a tap anywhere else closes it. Nothing opens without a record image.
 */
@Directive({
  selector: '[appCoverCompare]',
  standalone: true,
  host: {
    '(pointerenter)': 'onEnter($event)',
    '(pointerleave)': 'onLeave()',
    '(pointerup)': 'onTap($event)',
  },
})
export class CoverCompareDirective implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly overlay = inject(Overlay);

  /** The series record's cover (a stored poster, or a candidate image by token); null disables the preview. */
  readonly remoteUrl = input<string | null | undefined>(null, { alias: 'appCoverCompare' });
  readonly coverCompareLocal = input<string | null | undefined>(null);
  readonly coverCompareLabel = input('Series cover');

  private ref: OverlayRef | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private touchOpened = false;

  get isOpen(): boolean {
    return this.ref !== null;
  }

  onEnter(event: PointerEvent): void {
    if (event.pointerType !== 'mouse' || !this.remoteUrl()) return;
    this.clearTimer();
    this.timer = setTimeout(() => this.open(false), COVER_COMPARE_OPEN_DELAY_MS);
  }

  onLeave(): void {
    this.clearTimer();
    if (!this.touchOpened) this.close();
  }

  onTap(event: PointerEvent): void {
    if (event.pointerType === 'mouse' || !this.remoteUrl()) return;
    if (this.ref) this.close();
    else this.open(true);
  }

  private open(touch: boolean): void {
    this.clearTimer();
    if (this.ref) return;
    const strategy = this.overlay.position().flexibleConnectedTo(this.host.nativeElement)
      .withPositions([
        { originX: 'end', originY: 'top', overlayX: 'start', overlayY: 'top', offsetX: 8 },
        { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 8 },
        { originX: 'start', originY: 'top', overlayX: 'start', overlayY: 'bottom', offsetY: -8 },
      ])
      .withPush(true)
      .withViewportMargin(8);
    this.ref = this.overlay.create({
      positionStrategy: strategy,
      scrollStrategy: this.overlay.scrollStrategies.close(),
      hasBackdrop: touch,
      backdropClass: 'cdk-overlay-transparent-backdrop',
      panelClass: 'cover-compare-panel',
    });
    this.touchOpened = touch;
    if (touch) this.ref.backdropClick().subscribe(() => this.close());
    const component = this.ref.attach(new ComponentPortal(CoverComparePopoverComponent));
    component.setInput('localUrl', this.coverCompareLocal() ?? null);
    component.setInput('remoteUrl', this.remoteUrl() ?? null);
    component.setInput('remoteLabel', this.coverCompareLabel());
  }

  private close(): void {
    this.ref?.dispose();
    this.ref = null;
    this.touchOpened = false;
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }

  ngOnDestroy(): void {
    this.clearTimer();
    this.close();
  }
}
