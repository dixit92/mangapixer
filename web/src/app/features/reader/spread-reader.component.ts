import { Component, signal, input, computed } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * Spread (two-page) reader component.
 * Groups pages into spreads with cover/offset/wide-page handling.
 *
 * Rules:
 * - RTL spreads and odd last pages are correct.
 * - Cover page is shown alone.
 * - Wide pages are shown alone.
 * - Direction-aware spread grouping.
 */
@Component({
  selector: 'app-spread-reader',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="spread-container" [class.rtl]="direction() === 'rtl'">
      @if (currentSpread().length === 1) {
        <div class="single-page">
          <img [src]="getPageUrl(currentSpread()[0])" alt="Page {{ currentSpread()[0] + 1 }}" />
        </div>
      } @else {
        <div class="spread-pages">
          @for (pageIndex of currentSpread(); track pageIndex) {
            <img [src]="getPageUrl(pageIndex)" alt="Page {{ pageIndex + 1 }}" />
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .spread-container {
      height: 100vh;
      display: flex;
      justify-content: center;
      align-items: center;
      background: #1a1a1a;
    }
    .spread-container.rtl { direction: rtl; }
    .single-page img, .spread-pages img {
      max-height: 100vh;
      max-width: 100%;
    }
    .spread-pages {
      display: flex;
      gap: 2px;
    }
    .spread-pages img {
      max-width: 50%;
    }
  `],
})
export class SpreadReaderComponent {
  readonly itemId = input.required<string>();
  readonly pageCount = input.required<number>();
  readonly direction = input<'ltr' | 'rtl'>('ltr');
  readonly coverOffset = input<number>(0);
  readonly currentSpreadIndex = signal(0);

  /**
   * Computes the current spread (array of page indices).
   * Cover page (index 0) is shown alone.
   * Odd last page is shown alone.
   */
  readonly currentSpread = computed<number[]>(() => {
    const spreadIdx = this.currentSpreadIndex();
    const total = this.pageCount();
    if (total === 0) return [];

    // Page 0 is the cover — always shown alone
    if (spreadIdx === 0) return [0];

    // Calculate which pages belong to this spread
    // After cover, pages are paired: (1,2), (3,4), etc.
    const pageStart = 1 + (spreadIdx - 1) * 2;

    if (pageStart >= total) return [];

    // If only one page left, show it alone (odd last page)
    if (pageStart === total - 1) return [pageStart];

    return [pageStart, pageStart + 1];
  });

  readonly totalSpreads = computed(() => {
    const total = this.pageCount();
    if (total <= 1) return 1;
    // Cover + ceil((total-1)/2) spreads
    return 1 + Math.ceil((total - 1) / 2);
  });

  nextSpread(): void {
    if (this.currentSpreadIndex() < this.totalSpreads() - 1) {
      this.currentSpreadIndex.update((i) => i + 1);
    }
  }

  prevSpread(): void {
    if (this.currentSpreadIndex() > 0) {
      this.currentSpreadIndex.update((i) => i - 1);
    }
  }

  getPageUrl(index: number): string {
    return `/api/v1/items/${this.itemId()}/pages/${index}`;
  }
}
