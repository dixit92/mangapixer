import { Directive, ElementRef, OnDestroy, effect, inject, input } from '@angular/core';

import { SeriesInfoHoverService } from './series-info-hover.service';

/**
 * A hover zone for the series summary popover (1.27.0). Put it on the cover, the title
 * and the (i) of an item that shows the (i): `[appSeriesInfoHover]="shown ? node.id : null"`
 * (null disables the zone, e.g. no information or select mode). `hoverAnchor` is the
 * element the popover sits beside (the whole card or row), so every zone of one item
 * opens the same popover in the same place. Mouse and trackpad only: other pointer
 * types, keyboard focus and devices without a hovering fine pointer do nothing. A click
 * is untouched (the item still opens; the (i) still opens the side panel).
 */
@Directive({
  selector: '[appSeriesInfoHover]',
  standalone: true,
  host: {
    '(pointerover)': 'onOver($event)',
    '(pointerout)': 'onOut($event)',
  },
})
export class SeriesInfoHoverDirective implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly hover = inject(SeriesInfoHoverService);

  readonly nodeId = input<string | null | undefined>(null, { alias: 'appSeriesInfoHover' });
  readonly hoverAnchor = input<HTMLElement | null | undefined>(null);

  constructor() {
    effect(() => {
      if (this.nodeId()) this.hover.ensurePreference();
    });
  }

  onOver(event: PointerEvent): void {
    const id = this.nodeId();
    if (!id || event.pointerType !== 'mouse') return;
    this.hover.enter(id, this.hoverAnchor() ?? this.host.nativeElement);
  }

  onOut(event: PointerEvent): void {
    const id = this.nodeId();
    if (!id) return;
    const to = event.relatedTarget as Node | null;
    if (to && this.host.nativeElement.contains(to)) return;
    this.hover.leave(id);
  }

  ngOnDestroy(): void {
    const id = this.nodeId();
    if (id) this.hover.leave(id);
  }
}
