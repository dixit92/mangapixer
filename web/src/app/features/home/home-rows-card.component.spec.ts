import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { HomeRowsCardComponent } from './home-rows-card.component';
import { DEFAULT_HOME_ROW_ORDER, HomeRowId } from './home-rows';

@Component({
  standalone: true,
  imports: [HomeRowsCardComponent],
  template: `<app-home-rows-card [order]="order()" (orderChange)="order.set($event)" (reset)="resets = resets + 1" />`,
})
class HostComponent {
  readonly order = signal<HomeRowId[]>([...DEFAULT_HOME_ROW_ORDER]);
  resets = 0;
}

/** 1.54.0: Settings > Home - Move up / down with names, disabled ends, focus kept on the moved row, the move announced. */
describe('HomeRowsCardComponent', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const button = (el: HTMLElement, label: string) => el.querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`)!;
  const rows = (el: HTMLElement) => [...el.querySelectorAll('[data-testid="home-row-item"]')].map((r) => r.getAttribute('data-row'));

  it('names every button after its row and disables the two ends', () => {
    const { el } = create();
    expect(rows(el)).toEqual([...DEFAULT_HOME_ROW_ORDER]);
    expect(button(el, 'Move Continue reading up').disabled).toBe(true);
    expect(button(el, 'Move Continue reading down').disabled).toBe(false);
    expect(button(el, 'Move Libraries down').disabled).toBe(true);
    expect(button(el, 'Move Libraries up').disabled).toBe(false);
  });

  it('a move keeps the focus on the moved row and announces its new place', () => {
    const { fixture, el } = create();
    button(el, 'Move Libraries up').click();
    fixture.detectChanges();
    TestBed.tick(); // the afterNextRender that puts the focus back
    expect(rows(el)).toEqual(['continue', 'recommended', 'favorites', 'libraries', 'recent']);
    expect(document.activeElement).toBe(button(el, 'Move Libraries up'));
    expect(el.querySelector('[role="status"]')!.textContent).toContain('Libraries moved to position 4 of 5.');
  });

  it('at an end the focus goes to the row\\'s other button (the pressed one is disabled now)', () => {
    const { fixture, el } = create();
    button(el, 'Move Recommended for you up').click();
    fixture.detectChanges();
    TestBed.tick();
    expect(rows(el)[0]).toBe('recommended');
    expect(button(el, 'Move Recommended for you up').disabled).toBe(true);
    expect(document.activeElement).toBe(button(el, 'Move Recommended for you down'));
  });

  it('Reset is disabled on the default order and asks for a reset otherwise', () => {
    const { fixture, el } = create();
    const reset = () => [...el.querySelectorAll<HTMLButtonElement>('button')].find((b) => b.textContent!.includes('Reset to default order'))!;
    expect(reset().disabled).toBe(true);
    button(el, 'Move Libraries up').click();
    fixture.detectChanges();
    expect(reset().disabled).toBe(false);
    reset().click();
    expect(fixture.componentInstance.resets).toBe(1);
  });
});
