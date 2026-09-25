import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import { PageLoadIndicatorComponent, WebtoonPageComponent, slowLoadDelayMs } from './page-load-state.component';

/**
 * Reader loading feedback (1.24.0 follow-up) through its public surface: the
 * indicator's delayed "Loading…", and a strip page wrapped the way the reader's
 * webtoon view wraps it (projected img, load / error / retry), with a fake
 * IntersectionObserver standing in for "on screen".
 */

class FakeIO {
  static all: FakeIO[] = [];
  readonly observed = new Set<Element>();
  constructor(readonly callback: IntersectionObserverCallback) { FakeIO.all.push(this); }
  observe(el: Element) { this.observed.add(el); }
  unobserve(el: Element) { this.observed.delete(el); }
  disconnect() { this.observed.clear(); }
  fire(target: Element, isIntersecting: boolean) {
    this.callback([{ target, isIntersecting } as unknown as IntersectionObserverEntry], this as unknown as IntersectionObserver);
  }
}

@Component({
  selector: 'app-indicator-host',
  imports: [PageLoadIndicatorComponent],
  template: `<app-page-load-indicator [armed]="armed()" />`,
})
class IndicatorHostComponent {
  readonly armed = signal(true);
}

@Component({
  selector: 'app-strip-page-host',
  imports: [WebtoonPageComponent],
  template: `
    <div class="scroller" style="position: relative">
      @for (i of pages; track i) {
        <app-webtoon-page [pageNumber]="i + 1">
          <img class="webtoon-page" [src]="'/pages/' + i" alt="" />
        </app-webtoon-page>
      }
    </div>
  `,
})
class StripPageHostComponent {
  readonly pages = [0, 1];
}

describe('PageLoadIndicatorComponent', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function create() {
    TestBed.configureTestingModule({ imports: [IndicatorHostComponent] });
    const fixture = TestBed.createComponent(IndicatorHostComponent);
    fixture.detectChanges();
    const el = (fixture.nativeElement as HTMLElement).querySelector('app-page-load-indicator') as HTMLElement;
    return { fixture, el };
  }

  it('is a polite status region with a ring and no text at first', () => {
    const { el } = create();
    expect(el.getAttribute('role')).toBe('status');
    expect(el.getAttribute('aria-live')).toBe('polite');
    expect(el.querySelector('.ring')?.getAttribute('aria-hidden')).toBe('true');
    expect(el.textContent?.trim()).toBe('');
  });

  it('adds "Loading…" once the wait passes ~3 s', () => {
    const { fixture, el } = create();
    expect(slowLoadDelayMs).toBe(3000);
    vi.advanceTimersByTime(slowLoadDelayMs - 1);
    fixture.detectChanges();
    expect(el.textContent?.trim()).toBe('');
    vi.advanceTimersByTime(1);
    fixture.detectChanges();
    expect(el.textContent?.trim()).toBe('Loading…');
  });

  it('does not count while disarmed, and starts the full delay when armed', () => {
    const { fixture, el } = create();
    fixture.componentInstance.armed.set(false);
    fixture.detectChanges();
    vi.advanceTimersByTime(10_000);
    fixture.detectChanges();
    expect(el.textContent?.trim()).toBe('');
    fixture.componentInstance.armed.set(true);
    fixture.detectChanges();
    vi.advanceTimersByTime(slowLoadDelayMs - 1);
    fixture.detectChanges();
    expect(el.textContent?.trim()).toBe('');
    vi.advanceTimersByTime(1);
    fixture.detectChanges();
    expect(el.textContent?.trim()).toBe('Loading…');
  });

  it('stops the timer when destroyed', () => {
    const { fixture } = create();
    fixture.destroy();
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('WebtoonPageComponent', () => {
  let realIO: typeof IntersectionObserver | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    FakeIO.all = [];
    realIO = globalThis.IntersectionObserver;
    (globalThis as unknown as { IntersectionObserver: unknown }).IntersectionObserver = FakeIO;
  });
  afterEach(() => {
    vi.useRealTimers();
    (globalThis as unknown as { IntersectionObserver: unknown }).IntersectionObserver = realIO;
  });

  function create() {
    TestBed.configureTestingModule({ imports: [StripPageHostComponent] });
    const fixture = TestBed.createComponent(StripPageHostComponent);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const hosts = Array.from(root.querySelectorAll<HTMLElement>('app-webtoon-page'));
    const imgs = Array.from(root.querySelectorAll<HTMLImageElement>('img.webtoon-page'));
    const comps = fixture.debugElement.queryAll(By.directive(WebtoonPageComponent))
      .map((d) => d.componentInstance as WebtoonPageComponent);
    const text = (i: number) => hosts[i].textContent?.replace(/\s+/g, ' ').trim() ?? '';
    return { fixture, hosts, imgs, comps, text };
  }

  it('projects the img unchanged and veils every page with an indicator until it loads', () => {
    const { hosts, imgs, comps } = create();
    expect(imgs.length).toBe(2);
    expect(imgs[0].parentElement).toBe(hosts[0]);
    expect(imgs[0].getAttribute('src')).toBe('/pages/0');
    for (const c of comps) expect(c.state()).toBe('loading');
    for (const h of hosts) expect(h.querySelector('.veil app-page-load-indicator')).toBeTruthy();
  });

  it('the host stays unpositioned, so the img offsets stay relative to the scroller', () => {
    const { hosts, imgs } = create();
    expect(getComputedStyle(hosts[0]).position).toBe('static');
    expect(imgs[0].offsetParent === hosts[0]).toBe(false);
  });

  it('removes the veil when the img fires load', () => {
    const { fixture, hosts, imgs, comps } = create();
    imgs[0].dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(comps[0].state()).toBe('loaded');
    expect(hosts[0].querySelector('.veil')).toBeNull();
    expect(hosts[1].querySelector('.veil')).toBeTruthy();
  });

  it('treats an already decoded (cached) img as loaded', () => {
    TestBed.configureTestingModule({ imports: [StripPageHostComponent] });
    const complete = vi.spyOn(HTMLImageElement.prototype, 'complete', 'get').mockReturnValue(true);
    const width = vi.spyOn(HTMLImageElement.prototype, 'naturalWidth', 'get').mockReturnValue(800);
    try {
      const fixture = TestBed.createComponent(StripPageHostComponent);
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('.veil')).toBeNull();
    } finally {
      complete.mockRestore();
      width.mockRestore();
    }
  });

  it('shows "Loading…" only for an ON-SCREEN page that is still waiting after ~3 s', () => {
    const { fixture, hosts, text } = create();
    const io = FakeIO.all[0];
    expect(FakeIO.all.length).toBe(1); // one shared observer for the whole strip
    expect(io.observed.has(hosts[0]) && io.observed.has(hosts[1])).toBe(true);
    io.fire(hosts[0], true);
    fixture.detectChanges();
    vi.advanceTimersByTime(slowLoadDelayMs);
    fixture.detectChanges();
    expect(text(0)).toBe('Loading…');
    expect(text(1)).toBe(''); // below the fold: not being waited on yet
  });

  it('stops observing a page once it has loaded, and everything on destroy', () => {
    const { fixture, hosts, imgs } = create();
    const io = FakeIO.all[0];
    imgs[0].dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(io.observed.has(hosts[0])).toBe(false);
    expect(io.observed.has(hosts[1])).toBe(true);
    fixture.destroy();
    expect(io.observed.size).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });

  it('on error shows a retry button naming the page; retry requests the same URL again', () => {
    const { fixture, hosts, imgs, comps, text } = create();
    imgs[1].dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(comps[1].state()).toBe('error');
    expect(text(1)).toContain('Page 2 did not load');
    expect(text(1)).toContain('Tap to retry');
    const status = hosts[1].querySelector('[role="status"]') as HTMLElement;
    expect(status.getAttribute('aria-live')).toBe('polite');
    const button = hosts[1].querySelector('button.retry') as HTMLButtonElement;
    expect(button.getAttribute('aria-label')).toBe('Page 2 did not load. Retry');

    const removed = vi.spyOn(imgs[1], 'removeAttribute');
    const set = vi.spyOn(imgs[1], 'setAttribute');
    button.click();
    fixture.detectChanges();
    expect(removed).toHaveBeenCalledWith('src');
    expect(set).toHaveBeenCalledWith('src', '/pages/1');
    expect(imgs[1].getAttribute('src')).toBe('/pages/1');
    expect(comps[1].state()).toBe('loading');
    expect(hosts[1].querySelector('app-page-load-indicator')).toBeTruthy();

    imgs[1].dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(hosts[1].querySelector('.veil')).toBeNull();
  });

  it('on error pins the box to its reserved aspect-ratio height (WebKit collapses a broken img) until it loads', () => {
    const { fixture, hosts, imgs } = create();
    imgs[0].style.aspectRatio = '600 / 2800';
    vi.spyOn(imgs[0], 'offsetWidth', 'get').mockReturnValue(300);
    imgs[0].dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(hosts[0].classList.contains('failed')).toBe(true);
    expect(hosts[0].style.minHeight).toBe('1400px');
    imgs[0].dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(hosts[0].classList.contains('failed')).toBe(false);
    expect(hosts[0].style.minHeight).toBe('');
  });

  it('without a reserved ratio an error pins nothing', () => {
    const { fixture, hosts, imgs } = create();
    imgs[1].dispatchEvent(new Event('error'));
    fixture.detectChanges();
    expect(hosts[1].style.minHeight).toBe('');
  });

  it('a later load after an error still clears the failed state (e.g. a new src)', () => {
    const { fixture, hosts, imgs } = create();
    imgs[0].dispatchEvent(new Event('error'));
    fixture.detectChanges();
    imgs[0].dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(hosts[0].querySelector('.veil')).toBeNull();
  });
});
