import { Component, Signal, inject } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { BookmarkDto } from '../../core/api/api-types';

/**
 * What the bookmarks panel needs from the reader (1.17.0): the live list (a
 * signal, so the sheet re-renders as bookmarks are added/removed underneath it)
 * and the two actions it dispatches. `ReaderComponent` satisfies this
 * structurally and passes itself as the sheet's `MAT_BOTTOM_SHEET_DATA`, the
 * same host-injection pattern `ReaderOptionsHost` uses in
 * reader-settings-menu.component.ts.
 */
export interface BookmarksPanelHost {
  readonly bookmarks: Signal<BookmarkDto[]>;
  jumpToBookmark(bookmark: BookmarkDto): void;
  deleteBookmark(bookmark: BookmarkDto): void;
}

/**
 * Bottom sheet listing the current item's bookmarks (1.17.0). Tapping a row
 * jumps the reader to that page and closes the sheet; the trailing delete
 * button removes the bookmark without leaving the sheet, so clearing several
 * is one sheet-open for a reader.
 */
@Component({
  selector: 'app-bookmarks-panel',
  standalone: true,
  imports: [MatButtonModule, MatIconModule],
  template: `
    <div class="sheet">
      <div class="grabber" aria-hidden="true"></div>
      <div class="sheet-head">
        <h2 id="bookmarks-panel-title">Bookmarks</h2>
        <button mat-icon-button class="close" (click)="close()" aria-label="Close bookmarks">
          <mat-icon>close</mat-icon>
        </button>
      </div>

      @if (host.bookmarks().length === 0) {
        <p class="empty">No bookmarks yet. Tap the bookmark button on any page to add one.</p>
      } @else {
        <ul class="bookmark-list" aria-labelledby="bookmarks-panel-title">
          @for (bookmark of host.bookmarks(); track bookmark.id) {
            <li class="bookmark-row">
              <button type="button" class="jump" (click)="jump(bookmark)">
                <mat-icon aria-hidden="true">bookmark</mat-icon>
                <span class="label">{{ bookmark.label || ('Page ' + (bookmark.ordinal + 1)) }}</span>
              </button>
              <button mat-icon-button class="delete" (click)="delete(bookmark)"
                      [attr.aria-label]="'Delete bookmark: ' + (bookmark.label || ('page ' + (bookmark.ordinal + 1)))">
                <mat-icon>delete_outline</mat-icon>
              </button>
            </li>
          }
        </ul>
      }
    </div>
  `,
  styles: [`
    /* The sheet container renders in the CDK overlay, so surface/radius/safe-area
       are set through the panel class with ::ng-deep — same treatment as the
       reader options sheet. */
    ::ng-deep .mat-bottom-sheet-container.bookmarks-panel-sheet {
      padding: 0 0 env(safe-area-inset-bottom, 0);
      border-top-left-radius: 20px; border-top-right-radius: 20px;
      max-height: 70vh;
      background: var(--mat-sys-surface-container-high, #1e1e23);
      color: var(--mat-sys-on-surface, #eee);
    }
    .sheet { display: flex; flex-direction: column; gap: 4px; padding: 6px 8px 10px; }
    .grabber {
      width: 36px; height: 4px; border-radius: 2px; margin: 2px auto 0;
      background: var(--mat-sys-outline-variant, rgba(255, 255, 255, 0.25));
    }
    .sheet-head { display: flex; align-items: center; justify-content: space-between; min-height: 40px; padding: 0 8px; }
    .sheet-head h2 { margin: 0; font-size: 17px; font-weight: 500; line-height: 24px; }
    .sheet-head .close { margin-right: -8px; }
    .empty { margin: 8px 16px 16px; font-size: 13px; color: var(--mat-sys-on-surface-variant, #8a8a99); }
    .bookmark-list { list-style: none; margin: 0; padding: 0; overflow-y: auto; }
    .bookmark-row { display: flex; align-items: center; gap: 4px; }
    .jump {
      flex: 1; display: flex; align-items: center; gap: 12px; min-height: 48px;
      padding: 0 8px; background: transparent; border: 0; border-radius: 8px; color: inherit; cursor: pointer;
      font: inherit; text-align: left; -webkit-tap-highlight-color: transparent;
    }
    .jump mat-icon { color: var(--mp-accent, #b39dff); flex: none; }
    .jump .label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    @media (hover: hover) {
      .jump:hover { background: var(--mat-sys-surface-container-highest, rgba(255, 255, 255, 0.08)); }
    }
  `],
})
export class BookmarksPanelComponent {
  readonly host = inject<BookmarksPanelHost>(MAT_BOTTOM_SHEET_DATA);
  private readonly ref = inject<MatBottomSheetRef<BookmarksPanelComponent>>(MatBottomSheetRef);

  jump(bookmark: BookmarkDto): void {
    this.ref.dismiss();
    this.host.jumpToBookmark(bookmark);
  }

  delete(bookmark: BookmarkDto): void {
    this.host.deleteBookmark(bookmark);
  }

  close(): void { this.ref.dismiss(); }
}
