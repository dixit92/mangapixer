import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { LibraryIconComponent } from '../../../shared/library-icon/library-icon.component';

/**
 * Admin icon picker for a single library (1.22.0). Opened inline from the
 * Libraries card row in `admin.component.ts` (kept to a one-line wiring edit
 * there); this component owns the allowlist grid and the "Default" option, and
 * only emits the admin's choice - the parent still owns the API call and the
 * library list update, same as the existing rename/delete inline panels.
 *
 * `ALLOWED_ICONS` mirrors the server allowlist (`LibraryIcons.Allowed` in
 * `src/MangaPixer.Core/Catalog/LibraryIcons.cs`) so the grid never offers a
 * name the server would reject with 400. It is a curated presentation-layer
 * copy, not derived from an API call, so the two lists must be kept in sync by
 * hand when the allowlist changes - the server is still the source of truth
 * and validates independently.
 */
export const ALLOWED_ICONS: readonly string[] = [
  'menu_book',
  'auto_stories',
  'import_contacts',
  'local_library',
  'book',
  'bookmark',
  'bookmarks',
  'collections_bookmark',
  'library_books',
  'chrome_reader_mode',
  'star',
  'star_border',
  'favorite',
  'favorite_border',
  'bolt',
  'flash_on',
  'whatshot',
  'rocket_launch',
  'pets',
  'sports_martial_arts',
  'sports_kabaddi',
  'sports_esports',
  'theater_comedy',
  'brush',
  'palette',
  'color_lens',
  'category',
  'folder_special',
  'auto_awesome',
  'school',
  'explore',
  'public',
];

@Component({
  selector: 'app-library-icon-picker',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule, LibraryIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="picker">
      <button type="button" class="picker-cell default" [class.selected]="current() === null"
              matTooltip="Use the name-derived default" aria-label="Use the name-derived default"
              (click)="picked.emit(null)">
        <app-library-icon [name]="name()" [icon]="null" [size]="28" />
      </button>
      @for (name of icons; track name) {
        <button type="button" class="picker-cell" [class.selected]="current() === name"
                [matTooltip]="name" [attr.aria-label]="'Use icon: ' + name"
                (click)="picked.emit(name)">
          <mat-icon aria-hidden="true">{{ name }}</mat-icon>
        </button>
      }
      <button type="button" class="picker-close" (click)="cancelled.emit()">Cancel</button>
    </div>
  `,
  styles: [`
    .picker {
      display: flex; flex-wrap: wrap; gap: 6px; align-items: center;
      padding: 10px 0;
    }
    .picker-cell {
      display: inline-flex; align-items: center; justify-content: center;
      width: 40px; height: 40px;
      border: 1px solid var(--mp-nav-border, rgba(255,255,255,0.08));
      border-radius: 8px;
      background: transparent;
      cursor: pointer;
      color: inherit;
    }
    .picker-cell:hover { background: rgba(255,255,255,0.06); }
    .picker-cell.selected { border-color: var(--mp-accent, #b39dff); background: var(--mp-accent-bg, rgba(124,77,255,0.18)); }
    .picker-cell.default { font-size: 11px; }
    .picker-close { margin-left: 8px; background: none; border: none; color: inherit; cursor: pointer; text-decoration: underline; }
  `],
})
export class LibraryIconPickerComponent {
  /** Library display name, used to preview the name-derived default. */
  readonly name = input.required<string>();

  /** The library's current icon (null = default). */
  readonly current = input<string | null>(null);

  readonly icons = ALLOWED_ICONS;

  /** Emits the chosen icon name, or null for "Default". */
  readonly picked = output<string | null>();

  readonly cancelled = output<void>();
}
