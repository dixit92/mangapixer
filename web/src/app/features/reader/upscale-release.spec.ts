import { vi } from 'vitest';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { releaseUpscaler } from './anime4k-renderer';
import {
  FakeGpuDevice, FakeTexture, installNavigatorGpu, removeNavigatorGpu, stubWebGpuGlobals,
} from './fake-webgpu.testing';
import { resetGpuDeviceForTests } from './gpu-device';
import { UpscaleDirective, upscaleReleaseDelayMs } from './upscale.directive';

/**
 * Wiring of `releaseUpscaler()`: the directive is the one place that knows when
 * no Enhance overlay is live, so it must free the renderer's GPU memory when the
 * reader goes away or Enhance is switched off - but NOT on a page turn, which
 * re-creates the `<img>` (track entryKey) and would otherwise rebuild the whole
 * pipeline every page. Drives the real directive -> lazy renderer -> real
 * `anime4k-webgpu` `ModeA` against a fake GPU device.
 */
@Component({
  standalone: true,
  imports: [UpscaleDirective],
  template: `
    <div class="row">
      @for (p of pages(); track p) {
        <img [appUpscale]="on()" [src]="'/api/v1/items/i/pages/' + p" alt="Page" />
      }
    </div>`,
})
class ReaderLikeHostComponent {
  readonly on = signal(true);
  readonly pages = signal(['p0']);
}

/** A 400px-wide page painted 1200 CSS px wide: a 3x upscale at DPR 1. */
function layoutAndLoad(img: HTMLImageElement): void {
  const def = (k: string, value: unknown) => Object.defineProperty(img, k, { value, configurable: true });
  def('naturalWidth', 400);
  def('naturalHeight', 600);
  def('complete', true);
  def('offsetWidth', 1200);
  def('offsetHeight', 1800);
  def('offsetLeft', 0);
  def('offsetTop', 0);
  img.dispatchEvent(new Event('load'));
}

describe('UpscaleDirective GPU release wiring', () => {
  let devices: FakeGpuDevice[];
  const live = () => [...devices[0].textures, ...devices[0].buffers].filter((r) => !r.destroyed).length;

  beforeEach(() => {
    stubWebGpuGlobals(vi.stubGlobal);
    vi.stubGlobal('createImageBitmap', () => Promise.resolve({ close: () => undefined }));
    Object.defineProperty(window, 'devicePixelRatio', { value: 1, configurable: true });
    devices = [];
    installNavigatorGpu(() => { const d = new FakeGpuDevice(); devices.push(d); return d; });
    const context = {
      configure: () => undefined,
      getCurrentTexture: () => new FakeTexture('swapchain', [1, 1, 1]),
    };
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(context as unknown as GPUCanvasContext);
    resetGpuDeviceForTests();
    releaseUpscaler();
  });

  afterEach(() => {
    // No texture/buffer was ever used after being destroyed, in any test.
    expect(devices.flatMap((d) => d.violations)).toEqual([]);
    vi.useRealTimers();
    releaseUpscaler();
    resetGpuDeviceForTests();
    removeNavigatorGpu();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  async function enhancedPage() {
    TestBed.configureTestingModule({ imports: [ReaderLikeHostComponent] });
    const fixture = TestBed.createComponent(ReaderLikeHostComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    layoutAndLoad(host.querySelector('img') as HTMLImageElement);
    await vi.waitFor(() => {
      expect((host.querySelector('canvas') as HTMLCanvasElement | null)?.style.display).toBe('block');
    });
    expect(live()).toBeGreaterThan(30);
    return { fixture, host };
  }

  it('releases every GPU texture/buffer once the reader is destroyed (after the grace delay)', async () => {
    const { fixture } = await enhancedPage();
    vi.useFakeTimers();
    fixture.destroy();
    vi.advanceTimersByTime(upscaleReleaseDelayMs - 1);
    expect(live()).toBeGreaterThan(30); // not yet: a page turn would be back by now
    vi.advanceTimersByTime(1);
    expect(live()).toBe(0);
    expect(devices[0].textures.every((t) => t.destroyCalls === 1)).toBe(true);
  });

  it('releases when Enhance is switched off', async () => {
    const { fixture, host } = await enhancedPage();
    vi.useFakeTimers();
    fixture.componentInstance.on.set(false);
    fixture.detectChanges();
    expect((host.querySelector('canvas') as HTMLCanvasElement).style.display).toBe('none');
    vi.advanceTimersByTime(upscaleReleaseDelayMs);
    expect(live()).toBe(0);
  });

  it('does NOT release on a page turn (the <img> and its directive are re-created)', async () => {
    const { fixture, host } = await enhancedPage();
    const allocated = devices[0].textures.length;
    vi.useFakeTimers();
    fixture.componentInstance.pages.set(['p1']);
    fixture.detectChanges();
    vi.advanceTimersByTime(upscaleReleaseDelayMs * 2);
    expect(live()).toBe(allocated);

    // The new page renders at the same size: the cached pipeline is reused.
    vi.useRealTimers();
    layoutAndLoad(host.querySelector('img') as HTMLImageElement);
    await vi.waitFor(() => expect(devices[0].submits).toBe(2));
    expect(devices[0].textures.length).toBe(allocated);
  });

  it('zeroes the overlay canvas backing store on teardown', async () => {
    const { fixture, host } = await enhancedPage();
    const canvas = host.querySelector('canvas') as HTMLCanvasElement;
    expect(canvas.width).toBe(1200);
    fixture.destroy();
    expect(canvas.width).toBe(0);
    expect(canvas.height).toBe(0);
    expect(canvas.isConnected).toBe(false);
  });

  it('tearing down an overlay that never rendered is harmless', async () => {
    // Enhance on, but the page is not upscaled: the chunk is never needed.
    TestBed.configureTestingModule({ imports: [ReaderLikeHostComponent] });
    const fixture = TestBed.createComponent(ReaderLikeHostComponent);
    fixture.detectChanges();
    vi.useFakeTimers();
    expect(() => {
      fixture.destroy();
      vi.advanceTimersByTime(upscaleReleaseDelayMs);
    }).not.toThrow();
  });
});
