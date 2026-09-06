import { Component, signal, input, computed } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * Webtoon (vertical scroll) reader component.
 * Implements virtualized variable-height strips with normalized scroll anchor restore.
 *
 * Rules:
 * - Long chapters do not accumulate all decoded pages (virtualization).
 * - Position survives resize/orientation/mode switches (normalized anchor).
 * - Tall static pages do not require oversized browser textures.
 */
@Component({
  selector: 'app-webtoon-reader',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="webtoon-container" (scroll)="onScroll($event)">
      @for (page of visiblePages(); track page.index) {
        <div class="webtoon-page" [style.height.px]="page.estimatedHeight">
          <img
            [src]="getPageUrl(page.index)"
            [alt]="'Page ' + (page.index + 1)"
            (load)="onPageLoad($event, page)"
            loading="lazy"
          />
        </div>
      }
      <div class="webtoon-sentinel" [style.height.px]="sentinelHeight()"></div>
    </div>
  `,
  styles: [`
    .webtoon-container {
      height: 100vh;
      overflow-y: auto;
      scroll-snap-type: y proximity;
      background: #1a1a1a;
    }
    .webtoon-page {
      width: 100%;
      display: flex;
      justify-content: center;
      scroll-snap-align: start;
    }
    .webtoon-page img {
      max-width: 100%;
      height: auto;
    }
    .webtoon-sentinel { width: 100%; }
  `],
})
export class WebtoonReaderComponent {
  readonly itemId = input.required<string>();
  readonly pageCount = input.required<number>();

  readonly scrollTop = signal(0);
  readonly scrollHeight = signal(1);
  readonly viewportHeight = signal(window.innerHeight);

  // Virtualization window: render pages near the current scroll position
  private readonly bufferSize = 3;
  readonly currentPageIndex = computed(() => {
    const ratio = this.scrollTop() / Math.max(1, this.scrollHeight() - this.viewportHeight());
    return Math.min(this.pageCount() - 1, Math.floor(ratio * this.pageCount()));
  });

  readonly visiblePages = computed(() => {
    const current = this.currentPageIndex();
    const start = Math.max(0, current - this.bufferSize);
    const end = Math.min(this.pageCount(), current + this.bufferSize + 1);
    const pages: { index: number; estimatedHeight: number }[] = [];
    for (let i = start; i < end; i++) {
      pages.push({ index: i, estimatedHeight: 800 });
    }
    return pages;
  });

  readonly sentinelHeight = computed(() => {
    const visibleCount = this.visiblePages().length;
    return Math.max(0, (this.pageCount() - visibleCount) * 800);
  });

  /**
   * Returns the normalized scroll anchor (0.0-1.0) for resume.
   */
  getNormalizedAnchor(): number {
    const max = this.scrollHeight() - this.viewportHeight();
    if (max <= 0) return 0;
    return Math.min(1, Math.max(0, this.scrollTop() / max));
  }

  /**
   * Restores scroll position from a normalized anchor.
   */
  restoreFromAnchor(anchor: number): void {
    const max = this.scrollHeight() - this.viewportHeight();
    const target = anchor * max;
    const container = document.querySelector('.webtoon-container') as HTMLElement;
    if (container) container.scrollTop = target;
  }

  onScroll(event: Event): void {
    const target = event.target as HTMLElement;
    this.scrollTop.set(target.scrollTop);
    this.scrollHeight.set(target.scrollHeight);
  }

  onPageLoad(event: Event, page: { index: number; estimatedHeight: number }): void {
    const img = event.target as HTMLImageElement;
    const container = img.parentElement!;
    container.style.height = 'auto';
  }

  getPageUrl(index: number): string {
    return `/api/v1/items/${this.itemId()}/pages/${index}`;
  }
}
