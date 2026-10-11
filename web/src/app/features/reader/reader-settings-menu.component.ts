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
import { availabilityFor } from './upscale-engine';

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

/**
 * A selectable option: its value, label and the glyph it keeps even when selected. `menuLabel` is a longer label
 * for the bar's menus, where a row has room for it (the phone sheet's chips use `label`).
 */
export interface ReaderOption<T> { value: T; label: string; icon: string; menuLabel?: string }

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
 * Display upscaling choices, for pages painted LARGER than their natural size:
 * `smooth` is the browser's own resampling (the pre-1.19.0 picture), `sharp`
 * (1.25.0) AMD FSR 1 on WebGL2 - cheap, and it works over plain HTTP - and
 * `enhance` (1.19.0) the Anime4K line-art upscaler, on WebGPU or else WebGL2.
 * ONE short list: the engine in use is a small line under the selected option,
 * and an option that cannot run here is disabled with its reason
 * (`renderingRows`, from `UpscaleSupportService` / `upscale-engine.ts`).
 */
export const UPSCALER_OPTIONS: readonly ReaderOption<Upscaler>[] = [
  { value: 'smooth', label: 'Smooth', icon: 'blur_on' },
  { value: 'sharp', label: 'Crisp', icon: 'deblur' },
  { value: 'enhance', label: 'Enhance', icon: 'auto_fix_high' },
];

/** One Upscaling option as the menu and the sheet draw it. */
export interface RenderingRow extends ReaderOption<Upscaler> {
  /** Cannot run here (or WebGPU still being probed): offered, not selectable. */
  readonly disabled: boolean;
  /** The choice actually on screen (Smooth when the saved one cannot run). */
  readonly selected: boolean;
  /** The engine line under the selected option, or why a disabled one cannot run. */
  readonly note: string | null;
}

/** The Upscaling list for this device (shared by the desktop menu and the phone sheet). */
export function renderingRows(support: UpscaleSupportService): RenderingRow[] {
  const caps = support.caps();
  const active = support.effective();
  return UPSCALER_OPTIONS.map((opt) => {
    const a = availabilityFor(opt.value, caps);
    const selected = active === opt.value;
    const note = a.state === 'unavailable' ? a.reason
      : a.state === 'checking' ? 'Checking WebGPU…'
        : selected ? a.note : null;
    return { ...opt, disabled: a.state !== 'ready', selected, note };
  });
}

/**
 * Which Anime4K network Enhance runs (1.24.0): `balanced` is the light M chain
 * (the default everywhere), `max` the heavy VL chain paged Enhance used before.
 * The vertical (webtoon) view always runs Efficient, so the group is hidden there
 * (owner, 1.24.0); the stored choice is untouched and applies again in paged views.
 */
export const ENHANCE_QUALITY_OPTIONS: readonly ReaderOption<EnhanceQuality>[] = [
  { value: 'balanced', label: 'Efficient', icon: 'balance' },
  { value: 'max', label: 'Max quality', icon: 'diamond' },
];

/**
 * One line under the Enhance quality group (shared by the menu and the phone
 * sheet). On WebGL2 only Efficient runs (1.25.0), so that is what it says.
 */
export function enhanceQualityHint(quality: EnhanceQuality, webgl2 = false, secure = true): string {
  // Anime4K on WebGL2 runs Efficient only; name what would unlock Max quality.
  if (webgl2) return secure
    ? 'Max quality needs WebGPU, which this browser lacks; Efficient runs here'
    : 'Max quality needs WebGPU, which needs HTTPS; Efficient runs here';
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
 * per-item override, never a persisted device preference. Shared by the phone
 * sheet's Layout chips and the bar's Display menu (1.41.0).
 */
export type LayoutChoice = ViewPref | 'spread-shifted' | 'webtoon';
export const LAYOUT_OPTIONS: readonly ReaderOption<LayoutChoice>[] = [
  { value: 'auto', label: 'Auto', icon: 'screen_rotation', menuLabel: 'Auto (orientation)' },
  { value: 'paged', label: 'Single page', icon: 'crop_portrait' },
  { value: 'spread', label: 'Double page', icon: 'import_contacts' },
  { value: 'spread-shifted', label: 'Double, shifted', icon: 'auto_stories', menuLabel: 'Double page (shifted)' },
  { value: 'webtoon', label: 'Vertical', icon: 'view_day', menuLabel: 'Vertical (webtoon)' },
];

/** What the layout radio group reads from, and dispatches to, the reader. */
export type LayoutHost = Pick<ReaderOptionsHost, 'view' | 'viewPref' | 'spreadShifted' | 'chooseView' | 'chooseSpread'>;

/**
 * The layout option to highlight: the EFFECTIVE layout on screen. Coincides with
 * the stored device preference whenever one exists ('auto' wins over the
 * paged/spread it resolved to), and with the server-resolved view when none
 * does - so exactly one option is always checked, as a radio group should be.
 */
export function activeLayout(host: LayoutHost): LayoutChoice {
  if (host.view() === 'webtoon') return 'webtoon';
  if (host.viewPref() === 'auto') return 'auto';
  if (host.view() === 'spread') return host.spreadShifted() ? 'spread-shifted' : 'spread';
  return 'paged';
}

/** Apply a layout pick exactly as the reader's own entry points do. */
export function pickLayout(host: LayoutHost, choice: LayoutChoice): void {
  switch (choice) {
    case 'spread': host.chooseSpread(false); break;
    case 'spread-shifted': host.chooseSpread(true); break;
    default: host.chooseView(choice);
  }
}

/**
 * The overlay styles every reader menu shares (the bar's Display, Quality and Bookmark menus). The panels render in a
 * CDK overlay, so the rules are scoped via the `reader-options-menu` panel class and reach the projected items with
 * ::ng-deep; each menu component lists this one constant, so the look cannot drift between them.
 *  - Selected state (1.10.0, F4): the accent highlight instead of a checkmark, same values as the 1.8.1 View menu.
 *  - Group captions + one-line hints (1.19.0), the engine / reason line under an Upscaling option (1.25.0).
 *  - The Page width slider row of the Display menu in the vertical view (1.41.0).
 */
export const READER_MENU_STYLES = `
  ::ng-deep .reader-options-menu .selected-option { background: rgb(var(--mp-accent-strong-rgb) / 0.16); }
  ::ng-deep .reader-options-menu .selected-option,
  ::ng-deep .reader-options-menu .selected-option .mat-icon { color: var(--mp-accent); }
  ::ng-deep .reader-options-menu .menu-group-label {
    display: flex; justify-content: space-between; gap: 12px;
    padding: 8px 16px 2px; font-size: 11px; font-weight: 600;
    letter-spacing: 0.5px; text-transform: uppercase; opacity: 0.6;
  }
  ::ng-deep .reader-options-menu .menu-group-label .value { text-transform: none; letter-spacing: 0; font-variant-numeric: tabular-nums; }
  ::ng-deep .reader-options-menu .menu-hint { padding: 0 16px 6px; font-size: 11px; opacity: 0.6; max-width: 220px; }
  ::ng-deep .reader-options-menu .hint-tap {
    display: block; background: none; border: 0; color: inherit; font-family: inherit;
    text-align: left; cursor: default;
  }
  ::ng-deep .reader-options-menu .option-text { display: flex; flex-direction: column; line-height: 18px; }
  ::ng-deep .reader-options-menu .option-note { font-size: 11px; line-height: 14px; opacity: 0.7; white-space: normal; max-width: 200px; }
  ::ng-deep .reader-options-menu .mat-mdc-slider.menu-slider { display: block; width: auto; min-width: 168px; margin: 0 16px 4px; }
  ::ng-deep .reader-options-menu .menu-divider { height: 1px; margin: 4px 0; background: var(--mat-sys-outline-variant); }
`;

/**
 * The bar's Quality menu (1.41.0, owner decision 5; the 1.19.0 "Upscaling" menu, moved): how an UPSCALED page is
 * resampled (Upscaling, plus Enhance quality while Enhance runs), how many pixels are fetched per page (Page quality)
 * and how the server shrinks them (Downscale filter). One trigger, present in every view. The trigger's tooltip
 * doubles as the WebGPU status readout, which is the only way to confirm the GPU path on a real device (it cannot be
 * exercised in jsdom or on a headless box). The phone sheet's Quality section shows the same choices.
 *
 * Like the reader's other menus, the active option is marked with the accent COLOR HIGHLIGHT (1.10.0, F4) rather
 * than a checkmark, so every option keeps its own glyph; each item is a `menuitemradio` carrying `aria-checked`.
 * `opened` / `closed` are surfaced so the reader can pin its auto-hiding chrome while the menu is open.
 */
@Component({
  selector: 'app-reader-quality-menu',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    <button mat-icon-button [matMenuTriggerFor]="qualityMenu"
            [matTooltip]="'Quality - ' + support.statusText()" aria-label="Quality"
            (menuOpened)="opened.emit()" (menuClosed)="closed.emit()">
      <mat-icon>auto_fix_high</mat-icon>
    </button>
    <mat-menu #qualityMenu="matMenu" class="reader-options-menu">
      <div role="group" aria-label="Upscaling">
        <div class="menu-group-label">Upscaling</div>
        <!-- 1.25.0: a small second line names the engine under the selected
             option, or why a disabled option cannot run on this device. -->
        @for (row of renderingRows(); track row.value) {
          <button mat-menu-item role="menuitemradio"
                  [disabled]="row.disabled"
                  [class.selected-option]="row.selected"
                  [attr.aria-checked]="row.selected"
                  (click)="chooseUpscaler(row.value)"
                  [attr.aria-label]="'Upscaling: ' + row.label + (row.note ? ' - ' + row.note : '')">
            <mat-icon>{{ row.icon }}</mat-icon>
            <span class="option-text">{{ row.label }}@if (row.note) {<span class="option-note">{{ row.note }}</span>}</span>
          </button>
        }
        <!-- The status line is a text-styled button: tapping it 5 times
             toggles the device-verification timing readout (off by default). -->
        <button type="button" class="menu-hint hint-tap"
                (click)="$event.stopPropagation(); support.tapStats()">{{ renderingHint() }}</button>
      </div>
      <!-- Enhance quality is a sub-choice of Enhance (shown only while Enhance
           is the Upscaling on screen) and applies to the paged views only:
           vertical always runs Efficient, so the choice is hidden there (not
           explained). On WebGL2 Max quality is disabled (WebGPU only). -->
      @if (showEnhanceQuality()) {
        <div role="group" aria-label="Enhance quality">
          <div class="menu-group-label">Enhance quality</div>
          @for (opt of enhanceQualityOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [disabled]="qualityDisabled(opt.value)"
                    [class.selected-option]="activeQuality() === opt.value"
                    [attr.aria-checked]="activeQuality() === opt.value"
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
  styles: [READER_MENU_STYLES],
})
export class ReaderQualityMenuComponent {
  readonly prefs = inject(ReaderPreferencesService);
  readonly support = inject(UpscaleSupportService);
  readonly upscalerOptions = UPSCALER_OPTIONS;
  readonly enhanceQualityOptions = ENHANCE_QUALITY_OPTIONS;
  readonly pageQualityOptions = PAGE_QUALITY_OPTIONS;
  readonly downscaleFilterOptions = DOWNSCALE_FILTER_OPTIONS;
  readonly filterOptionHint = filterOptionHint;

  /** The reader's current view: Enhance quality is a paged-view choice. */
  readonly view = input<ReaderView>('paged');

  /** Emitted when the menu opens / closes, so the reader can pin chrome. */
  readonly opened = output<void>();
  readonly closed = output<void>();

  /**
   * The Upscaling list: Smooth / Crisp / Enhance, each disabled with its reason
   * when it cannot run on this device, the selected one with its engine line
   * (1.25.0). Disabled-with-a-reason beats an option that does nothing. Every
   * view is covered since 1.24.0 (webtoon through the banded renderer).
   */
  readonly renderingRows = computed(() => renderingRows(this.support));

  /** Enhance cannot run here (or WebGPU is still being probed). */
  readonly enhanceDisabled = computed<boolean>(() => this.support.enhance().state !== 'ready');

  /** The status line under the Upscaling group (5 taps toggle the timing readout). */
  readonly renderingHint = computed<string>(() => this.support.statusText(false));  // desktop: the option line names the engine

  /**
   * Enhance quality is a sub-choice of Enhance, and a paged-view choice: the
   * vertical view always runs Efficient.
   */
  readonly showEnhanceQuality = computed<boolean>(() => this.view() !== 'webtoon' && this.support.effective() === 'enhance');

  /** On WebGL2 only Efficient runs; that is the one highlighted there. */
  readonly activeQuality = computed<EnhanceQuality>(() =>
    this.support.enhanceEngine() === 'webgl2' ? 'balanced' : this.prefs.enhanceQuality());

  readonly qualityHint = computed<string>(() =>
    enhanceQualityHint(this.prefs.enhanceQuality(), this.support.enhanceEngine() === 'webgl2', this.support.secure()));

  qualityDisabled(quality: EnhanceQuality): boolean {
    return this.enhanceDisabled() || (quality === 'max' && this.support.enhanceEngine() === 'webgl2');
  }

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

  chooseUpscaler(upscaler: Upscaler): void {
    if (availabilityFor(upscaler, this.support.caps()).state !== 'ready') return;
    this.prefs.setUpscaler(upscaler);
  }

  choosePageQuality(quality: PageQuality): void {
    this.prefs.setPageQuality(quality);
  }

  chooseEnhanceQuality(quality: EnhanceQuality): void {
    if (this.qualityDisabled(quality)) return;
    this.prefs.setEnhanceQuality(quality);
  }

  chooseDownscaleFilter(filter: DownscaleFilter): void {
    if (this.filterDisabled()) return;
    this.prefs.setDownscaleFilter(filter);
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
  /** Is the page on screen bookmarked (1.27.0 phone bookmark / favorite row)? */
  readonly isCurrentPageBookmarked: Signal<boolean>;
  /** Is the open archive a favorite? */
  readonly currentFavorite: Signal<boolean>;
  /**
   * The series page the open archive belongs to (1.41.0): the anchor node id when the archive resolves to series
   * information (the rule of browse's Series info button), otherwise null - and then no Series info control shows.
   */
  readonly seriesInfoAnchor: Signal<string | null>;
  chooseView(pref: ViewPref | 'webtoon'): void;
  chooseSpread(shifted: boolean): void;
  setFitMode(mode: FitMode): void;
  setDirection(direction: ReadingDirection): void;
  setWebtoonWidth(pct: number): void;
  prevChapter(): void;
  nextChapter(): void;
  toggleHelp(): void;
  /** The desktop bar's handlers, reused by the phone sheet (1.27.0). */
  toggleBookmark(): void;
  openBookmarks(): void;
  toggleFavorite(): void;
  /** Leave the reader for the series page (1.41.0); the reader saves the page on the way out. */
  openSeriesInfo(): void;
}

/**
 * Phone reader options (1.10.0, finding F2). On a handset-width screen the
 * reader bar packed ~8 icon buttons into ~375px, so the last ones were clipped
 * off-screen. The bar now keeps only the two actions a reader reaches for while
 * actually reading (Next archive, Fullscreen) plus one "more" trigger, and the
 * rest live here, in a bottom sheet - the established mobile home for reader
 * settings (Mihon/Tachiyomi, Kindle, Apple Books) and the region of the screen
 * the thumb can reach.
 *
 * Design:
 *  - 1.41.0: the sheet follows the bar's grouping - the action tiles (Bookmark page / Bookmarks / Favorite / Series
 *    info, the last only when the archive has series information), then a Display section (Layout, Image fit or
 *    Page width, Reading direction, Page transition or Tap to scroll) and a Quality section (Upscaling, Enhance
 *    quality, Page quality, Downscale filter), then the archive buttons and help.
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

      <!-- 1.27.0: the desktop bar's Bookmark this page / Bookmarks / Favorite, which the
           phone bar has no room for. Same handlers and screen-reader labels as desktop;
           the toggles show their state with the filled glyph and the accent highlight. -->
      <div class="action-row">
        <button type="button" class="chip action" (click)="host.toggleBookmark()"
                [class.selected]="host.isCurrentPageBookmarked()"
                [attr.aria-pressed]="host.isCurrentPageBookmarked()"
                [attr.aria-label]="host.isCurrentPageBookmarked() ? 'Remove bookmark from this page' : 'Bookmark this page'">
          <mat-icon aria-hidden="true">{{ host.isCurrentPageBookmarked() ? 'bookmark' : 'bookmark_border' }}</mat-icon>
          Bookmark page
        </button>
        <button type="button" class="chip action" (click)="bookmarks()" aria-label="Bookmarks" aria-haspopup="dialog">
          <mat-icon aria-hidden="true">bookmarks</mat-icon>
          Bookmarks
        </button>
        <button type="button" class="chip action" (click)="host.toggleFavorite()"
                [class.selected]="host.currentFavorite()"
                [attr.aria-pressed]="host.currentFavorite()"
                [attr.aria-label]="host.currentFavorite() ? 'Remove from favorites' : 'Add to favorites'">
          <mat-icon aria-hidden="true">{{ host.currentFavorite() ? 'star' : 'star_border' }}</mat-icon>
          Favorite
        </button>
        @if (host.seriesInfoAnchor()) {
          <!-- 1.41.0: the bar's Series info, when the archive resolves to series information. -->
          <button type="button" class="chip action" (click)="seriesInfo()" aria-label="Series info">
            <mat-icon aria-hidden="true">info_outline</mat-icon>
            Series info
          </button>
        }
      </div>

      <!-- 1.41.0: the same two sections as the bar's Display and Quality menus. -->
      <h3 class="section-label" id="reader-options-display">Display</h3>

      <section class="group">
        <h4 class="group-label" id="reader-options-layout">Layout</h4>
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
          <h4 class="group-label" id="reader-options-width">
            Page width <span class="value">{{ host.webtoonWidthPct() }}%</span>
          </h4>
          <mat-slider class="width-slider" min="15" max="100" step="5">
            <input matSliderThumb [value]="host.webtoonWidthPct()"
                   (valueChange)="host.setWebtoonWidth($event)" aria-labelledby="reader-options-width">
          </mat-slider>
        </section>
        <section class="group">
          <h4 class="group-label" id="reader-options-tap">Tap to scroll</h4>
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
          <h4 class="group-label" id="reader-options-fit">Image fit</h4>
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
          <h4 class="group-label" id="reader-options-direction">Reading direction</h4>
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
          <h4 class="group-label" id="reader-options-transition">Page transition</h4>
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

      <h3 class="section-label" id="reader-options-quality-section">Quality</h3>
      <!-- 1.19.0 image scaling, shown in every view: Upscaling is how an UPSCALED
           page is resampled, Page quality is how many pixels are fetched. Since
           1.25.0 Upscaling is Smooth / Crisp / Enhance; a choice that cannot run
           here is disabled, and the line under the chips names the engine in use
           and each reason. Since 1.24.0 it covers the vertical view too. -->
      <section class="group">
        <h4 class="group-label" id="reader-options-rendering">Upscaling</h4>
        <div class="chips" role="radiogroup" aria-labelledby="reader-options-rendering">
          @for (row of renderingRows(); track row.value) {
            <button type="button" class="chip" role="radio"
                    [disabled]="row.disabled"
                    [class.selected]="row.selected"
                    [attr.aria-checked]="row.selected"
                    (click)="pickUpscaler(row.value)">
              <mat-icon aria-hidden="true">{{ row.icon }}</mat-icon>{{ row.label }}
            </button>
          }
        </div>
        @if (renderingNotes()) { <p class="group-hint rendering-notes">{{ renderingNotes() }}</p> }
        <!-- Text-styled button: 5 taps toggle the GPU timing readout (off by default). -->
        <button type="button" class="group-hint hint-tap" (click)="support.tapStats()">{{ renderingHint() }}</button>
      </section>
      <!-- Paged views only, as in the desktop menu. -->
      @if (showEnhanceQuality()) {
        <section class="group">
          <h4 class="group-label" id="reader-options-enhance-quality">Enhance quality</h4>
          <div class="chips" role="radiogroup" aria-labelledby="reader-options-enhance-quality">
            @for (opt of enhanceQualityOptions; track opt.value) {
              <button type="button" class="chip" role="radio"
                      [disabled]="qualityDisabled(opt.value)"
                      [class.selected]="activeQuality() === opt.value"
                      [attr.aria-checked]="activeQuality() === opt.value"
                      (click)="pickEnhanceQuality(opt.value)">
                <mat-icon aria-hidden="true">{{ opt.icon }}</mat-icon>{{ opt.label }}
              </button>
            }
          </div>
          <p class="group-hint">{{ qualityHint() }}</p>
        </section>
      }
      <section class="group">
        <h4 class="group-label" id="reader-options-quality">Page quality</h4>
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
        <h4 class="group-label" id="reader-options-filter">Downscale filter</h4>
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
                [attr.aria-label]="host.prevNeighbor() ? 'Previous archive: ' + host.prevNeighbor()!.displayName : 'No previous archive'">
          <mat-icon>skip_previous</mat-icon> Previous archive
        </button>
        <button mat-stroked-button class="chapter" (click)="chapter('next')" [disabled]="!host.hasNextChapter()"
                [attr.aria-label]="host.nextNeighbor() ? 'Next archive: ' + host.nextNeighbor()!.displayName : 'No next archive'">
          Next archive <mat-icon iconPositionEnd>skip_next</mat-icon>
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
      background: var(--mat-sys-surface-container-high);
      color: var(--mat-sys-on-surface);
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
      background: var(--mat-sys-outline-variant);
    }
    .sheet-head { display: flex; align-items: center; justify-content: space-between; min-height: 40px; }
    .sheet-head h2 { margin: 0; font-size: 17px; font-weight: 500; line-height: 24px; }
    .sheet-head .close { margin-right: -8px; }
    .group { display: flex; flex-direction: column; gap: 8px; }
    /* Section headings (1.41.0): Display / Quality, the names of the bar's two menus. */
    .section-label {
      margin: 6px 0 -4px; padding-top: 10px; border-top: 1px solid var(--mat-sys-outline-variant);
      font-size: 14px; font-weight: 600; line-height: 20px; color: var(--mat-sys-on-surface);
    }
    .group-label {
      display: flex; align-items: baseline; justify-content: space-between; margin: 0;
      font-size: 11px; font-weight: 600; letter-spacing: 0.5px; text-transform: uppercase;
      color: var(--mat-sys-on-surface-variant);
    }
    .group-label .value { font-variant-numeric: tabular-nums; text-transform: none; letter-spacing: 0; font-weight: 500; }
    .chips { display: flex; flex-wrap: wrap; gap: 8px; }
    .note { margin: 0; font-size: 12px; line-height: 16px; color: var(--mat-sys-on-surface-variant); }
    /* Same look as .note, but a separate class: .note is the narrow-portrait
       double-page explanation and a test asserts it is absent everywhere else. */
    .group-hint { margin: 0; font-size: 12px; line-height: 16px; color: var(--mat-sys-on-surface-variant); }
    .hint-tap { display: block; padding: 0; background: none; border: 0; font-family: inherit; text-align: left; cursor: default; }
    /* Chips: 44px touch targets, the option's own glyph, and the accent highlight
       (not a tick) for the selected one. */
    .chip {
      display: inline-flex; align-items: center; gap: 6px;
      min-height: 44px; padding: 0 14px 0 12px; border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: transparent; color: inherit; cursor: pointer;
      font: inherit; font-size: 14px; font-weight: 500; line-height: 20px;
      -webkit-tap-highlight-color: transparent;
      transition: background-color .15s ease, border-color .15s ease, color .15s ease;
    }
    .chip mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .chip.selected { background: var(--mp-accent-bg); color: var(--mp-accent); border-color: transparent; }
    .chip:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 2px; }
    .chip:disabled { opacity: 0.4; cursor: default; }
    @media (hover: hover) {
      .chip:hover:not(.selected) { background: var(--mat-sys-surface-container-highest); }
    }
    .width-slider { width: 100%; margin: 0; }
    /* Three equal icon-over-label tiles: fits a 320px screen without wrapping. */
    .action-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(64px, 1fr)); gap: 8px; }
    .chip.action { flex-direction: column; justify-content: center; gap: 2px; min-height: 56px; padding: 6px 4px; font-size: 13px; line-height: 16px; text-align: center; }
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

  /** Same list as the desktop menu. */
  readonly renderingRows = computed(() => renderingRows(this.support));

  /**
   * Chips have no room for a second line, so the reason each disabled option
   * cannot run is one line under them ("Enhance: Needs a secure connection
   * (HTTPS)"); the engine in use is the status line below it.
   */
  readonly renderingNotes = computed<string>(() =>
    this.renderingRows().filter((r) => r.disabled && r.note).map((r) => `${r.label}: ${r.note}`).join(' · '));

  /** Same rule as the desktop menu. */
  readonly enhanceDisabled = computed<boolean>(() => this.support.enhance().state !== 'ready');

  readonly renderingHint = computed<string>(() => this.support.statusText());

  /** Same rule as the desktop menu: Enhance on screen, and hidden in the vertical view. */
  readonly showEnhanceQuality = computed<boolean>(() => this.host.view() !== 'webtoon' && this.support.effective() === 'enhance');

  readonly activeQuality = computed<EnhanceQuality>(() =>
    this.support.enhanceEngine() === 'webgl2' ? 'balanced' : this.prefs.enhanceQuality());

  readonly qualityHint = computed<string>(() =>
    enhanceQualityHint(this.prefs.enhanceQuality(), this.support.enhanceEngine() === 'webgl2', this.support.secure()));

  qualityDisabled(quality: EnhanceQuality): boolean {
    return this.enhanceDisabled() || (quality === 'max' && this.support.enhanceEngine() === 'webgl2');
  }

  /** Same rule as the desktop menu: only a sized (Auto) request can be filtered. */
  readonly filterDisabled = computed<boolean>(() => this.prefs.pageQuality() === 'full');

  readonly filterHint = computed<string>(() => {
    if (this.filterDisabled()) return 'Applies to Auto page quality';
    return this.filterOptionHint(this.prefs.downscaleFilter());
  });

  pickUpscaler(upscaler: Upscaler): void {
    if (availabilityFor(upscaler, this.support.caps()).state !== 'ready') return;
    this.prefs.setUpscaler(upscaler);
  }

  pickEnhanceQuality(quality: EnhanceQuality): void {
    if (this.qualityDisabled(quality)) return;
    this.prefs.setEnhanceQuality(quality);
  }

  pickDownscaleFilter(filter: DownscaleFilter): void {
    if (this.filterDisabled()) return;
    this.prefs.setDownscaleFilter(filter);
  }

  /** The layout chip to highlight (the shared rule, see `activeLayout`). */
  activeLayout(): LayoutChoice {
    return activeLayout(this.host);
  }

  pickLayout(choice: LayoutChoice): void {
    pickLayout(this.host, choice);
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

  /** The series page replaces the reader, so the sheet closes first. */
  seriesInfo(): void {
    this.ref.dismiss();
    this.host.openSeriesInfo();
  }

  /**
   * The bookmarks list is a bottom sheet too, and only one sheet shows at a time:
   * open it once this sheet has gone, so the reader's dismiss handling for this
   * one runs first and the chrome stays pinned under the list.
   */
  bookmarks(): void {
    this.ref.afterDismissed().subscribe(() => this.host.openBookmarks());
    this.ref.dismiss();
  }

  close(): void { this.ref.dismiss(); }
}
