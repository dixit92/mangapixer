import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { MatIconModule } from '@angular/material/icon';

import { MetadataReviewAuthorDto } from '../../../core/api/api-types';

/**
 * The Authors list on a phone (1.33.0): authors with two or more works waiting in Needs review, largest first. Tapping one
 * closes the sheet with it (the dashboard then lists that author's works). The desktop shows the same list as a menu.
 */
@Component({
  selector: 'app-review-authors-sheet',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="sheet" data-testid="review-authors-sheet">
      <h3>Authors with several works waiting</h3>
      @for (a of authors; track a.key) {
        <button type="button" class="author" (click)="pick(a)" data-testid="review-author">
          <mat-icon>groups</mat-icon><span class="name">{{ a.label }}</span><span class="count">{{ a.count }}</span>
        </button>
      } @empty {
        <p class="empty">No author has two or more works waiting.</p>
      }
    </div>
  `,
  styles: [`
    .sheet { padding: 4px 0 calc(8px + env(safe-area-inset-bottom)); max-height: 70vh; overflow-y: auto; }
    h3 { font-size: 14px; font-weight: 500; margin: 4px 8px 8px; color: #d0d0dc; }
    .author { display: flex; align-items: center; gap: 10px; width: 100%; min-height: 44px; padding: 0 8px; border: 0;
      background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; border-radius: 8px; }
    .author:hover, .author:focus-visible { background: rgba(255, 255, 255, 0.06); }
    .name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .count { font-size: 12px; font-weight: 600; padding: 0 7px; border-radius: 9px; background: rgba(255, 183, 77, 0.2); line-height: 20px; }
    .empty { color: #9a9aa8; margin: 8px; }
  `],
})
export class ReviewAuthorsSheetComponent {
  private readonly ref = inject<MatBottomSheetRef<ReviewAuthorsSheetComponent, MetadataReviewAuthorDto>>(MatBottomSheetRef);
  readonly authors = inject<MetadataReviewAuthorDto[]>(MAT_BOTTOM_SHEET_DATA);

  pick(author: MetadataReviewAuthorDto): void {
    this.ref.dismiss(author);
  }
}
