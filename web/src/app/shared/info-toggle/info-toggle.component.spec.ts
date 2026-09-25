import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { InfoToggleComponent } from './info-toggle.component';
import { SeriesInfoOverlayService } from '../../features/metadata/series-info-overlay.service';

@Component({
  standalone: true,
  imports: [InfoToggleComponent],
  template: `
    <a class="card" href="/somewhere" (click)="cardClicks = cardClicks + 1">
      <div class="cover"><app-info-toggle [nodeId]="nodeId()" [overlay]="true" /></div>
    </a>
  `,
})
class HostComponent {
  readonly nodeId = signal('node-7');
  cardClicks = 0;
}

/** Card (i) affordance (1.24.0): opens the overlay for its node and never triggers the card. */
describe('InfoToggleComponent', () => {
  const open = vi.fn(() => Promise.resolve());

  function create() {
    open.mockClear();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: SeriesInfoOverlayService, useValue: { open } }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders an accessible info button in overlay mode', () => {
    const fixture = create();
    const host: HTMLElement = fixture.nativeElement.querySelector('app-info-toggle');
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="info-toggle"]');
    expect(host.classList.contains('overlay')).toBe(true);
    expect(button.getAttribute('aria-label')).toBe('Series info');
    expect(button.textContent).toContain('info_outline');
  });

  it('opens the overlay for its node without activating the card link', () => {
    const fixture = create();
    const button: HTMLButtonElement = fixture.nativeElement.querySelector('[data-testid="info-toggle"]');
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });

    button.dispatchEvent(event);

    expect(open).toHaveBeenCalledWith('node-7');
    expect(fixture.componentInstance.cardClicks).toBe(0);
    expect(event.defaultPrevented).toBe(true);
  });
});
