import { Component, Signal, inject, output } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ReaderPreferencesService, PageAnimation } from '../../core/reading/reader-preferences.service';

// --- Reader option vocabulary -------------------------------------------------
// The value types the reader's settings surfaces (desktop menus + the phone
// options sheet) agree on. They live here, with the settings surface, so the
// sheet never has to import the (large) ReaderComponent.

export type ReaderView = 'paged' | 'spread' | 'webtoon';
// Per-device default PAGED LAYOUT (owner request, 1.2.x; scoped to paged-only,
// 2026-09-11 "Option A" fix). A device-local override of single/double/auto page
// layout, distinct from the server's content-semantic ReaderMode. 'auto' picks
// paged (portrait) / spread (landscape) live as the device rotates. `null` (unset)
// means "follow whatever the server resolves for the item". This can no longer
// hold 'webtoon': webtoon-vs-paged is CONTENT ORIENTATION, resolved server-side
// (`ReaderModeResolver`) and must never be forced device-global across libraries
// (that was the reader-mode-sticky bug — vertical bleeding from a webtoon into the
// next manga opened on the same device). Picking "Vertical (webtoon)" from the
// menu is instead a per-item/session-only override; see `ReaderComponent.chooseView`.
export type ViewPref = 'auto' | 'paged' | 'spread';
export type FitMode = 'screen' | 'width' | 'height' | 'original';
export type ReadingDirection = 'ltr' | 'rtl';

/** A selectable option: its value, label and the glyph it keeps even when selected. */
export interface ReaderOption<T> { value: T; label: string; icon: string }

/** Page-transition choices (1.9.0), shared by the desktop menu and the phone sheet. */
export const PAGE_ANIMATION_OPTIONS: readonly ReaderOption<PageAnimation>[] = [
  { value: 'slide', label: 'Slide', icon: 'view_carousel' },
  { value: 'reveal', label: 'Reveal', icon: 'gradient' },
  { value: 'none', label: 'None', icon: 'block' },
];

export const FIT_OPTIONS: readonly ReaderOption<FitMode>[] = [
  { value: 'screen', label: 'Fit screen', icon: 'fit_screen' },
  { value: 'width', label: 'Fit width', icon: 'swap_horiz' },
  { value: 'height', label: 'Fit height', icon: 'swap_vert' },
  { value: 'original', label: 'Original size', icon: 'crop_original' },
];

export const DIRECTION_OPTIONS: readonly ReaderOption<ReadingDirection>[] = [
  { value: 'ltr', label: 'Left to right', icon: 'format_textdirection_l_to_r' },
  { value: 'rtl', label: 'Right to left', icon: 'format_textdirection_r_to_l' },
];

/**
 * Layout choices as ONE radio group. 'spread' / 'spread-cover' are the two
 * double-page pairing modes (see `ReaderComponent.chooseSpread`); 'webtoon' is a
 * per-item override, never a persisted device preference.
 */
export type LayoutChoice = ViewPref | 'spread-cover' | 'webtoon';
export const LAYOUT_OPTIONS: readonly ReaderOption<LayoutChoice>[] = [
  { value: 'auto', label: 'Auto', icon: 'screen_rotation' },
  { value: 'paged', label: 'Single page', icon: 'crop_portrait' },
  { value: 'spread', label: 'Double page', icon: 'import_contacts' },
  { value: 'spread-cover', label: 'Double, cover alone', icon: 'auto_stories' },
  { value: 'webtoon', label: 'Vertical', icon: 'view_day' },
];

/**
 * Reader settings config surface (1.9.0). A small, self-contained toolbar button
 * that opens a menu for the page-navigation TRANSITION (Slide / Reveal / None),
 * kept out of the large `ReaderComponent` so the reader only needs a one-line
 * wiring point. The choice persists per-device via `ReaderPreferencesService`.
 *
 * It mirrors the reader's other menus: the active option is marked with the
 * accent COLOR HIGHLIGHT (1.10.0, matching the browse View menu shipped in 1.8.1)
 * rather than a checkmark, so every option keeps its own glyph; each item is a
 * `menuitemradio` carrying `aria-checked`. `opened`/`closed` are surfaced so the
 * reader can pin its auto-hiding chrome visible while the menu is open (same
 * treatment as the mode/fit menus). Desktop / tablet only: on a phone the same
 * choices live in the `ReaderOptionsSheetComponent` below.
 */
@Component({
  selector: 'app-reader-settings-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="animMenu"
            matTooltip="Page transition" aria-label="Page transition"
            (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
      <mat-icon>animation</mat-icon>
    </button>
    <mat-menu #animMenu="matMenu" class="reader-options-menu">
      @for (opt of options; track opt.value) {
        <button mat-menu-item role="menuitemradio"
                [class.selected-option]="prefs.pageAnimation() === opt.value"
                [attr.aria-checked]="prefs.pageAnimation() === opt.value"
                (click)="choose(opt.value)" [attr.aria-label]="'Page transition: ' + opt.label">
          <mat-icon>{{ opt.icon }}</mat-icon>
          {{ opt.label }}
        </button>
      }
    </mat-menu>
  `,
  styles: [`
    /* Selected-state highlight (1.10.0): the panel renders in a CDK overlay, so
       the rule is scoped via the reader-options-menu panel class and reaches the
       projected items with ::ng-deep. Same values as the 1.8.1 View menu. */
    ::ng-deep .reader-options-menu .selected-option { background: rgba(124, 77, 255, 0.16); }
    ::ng-deep .reader-options-menu .selected-option,
    ::ng-deep .reader-options-menu .selected-option .mat-icon { color: #b39dff; }
  `],
})
export class ReaderSettingsMenuComponent {
  readonly prefs = inject(ReaderPreferencesService);
  readonly options = PAGE_ANIMATION_OPTIONS;

  /** Emitted when the transition menu opens / closes, so the reader can pin chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  choose(mode: PageAnimation): void {
    this.prefs.setPageAnimation(mode);
  }
}

// --- Phone options sheet ------------------------------------------------------

/**
 * What the phone options sheet needs from the reader: the live state it
 * displays (signals, so the sheet re-renders as the reader changes underneath
 * it) and the actions it dispatches. `ReaderComponent` satisfies this
 * structurally and passes itself as the sheet's `MAT_BOTTOM_SHEET_DATA`, so no
 * state is duplicated and no new service is needed.
 */
export interface ReaderOptionsHost {
  readonly view: Signal<ReaderView>;
  readonly viewPref: Signal<ViewPref | null>;
  readonly coverIsStandalone: Signal<boolean>;
  readonly fitMode: Signal<FitMode>;
  readonly direction: Signal<ReadingDirection>;
  readonly webtoonWidthPct: Signal<number>;
  readonly hasPrevChapter: Signal<boolean>;
  readonly hasNextChapter: Signal<boolean>;
  readonly prevNeighbor: Signal<{ displayName: string } | null>;
  readonly nextNeighbor: Signal<{ displayName: string } | null>;
  chooseView(pref: ViewPref | 'webtoon'): void;
  chooseSpread(coverStandalone: boolean): void;
  setFitMode(mode: FitMode): void;
  setDirection(direction: ReadingDirection): void;
  setWebtoonWidth(pct: number): void;
  prevChapter(): void;
  nextChapter(): void;
  toggleHelp(): void;
}

/**
 * Phone reader options (1.10.0, finding F2). On a handset-width screen the
 * reader bar packed ~8 icon buttons into ~375px, so the last ones were clipped
 * off-screen. The bar now keeps only the two actions a reader reaches for while
 * actually reading (Next chapter, Fullscreen) plus one "more" trigger, and the
 * rest live here, in a bottom sheet - the established mobile home for reader
 * settings (Mihon/Tachiyomi, Kindle, Apple Books) and the region of the screen
 * the thumb can reach.
 *
 * Design:
 *  - Progressive disclosure: settings are GROUPED (Layout / Image fit /
 *    Direction / Page transition) as labelled radio-groups of chips, not a flat
 *    15-item menu. Each group is a single row (or two) of large, labelled,
 *    iconed chips, so the whole sheet reads at a glance and fits a phone screen
 *    without scrolling.
 *  - The active choice is the accent COLOR HIGHLIGHT (finding F4), the same
 *    treatment as the desktop menus and the browse View menu.
 *  - Picking a setting applies immediately and keeps the sheet open (the page
 *    behind it updates live, so several tweaks are one gesture away from each
 *    other); the NAVIGATION actions (chapter prev/next, help) close the sheet
 *    first because they replace what is on screen.
 *  - Webtoon swaps the paged-only groups for the Page width slider, mirroring
 *    what the desktop bar does.
 *  - Touch: every control is >= 44px tall. Motion: the sheet's slide-up is cut
 *    to ~1ms under prefers-reduced-motion (the container needs the animation
 *    events to finalise its enter/exit, so it is shortened, not removed).
 *  - Colour: Material system tokens for surfaces/outline (theme-following) and
 *    the app's --mp-accent / --mp-accent-bg tokens for the highlight.
 */
@Component({
  selector: 'app-reader-options-sheet',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatSliderModule],
  template: `
    <div class="sheet">
      <div class="grabber" aria-hidden="true"></div>
      <div class="sheet-head">
        <h2 id="reader-options-title">Reader options</h2>
        <button mat-icon-button class="close" (click)="close()" aria-label="Close reader options">
          <mat-icon>close</mat-icon>
        </button>
      </div>

      <section class="group">
        <h3 class="group-label" id="reader-options-layout">Layout</h3>
        <div class="chips" role="radiogroup" aria-labelledby="reader-options-layout">
          @for (opt of layoutOptions; track opt.value) {
            <button type="button" class="chip" role="radio"
                    [class.selected]="activeLayout() === opt.value"
                    [attr.aria-checked]="activeLayout() === opt.value"
                    (click)="pickLayout(opt.value)">
              <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
            </button>
          }
        </div>
      </section>

      @if (host.view() === 'webtoon') {
        <section class="group">
          <h3 class="group-label" id="reader-options-width">
            Page width <span class="value">{{ host.webtoonWidthPct() }}%</span>
          </h3>
          <mat-slider class="width-slider" min="15" max="100" step="5">
            <input matSliderThumb [value]="host.webtoonWidthPct()"
                   (valueChange)="host.setWebtoonWidth($event)" aria-labelledby="reader-options-width">
          </mat-slider>
        </section>
      } @else {
        <section class="group">
          <h3 class="group-label" id="reader-options-fit">Image fit</h3>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-fit">
            @for (opt of fitOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [class.selected]="host.fitMode() === opt.value"
                      [attr.aria-checked]="host.fitMode() === opt.value"
                      (click)="host.setFitMode(opt.value)">
                <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
              </button>
            }
          </div>
        </section>
        <section class="group">
          <h3 class="group-label" id="reader-options-direction">Reading direction</h3>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-direction">
            @for (opt of directionOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [class.selected]="host.direction() === opt.value"
                      [attr.aria-checked]="host.direction() === opt.value"
                      (click)="host.setDirection(opt.value)">
                <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
              </button>
            }
          </div>
        </section>
        <section class="group">
          <h3 class="group-label" id="reader-options-transition">Page transition</h3>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-transition">
            @for (opt of transitionOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [class.selected]="prefs.pageAnimation() === opt.value"
                      [attr.aria-checked]="prefs.pageAnimation() === opt.value"
                      (click)="prefs.setPageAnimation(opt.value)">
                <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
              </button>
            }
          </div>
        </section>
      }

      <div class="chapter-row">
        <button mat-stroked-button class="chapter" (click)="chapter('prev')" [disabled]="!host.hasPrevChapter()"
                [attr.aria-label]="host.prevNeighbor() ? 'Previous chapter: ' + host.prevNeighbor()!.displayName : 'No previous chapter'">
          <mat-icon>skip_previous</mat-icon> Previous chapter
        </button>
        <button mat-stroked-button class="chapter" (click)="chapter('next')" [disabled]="!host.hasNextChapter()"
                [attr.aria-label]="host.nextNeighbor() ? 'Next chapter: ' + host.nextNeighbor()!.displayName : 'No next chapter'">
          Next chapter <mat-icon iconPositionEnd>skip_next</mat-icon>
        </button>
      </div>
      <button mat-button class="help-row" (click)="help()">
        <mat-icon>help_outline</mat-icon> Reading help
      </button>
    </div>
  `,
  styles: [`
    /* The sheet container itself renders in the CDK overlay (outside this view),
       so its surface, radius and safe-area padding are set through the panel
       class with ::ng-deep. Surface + text follow the Material theme tokens. */
    ::ng-deep .mat-bottom-sheet-container.reader-options-sheet {
      padding: 0 0 env(safe-area-inset-bottom, 0);
      border-top-left-radius: 20px; border-top-right-radius: 20px;
      max-height: 88vh;
      background: var(--mat-sys-surface-container-high, #1e1e23);
      color: var(--mat-sys-on-surface, #eee);
    }
    @media (prefers-reduced-motion: reduce) {
      /* Keep the enter/exit keyframes (the container waits on their events) but
         make them effectively instant. */
      ::ng-deep .mat-bottom-sheet-container.reader-options-sheet { animation-duration: 1ms !important; }
      .chip { transition: none; }
    }
    .sheet { display: flex; flex-direction: column; gap: 14px; padding: 6px 16px 10px; }
    .grabber {
      width: 36px; height: 4px; border-radius: 2px; margin: 2px auto 0;
      background: var(--mat-sys-outline-variant, rgba(255, 255, 255, 0.25));
    }
    .sheet-head { display: flex; align-items: center; justify-content: space-between; min-height: 40px; }
    .sheet-head h2 { margin: 0; font-size: 17px; font-weight: 500; line-height: 24px; }
    .sheet-head .close { margin-right: -8px; }
    .group { display: flex; flex-direction: column; gap: 8px; }
    .group-label {
      display: flex; align-items: baseline; justify-content: space-between; margin: 0;
      font-size: 11px; font-weight: 600; letter-spacing: 0.5px; text-transform: uppercase;
      color: var(--mat-sys-on-surface-variant, #8a8a99);
    }
    .group-label .value { font-variant-numeric: tabular-nums; text-transform: none; letter-spacing: 0; font-weight: 500; }
    .chips { display: flex; flex-wrap: wrap; gap: 8px; }
    /* Chips: 44px touch targets, the option's own glyph, and the accent highlight
       (not a tick) for the selected one. */
    .chip {
      display: inline-flex; align-items: center; gap: 6px;
      min-height: 44px; padding: 0 14px 0 12px; border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant, rgba(255, 255, 255, 0.22));
      background: transparent; color: inherit; cursor: pointer;
      font: inherit; font-size: 14px; font-weight: 500; line-height: 20px;
      -webkit-tap-highlight-color: transparent;
      transition: background-color .15s ease, border-color .15s ease, color .15s ease;
    }
    .chip mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .chip.selected { background: var(--mp-accent-bg, rgba(124, 77, 255, 0.18)); color: var(--mp-accent, #b39dff); border-color: transparent; }
    .chip:focus-visible { outline: 2px solid var(--mp-accent, #b39dff); outline-offset: 2px; }
    @media (hover: hover) {
      .chip:hover:not(.selected) { background: var(--mat-sys-surface-container-highest, rgba(255, 255, 255, 0.08)); }
    }
    .width-slider { width: 100%; margin: 0; }
    .chapter-row { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin-top: 2px; }
    .chapter { min-height: 44px; }
    .help-row { min-height: 44px; justify-content: flex-start; margin-bottom: 2px; }
  `],
})
export class ReaderOptionsSheetComponent {
  readonly host = inject<ReaderOptionsHost>(MAT_BOTTOM_SHEET_DATA);
  readonly prefs = inject(ReaderPreferencesService);
  private readonly ref = inject<MatBottomSheetRef<ReaderOptionsSheetComponent>>(MatBottomSheetRef);

  readonly layoutOptions = LAYOUT_OPTIONS;
  readonly fitOptions = FIT_OPTIONS;
  readonly directionOptions = DIRECTION_OPTIONS;
  readonly transitionOptions = PAGE_ANIMATION_OPTIONS;

  /**
   * The layout chip to highlight: the EFFECTIVE layout on screen. Coincides with
   * the stored device preference whenever one exists ('auto' wins over the
   * paged/spread it resolved to), and with the server-resolved view when none
   * does - so exactly one chip is always checked, as a radio group should be.
   */
  activeLayout(): LayoutChoice {
    if (this.host.view() === 'webtoon') return 'webtoon';
    if (this.host.viewPref() === 'auto') return 'auto';
    if (this.host.view() === 'spread') return this.host.coverIsStandalone() ? 'spread-cover' : 'spread';
    return 'paged';
  }

  pickLayout(choice: LayoutChoice): void {
    switch (choice) {
      case 'spread': this.host.chooseSpread(false); break;
      case 'spread-cover': this.host.chooseSpread(true); break;
      default: this.host.chooseView(choice);
    }
  }

  /** Chapter navigation replaces the page, so the sheet closes first. */
  chapter(which: 'prev' | 'next'): void {
    this.ref.dismiss();
    which === 'next' ? this.host.nextChapter() : this.host.prevChapter();
  }

  help(): void {
    this.ref.dismiss();
    this.host.toggleHelp();
  }

  close(): void { this.ref.dismiss(); }
}
