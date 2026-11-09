import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { EscapeCancelsDirective } from './escape-cancels.directive';
import { OverlayBackService } from './overlay-back/overlay-back.service';

@Component({
  standalone: true,
  imports: [EscapeCancelsDirective],
  template: `
    @if (!asking()) {
      <button type="button" data-testid="trigger" (click)="asking.set(true)">Revoke</button>
    } @else {
      <div role="alertdialog" appEscapeCancels [escapeDisabled]="busy()" returnFocus="[data-testid=trigger]"
           (escapeCancel)="cancelled = cancelled + 1; asking.set(false)">
        <button type="button" data-testid="cancel" (click)="asking.set(false)">Cancel</button>
      </div>
    }
  `,
})
class HostComponent {
  readonly asking = signal(false);
  readonly busy = signal(false);
  cancelled = 0;
}

const escape = (init: KeyboardEventInit = {}) => new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true, ...init });

/** Inline confirm panels (1.52.0, sweep C2): Escape cancels, unless handled above or busy; focus goes back to the trigger. */
describe('EscapeCancelsDirective', () => {
  function setup() {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const open = () => {
      el.querySelector<HTMLButtonElement>('[data-testid="trigger"]')!.click();
      fixture.detectChanges();
    };
    return { fixture, el, host: fixture.componentInstance, open };
  }

  it('Escape cancels the panel and focus returns to the trigger drawn again', async () => {
    const { fixture, el, host, open } = setup();
    open();
    el.querySelector<HTMLButtonElement>('[data-testid="cancel"]')!.focus();
    document.dispatchEvent(escape());
    fixture.detectChanges();
    expect(host.cancelled).toBe(1);
    expect(el.querySelector('[role="alertdialog"]')).toBeNull();
    await new Promise((r) => setTimeout(r));
    expect(document.activeElement).toBe(el.querySelector('[data-testid="trigger"]'));
  });

  it('a Cancel tap also gives focus back to the trigger', async () => {
    const { fixture, el, open } = setup();
    open();
    const cancel = el.querySelector<HTMLButtonElement>('[data-testid="cancel"]')!;
    cancel.focus();
    cancel.click();
    fixture.detectChanges();
    await new Promise((r) => setTimeout(r));
    expect(document.activeElement).toBe(el.querySelector('[data-testid="trigger"]'));
  });

  it('leaves an Escape that something on top handled, and does nothing while busy', () => {
    const { fixture, el, host, open } = setup();
    open();
    const handled = escape();
    handled.preventDefault();
    document.dispatchEvent(handled);
    host.busy.set(true);
    fixture.detectChanges();
    document.dispatchEvent(escape());
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    fixture.detectChanges();
    expect(host.cancelled).toBe(0);
    expect(el.querySelector('[role="alertdialog"]')).not.toBeNull();
  });

  it('is a Back surface while shown: Back cancels it (m1); a busy panel refuses', async () => {
    const opened: { close(): boolean }[] = [];
    const closed: unknown[] = [];
    TestBed.overrideProvider(OverlayBackService, { useValue: { opened: (x: { close(): boolean }) => opened.push(x), closed: (x: unknown) => closed.push(x) } });
    const { fixture, el, host, open } = setup();
    open();
    expect(opened).toHaveLength(1);
    host.busy.set(true);
    fixture.detectChanges();
    expect(opened[0].close()).toBe(false);
    host.busy.set(false);
    fixture.detectChanges();
    expect(opened[0].close()).toBe(true);
    fixture.detectChanges();
    expect(host.cancelled).toBe(1);
    expect(el.querySelector('[role="alertdialog"]')).toBeNull();
    expect(closed).toEqual([opened[0]]);
  });
});
