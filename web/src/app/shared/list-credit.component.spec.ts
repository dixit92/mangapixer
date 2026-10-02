import { TestBed } from '@angular/core/testing';

import { ListCreditComponent } from './list-credit.component';

/** The Wikipedia credit line (1.32.0): a link to the page, opened without a referrer; nothing without a credit. */
describe('ListCreditComponent', () => {
  function render(credit: { name: string; url: string; title: string } | null) {
    const fixture = TestBed.createComponent(ListCreditComponent);
    fixture.componentRef.setInput('credit', credit);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('links the page by its name, in a new tab with no referrer', () => {
    const el = render({ name: 'Wikipedia', url: 'https://en.wikipedia.org/wiki/List_of_Example_chapters', title: 'List of Example chapters' });
    expect(el.querySelector('[data-testid="list-credit"]')?.textContent).toContain('Volume list completed from Wikipedia');
    const link = el.querySelector('a') as HTMLAnchorElement;
    expect(link.href).toBe('https://en.wikipedia.org/wiki/List_of_Example_chapters');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    expect(link.title).toBe('List of Example chapters');
  });

  it('renders nothing without a credit', () => {
    expect(render(null).querySelector('[data-testid="list-credit"]')).toBeNull();
  });
});
