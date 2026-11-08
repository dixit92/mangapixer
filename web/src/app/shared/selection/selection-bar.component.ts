import { ChangeDetectionStrategy, Component, ViewEncapsulation, input, output, signal } from '@angular/core';
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
 * component would never reach them (the phone breakpoint draws no label - it stays the accessible name, visually hidden - and the icons scroll sideways in one
 * short row). Every
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
    <div class="actions" [class.more-open]="moreOpen()">
      <button mat-button class="primary" [matMenuTriggerFor]="selectMenu" [disabled]="busy() || selectDisabled()"
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
      <button mat-button class="primary" (click)="markRead.emit(true)" [disabled]="busy() || count() === 0" data-testid="selection-mark-read">
        <mat-icon>check_circle</mat-icon><span class="lbl">Mark read</span>
      </button>
      <!-- 1.52.0 (sweep: 2 of 8 actions at 360 px): on a phone the row shows Select and Mark read; More opens the rest (Mark unread,
           Favorites, the host's actions) in rows of their own below, with their names. Wider screens show every action and no More. -->
      <button mat-button class="more-toggle" (click)="moreOpen.set(!moreOpen())" [attr.aria-expanded]="moreOpen()"
              data-testid="selection-more">
        <mat-icon>{{ moreOpen() ? 'expand_less' : 'more_horiz' }}</mat-icon><span class="lbl">{{ moreOpen() ? 'Fewer actions' : 'More actions' }}</span>
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
    app-selection-bar .count { font-weight: 600; white-space: nowrap; }
    app-selection-bar .actions { flex: 1 1 auto; display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    app-selection-bar .actions mat-icon, app-selection-bar .done mat-icon { margin-right: 4px; }
    /* 1.50.1: Done never shrinks - squeezed on a phone it drew its icon across its border; the actions beside it wrap instead. */
    app-selection-bar .done { flex: none; }
    app-selection-bar .more-toggle { display: none; }
    @media (max-width: 599.98px), (max-height: 499.98px) and (pointer: coarse) {
      /* 1.51.0 (mobile sweep M2): ONE short row on a phone (and a phone held sideways, M1) - "N selected", the action icons, Done. The icons no longer wrap
         into a column beside Done (216-323 px of bar); they scroll sideways inside their own row, which takes what the count
         and Done leave (basis 0, so a wrapping host bar keeps all three on one line). 48 px tall so Material's touch target
         of each button stays inside the scroller. */
      app-selection-bar .actions {
        flex: 1 1 0; min-width: 0; min-height: 48px; flex-wrap: nowrap;
        overflow-x: auto; overflow-y: hidden; overscroll-behavior-x: contain; scrollbar-width: none;
        /* More actions than fit: the row's right edge fades out (a hint that it scrolls); the end padding lets the last icon
           scroll clear of the fade. */
        padding-right: 20px; mask-image: linear-gradient(to right, black calc(100% - 20px), transparent);
      }
      app-selection-bar .actions::-webkit-scrollbar { display: none; }
      app-selection-bar .actions > * { flex: none; }
      app-selection-bar .actions .mat-mdc-button { min-width: 44px; padding: 0 10px; }
      app-selection-bar .actions mat-icon { margin-right: 0; }
      /* M3: the labels are not drawn, but they stay the buttons' accessible names (visually hidden, not display: none) - also
         for the host's projected actions, whose own phone rule hides their .lbl (this rule out-ranks their scoped one). */
      app-selection-bar .actions .lbl {
        display: inline; position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; border: 0;
        overflow: hidden; clip-path: inset(50%); white-space: nowrap;
      }
    }
    @media (max-width: 599.98px) {
      /* 1.52.0: a phone in portrait. Closed: Select, Mark read and More in the row (they fit at 360 px - Done drops its icon);
         the rest waits behind More. Open: the actions take the rows below "N selected" and Done, wrapped, each with its name. */
      app-selection-bar .more-toggle { display: inline-flex; }
      /* (Out-ranks Material's own outlined-button rules, which use :has(mat-icon).) */
      app-selection-bar .done.mat-mdc-outlined-button { min-width: 0; padding: 0 12px; }
      app-selection-bar .done.mat-mdc-outlined-button > mat-icon { display: none; }
      app-selection-bar .actions:not(.more-open) .mat-mdc-button { min-width: 40px; padding: 0 8px; }
      :has(> app-selection-bar) { column-gap: 8px; }
      app-selection-bar .actions:not(.more-open) { padding-right: 0; mask-image: none; }
      app-selection-bar .actions:not(.more-open) > :not(.primary):not(.more-toggle) { display: none; }
      app-selection-bar .actions.more-open {
        order: 3; flex: 1 0 100%; flex-wrap: wrap; overflow: visible; padding-right: 0; mask-image: none; gap: 4px 8px;
      }
      app-selection-bar .actions.more-open .mat-mdc-button { padding: 0 12px; }
      app-selection-bar .actions.more-open mat-icon { margin-right: 4px; }
      app-selection-bar .actions.more-open .lbl {
        position: static; width: auto; height: auto; margin: 0; overflow: visible; clip-path: none;
      }
      app-selection-bar .actions.more-open ~ .done { margin-left: auto; }
      :has(> app-selection-bar > .actions.more-open) { flex-wrap: wrap; row-gap: 8px; }
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

  /** Phone: the actions behind More are shown (1.52.0). */
  readonly moreOpen = signal(false);
}
