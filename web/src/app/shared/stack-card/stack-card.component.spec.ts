import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { StackCardComponent } from './stack-card.component';

@Component({
  standalone: true,
  imports: [StackCardComponent],
  template: `
    <a class="host-link" href="#">
      <app-stack-card [stacked]="stacked()">
        <img src="/api/v1/items/a1/cover" alt="">
        <span class="badge">3</span>
      </app-stack-card>
    </a>
  `,
})
class HostComponent {
  readonly stacked = signal(true);
}

describe('StackCardComponent (1.29.0)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('projects the cover content into the 2:3 frame inside the stack', () => {
    const el = create().nativeElement as HTMLElement;
    const cover = el.querySelector('app-stack-card > .stack > .cover') as HTMLElement;
    expect(cover).not.toBeNull();
    expect(cover.querySelector('img')?.getAttribute('src')).toBe('/api/v1/items/a1/cover');
    expect(cover.querySelector('.badge')?.textContent).toBe('3');
  });

  it('draws the paper sheets only when stacked', () => {
    const fixture = create();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('.stack.stacked')).not.toBeNull();
    fixture.componentInstance.stacked.set(false);
    fixture.detectChanges();
    expect(el.querySelector('.stack.stacked')).toBeNull();
    expect(el.querySelector('.stack .cover')).not.toBeNull();
  });
});
