import { Component, computed, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ReaderPreferencesService, PageAnimation } from '../../core/reading/reader-preferences.service';
import { WebtoonNavPreferencesService, WebtoonTapStep, WebtoonForwardEdge } from './webtoon-nav.service';
import {
  ReaderOptionsHost, LayoutChoice, LAYOUT_OPTIONS, FIT_OPTIONS, DIRECTION_OPTIONS, PAGE_ANIMATION_OPTIONS,
  WEBTOON_TAP_STEP_OPTIONS, WEBTOON_FORWARD_EDGE_OPTIONS, READER_MENU_STYLES, activeLayout, pickLayout,
} from './reader-settings-menu.component';

/** Keys the Page width slider handles itself; inside the menu they would otherwise move focus between items. */
const SliderKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown']);

/**
 * The bar's Display menu (1.41.0, owner decision 5): how the pages are laid out on screen, in one menu instead of
 * four bar buttons - Layout (the old Reading mode menu), Image fit, Reading direction and Page transition; in the
 * vertical view the Page width slider takes Image fit's place, Reading direction drops out and Tap to scroll takes
 * Page transition's slot, as the bar did before, followed by Edge taps (1.41.0: which middle-band edge goes forward;
 * shown but disabled while Tap to scroll is Off). Every list is the phone sheet's own (`LAYOUT_OPTIONS`, `FIT_OPTIONS`,
 * ...) and the layout rule is the sheet's `activeLayout`, so the two surfaces cannot drift. The trigger shows the
 * reading mode on screen (`icon`, the reader's `viewIcon()`).
 *
 * Every pick applies at once and closes the menu (a menu item's normal behaviour); the slider keeps the menu open
 * while it is dragged, and Tab from an item moves to it (a menu otherwise closes on Tab), so it is reachable by
 * keyboard. The active option carries the accent highlight (F4); items are `menuitemradio` + `aria-checked`, named
 * with their group ("Image fit: Fit width"). 1.47.0 (owner): the paged menu was 808 px tall, taller than a ~720 px viewport (it scrolled
 * inside); on a wide screen its groups now sit in two columns (Layout | Image fit, Reading direction | Page transition), and on a
 * short screen the items are a little closer together - no inner scrolling down to about 600 px. A narrow screen keeps one column.
 */
@Component({
  selector: 'app-reader-display-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatSliderModule, MatTooltipModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="displayMenu" matTooltip="Display" aria-label="Display"
            (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
      <mat-icon>{{ icon() }}</mat-icon>
    </button>
    <mat-menu #displayMenu="matMenu" class="reader-options-menu reader-display-menu">
      <div class="display-groups" tabindex="-1" (keydown)="onPanelKey($event)">
        <div role="group" aria-label="Layout">
          <div class="menu-group-label">Layout</div>
          @for (opt of layoutOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [class.selected-option]="layout() === opt.value" [attr.aria-checked]="layout() === opt.value"
                    (click)="chooseLayout(opt.value)" [attr.aria-label]="'Layout: ' + (opt.menuLabel ?? opt.label)">
              <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.menuLabel ?? opt.label }}</button>
          }
        </div>
        @if (host().view() === 'webtoon') {
          <div role="group" aria-label="Page width">
            <div class="menu-group-label">Page width <span class="value">{{ host().webtoonWidthPct() }}%</span></div>
            <mat-slider class="menu-slider" min="15" max="100" step="5" (click)="$event.stopPropagation()">
              <input matSliderThumb [value]="host().webtoonWidthPct()"
                     (valueChange)="host().setWebtoonWidth($event)" aria-label="Webtoon page width">
            </mat-slider>
          </div>
          <div role="group" aria-label="Tap to scroll">
            <div class="menu-group-label">Tap to scroll</div>
            @for (opt of tapStepOptions; track opt.value) {
              <button mat-menu-item role="menuitemradio"
                      [class.selected-option]="webtoonNav.tapStep() === opt.value"
                      [attr.aria-checked]="webtoonNav.tapStep() === opt.value"
                      (click)="chooseTapStep(opt.value)" [attr.aria-label]="'Tap to scroll: ' + opt.label">
                @if (opt.icon) { <mat-icon>{{ opt.icon }}</mat-icon> }
                {{ opt.label }}
              </button>
            }
          </div>
          <div role="group" aria-label="Edge taps">
            <div class="menu-group-label">Edge taps</div>
            @for (opt of forwardEdgeOptions; track opt.value) {
              <button mat-menu-item role="menuitemradio" [disabled]="!webtoonNav.tapZonesEnabled()"
                      [class.selected-option]="webtoonNav.forwardEdge() === opt.value"
                      [attr.aria-checked]="webtoonNav.forwardEdge() === opt.value"
                      (click)="chooseForwardEdge(opt.value)" [attr.aria-label]="'Edge taps: ' + opt.label">
                <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.label }}</button>
            }
            @if (!webtoonNav.tapZonesEnabled()) { <div class="menu-hint">Off while Tap to scroll is Off.</div> }
          </div>
        } @else {
          <div role="group" aria-label="Image fit">
            <div class="menu-group-label">Image fit</div>
            @for (opt of fitOptions; track opt.value) {
              <button mat-menu-item role="menuitemradio"
                      [class.selected-option]="host().fitMode() === opt.value" [attr.aria-checked]="host().fitMode() === opt.value"
                      (click)="host().setFitMode(opt.value)" [attr.aria-label]="'Image fit: ' + opt.label">
                <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.label }}</button>
            }
          </div>
          <div role="group" aria-label="Reading direction">
            <div class="menu-group-label">Reading direction</div>
            @for (opt of directionOptions; track opt.value) {
              <button mat-menu-item role="menuitemradio"
                      [class.selected-option]="host().direction() === opt.value" [attr.aria-checked]="host().direction() === opt.value"
                      (click)="host().setDirection(opt.value)" [attr.aria-label]="'Reading direction: ' + opt.label">
                <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.label }}</button>
            }
          </div>
          <div role="group" aria-label="Page transition">
            <div class="menu-group-label">Page transition</div>
            @for (opt of transitionOptions; track opt.value) {
              <button mat-menu-item role="menuitemradio"
                      [class.selected-option]="prefs.pageAnimation() === opt.value"
                      [attr.aria-checked]="prefs.pageAnimation() === opt.value"
                      (click)="chooseTransition(opt.value)" [attr.aria-label]="'Page transition: ' + opt.label">
                <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.label }}</button>
            }
          </div>
        }
      </div>
    </mat-menu>
  `,
  styles: [READER_MENU_STYLES],
})
export class ReaderDisplayMenuComponent {
  readonly prefs = inject(ReaderPreferencesService);
  readonly webtoonNav = inject(WebtoonNavPreferencesService);

  /** The reader (live signals in, actions out - the phone sheet's contract). */
  readonly host = input.required<ReaderOptionsHost>();
  /** The trigger's glyph: the reading mode on screen. */
  readonly icon = input('crop_portrait');

  /** Emitted when the menu opens / closes, so the reader can pin its chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  readonly layoutOptions = LAYOUT_OPTIONS;
  readonly fitOptions = FIT_OPTIONS;
  readonly directionOptions = DIRECTION_OPTIONS;
  readonly transitionOptions = PAGE_ANIMATION_OPTIONS;
  readonly tapStepOptions = WEBTOON_TAP_STEP_OPTIONS;
  readonly forwardEdgeOptions = WEBTOON_FORWARD_EDGE_OPTIONS;

  /** The layout to highlight - the phone sheet's rule. */
  readonly layout = computed(() => activeLayout(this.host()));

  chooseLayout(choice: LayoutChoice): void { pickLayout(this.host(), choice); }
  chooseTransition(mode: PageAnimation): void { this.prefs.setPageAnimation(mode); }
  chooseTapStep(step: WebtoonTapStep): void { this.webtoonNav.setTapStep(step); }
  chooseForwardEdge(edge: WebtoonForwardEdge): void { this.webtoonNav.setForwardEdge(edge); }

  /**
   * Keyboard inside the menu: the Page width slider keeps its own arrow keys, and Tab from a menu item moves to the
   * slider instead of closing the menu (Tab or Escape on the slider still close it, as from any item).
   */
  onPanelKey(e: KeyboardEvent): void {
    const panel = e.currentTarget as HTMLElement | null;
    const slider = panel?.querySelector<HTMLInputElement>('input[type="range"]');
    if (!slider) return;
    if (e.target === slider) {
      if (SliderKeys.has(e.key)) e.stopPropagation();
      return;
    }
    if (e.key === 'Tab' && !e.shiftKey) {
      e.preventDefault();
      e.stopPropagation();
      slider.focus();
    }
  }
}

/**
 * The bar's Bookmark menu (1.41.0, owner decision 5): "Bookmark this page" / "Remove bookmark" (the old bar toggle)
 * and "Bookmarks..." (the old second button, opening the list). The trigger keeps the page's state visible at a
 * glance: the filled glyph, in the accent, while the page on screen is bookmarked.
 *
 * "Bookmarks..." opens the list once the menu has CLOSED: the menu hands focus back to its trigger as it closes,
 * which would otherwise land behind the freshly opened sheet, and the reader's chrome pin is released by the menu
 * before the list takes it again.
 */
@Component({
  selector: 'app-reader-bookmark-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <button mat-icon-button class="bookmark-trigger" [class.marked]="marked()" [matMenuTriggerFor]="bookmarkMenu"
            [matTooltip]="marked() ? 'Bookmarks - this page is bookmarked' : 'Bookmarks'"
            [attr.aria-label]="marked() ? 'Bookmark menu, this page is bookmarked' : 'Bookmark menu'"
            (menuOpened)="opened.emit()" (menuClosed)="onClosed()">
      <mat-icon>{{ marked() ? 'bookmark' : 'bookmark_border' }}</mat-icon>
    </button>
    <mat-menu #bookmarkMenu="matMenu" class="reader-options-menu">
      <button mat-menu-item (click)="host().toggleBookmark()"
              [attr.aria-label]="marked() ? 'Remove bookmark from this page' : 'Bookmark this page'">
        <mat-icon>{{ marked() ? 'bookmark_remove' : 'bookmark_add' }}</mat-icon>
        {{ marked() ? 'Remove bookmark' : 'Bookmark this page' }}
      </button>
      <button mat-menu-item (click)="listRequested = true" aria-label="Bookmarks" aria-haspopup="dialog">
        <mat-icon>bookmarks</mat-icon> Bookmarks…
      </button>
    </mat-menu>
  `,
  styles: [`
    .bookmark-trigger.marked { color: var(--mp-accent); }
  `, READER_MENU_STYLES],
})
export class ReaderBookmarkMenuComponent {
  /** The reader (live signals in, actions out). */
  readonly host = input.required<Pick<ReaderOptionsHost, 'isCurrentPageBookmarked' | 'toggleBookmark' | 'openBookmarks'>>();

  /** Emitted when the menu opens / closes, so the reader can pin its chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  readonly marked = computed(() => this.host().isCurrentPageBookmarked());

  /** Set by "Bookmarks...": open the list once the menu has gone. */
  listRequested = false;

  onClosed(): void {
    this.closed.emit();
    if (!this.listRequested) return;
    this.listRequested = false;
    this.host().openBookmarks();
  }
}
