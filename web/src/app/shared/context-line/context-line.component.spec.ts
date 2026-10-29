import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ContextLineComponent } from './context-line.component';

describe('ContextLineComponent', () => {
  function mount(name: string | null | undefined): ComponentFixture<ContextLineComponent> {
    TestBed.configureTestingModule({ imports: [ContextLineComponent] });
    const fixture = TestBed.createComponent(ContextLineComponent);
    fixture.componentRef.setInput('name', name);
    fixture.detectChanges();
    return fixture;
  }

  it('shows the folder name on one muted line with the whole name as its tooltip', () => {
    const el = mount('Berserk').nativeElement as HTMLElement;
    const line = el.querySelector('[data-testid="context-line"]') as HTMLElement;
    expect(line.textContent?.trim()).toBe('Berserk');
    expect(line.getAttribute('title')).toBe('Berserk');
  });

  it('keeps a long name on one line (no wrapping, ellipsis) so a card never grows', () => {
    const long = 'A very long series folder name that would wrap on a phone card if it were allowed to';
    const fixture = mount(long);
    const line = fixture.nativeElement.querySelector('.ctx') as HTMLElement;
    const css = getComputedStyle(line);
    expect(css.whiteSpace).toBe('nowrap');
    expect(css.overflow).toBe('hidden');
    expect(css.textOverflow).toBe('ellipsis');
    expect(line.getAttribute('title')).toBe(long);
  });

  it.each([null, undefined, '', '   '])('renders nothing for %j', (name) => {
    const el = mount(name).nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="context-line"]')).toBeNull();
    expect(el.textContent?.trim()).toBe('');
  });

  it('trims the name and follows a changed input', () => {
    const fixture = mount('  Series A ');
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.ctx')?.textContent?.trim()).toBe('Series A');
    fixture.componentRef.setInput('name', 'Series B');
    fixture.detectChanges();
    expect(el.querySelector('.ctx')?.textContent?.trim()).toBe('Series B');
    fixture.componentRef.setInput('name', null);
    fixture.detectChanges();
    expect(el.querySelector('.ctx')).toBeNull();
  });
});
