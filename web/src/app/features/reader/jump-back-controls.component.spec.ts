import { TestBed } from '@angular/core/testing';
import { JumpBackButtonComponent, JumpBackChipComponent } from './jump-back-controls.component';
import { JumpState } from './jump-back';

describe('jump back controls (1.54.0)', () => {
  const back: JumpState = { origin: 41, landing: 186, mode: 'back', at: 0 };
  const ret: JumpState = { origin: 186, landing: 41, mode: 'return', at: 0 };

  it('the bar button names the page it leads to and flips its icon after Back', () => {
    const f = TestBed.createComponent(JumpBackButtonComponent);
    f.componentRef.setInput('state', back);
    let n = 0;
    f.componentInstance.back.subscribe(() => n++);
    f.detectChanges();
    const btn = (f.nativeElement as HTMLElement).querySelector('button')!;
    expect(btn.getAttribute('aria-label')).toBe('Back to page 42');
    expect(btn.textContent!.replace(/\s+/g, ' ').trim()).toBe('undo42');
    btn.click();
    expect(n).toBe(1);
    f.componentRef.setInput('state', ret);
    f.detectChanges();
    expect(btn.getAttribute('aria-label')).toBe('Return to page 187');
    expect(btn.textContent!.trim()).toBe('redo187');
  });

  it('the chip says what happened, offers the way back and can be dismissed', () => {
    const f = TestBed.createComponent(JumpBackChipComponent);
    f.componentRef.setInput('state', back);
    f.componentRef.setInput('kind', 'jumped');
    const events: string[] = [];
    f.componentInstance.back.subscribe(() => events.push('back'));
    f.componentInstance.dismissed.subscribe(() => events.push('x'));
    f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    const text = () => el.querySelector('[role="status"] .long')!.textContent;
    expect(text()).toBe('Jumped from page 42');
    expect(el.querySelector('[role="status"] .short')!.textContent).toBe('From p. 42');
    expect(el.querySelector('button.go')!.getAttribute('aria-label')).toBe('Back to page 42');
    f.componentRef.setInput('kind', 'left');
    f.detectChanges();
    expect(text()).toBe('You jumped just before leaving');
    f.componentRef.setInput('state', ret);
    f.componentRef.setInput('kind', 'returned');
    f.componentRef.setInput('low', true);
    f.detectChanges();
    expect(text()).toBe('Back on page 42');
    expect(el.querySelector('button.go .short')!.textContent).toBe('p. 187');
    expect(el.classList).toContain('low');
    (el.querySelector('button.go') as HTMLButtonElement).click();
    (el.querySelector('button.x') as HTMLButtonElement).click();
    expect(events).toEqual(['back', 'x']);
  });
});
