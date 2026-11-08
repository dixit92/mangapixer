import { Directive, ElementRef, OnDestroy, inject, input, output } from '@angular/core';
import { DOCUMENT } from '@angular/common';

/**
 * An inline confirm panel (`role="alertdialog"` - Revoke a token, Empty trash, Disconnect a tracker, ...) that Escape cancels
 * (1.52.0, mobile sweep C2). Put it on the panel and bind `(escapeCancel)` to what its Cancel button does; Escape anywhere on the
 * page then cancels - unless something on top took the key first (an open menu or dialog marks it handled) or the panel is busy
 * (`[escapeDisabled]`, as its Cancel button). When the panel goes away (cancelled either way, or done) and focus would be lost with
 * it, focus returns to its trigger: `returnFocus` (a selector, for a trigger the panel replaces) or the element focused when the
 * panel opened.
 */
@Directive({
  selector: '[appEscapeCancels]',
  standalone: true,
  host: { '(document:keydown)': 'onKeydown($event)' },
})
export class EscapeCancelsDirective implements OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly document = inject(DOCUMENT);

  /** While true (a request in flight), Escape does nothing - as the panel's disabled Cancel. */
  readonly escapeDisabled = input(false);
  /** Selector of the control that opened the panel, when the panel replaces it (it is drawn again on cancel). */
  readonly returnFocus = input<string | null>(null);
  readonly escapeCancel = output<void>();

  /** The control focused when the panel opened (its trigger, when the panel was opened from the keyboard or a tap that focuses). */
  private readonly opener = this.document.activeElement instanceof HTMLElement && this.document.activeElement !== this.document.body
    ? this.document.activeElement : null;

  onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || event.defaultPrevented || this.escapeDisabled()) return;
    event.preventDefault();
    this.escapeCancel.emit();
  }

  ngOnDestroy(): void {
    const panel = this.host.nativeElement;
    const selector = this.returnFocus();
    const lost = () => {
      const active = this.document.activeElement;
      return !active || active === this.document.body || panel.contains(active) || !active.isConnected;
    };
    if (!lost()) return;
    // The trigger is drawn again in the same change detection as the panel goes: look it up once that has run.
    setTimeout(() => {
      if (!lost()) return;
      const target = (selector ? this.document.querySelector<HTMLElement>(selector) : null)
        ?? (this.opener?.isConnected ? this.opener : null);
      target?.focus();
    });
  }
}
