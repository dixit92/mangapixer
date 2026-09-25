import { Component, Signal, computed, inject, input, output } from '@angular/core';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSliderModule } from '@angular/material/slider';
import { MatTooltipModule } from '@angular/material/tooltip';

import {
  ReaderPreferencesService, PageAnimation, PageQuality, Upscaler, DownscaleFilter, EnhanceQuality,
} from '../../core/reading/reader-preferences.service';
import { DOWNSCALE_FILTER_OPTIONS, filterOptionHint } from '../../core/reading/downscale-filters';
import { WebtoonNavPreferencesService, WebtoonTapStep } from './webtoon-nav.service';
import { UpscaleSupportService } from './upscale.directive';

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

/**
 * Webtoon tap-to-scroll choices (1.11.0): one radio group covering both the
 * on/off switch and the step, so the setting stays a single row. Off keeps the
 * pre-1.11.0 free-scroll-only reader (a tap anywhere toggles the chrome). The
 * step chips carry no glyph - a percentage needs none, and three identical
 * icons in a row would only add noise.
 */
export const WEBTOON_TAP_STEP_OPTIONS: readonly ReaderOption<WebtoonTapStep>[] = [
  { value: 0, label: 'Off', icon: 'block' },
  { value: 80, label: '80%', icon: '' },
  { value: 90, label: '90%', icon: '' },
  { value: 100, label: '100%', icon: '' },
];

export const FIT_OPTIONS: readonly ReaderOption<FitMode>[] = [
  { value: 'screen', label: 'Fit screen', icon: 'fit_screen' },
  { value: 'width', label: 'Fit width', icon: 'swap_horiz' },
  { value: 'height', label: 'Fit height', icon: 'swap_vert' },
  { value: 'original', label: 'Original size', icon: 'crop_original' },
];

/**
 * Display upscaling choices (1.19.0). `enhance` runs the Anime4K line-art
 * upscaler on the GPU for pages painted LARGER than their natural size; `smooth`
 * is the browser's own resampling, i.e. exactly the pre-1.19.0 picture. Needs
 * WebGPU - where it is missing the option is offered disabled with a reason
 * rather than silently doing nothing (`UpscaleSupportService`).
 */
export const UPSCALER_OPTIONS: readonly ReaderOption<Upscaler>[] = [
  { value: 'smooth', label: 'Smooth', icon: 'blur_on' },
  { value: 'enhance', label: 'Enhance', icon: 'auto_fix_high' },
];

/**
 * Which Anime4K network Enhance runs (1.24.0): `balanced` is the light M chain
 * (the default everywhere), `max` the heavy VL chain paged Enhance used before.
 * The vertical (webtoon) view always runs Balanced, so the group is hidden there
 * (owner, 1.24.0); the stored choice is untouched and applies again in paged views.
 */
export const ENHANCE_QUALITY_OPTIONS: readonly ReaderOption<EnhanceQuality>[] = [
  { value: 'balanced', label: 'Balanced', icon: 'balance' },
  { value: 'max', label: 'Max quality', icon: 'diamond' },
];

/** One line under the Enhance quality group (shared by the menu and the phone sheet). */
export function enhanceQualityHint(quality: EnhanceQuality): string {
  return quality === 'max'
    ? 'Sharpest; more GPU memory and battery (single and double page)'
    : 'Lighter on GPU memory and battery';
}

/**
 * How many pixels to fetch per page (1.19.0). `auto` sizes each request to the
 * screen (`?maxDim=`, server-side Lanczos: fewer bytes AND a sharper downscale);
 * `full` always takes the full-size transcode, the pre-1.19.0 behaviour.
 */
export const PAGE_QUALITY_OPTIONS: readonly ReaderOption<PageQuality>[] = [
  { value: 'auto', label: 'Auto', icon: 'tune' },
  { value: 'full', label: 'Full', icon: 'high_quality' },
];

// DOWNSCALE_FILTER_OPTIONS re-exported from downscale-filters.ts (single source of truth)
export { DOWNSCALE_FILTER_OPTIONS };

export const DIRECTION_OPTIONS: readonly ReaderOption<ReadingDirection>[] = [
  { value: 'ltr', label: 'Left to right', icon: 'format_textdirection_l_to_r' },
  { value: 'rtl', label: 'Right to left', icon: 'format_textdirection_r_to_l' },
];

/**
 * Layout choices as ONE radio group. 'spread' / 'spread-shifted' are the two
 * double-page pairings (see `ReaderComponent.chooseSpread`; since 1.23.0 they
 * reflect and re-pair the CURRENT spread, saved per archive); 'webtoon' is a
 * per-item override, never a persisted device preference.
 */
export type LayoutChoice = ViewPref | 'spread-shifted' | 'webtoon';
export const LAYOUT_OPTIONS: readonly ReaderOption<LayoutChoice>[] = [
  { value: 'auto', label: 'Auto', icon: 'screen_rotation' },
  { value: 'paged', label: 'Single page', icon: 'crop_portrait' },
  { value: 'spread', label: 'Double page', icon: 'import_contacts' },
  { value: 'spread-shifted', label: 'Double, shifted', icon: 'auto_stories' },
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
 *
 * 1.11.0: the same toolbar slot is contextual. In the webtoon (vertical) view a
 * page transition is meaningless (page changes are scroll-driven), so the button
 * offers the webtoon "Tap to scroll" step instead (Off / 80% / 90% / 100%) -
 * one settings button per view, never two.
 */
@Component({
  selector: 'app-reader-settings-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    @if (view() === 'webtoon') {
      <button mat-icon-button [matMenuTriggerFor]="tapMenu"
              matTooltip="Tap to scroll" aria-label="Tap to scroll"
              (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
        <mat-icon>touch_app</mat-icon>
      </button>
      <mat-menu #tapMenu="matMenu" class="reader-options-menu">
        @for (opt of tapStepOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio"
                  [class.selected-option]="webtoonNav.tapStep() === opt.value"
                  [attr.aria-checked]="webtoonNav.tapStep() === opt.value"
                  (click)="chooseTapStep(opt.value)" [attr.aria-label]="'Tap to scroll: ' + opt.label">
            @if (opt.icon) { <mat-icon>{{ opt.icon }}</mat-icon> }
            {{ opt.label }}
          </button>
        }
      </mat-menu>
    } @else {
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
    }

    <!-- 1.19.0 image scaling. One extra trigger, present in every view, holding the
         two picture-quality decisions: how an UPSCALED page is resampled
         (Rendering) and how many pixels are fetched per page (Page quality). The
         trigger's tooltip doubles as the WebGPU status readout, which is the only
         way to confirm the GPU path on a real device (it cannot be exercised in
         jsdom or on a headless box). -->
    <button mat-icon-button [matMenuTriggerFor]="renderMenu"
            [matTooltip]="'Rendering - ' + support.statusText()" aria-label="Rendering"
            (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
      <mat-icon>auto_fix_high</mat-icon>
    </button>
    <mat-menu #renderMenu="matMenu" class="reader-options-menu">
      <div role="group" aria-label="Rendering">
        <div class="menu-group-label">Rendering</div>
        @for (opt of upscalerOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio"
                  [disabled]="opt.value === 'enhance' && enhanceDisabled()"
                  [class.selected-option]="prefs.upscaler() === opt.value"
                  [attr.aria-checked]="prefs.upscaler() === opt.value"
                  (click)="chooseUpscaler(opt.value)" [attr.aria-label]="'Rendering: ' + opt.label">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        <!-- The status line is a text-styled button: tapping it 5 times
             toggles the device-verification timing readout (off by default). -->
        <button type="button" class="menu-hint hint-tap"
                (click)="$event.stopPropagation(); support.tapStats()">{{ renderingHint() }}</button>
      </div>
      <!-- Enhance quality applies to the paged views only: vertical always runs
           Balanced, so the choice is hidden there (not explained). -->
      @if (showEnhanceQuality()) {
        <div role="group" aria-label="Enhance quality">
          <div class="menu-group-label">Enhance quality</div>
          @for (opt of enhanceQualityOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [disabled]="enhanceDisabled()"
                    [class.selected-option]="prefs.enhanceQuality() === opt.value"
                    [attr.aria-checked]="prefs.enhanceQuality() === opt.value"
                    (click)="chooseEnhanceQuality(opt.value)" [attr.aria-label]="'Enhance quality: ' + opt.label">
              <mat-icon>{{ opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <div class="menu-hint">{{ qualityHint() }}</div>
        </div>
      }
      <div role="group" aria-label="Page quality">
        <div class="menu-group-label">Page quality</div>
        @for (opt of pageQualityOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio"
                  [class.selected-option]="prefs.pageQuality() === opt.value"
                  [attr.aria-checked]="prefs.pageQuality() === opt.value"
                  (click)="choosePageQuality(opt.value)" [attr.aria-label]="'Page quality: ' + opt.label">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
      </div>
      <div role="group" aria-label="Downscale filter">
        <div class="menu-group-label">Downscale filter</div>
        @for (opt of downscaleFilterOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio"
                  [disabled]="filterDisabled()"
                  [class.selected-option]="prefs.downscaleFilter() === opt.value"
                  [attr.aria-checked]="prefs.downscaleFilter() === opt.value"
                  (click)="chooseDownscaleFilter(opt.value)"
                  [matTooltip]="filterOptionHint(opt.value)" matTooltipPosition="right"
                  [attr.aria-label]="'Downscale filter: ' + opt.label">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        <div class="menu-hint">{{ filterHint() }}</div>
      </div>
    </mat-menu>
  `,
  styles: [`
    /* Selected-state highlight (1.10.0): the panel renders in a CDK overlay, so
       the rule is scoped via the reader-options-menu panel class and reaches the
       projected items with ::ng-deep. Same values as the 1.8.1 View menu. */
    ::ng-deep .reader-options-menu .selected-option { background: rgba(124, 77, 255, 0.16); }
    ::ng-deep .reader-options-menu .selected-option,
    ::ng-deep .reader-options-menu .selected-option .mat-icon { color: #b39dff; }
    /* 1.19.0: the Rendering menu carries two radio groups, so each needs a small
       caption, plus a one-line hint for why Enhance may be unavailable. */
    ::ng-deep .reader-options-menu .menu-group-label {
      padding: 8px 16px 2px; font-size: 11px; font-weight: 600;
      letter-spacing: 0.5px; text-transform: uppercase; opacity: 0.6;
    }
    ::ng-deep .reader-options-menu .menu-hint { padding: 0 16px 6px; font-size: 11px; opacity: 0.6; max-width: 220px; }
    ::ng-deep .reader-options-menu .hint-tap {
      display: block; background: none; border: 0; color: inherit; font-family: inherit;
      text-align: left; cursor: default;
    }
  `],
})
export class ReaderSettingsMenuComponent {
  readonly prefs = inject(ReaderPreferencesService);
  readonly webtoonNav = inject(WebtoonNavPreferencesService);
  readonly support = inject(UpscaleSupportService);
  readonly options = PAGE_ANIMATION_OPTIONS;
  readonly tapStepOptions = WEBTOON_TAP_STEP_OPTIONS;
  readonly upscalerOptions = UPSCALER_OPTIONS;
  readonly enhanceQualityOptions = ENHANCE_QUALITY_OPTIONS;
  readonly pageQualityOptions = PAGE_QUALITY_OPTIONS;
  readonly downscaleFilterOptions = DOWNSCALE_FILTER_OPTIONS;
  readonly filterOptionHint = filterOptionHint;

  /** The reader's current view: webtoon swaps the transition menu for tap-to-scroll. */
  readonly view = input<ReaderView>('paged');

  /** Emitted when the transition menu opens / closes, so the reader can pin chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  /**
   * GPU upscaling is offered but not selectable when the platform has no usable
   * WebGPU device. Disabled-with-a-reason beats an option that does nothing. Every
   * view is covered since 1.24.0 (webtoon through the banded renderer).
   */
  readonly enhanceDisabled = computed<boolean>(() => this.support.support() !== 'ready');

  /** One short line under the Rendering group explaining the current state. */
  readonly renderingHint = computed<string>(() => {
    if (this.support.support() !== 'ready') return 'Enhance needs WebGPU';
    return this.support.statusText();
  });

  /** Enhance quality is a paged-view choice: the vertical view always runs Balanced. */
  readonly showEnhanceQuality = computed<boolean>(() => this.view() !== 'webtoon');

  readonly qualityHint = computed<string>(() => enhanceQualityHint(this.prefs.enhanceQuality()));

  /**
   * The Downscale filter only affects a SIZED (`?maxDim=`) request, which only
   * happens under Page quality: Auto (see `page-variant.ts`). Under Full it is
   * offered disabled with a reason, the same treatment as Enhance above.
   */
  readonly filterDisabled = computed<boolean>(() => this.prefs.pageQuality() === 'full');

  /** One short line under the Downscale filter group: why it's disabled, or what the current pick does. */
  readonly filterHint = computed<string>(() => {
    if (this.filterDisabled()) return 'Applies to Auto page quality';
    return this.filterOptionHint(this.prefs.downscaleFilter());
  });

  choose(mode: PageAnimation): void {
    this.prefs.setPageAnimation(mode);
  }

  chooseUpscaler(upscaler: Upscaler): void {
    if (upscaler === 'enhance' && this.enhanceDisabled()) return;
    this.prefs.setUpscaler(upscaler);
  }

  choosePageQuality(quality: PageQuality): void {
    this.prefs.setPageQuality(quality);
  }

  chooseEnhanceQuality(quality: EnhanceQuality): void {
    if (this.enhanceDisabled()) return;
    this.prefs.setEnhanceQuality(quality);
  }

  chooseDownscaleFilter(filter: DownscaleFilter): void {
    if (this.filterDisabled()) return;
    this.prefs.setDownscaleFilter(filter);
  }

  chooseTapStep(step: WebtoonTapStep): void {
    this.webtoonNav.setTapStep(step);
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
  /** Is the spread on screen paired on the shifted parity (1.23.0)? */
  readonly spreadShifted: Signal<boolean>;
  /** Narrow portrait screen: a chosen double page renders as single pages (1.11.0). */
  readonly narrowPortrait: Signal<boolean>;
  readonly fitMode: Signal<FitMode>;
  readonly direction: Signal<ReadingDirection>;
  readonly webtoonWidthPct: Signal<number>;
  readonly hasPrevChapter: Signal<boolean>;
  readonly hasNextChapter: Signal<boolean>;
  readonly prevNeighbor: Signal<{ displayName: string } | null>;
  readonly nextNeighbor: Signal<{ displayName: string } | null>;
  chooseView(pref: ViewPref | 'webtoon'): void;
  chooseSpread(shifted: boolean): void;
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
        @if (host.view() === 'spread' && host.narrowPortrait()) {
          <!-- 1.11.0 adaptive double page: the pick is kept (chip stays checked) but
               this screen shows single pages; say so here rather than with a snackbar
               that would land under the sheet. -->
          <p class="note">Shows one page at a time on this narrow screen; double page returns in landscape.</p>
        }
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
        <section class="group">
          <h3 class="group-label" id="reader-options-tap">Tap to scroll</h3>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-tap">
            @for (opt of tapStepOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [class.selected]="webtoonNav.tapStep() === opt.value"
                      [attr.aria-checked]="webtoonNav.tapStep() === opt.value"
                      (click)="webtoonNav.setTapStep(opt.value)">
                @if (opt.icon) { <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon> }{{ opt.label }}
              </button>
            }
          </div>
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

      <!-- 1.19.0 image scaling, shown in every view: Rendering is how an UPSCALED
           page is resampled, Page quality is how many pixels are fetched. Enhance
           is disabled (with the reason) without WebGPU; since 1.24.0 it covers
           the vertical view too. -->
      <section class="group">
        <h3 class="group-label" id="reader-options-rendering">Rendering</h3>
        <div class="chips" role="radiogroup" aria-labelledby="reader-options-rendering">
          @for (opt of upscalerOptions; track opt.value) {
            <button type="button" class="chip" role="radio"
                    [disabled]="opt.value === 'enhance' && enhanceDisabled()"
                    [class.selected]="prefs.upscaler() === opt.value"
                    [attr.aria-checked]="prefs.upscaler() === opt.value"
                    (click)="pickUpscaler(opt.value)">
              <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
            </button>
          }
        </div>
        <!-- Text-styled button: 5 taps toggle the GPU timing readout (off by default). -->
        <button type="button" class="group-hint hint-tap" (click)="support.tapStats()">{{ renderingHint() }}</button>
      </section>
      <!-- Paged views only, as in the desktop menu. -->
      @if (showEnhanceQuality()) {
        <section class="group">
          <h3 class="group-label" id="reader-options-enhance-quality">Enhance quality</h3>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-enhance-quality">
            @for (opt of enhanceQualityOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [disabled]="enhanceDisabled()"
                      [class.selected]="prefs.enhanceQuality() === opt.value"
                      [attr.aria-checked]="prefs.enhanceQuality() === opt.value"
                      (click)="pickEnhanceQuality(opt.value)">
                <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
              </button>
            }
          </div>
          <p class="group-hint">{{ qualityHint() }}</p>
        </section>
      }
      <section class="group">
        <h3 class="group-label" id="reader-options-quality">Page quality</h3>
        <div class="chips" role="radiogroup" aria-labelledby="reader-options-quality">
          @for (opt of pageQualityOptions; track opt.value) {
            <button type="button" class="chip" role="radio"
                    [class.selected]="prefs.pageQuality() === opt.value"
                    [attr.aria-checked]="prefs.pageQuality() === opt.value"
                    (click)="prefs.setPageQuality(opt.value)">
              <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
            </button>
          }
        </div>
      </section>
      <!-- 1.20.0 downscale filter: only takes effect for a sized (Auto) request,
           so it is offered disabled with a reason under Full - same treatment
           as Enhance above. -->
      <section class="group">
        <h3 class="group-label" id="reader-options-filter">Downscale filter</h3>
        <div class="chips" role="radiogroup" aria-labelledby="reader-options-filter">
          @for (opt of downscaleFilterOptions; track opt.value) {
            <button type="button" class="chip" role="radio"
                    [disabled]="filterDisabled()"
                    [class.selected]="prefs.downscaleFilter() === opt.value"
                    [attr.aria-checked]="prefs.downscaleFilter() === opt.value"
                    (click)="pickDownscaleFilter(opt.value)">
              <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
            </button>
          }
        </div>
        <p class="group-hint">{{ filterHint() }}</p>
      </section>

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
    .note { margin: 0; font-size: 12px; line-height: 16px; color: var(--mat-sys-on-surface-variant, #8a8a99); }
    /* Same look as .note, but a separate class: .note is the narrow-portrait
       double-page explanation and a test asserts it is absent everywhere else. */
    .group-hint { margin: 0; font-size: 12px; line-height: 16px; color: var(--mat-sys-on-surface-variant, #8a8a99); }
    .hint-tap { display: block; padding: 0; background: none; border: 0; font-family: inherit; text-align: left; cursor: default; }
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
    .chip:disabled { opacity: 0.4; cursor: default; }
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
  readonly webtoonNav = inject(WebtoonNavPreferencesService);
  readonly support = inject(UpscaleSupportService);
  private readonly ref = inject<MatBottomSheetRef<ReaderOptionsSheetComponent>>(MatBottomSheetRef);

  readonly layoutOptions = LAYOUT_OPTIONS;
  readonly fitOptions = FIT_OPTIONS;
  readonly directionOptions = DIRECTION_OPTIONS;
  readonly transitionOptions = PAGE_ANIMATION_OPTIONS;
  readonly tapStepOptions = WEBTOON_TAP_STEP_OPTIONS;
  readonly upscalerOptions = UPSCALER_OPTIONS;
  readonly enhanceQualityOptions = ENHANCE_QUALITY_OPTIONS;
  readonly pageQualityOptions = PAGE_QUALITY_OPTIONS;
  readonly downscaleFilterOptions = DOWNSCALE_FILTER_OPTIONS;
  readonly filterOptionHint = filterOptionHint;

  /** Same rule as the desktop menu: no usable WebGPU. */
  readonly enhanceDisabled = computed<boolean>(() => this.support.support() !== 'ready');

  readonly renderingHint = computed<string>(() => {
    if (this.support.support() !== 'ready') return 'Enhance needs WebGPU';
    return this.support.statusText();
  });

  /** Same rule as the desktop menu: hidden in the vertical view. */
  readonly showEnhanceQuality = computed<boolean>(() => this.host.view() !== 'webtoon');

  readonly qualityHint = computed<string>(() => enhanceQualityHint(this.prefs.enhanceQuality()));

  /** Same rule as the desktop menu: only a sized (Auto) request can be filtered. */
  readonly filterDisabled = computed<boolean>(() => this.prefs.pageQuality() === 'full');

  readonly filterHint = computed<string>(() => {
    if (this.filterDisabled()) return 'Applies to Auto page quality';
    return this.filterOptionHint(this.prefs.downscaleFilter());
  });

  pickUpscaler(upscaler: Upscaler): void {
    if (upscaler === 'enhance' && this.enhanceDisabled()) return;
    this.prefs.setUpscaler(upscaler);
  }

  pickEnhanceQuality(quality: EnhanceQuality): void {
    if (this.enhanceDisabled()) return;
    this.prefs.setEnhanceQuality(quality);
  }

  pickDownscaleFilter(filter: DownscaleFilter): void {
    if (this.filterDisabled()) return;
    this.prefs.setDownscaleFilter(filter);
  }

  /**
   * The layout chip to highlight: the EFFECTIVE layout on screen. Coincides with
   * the stored device preference whenever one exists ('auto' wins over the
   * paged/spread it resolved to), and with the server-resolved view when none
   * does - so exactly one chip is always checked, as a radio group should be.
   */
  activeLayout(): LayoutChoice {
    if (this.host.view() === 'webtoon') return 'webtoon';
    if (this.host.viewPref() === 'auto') return 'auto';
    if (this.host.view() === 'spread') return this.host.spreadShifted() ? 'spread-shifted' : 'spread';
    return 'paged';
  }

  pickLayout(choice: LayoutChoice): void {
    switch (choice) {
      case 'spread': this.host.chooseSpread(false); break;
      case 'spread-shifted': this.host.chooseSpread(true); break;
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
