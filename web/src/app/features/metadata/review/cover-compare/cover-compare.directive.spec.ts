import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { COVER_COMPARE_OPEN_DELAY_MS, CoverCompareDirective } from './cover-compare.directive';

@Component({
  standalone: true,
  imports: [CoverCompareDirective],
  template: `<button class="thumb" [appCoverCompare]="remote()" [coverCompareLocal]="local()" coverCompareLabel="Linked series">x</button>`,
})
class HostComponent {
  readonly remote = signal<string | null>('/api/v1/nodes/n1/series-info/image?v=r1');
  readonly local = signal<string | null>('/api/v1/items/a1/cover');
}

/** Review-row cover comparison (1.28.0, owner): mouse hover after a delay, touch tap toggles, local URLs only. */
describe('CoverCompareDirective', () => {
  function create(remote: string | null = '/api/v1/nodes/n1/series-info/image?v=r1') {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.remote.set(remote);
    fixture.detectChanges();
    const thumb = fixture.nativeElement.querySelector('.thumb') as HTMLElement;
    return { fixture, thumb };
  }
  const panel = () => document.querySelector('[data-testid="cover-compare"]');
  const pointer = (type: string, pointerType: string) => new PointerEvent(type, { pointerType, bubbles: true });

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    document.querySelector('.cdk-overlay-container')?.replaceChildren();
  });

  it('opens both covers side by side after the mouse rests, and closes on leave', () => {
    const { fixture, thumb } = create();
    thumb.dispatchEvent(pointer('pointerenter', 'mouse'));
    vi.advanceTimersByTime(COVER_COMPARE_OPEN_DELAY_MS - 1);
    expect(panel()).toBeNull(); // a sweep opens (and fetches) nothing
    vi.advanceTimersByTime(1);
    fixture.detectChanges();
    const imgs = Array.from(panel()!.querySelectorAll('img')).map((i) => i.getAttribute('src'));
    expect(imgs).toEqual(['/api/v1/items/a1/cover', '/api/v1/nodes/n1/series-info/image?v=r1']);
    expect(panel()!.textContent).toContain('Your cover');
    expect(panel()!.textContent).toContain('Linked series');
    thumb.dispatchEvent(pointer('pointerleave', 'mouse'));
    expect(panel()).toBeNull();
  });

  it('a tap toggles it on touch screens', () => {
    const { fixture, thumb } = create();
    thumb.dispatchEvent(pointer('pointerup', 'touch'));
    fixture.detectChanges();
    expect(panel()).not.toBeNull();
    thumb.dispatchEvent(pointer('pointerleave', 'touch'));
    expect(panel()).not.toBeNull(); // a touch-opened preview stays until the next tap
    thumb.dispatchEvent(pointer('pointerup', 'touch'));
    expect(panel()).toBeNull();
  });

  it('does nothing without a series cover to compare with', () => {
    const { thumb } = create(null);
    thumb.dispatchEvent(pointer('pointerenter', 'mouse'));
    vi.advanceTimersByTime(COVER_COMPARE_OPEN_DELAY_MS);
    thumb.dispatchEvent(pointer('pointerup', 'touch'));
    expect(panel()).toBeNull();
  });
});
