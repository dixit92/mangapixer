import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of } from 'rxjs';

import { SeriesInfoHoverDirective } from './series-info-hover.directive';
import { HOVER_CLOSE_GRACE_MS, HOVER_OPEN_DELAY_MS, SeriesInfoHoverService } from './series-info-hover.service';
import { SeriesInfoHoverPreferenceService } from './series-info-hover-preference.service';
import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from '../../features/metadata/metadata-api.service';
import { seriesInfo } from '../../features/metadata/series-info.testing';

@Component({
  standalone: true,
  imports: [SeriesInfoHoverDirective],
  template: `
    @for (id of ids(); track id) {
      <a #card class="card" [attr.data-id]="id" href="/x">
        <div class="cover" [appSeriesInfoHover]="enabled() ? id : null" [hoverAnchor]="card"><span class="inner">cover</span></div>
        <div class="title" [appSeriesInfoHover]="enabled() ? id : null" [hoverAnchor]="card">{{ id }}</div>
        <div class="other">not a zone</div>
      </a>
    }
  `,
})
class HostComponent {
  readonly ids = signal(['a', 'b', 'c', 'd', 'e', 'f']);
  readonly enabled = signal(true);
}

/**
 * Series information on hover (1.27.0): one controller behind every zone. Nothing is
 * fetched while the pointer sweeps across cards; resting 600 ms fetches once (then the
 * cache serves it) and opens the popover beside the card; leaving closes after a grace
 * that lets the pointer reach the popover; touch, keyboard, a non-hover device and the
 * user's option turned off open nothing.
 */
describe('SeriesInfoHoverDirective + SeriesInfoHoverService', () => {
  const originalMatchMedia = globalThis.matchMedia;
  let getSeriesInfo: ReturnType<typeof vi.fn>;
  let getLibraryPreferences: ReturnType<typeof vi.fn>;
  let hoverDevice = true;

  function setup(opts: { pref?: boolean; info?: SeriesInfoDto } = {}) {
    hoverDevice = true;
    globalThis.matchMedia = ((q: string) => ({ matches: hoverDevice && q.includes('hover: hover') })) as unknown as typeof matchMedia;
    getSeriesInfo = vi.fn((id: string) => of(opts.info ?? seriesInfo({ nodeId: id, title: 'Synthetic ' + id })));
    getLibraryPreferences = vi.fn(() => of({ viewMode: 'grid', density: 'comfortable', sort: 'name', seriesInfoOnHover: opts.pref ?? true }));
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: { getLibraryPreferences } },
        { provide: AuthService, useValue: { currentUser: signal({ id: 'u1' }) } },
        { provide: MetadataApiService, useValue: { getSeriesInfo } },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, service: TestBed.inject(SeriesInfoHoverService) };
  }

  function pointer(type: 'pointerover' | 'pointerout', target: Element, related: Element | null = null, pointerType = 'mouse') {
    const event = new MouseEvent(type, { bubbles: true, relatedTarget: related });
    Object.defineProperty(event, 'pointerType', { value: pointerType });
    target.dispatchEvent(event);
  }

  const zone = (el: HTMLElement, id: string, part: 'cover' | 'title' | 'other') =>
    el.querySelector(`[data-id="${id}"] .${part}`) as HTMLElement;
  const popover = () => document.querySelector('[data-testid="series-info-popover"]');

  async function settle() {
    for (let i = 0; i < 5; i++) await vi.advanceTimersByTimeAsync(0);
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    TestBed.inject(SeriesInfoHoverService).close();
    vi.useRealTimers();
    globalThis.matchMedia = originalMatchMedia;
  });

  it('sweeping the pointer across a grid sends no request and opens nothing', async () => {
    const { el } = setup();
    const ids = ['a', 'b', 'c', 'd', 'e', 'f'];
    ids.forEach((id, i) => {
      pointer('pointerover', zone(el, id, 'cover'));
      vi.advanceTimersByTime(120);
      pointer('pointerout', zone(el, id, 'cover'), i + 1 < ids.length ? zone(el, ids[i + 1], 'cover') : null);
    });
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS + HOVER_CLOSE_GRACE_MS);
    await settle();
    expect(getSeriesInfo).not.toHaveBeenCalled();
    expect(popover()).toBeNull();
  });

  it('resting 600 ms fetches once and opens the compact summary beside the card; the cache serves the next rest', async () => {
    const { el, service } = setup();
    pointer('pointerover', zone(el, 'a', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS - 50);
    expect(getSeriesInfo).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(50);
    await settle();

    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
    expect(getSeriesInfo).toHaveBeenCalledWith('a');
    expect(service.openFor()).toBe('a');
    expect(popover()?.textContent).toContain('Synthetic a');
    expect(popover()?.getAttribute('role')).toBe('tooltip');

    // Leave, come back: no second request.
    pointer('pointerout', zone(el, 'a', 'cover'), zone(el, 'b', 'other'));
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS);
    expect(popover()).toBeNull();
    pointer('pointerover', zone(el, 'a', 'title'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    expect(service.openFor()).toBe('a');
    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
  });

  it('moving from the cover to the title (or into the popover) within the grace keeps it open', async () => {
    const { el, service } = setup();
    pointer('pointerover', zone(el, 'b', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    expect(service.openFor()).toBe('b');

    pointer('pointerout', zone(el, 'b', 'cover'), zone(el, 'b', 'title'));
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS / 2);
    pointer('pointerover', zone(el, 'b', 'title'));
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS * 2);
    expect(service.openFor()).toBe('b');

    pointer('pointerout', zone(el, 'b', 'title'), null);
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS / 2);
    popover()!.dispatchEvent(new MouseEvent('pointerenter'));
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS * 2);
    expect(service.openFor()).toBe('b');

    popover()!.dispatchEvent(new MouseEvent('pointerleave'));
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS);
    expect(service.openFor()).toBeNull();
    expect(popover()).toBeNull();
  });

  it('moving inside one zone (a child element) does not restart or close anything', async () => {
    const { el, service } = setup();
    const cover = zone(el, 'c', 'cover');
    pointer('pointerover', cover);
    await vi.advanceTimersByTimeAsync(300);
    const inner = cover.querySelector('.inner')!;
    pointer('pointerout', cover, inner);
    pointer('pointerover', inner);
    await vi.advanceTimersByTimeAsync(300);
    await settle();
    expect(service.openFor()).toBe('c');
  });

  it('Esc and a pointer press close it', async () => {
    const { el, service } = setup();
    pointer('pointerover', zone(el, 'a', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(service.openFor()).toBeNull();

    pointer('pointerover', zone(el, 'a', 'title'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    expect(service.openFor()).toBe('a');
    zone(el, 'a', 'title').dispatchEvent(new MouseEvent('pointerdown', { bubbles: true }));
    expect(service.openFor()).toBeNull();
  });

  it('touch and pen pointers, a device without hover, and a disabled zone open nothing', async () => {
    const { el, fixture } = setup();
    pointer('pointerover', zone(el, 'a', 'cover'), null, 'touch');
    pointer('pointerover', zone(el, 'b', 'cover'), null, 'pen');
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS * 2);

    hoverDevice = false;
    pointer('pointerover', zone(el, 'c', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS * 2);

    hoverDevice = true;
    fixture.componentInstance.enabled.set(false);
    fixture.detectChanges();
    pointer('pointerover', zone(el, 'd', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS * 2);
    await settle();

    expect(getSeriesInfo).not.toHaveBeenCalled();
    expect(popover()).toBeNull();
  });

  it('keyboard focus does not open it', async () => {
    const { el } = setup();
    zone(el, 'a', 'cover').closest('a')!.focus();
    zone(el, 'a', 'cover').dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS * 2);
    expect(getSeriesInfo).not.toHaveBeenCalled();
  });

  it("the user's option off: loaded once, nothing opens; turned on in Settings, it works without a reload", async () => {
    const { el, service } = setup({ pref: false });
    expect(getLibraryPreferences).toHaveBeenCalledTimes(1);
    pointer('pointerover', zone(el, 'a', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS * 2);
    expect(getSeriesInfo).not.toHaveBeenCalled();
    pointer('pointerout', zone(el, 'a', 'cover'));

    TestBed.inject(SeriesInfoHoverPreferenceService).set(true);
    pointer('pointerover', zone(el, 'e', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    expect(service.openFor()).toBe('e');
    expect(getLibraryPreferences).toHaveBeenCalledTimes(1);
  });

  it('a node that resolves to nothing to show opens no popover', async () => {
    const { el, service } = setup({ info: seriesInfo({ state: 'None', title: null }) });
    pointer('pointerover', zone(el, 'a', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    await settle();
    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
    expect(service.openFor()).toBeNull();
    expect(popover()).toBeNull();
  });

  it('leaving before a slow response arrives opens nothing when it lands', async () => {
    const { el, service } = setup();
    const slow = new Subject<SeriesInfoDto>();
    getSeriesInfo.mockReturnValue(slow);
    pointer('pointerover', zone(el, 'f', 'cover'));
    await vi.advanceTimersByTimeAsync(HOVER_OPEN_DELAY_MS);
    expect(getSeriesInfo).toHaveBeenCalledTimes(1);
    pointer('pointerout', zone(el, 'f', 'cover'), null);
    await vi.advanceTimersByTimeAsync(HOVER_CLOSE_GRACE_MS);
    slow.next(seriesInfo({ nodeId: 'f' }));
    slow.complete();
    await settle();
    expect(service.openFor()).toBeNull();
  });
});
