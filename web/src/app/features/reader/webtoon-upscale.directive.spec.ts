import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';

import type { BandRenderRequest, BandRenderResult } from './anime4k-tile-renderer';
import type { UpscaleBackend } from './upscale-engine';
import {
  TileRendererApi, WEBTOON_TILE_RENDERER, WebtoonEnhanceCoordinator, pageRootMargin, bandRootMargin, webgpuEnhance,
} from './webtoon-enhance-coordinator';
import { WebtoonEnhanceHostDirective, WebtoonUpscaleDirective } from './webtoon-upscale.directive';

/**
 * The webtoon Enhance directives through their public surface: a host template
 * shaped like the reader's webtoon view (scroller + strip imgs + a trailing
 * element), with fake IntersectionObserver / ResizeObserver and a mocked lazy
 * renderer whose loader calls are counted.
 */

class FakeIO {
  static all: FakeIO[] = [];
  readonly observed = new Set<Element>();
  constructor(readonly callback: IntersectionObserverCallback, readonly options: IntersectionObserverInit) { FakeIO.all.push(this); }
  observe(el: Element) { this.observed.add(el); }
  unobserve(el: Element) { this.observed.delete(el); }
  disconnect() { this.observed.clear(); }
  fire(targets: Element[], isIntersecting: boolean) {
    this.callback(targets.map((target) => ({ target, isIntersecting }) as unknown as IntersectionObserverEntry), this as unknown as IntersectionObserver);
  }
}
class FakeRO {
  observe() { /* noop */ }
  unobserve() { /* noop */ }
  disconnect() { /* noop */ }
}

@Component({
  selector: 'app-strip-host',
  imports: [WebtoonEnhanceHostDirective, WebtoonUpscaleDirective],
  template: `
    @if (shown()) {
      <div class="scroller" [appWebtoonEnhanceHost]="on()">
        @for (i of pages; track i) {
          <img class="webtoon-page" appWebtoonUpscale [attr.data-i]="i" alt="" />
        }
        <div class="webtoon-end"></div>
      </div>
    }
  `,
})
class StripHostComponent {
  readonly on = signal<UpscaleBackend | null>(null);
  readonly shown = signal(true);
  readonly pages = [0, 1, 2];
}

function define(el: object, props: Record<string, unknown>) {
  for (const [k, v] of Object.entries(props)) Object.defineProperty(el, k, { configurable: true, get: () => v });
}

describe('WebtoonEnhanceHostDirective / WebtoonUpscaleDirective', () => {
  let loads: number;
  let renderer: TileRendererApi;
  let releaseTiles: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    FakeIO.all = [];
    loads = 0;
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date', 'performance'] });
    vi.stubGlobal('IntersectionObserver', FakeIO);
    vi.stubGlobal('ResizeObserver', FakeRO);
    (navigator as unknown as { gpu?: unknown }).gpu = {};
    releaseTiles = vi.fn<() => void>();
    renderer = {
      renderBand: (_req: BandRenderRequest) => Promise.resolve<BandRenderResult>({ status: 'ok', gpuMs: 5, slices: 2 }),
      releaseTiles,
      onTilesLost: () => () => undefined,
      createSliceBudget: () => ({ k: 4 }),
    };
    TestBed.configureTestingModule({
      imports: [StripHostComponent],
      providers: [{ provide: WEBTOON_TILE_RENDERER, useValue: () => { loads++; return Promise.resolve(renderer); } }],
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    delete (navigator as unknown as { gpu?: unknown }).gpu;
  });

  function render() {
    const fixture = TestBed.createComponent(StripHostComponent);
    fixture.detectChanges();
    const scroller = () => (fixture.nativeElement as HTMLElement).querySelector('.scroller') as HTMLElement;
    const imgs = () => Array.from(scroller().querySelectorAll<HTMLImageElement>('img'));
    for (const img of imgs()) {
      define(img, { complete: true, naturalWidth: 300, naturalHeight: 1200, currentSrc: `/p${img.dataset['i']}`,
        offsetLeft: 0, offsetTop: Number(img.dataset['i']) * 1600, offsetWidth: 400, offsetHeight: 1600 });
    }
    define(scroller(), { clientHeight: 500, scrollTop: 0 });
    return { fixture, scroller, imgs };
  }

  it('the host provides ONE coordinator that every strip img directive shares', () => {
    const { fixture } = render();
    const host = fixture.debugElement.query(By.directive(WebtoonEnhanceHostDirective));
    const shared = host.injector.get(WebtoonEnhanceCoordinator);
    const pages = fixture.debugElement.queryAll(By.directive(WebtoonUpscaleDirective));
    expect(pages.length).toBe(3);
    for (const p of pages) expect(p.injector.get(WebtoonEnhanceCoordinator)).toBe(shared);
  });

  it('the input turns the enhance layer on and off; it is appended after the template nodes', async () => {
    const { fixture, scroller, imgs } = render();
    expect(scroller().querySelector('.mp-enhance-layer')).toBeNull();
    fixture.componentInstance.on.set(webgpuEnhance);
    fixture.detectChanges();
    expect(scroller().lastElementChild?.className).toBe('mp-enhance-layer');
    const pageIO = FakeIO.all.find((o) => o.options.rootMargin === pageRootMargin)!;
    expect([...pageIO.observed]).toEqual(imgs());

    // A page comes near, a band becomes visible, the band renders onto a canvas.
    pageIO.fire([imgs()[0]], true);
    const bandIO = FakeIO.all.find((o) => o.options.rootMargin === bandRootMargin)!;
    bandIO.fire([[...bandIO.observed][0]], true);
    await vi.advanceTimersByTimeAsync(0);
    expect(loads).toBe(1);
    expect(scroller().querySelectorAll('canvas').length).toBe(1);

    fixture.componentInstance.on.set(null);
    fixture.detectChanges();
    expect(scroller().querySelector('.mp-enhance-layer')).toBeNull();
    expect(scroller().querySelectorAll('canvas').length).toBe(0);
    expect(releaseTiles).toHaveBeenCalledTimes(1);
  });

  it('without WebGPU: no layer, no canvas, and the lazy renderer is never imported', async () => {
    delete (navigator as unknown as { gpu?: unknown }).gpu;
    const { fixture, scroller } = render();
    fixture.componentInstance.on.set(webgpuEnhance);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(100);
    expect(scroller().querySelector('.mp-enhance-layer')).toBeNull();
    expect(scroller().querySelectorAll('canvas').length).toBe(0);
    expect(FakeIO.all.length).toBe(0);
    expect(loads).toBe(0);
  });

  it('destroying the scroller (view switch / reader closed) releases every canvas and pipeline', async () => {
    const { fixture, scroller, imgs } = render();
    fixture.componentInstance.on.set(webgpuEnhance);
    fixture.detectChanges();
    FakeIO.all.find((o) => o.options.rootMargin === pageRootMargin)!.fire([imgs()[0]], true);
    const bandIO = FakeIO.all.find((o) => o.options.rootMargin === bandRootMargin)!;
    bandIO.fire([[...bandIO.observed][0]], true);
    await vi.advanceTimersByTimeAsync(0);
    const canvas = scroller().querySelector('canvas') as HTMLCanvasElement;
    canvas.width = 600;
    fixture.componentInstance.shown.set(false);
    fixture.detectChanges();
    expect(canvas.width).toBe(0);
    expect(canvas.isConnected).toBe(false);
    expect(releaseTiles).toHaveBeenCalledTimes(1);
  });
});
