import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

/**
 * The actions half of the browse selection bar (1.30.0, extracted from `library-browse.component.ts` so the volume stack page
 * offers the same bar): "N selected", the Select menu (all / unread / read), Mark read / unread, Favorites (add / remove), the
 * host's own actions (projected: reading direction, series metadata, cover, folder view - whatever fits its items) and Done.
 * The host owns the sticky container (`.browse-bar.selecting` in browse, the stack page's own bar) and every action; this
 * component only says which button was pressed.
 *
 * Styles are not encapsulated: the projected admin actions carry the HOST's style scope, so a scoped `.lbl` rule of this
 * component would never reach them (the phone breakpoint hides every label so the bar stays one short row of icons). Every
 * rule is namespaced under `app-selection-bar`.
 */
@Component({
  selector: 'app-selection-bar',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  template: `
    <span class="count" data-testid="selection-count">{{ count() }} selected</span>
    <div class="actions">
      <button mat-button [matMenuTriggerFor]="selectMenu" [disabled]="busy() || selectDisabled()"
              matTooltip="Select the whole list" data-testid="selection-select-menu">
        <mat-icon>playlist_add_check</mat-icon><span class="lbl">Select</span>
      </button>
      <mat-menu #selectMenu="matMenu">
        <button mat-menu-item (click)="selectAll.emit()">
          <mat-icon>select_all</mat-icon> Select all
        </button>
        <button mat-menu-item (click)="selectUnread.emit()">
          <mat-icon>radio_button_unchecked</mat-icon> Select all unread
        </button>
        <button mat-menu-item (click)="selectRead.emit()">
          <mat-icon>check_circle</mat-icon> Select all read
        </button>
      </mat-menu>
      <button mat-button (click)="markRead.emit(true)" [disabled]="busy() || count() === 0" data-testid="selection-mark-read">
        <mat-icon>check_circle</mat-icon><span class="lbl">Mark read</span>
      </button>
      <button mat-button (click)="markRead.emit(false)" [disabled]="busy() || count() === 0" data-testid="selection-mark-unread">
        <mat-icon>remove_done</mat-icon><span class="lbl">Mark unread</span>
      </button>
      <button mat-button [matMenuTriggerFor]="favoriteMenu" [disabled]="busy() || count() === 0"
              matTooltip="Favorites for the selection" data-testid="selection-favorites">
        <mat-icon>star_border</mat-icon><span class="lbl">Favorites</span>
      </button>
      <mat-menu #favoriteMenu="matMenu">
        <button mat-menu-item (click)="favorite.emit(true)" data-testid="selection-add-favorite">
          <mat-icon>star</mat-icon> Add to favorites
        </button>
        <button mat-menu-item (click)="favorite.emit(false)" data-testid="selection-remove-favorite">
          <mat-icon>star_border</mat-icon> Remove from favorites
        </button>
      </mat-menu>
      <ng-content />
    </div>
    <button mat-stroked-button class="done" (click)="done.emit()" data-testid="selection-done">
      <mat-icon>close</mat-icon> Done
    </button>
  `,
  styles: [`
    app-selection-bar { display: contents; }
    app-selection-bar .count { font-weight: 600; }
    app-selection-bar .actions { flex: 1 1 auto; display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    app-selection-bar .actions mat-icon, app-selection-bar .done mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) {
      app-selection-bar .actions .lbl { display: none; }
      app-selection-bar .actions mat-icon { margin-right: 0; }
    }
  `],
})
export class SelectionBarComponent {
  /** How many items are selected. */
  readonly count = input.required<number>();

  /** The host is applying an action: every button waits. */
  readonly busy = input(false);

  /** Nothing to select (an empty list): the Select menu is off. */
  readonly selectDisabled = input(false);

  readonly selectAll = output<void>();
  readonly selectUnread = output<void>();
  readonly selectRead = output<void>();
  readonly markRead = output<boolean>();
  /** True = add to favorites, false = remove. */
  readonly favorite = output<boolean>();
  readonly done = output<void>();
}
