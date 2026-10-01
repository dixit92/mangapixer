import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { SelectionBarComponent } from './selection-bar.component';

@Component({
  standalone: true,
  imports: [SelectionBarComponent],
  template: `
    <app-selection-bar [count]="count" [busy]="busy" [selectDisabled]="selectDisabled"
                       (selectAll)="log.push('all')" (selectUnread)="log.push('unread')" (selectRead)="log.push('read')"
                       (markRead)="log.push('mark:' + $event)" (favorite)="log.push('fav:' + $event)" (done)="log.push('done')">
      <button type="button" data-testid="host-action">Host action</button>
    </app-selection-bar>`,
})
class HostComponent {
  count = 2;
  busy = false;
  selectDisabled = false;
  readonly log: string[] = [];
}

/** The actions half of the selection bar (1.30.0), shared by the folder browse and the volume stack page. */
describe('SelectionBarComponent', () => {
  function create(init: Partial<HostComponent> = {}) {
    TestBed.configureTestingModule({ imports: [HostComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(HostComponent);
    Object.assign(fixture.componentInstance, init);
    fixture.detectChanges();
    return { fixture, host: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  const button = (el: HTMLElement, testId: string) => el.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement;

  it('shows the count, projects the host actions and says which button was pressed', () => {
    const { host, el } = create();

    expect(el.querySelector('.count')!.textContent!.trim()).toBe('2 selected');
    expect(el.querySelector('.actions [data-testid="host-action"]')).not.toBeNull();
    button(el, 'selection-mark-read').click();
    button(el, 'selection-mark-unread').click();
    button(el, 'selection-done').click();
    expect(host.log).toEqual(['mark:true', 'mark:false', 'done']);
  });

  it('offers Select all / unread / read and Favorites add / remove in menus', () => {
    const { fixture, host, el } = create();

    button(el, 'selection-select-menu').click();
    fixture.detectChanges();
    const items = Array.from(document.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    expect(items.map((i) => i.textContent!.replace(/\s+/g, ' ').trim())).toEqual(
      expect.arrayContaining([expect.stringContaining('Select all unread'), expect.stringContaining('Select all read')]));
    items.find((i) => i.textContent!.includes('Select all unread'))!.click();
    fixture.detectChanges();

    button(el, 'selection-favorites').click();
    fixture.detectChanges();
    (document.querySelector('[data-testid="selection-add-favorite"]') as HTMLElement).click();
    fixture.detectChanges();
    button(el, 'selection-favorites').click();
    fixture.detectChanges();
    (document.querySelector('[data-testid="selection-remove-favorite"]') as HTMLElement).click();

    expect(host.log).toEqual(['unread', 'fav:true', 'fav:false']);
  });

  it('waits while busy and has nothing to act on without a selection', () => {
    const { el } = create({ count: 0 });
    expect(button(el, 'selection-mark-read').disabled).toBe(true);
    expect(button(el, 'selection-favorites').disabled).toBe(true);

    TestBed.resetTestingModule();
    const busy = create({ busy: true, selectDisabled: true });
    expect(button(busy.el, 'selection-mark-read').disabled).toBe(true);
    expect(button(busy.el, 'selection-select-menu').disabled).toBe(true);
    expect(button(busy.el, 'selection-done').disabled).toBe(false); // Done always works
  });
});
