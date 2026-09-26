import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { InfoToggleComponent } from './info-toggle.component';
import { SeriesInfoOverlayService } from '../../features/metadata/series-info-overlay.service';
import { MetadataApiService } from '../../features/metadata/metadata-api.service';
import { MetadataStateService } from '../../features/metadata/metadata-state.service';

@Component({
  standalone: true,
  imports: [InfoToggleComponent],
  template: `
    <a class="card" href="/somewhere" (click)="cardClicks = cardClicks + 1">
      <div class="cover"><app-info-toggle [nodeId]="nodeId()" [hasSeriesInfo]="has()" [overlay]="true" /></div>
    </a>
  `,
})
class HostComponent {
  readonly nodeId = signal('node-7');
  readonly has = signal(true);
  cardClicks = 0;
}

/** Card (i) affordance (1.24.0): opens the overlay for its node and never triggers the card. */
describe('InfoToggleComponent', () => {
  const open = vi.fn(() => Promise.resolve());

  function create() {
    open.mockClear();
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SeriesInfoOverlayService, useValue: { open } },
        { provide: MetadataApiService, useValue: {} },
      ],
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

  it('shows itself only while the node has its own information, synced in place by link changes', () => {
    const fixture = create();
    const button = () => fixture.nativeElement.querySelector('[data-testid="info-toggle"]');
    fixture.componentInstance.has.set(false);
    fixture.detectChanges();
    expect(button()).toBeNull();

    const state = TestBed.inject(MetadataStateService);
    state.announce('other-node', true); // another card: no change here
    fixture.detectChanges();
    expect(button()).toBeNull();

    state.announce('node-7', true); // Link in the identify dialog
    fixture.detectChanges();
    expect(button()).not.toBeNull();

    state.announce('node-7', false); // Unlink / Don't match
    fixture.detectChanges();
    expect(button()).toBeNull();
  });
});
