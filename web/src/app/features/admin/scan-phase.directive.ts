import { Directive, ElementRef, afterNextRender, inject } from '@angular/core';

/**
 * 1.45.0 (owner, 2026-10-19 - several libraries scanning): every running scan's ring and bar move in ONE phase, however late its
 * row started scanning. Put on the element that runs the CSS animation (it is inserted when the scan starts): its animation is
 * pinned to the page's own clock (the document timeline's start), so all of them line up exactly. Where the Web Animations API
 * is missing, a negative `animation-delay` of "time since the page loaded" gives the same phase to within a frame.
 */
@Directive({
  selector: '[appScanPhase]',
  standalone: true,
})
export class ScanPhaseDirective {
  constructor() {
    const el = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    afterNextRender(() => alignToPageClock(el));
  }
}

/** Pins the element's running CSS animations to the document timeline's origin (exported for the unit test). */
export function alignToPageClock(el: HTMLElement): void {
  const animations = typeof el.getAnimations === 'function' ? el.getAnimations() : null;
  if (animations && animations.length > 0) {
    for (const animation of animations) animation.startTime = 0;
    return;
  }
  const now = typeof performance !== 'undefined' ? performance.now() : 0;
  el.style.animationDelay = `${-Math.round(now)}ms`;
}
