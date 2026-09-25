import { AfterViewInit, Directive, ElementRef, OnDestroy, OnInit, effect, inject, input } from '@angular/core';

import { WebtoonEnhanceCoordinator } from './webtoon-enhance-coordinator';

/**
 * Webtoon Enhance wiring (1.24.0). Two thin directives around
 * `WebtoonEnhanceCoordinator`, which holds all of the logic:
 *
 *  - `div[appWebtoonEnhanceHost]` on the webtoon scroller PROVIDES the
 *    coordinator, so exactly one exists per strip and it lives exactly as long as
 *    the scroller (switching to paged, a chapter change that swaps the view, or
 *    closing the reader destroys it and releases every canvas and pipeline);
 *  - `img[appWebtoonUpscale]` on each strip page injects that coordinator from
 *    the parent element injector and registers its img and `load` events.
 *
 * The input is `ReaderComponent.upscaleActive()` (Rendering: Enhance) - the same
 * single preference as paged. Without WebGPU or IntersectionObserver the
 * coordinator is inert, the lazy renderer chunk is never requested, and the
 * reader shows exactly the plain `<img>`s it always did.
 */
@Directive({
  selector: 'div[appWebtoonEnhanceHost]',
  standalone: true,
  providers: [WebtoonEnhanceCoordinator],
})
export class WebtoonEnhanceHostDirective implements AfterViewInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly coordinator = inject(WebtoonEnhanceCoordinator);

  /** True when Rendering is Enhance. */
  readonly appWebtoonEnhanceHost = input(false);

  constructor() {
    effect(() => this.coordinator.setEnabled(this.appWebtoonEnhanceHost()));
  }

  ngAfterViewInit(): void {
    this.coordinator.attach(this.host.nativeElement);
  }

  ngOnDestroy(): void {
    this.coordinator.destroy();
  }
}

@Directive({
  selector: 'img[appWebtoonUpscale]',
  standalone: true,
  host: { '(load)': 'onLoad()' },
})
export class WebtoonUpscaleDirective implements OnInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLImageElement>>(ElementRef);
  private readonly coordinator = inject(WebtoonEnhanceCoordinator);

  ngOnInit(): void {
    this.coordinator.register(this.host.nativeElement);
  }

  onLoad(): void {
    this.coordinator.loaded(this.host.nativeElement);
  }

  ngOnDestroy(): void {
    this.coordinator.unregister(this.host.nativeElement);
  }
}
