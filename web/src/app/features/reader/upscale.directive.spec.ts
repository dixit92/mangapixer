import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { UpscaleDirective, UpscaleSupportService, hasWebGpu } from './upscale.directive';

/**
 * The directive's whole contract in a jsdom world is "do nothing, loudly never".
 * There is no WebGPU here (and no image decoding), so every path must end in a
 * silent no-op: no canvas in the DOM, no dynamic import of the Anime4K chunk, no
 * thrown error. The GPU path itself cannot be exercised headlessly - see the
 * owner device-verification steps in the feature note.
 */
@Component({
  standalone: true,
  imports: [UpscaleDirective],
  template: `<div class="row"><img [appUpscale]="on()" src="/api/v1/items/i/pages/p0" alt="Page" /></div>`,
})
class HostComponent {
  readonly on = signal(true);
}

/** Give the <img> a natural size and a layout box, which jsdom never computes. */
function fakeLayout(img: HTMLImageElement, natural: number, box: number): void {
  Object.defineProperty(img, 'naturalWidth', { value: natural, configurable: true });
  Object.defineProperty(img, 'naturalHeight', { value: natural * 2, configurable: true });
  Object.defineProperty(img, 'complete', { value: true, configurable: true });
  Object.defineProperty(img, 'offsetWidth', { value: box, configurable: true });
  Object.defineProperty(img, 'offsetHeight', { value: box * 2, configurable: true });
  Object.defineProperty(img, 'offsetLeft', { value: 0, configurable: true });
  Object.defineProperty(img, 'offsetTop', { value: 0, configurable: true });
}

describe('UpscaleDirective', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, host, img: host.querySelector('img') as HTMLImageElement };
  }

  afterEach(() => {
    delete (navigator as unknown as { gpu?: unknown }).gpu;
    vi.restoreAllMocks();
  });

  it('adds no canvas and throws nothing without WebGPU', async () => {
    const { fixture, host, img } = create();
    fakeLayout(img, 400, 1200); // a 3x upscale: it WOULD want the GPU
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(hasWebGpu()).toBe(false);
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the page is NOT being upscaled, even with WebGPU present', async () => {
    // Pretend the platform has WebGPU so the only reason to bail is the scale.
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fakeLayout(img, 2000, 1000); // rendered at HALF its natural size: a downscale
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the page is rendered at exactly its natural size', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fakeLayout(img, 1000, 1000);
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('does nothing when the preference is off', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const { fixture, host, img } = create();
    fixture.componentInstance.on.set(false);
    fixture.detectChanges();
    fakeLayout(img, 400, 1200);
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    fixture.detectChanges();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('survives an image that never decodes (naturalWidth 0)', async () => {
    const { fixture, host, img } = create();
    Object.defineProperty(img, 'complete', { value: true, configurable: true });
    img.dispatchEvent(new Event('load'));
    await new Promise((r) => setTimeout(r, 40));
    expect(() => fixture.detectChanges()).not.toThrow();
    expect(host.querySelector('canvas')).toBeNull();
  });

  it('cleans up without error when the host is destroyed', () => {
    const { fixture } = create();
    expect(() => fixture.destroy()).not.toThrow();
  });

  describe('device-pixel gate (devicePixelRatio)', () => {
    function setDpr(value: unknown): void {
      Object.defineProperty(window, 'devicePixelRatio', { value, configurable: true });
    }

    let originalDpr: number;

    beforeEach(() => {
      originalDpr = window.devicePixelRatio;
    });

    afterEach(() => {
      setDpr(originalDpr);
    });

    it('treats a CSS-px downscale as an upscale once DPR 3 device pixels are counted', async () => {
      setDpr(3);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      // Painted at 390 CSS px against a 780px-wide source: 0.5x in CSS px (would
      // read as a downscale) but 390 * 3 / 780 = 1.5x in device px: an upscale.
      fakeLayout(img, 780, 390);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).not.toBeNull();
    });

    it('stays a downscale at DPR 1 for the same painted/natural widths', async () => {
      setDpr(1);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).toBeNull();
    });

    it('is an upscale for the iPad spread case: DPR 2, 1366 CSS px painted, 1415 natural', async () => {
      setDpr(2);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      // 1366 * 2 = 2732 device px against a 1415px-wide source: ~1.93x, an upscale.
      fakeLayout(img, 1415, 1366);
      img.dispatchEvent(new Event('load'));
      await new Promise((r) => setTimeout(r, 40));
      fixture.detectChanges();
      expect(host.querySelector('canvas')).not.toBeNull();
    });

    it('treats a missing devicePixelRatio as 1 without throwing', async () => {
      setDpr(undefined);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390); // downscale at DPR 1
      img.dispatchEvent(new Event('load'));
      await expect(new Promise((r) => setTimeout(r, 40))).resolves.not.toThrow();
      expect(() => fixture.detectChanges()).not.toThrow();
      expect(host.querySelector('canvas')).toBeNull();
    });

    it('treats a zero devicePixelRatio as 1 without throwing', async () => {
      setDpr(0);
      (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
      const { fixture, host, img } = create();
      fakeLayout(img, 780, 390); // downscale at DPR 1
      img.dispatchEvent(new Event('load'));
      await expect(new Promise((r) => setTimeout(r, 40))).resolves.not.toThrow();
      expect(() => fixture.detectChanges()).not.toThrow();
      expect(host.querySelector('canvas')).toBeNull();
    });
  });
});

describe('hasWebGpu', () => {
  afterEach(() => { delete (navigator as unknown as { gpu?: unknown }).gpu; });

  it('is false in jsdom (no navigator.gpu)', () => {
    expect(hasWebGpu()).toBe(false);
  });

  it('is true once a gpu entry point exists', () => {
    (navigator as unknown as { gpu?: unknown }).gpu = {};
    expect(hasWebGpu()).toBe(true);
  });
});

describe('UpscaleSupportService', () => {
  afterEach(() => { delete (navigator as unknown as { gpu?: unknown }).gpu; });

  it('reports unavailable with no navigator.gpu, with a readable status', () => {
    const svc = new UpscaleSupportService();
    expect(svc.support()).toBe('unavailable');
    expect(svc.statusText()).toBe('GPU: WebGPU unavailable');
  });

  it('reports ready when an adapter is handed out', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve({}) };
    const svc = new UpscaleSupportService();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('ready');
    expect(svc.statusText()).toBe('GPU: WebGPU ready');
  });

  it('reports unavailable when the adapter request resolves to null', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.resolve(null) };
    const svc = new UpscaleSupportService();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });

  it('reports unavailable when the adapter request rejects', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = { requestAdapter: () => Promise.reject(new Error('no')) };
    const svc = new UpscaleSupportService();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });

  it('reports unavailable when requestAdapter throws synchronously', async () => {
    (navigator as unknown as { gpu?: unknown }).gpu = {
      requestAdapter: () => { throw new Error('boom'); },
    };
    const svc = new UpscaleSupportService();
    await new Promise((r) => setTimeout(r, 40));
    expect(svc.support()).toBe('unavailable');
  });
});
