import { Component, inject, signal, computed, effect, untracked, OnInit, OnDestroy, HostListener, ElementRef, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { CommonModule, Location } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { BreakpointObserver, Breakpoints } from '@angular/cdk/layout';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSliderModule } from '@angular/material/slider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';
import { map } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { ReadStateService } from '../../core/reading/read-state.service';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import {
  ReaderSettingsMenuComponent, ReaderOptionsSheetComponent, ReaderOptionsHost,
  ReaderView, ViewPref, FitMode, ReadingDirection, FIT_OPTIONS, DOWNSCALE_FILTER_OPTIONS,
} from './reader-settings-menu.component';
import { BookmarksPanelComponent, BookmarksPanelHost } from './bookmarks-panel.component';
import { ManifestPageEntry, ItemManifest, ItemReadiness, ApiError, ReaderMode, BookmarkDto } from '../../core/api/api-types';
import { targetMaxDim, withMaxDim, VariantFitMode } from './page-variant';
import { UpscaleDirective, UpscaleSupportService } from './upscale.directive';
import { nextUpscaler } from './upscale-engine';
import { WebtoonEnhanceHostDirective, WebtoonUpscaleDirective } from './webtoon-upscale.directive';
import { PageLoadIndicatorComponent, WebtoonPageComponent } from './page-load-state.component';
import {
  WebtoonNavPreferencesService, webtoonTapZone, webtoonScrollTarget, prefersReducedMotion,
} from './webtoon-nav.service';
import { isApplePlatformTouch, isStandaloneDisplay } from './platform';
import { InstallHintService } from '../../shared/install-hint/install-hint.service';
import {
  groupSpreads, fallbackSpreadStarts, normalizeSpreadStarts, isShiftedSpread, shiftSpreadAt, ensureSpreadStart,
} from './spread-layout';

type ReaderPhase = 'preparing' | 'ready' | 'error';
// ReaderView / ViewPref (the per-device paged-layout preference; see the note on
// its definition) / FitMode live with the settings surface in
// reader-settings-menu.component.ts, shared with the phone options sheet.

/**
 * Manifest-first reader (audit defects D3, D14, D36) with paged / double-spread /
 * vertical-webtoon views. All views address pages by the manifest's opaque entry
 * keys, never numeric indices.
 *
 * Reader-view behavior:
 *  1. Fit-to-screen (contain) is the default image fit.
 *  2. Page navigation is via the invisible left/right edge tap zones and the arrow
 *     keys; the on-screen prev/next chevron FABs were removed (2026-09-08) as
 *     redundant with the zones. The edge zones are direction-aware (tap the right
 *     side in RTL to go back), and the Help overlay labels them per direction.
 *  3. The reader controls (mode / fit / direction / fullscreen / nav) stay visible
 *     in fullscreen. (Revised 2026-09-08 per owner feedback: the earlier
 *     hide-in-fullscreen behavior was inconsistent — the Fullscreen API button
 *     hid them but browser-native F11 fullscreen did not, since F11 never fires
 *     `fullscreenchange`. Immersive auto-hide of the chrome on idle is tracked as
 *     a separate proposal in the UI/UX plan and will supersede this.)
 *  4. Every control carries a tooltip AND an aria-label (tooltip is supplementary
 *     so touch devices are not left without an affordance).
 *  5. Double-page reading has two modes (2026-09-08): "Double page" pairs from the
 *     first page (0-1, 2-3…) and "Double page (shifted)" (1.23.0; was "offset
 *     cover") keeps the cover standalone then pairs (1-2, 3-4…). Which a comic needs
 *     can't be inferred, so it's an explicit reader choice.
 *     A wide (landscape) page — typically a pre-stitched two-page spread — is never
 *     paired; it renders solo, full width, in both double modes (see `isWide` /
 *     `computeSpreads`) and restarts the pairing cadence.
 *     1.23.0 shifted pairing: the pairing is a property of the ARCHIVE, saved on the
 *     server and shared by everyone who reads it (`spreadLayout`, forced spread-start
 *     indices; rules in spread-layout.ts). The two entries now mean "pairing at the
 *     CURRENT spread": the highlighted one follows the spread on screen, picking the
 *     other (or `o`) re-pairs from that spread onward, and `d` into double page makes
 *     the page being read start a spread. The device cover setting
 *     (`coverIsStandalone`) is only the fallback for an archive with no saved layout.
 *  6. Immersive chrome (2026-09-08): immersion is fullscreen-only. In fullscreen the
 *     toolbar overlays the page and, with the bottom nav, auto-hides after ~3s idle;
 *     reveal by moving the mouse into the TOP hot-zone, tapping the centre zone
 *     (edges still navigate), or pressing `m`; a hovered toolbar / open menu pins it
 *     visible. Windowed reading keeps the toolbar in normal flow, always visible
 *     (no auto-hide). A very thin progress rail stays pinned to the bottom (RTL
 *     fills from the right). The tap zones are invisible in normal use; a Help
 *     overlay (the '?' toolbar button or key) surfaces them prominently with the
 *     keyboard shortcuts.
 *  7. Webtoon load placeholders (2026-09-08): each vertical page reserves its box
 *     from the manifest's intrinsic aspect ratio before it lazy-loads, so streaming
 *     images don't jerk the scroll position; see `aspectRatioFor`.
 *  8. Auto-advance (2026-09-08): the forward gesture (arrow key / edge tap) on the
 *     last screen loads the next archive in the folder (`nextNeighbor` from the
 *     catalog neighbor endpoint); with no next archive it shows a brief notice.
 *     Applies to paged / spread; webtoon (scroll-driven) is not auto-advanced.
 *  9. Previous-chapter advance (2026-09-09): symmetrically, the backward gesture on
 *     the first screen loads the PREVIOUS archive (`prevNeighbor`) and lands on its
 *     last page (via the `at=end` query param). Merely landing there does not save
 *     progress, so it never falsely completes/marks-read an unread chapter.
 * 10. Phone bar (1.10.0, finding F2): at handset width (`compact`, CDK XSmall,
 *     < 600px) the full icon bar - ~8 controls that no longer fit - collapses to
 *     the two actions a reader reaches for mid-read (Next archive, Fullscreen) plus
 *     a "Reader options" trigger that opens `ReaderOptionsSheetComponent`, a bottom
 *     sheet holding the rest, grouped. Desktop and tablet keep the full bar as is.
 *     Reader menus everywhere mark the active option with the accent highlight
 *     instead of a checkmark (finding F4, matching the 1.8.1 View menu).
 * 11. Webtoon tap-to-scroll (1.11.0): free scroll stays the primary way to read
 *     vertically, but the tap zones readers know from Mihon/Comixor are added on
 *     top - bottom third = forward a screen, top third = back, centre = toggle
 *     chrome - plus a horizontal swipe (left = forward, right = back) moving by
 *     the same step. The step (80/90/100% of the viewport; default 90% so screens
 *     overlap slightly and no panel is skipped) and the Off switch are one
 *     per-device preference (`WebtoonNavPreferencesService`). Never auto-advances
 *     chapters (the 1.7.1 revert stands); the end-of-chapter footer does that.
 * 12. Adaptive double page (1.11.0): a synthetic two-up spread on a narrow portrait
 *     screen (CDK HandsetPortrait, < 600px wide) makes every page tiny, so the
 *     reader renders SINGLE pages there while the chosen mode stays "Double page"
 *     and returns the moment the device is rotated / widened (`effectiveView`).
 *     A wide source page (a stitched spread) is untouched - it never paired anyway.
 * 13. Page-turn ghost (1.11.0): the outgoing page stays on screen underneath for
 *     the duration of the Slide / Reveal transition, so the new page slides over
 *     or wipes across the OLD page instead of over the dark background (which made
 *     the 1.9.1 Reveal read as a dark curtain). See `outgoing`.
 */
@Component({
  selector: 'app-reader',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule,
    MatIconModule,
    MatToolbarModule,
    MatMenuModule,
    MatTooltipModule,
    MatSliderModule,
    MatProgressSpinnerModule,
    MatSnackBarModule,
    ReaderSettingsMenuComponent,
    UpscaleDirective,
    WebtoonEnhanceHostDirective,
    WebtoonPageComponent,
    PageLoadIndicatorComponent,
    WebtoonUpscaleDirective,
    StarToggleComponent,
  ],
  template: `
    <div class="reader-container">
      <mat-toolbar class="reader-toolbar" [class.immersive]="isFullscreen()"
                   [class.chrome-hidden]="!chromeVisible()"
                   (mouseenter)="lockChrome(true)" (mouseleave)="lockChrome(false)">
        <button mat-icon-button (click)="goBack()" matTooltip="Back to folder" aria-label="Back to folder">
          <mat-icon>arrow_back</mat-icon>
        </button>
        <span class="page-info">
          @if (phase() === 'ready') { {{ currentPageIndicator() }} / {{ pageCount() }} }
        </span>
        <span class="spacer"></span>

        <!-- Controls stay visible in fullscreen. -->
        @if (phase() === 'ready' && compact()) {
          <!-- PHONE bar: Next archive + Fullscreen stay where they
               were (first and last of the right-hand group), everything else is
               one tap away in the options sheet. -->
          <button mat-icon-button class="chapter-arrow" [class.direction-mirrored]="direction() === 'rtl'"
                  (click)="nextChapter()" [disabled]="!hasNextChapter()"
                  [matTooltip]="nextNeighbor() ? 'Next archive: ' + nextNeighbor()!.displayName : 'No next archive'"
                  [attr.aria-label]="nextNeighbor() ? 'Next archive: ' + nextNeighbor()!.displayName : 'No next archive'">
            <mat-icon>skip_next</mat-icon>
          </button>
          <button mat-icon-button (click)="toggleFullscreen()"
                  [matTooltip]="isFullscreen() ? 'Exit fullscreen' : 'Fullscreen'"
                  [attr.aria-label]="isFullscreen() ? 'Exit fullscreen' : 'Enter fullscreen'">
            <mat-icon>{{ isFullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon>
          </button>
          <button mat-icon-button class="options-trigger" (click)="openOptions()"
                  matTooltip="Reader options" aria-label="Reader options"
                  aria-haspopup="dialog" [attr.aria-expanded]="optionsOpen()">
            <mat-icon>more_vert</mat-icon>
          </button>
        } @else if (phase() === 'ready') {
          <!-- 1.7.0 reader-bar CHAPTER arrows: move between archives in the folder
               (distinct from page turning); disabled at the ends of the folder.
               1.17.0: the glyph mirrors (CSS transform) and picks up an accent
               tint when the reading direction is RTL, so the arrows read with the
               manga flow instead of always pointing the LTR way — see
               .chapter-arrow.direction-mirrored below. -->
          <button mat-icon-button class="chapter-arrow" [class.direction-mirrored]="direction() === 'rtl'"
                  (click)="prevChapter()" [disabled]="!hasPrevChapter()"
                  [matTooltip]="prevNeighbor() ? 'Previous archive: ' + prevNeighbor()!.displayName : 'No previous archive'"
                  [attr.aria-label]="prevNeighbor() ? 'Previous archive: ' + prevNeighbor()!.displayName : 'No previous archive'">
            <mat-icon>skip_previous</mat-icon>
          </button>
          <button mat-icon-button class="chapter-arrow" [class.direction-mirrored]="direction() === 'rtl'"
                  (click)="nextChapter()" [disabled]="!hasNextChapter()"
                  [matTooltip]="nextNeighbor() ? 'Next archive: ' + nextNeighbor()!.displayName : 'No next archive'"
                  [attr.aria-label]="nextNeighbor() ? 'Next archive: ' + nextNeighbor()!.displayName : 'No next archive'">
            <mat-icon>skip_next</mat-icon>
          </button>
          <button mat-icon-button [matMenuTriggerFor]="modeMenu" matTooltip="Reading mode" aria-label="Reading mode"
                  (menuOpened)="menuOpen.set(true)" (menuClosed)="onMenuClosed()">
            <mat-icon>{{ viewIcon() }}</mat-icon>
          </button>
          <!-- Reader menus (1.10.0, F4): the active option carries the accent
               COLOR HIGHLIGHT (selected-option) and keeps its own glyph; no
               checkmark. Items are menuitemradio + aria-checked for AT. The panel
               class is the ::ng-deep styling hook (CDK overlay). -->
          <mat-menu #modeMenu="matMenu" class="reader-options-menu">
            <button mat-menu-item role="menuitemradio" (click)="chooseView('auto')"
                    [class.selected-option]="viewPref() === 'auto' && view() !== 'webtoon'"
                    [attr.aria-checked]="viewPref() === 'auto' && view() !== 'webtoon'">
              <mat-icon>screen_rotation</mat-icon> Auto (orientation)</button>
            <button mat-menu-item role="menuitemradio" (click)="chooseView('paged')"
                    [class.selected-option]="viewPref() === 'paged' && view() !== 'webtoon'"
                    [attr.aria-checked]="viewPref() === 'paged' && view() !== 'webtoon'">
              <mat-icon>crop_portrait</mat-icon> Single page</button>
            <!-- The two double-page entries reflect the CURRENT spread (1.23.0):
                 "(shifted)" when its pairing runs on the other parity of its run
                 of pages; picking the other one re-pairs from this spread on. -->
            <button mat-menu-item role="menuitemradio" (click)="chooseSpread(false)"
                    [class.selected-option]="viewPref() === 'spread' && !spreadShifted() && view() !== 'webtoon'"
                    [attr.aria-checked]="viewPref() === 'spread' && !spreadShifted() && view() !== 'webtoon'">
              <mat-icon>import_contacts</mat-icon> Double page</button>
            <button mat-menu-item role="menuitemradio" (click)="chooseSpread(true)"
                    [class.selected-option]="viewPref() === 'spread' && spreadShifted() && view() !== 'webtoon'"
                    [attr.aria-checked]="viewPref() === 'spread' && spreadShifted() && view() !== 'webtoon'">
              <mat-icon>auto_stories</mat-icon> Double page (shifted)</button>
            <button mat-menu-item role="menuitemradio" (click)="chooseView('webtoon')"
                    [class.selected-option]="view() === 'webtoon'"
                    [attr.aria-checked]="view() === 'webtoon'">
              <mat-icon>view_day</mat-icon> Vertical (webtoon)</button>
          </mat-menu>

          <!-- In-reader bookmarks (1.17.0): the toggle marks/unmarks the CURRENT
               page (reflects its bookmarked state via isCurrentPageBookmarked),
               the second button opens the panel listing every bookmark on this
               item. Shown in every view (paged/spread/webtoon) — bookmarking a
               page is meaningful regardless of how it turns. -->
          <button mat-icon-button (click)="toggleBookmark()"
                  [matTooltip]="isCurrentPageBookmarked() ? 'Remove bookmark' : 'Bookmark this page'"
                  [attr.aria-label]="isCurrentPageBookmarked() ? 'Remove bookmark from this page' : 'Bookmark this page'"
                  [attr.aria-pressed]="isCurrentPageBookmarked()">
            <mat-icon>{{ isCurrentPageBookmarked() ? 'bookmark' : 'bookmark_border' }}</mat-icon>
          </button>
          <button mat-icon-button (click)="openBookmarks()" matTooltip="Bookmarks" aria-label="Bookmarks"
                  aria-haspopup="dialog">
            <mat-icon>bookmarks</mat-icon>
          </button>
          <!-- Favorite this archive (1.21.0): the reader star targets the currently open
               archive (itemId). Its own component styles keep the reader's near-budget
               inline CSS untouched. -->
          <app-star-toggle [nodeId]="itemId()" [favorite]="currentFavorite()" />

          @if (view() === 'webtoon') {
            <!-- Webtoon width slider replaces the inoperative fit menu. -->
            <mat-icon class="slider-icon" aria-hidden="true">width_normal</mat-icon>
            <mat-slider class="width-slider" min="15" max="100" step="5"
                        matTooltip="Page width" aria-label="Webtoon page width">
              <input matSliderThumb [value]="webtoonWidthPct()"
                     (valueChange)="setWebtoonWidth($event)" aria-label="Webtoon page width">
            </mat-slider>
          } @else {
            <button mat-icon-button [matMenuTriggerFor]="fitMenu" matTooltip="Image fit" aria-label="Image fit"
                    (menuOpened)="menuOpen.set(true)" (menuClosed)="onMenuClosed()">
              <mat-icon>aspect_ratio</mat-icon>
            </button>
            <mat-menu #fitMenu="matMenu" class="reader-options-menu">
              @for (opt of fitOptions; track opt.value) {
                <button mat-menu-item role="menuitemradio" (click)="setFitMode(opt.value)"
                        [class.selected-option]="fitMode() === opt.value"
                        [attr.aria-checked]="fitMode() === opt.value">
                  <mat-icon>{{ opt.icon }}</mat-icon> {{ opt.label }}</button>
              }
            </mat-menu>
          }

          @if (view() !== 'webtoon') {
            <button mat-icon-button (click)="toggleDirection()"
                    [matTooltip]="direction() === 'rtl' ? 'Right-to-left (manga)' : 'Left-to-right'"
                    [attr.aria-label]="direction() === 'rtl' ? 'Switch to left-to-right' : 'Switch to right-to-left'">
              <mat-icon>{{ direction() === 'rtl' ? 'format_textdirection_r_to_l' : 'format_textdirection_l_to_r' }}</mat-icon>
            </button>
          }

          <!-- 1.9.0 page-transition picker (Slide / Reveal / None); in webtoon the
               same slot offers Tap to scroll (1.11.0). A separate component so the
               reader only needs this one-line wiring point; it pins the chrome
               while its menu is open, like the mode/fit menus. -->
          <app-reader-settings-menu [view]="view()"
            (opened)="menuOpen.set(true)" (closed)="onMenuClosed()"></app-reader-settings-menu>

          <button mat-icon-button (click)="toggleHelp()" matTooltip="Reading help" aria-label="Reading help">
            <mat-icon>help_outline</mat-icon>
          </button>
          <button mat-icon-button (click)="toggleFullscreen()"
                  [matTooltip]="isFullscreen() ? 'Exit fullscreen' : 'Fullscreen'"
                  [attr.aria-label]="isFullscreen() ? 'Exit fullscreen' : 'Enter fullscreen'">
            <mat-icon>{{ isFullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon>
          </button>
        }
      </mat-toolbar>

      @if (phase() === 'preparing') {
        <div class="status">
          <mat-spinner diameter="40"></mat-spinner>
          <p>{{ statusMessage() }}</p>
        </div>
      } @else if (phase() === 'error') {
        <div class="status error">
          <mat-icon>error_outline</mat-icon>
          <p>{{ statusMessage() }}</p>
          <button mat-stroked-button (click)="retry()">Try again</button>
        </div>
      } @else if (view() === 'webtoon') {
        <!-- Vertical continuous scroll; progress tracked by scroll position.
             Tap-to-scroll (added 1.11.0): a tap resolves by vertical
             thirds (onWebtoonTap) and a horizontal swipe steps a screen (the shared
             pointer tracking, see onReaderPointerDown). touch-action keeps the
             vertical pan + pinch native and claims only horizontal drags, and only
             while the feature is on and the page is not pinch-zoomed.
             Keyboard (1.23.0 a11y): the scroller is focusable (tabindex 0, never
             focused programmatically) so arrow / Page keys scroll it natively, and
             Enter on it is the keyboard twin of the centre tap (show / hide the
             controls). Tap and scroll behaviour are unchanged.
             Enhance (1.24.0) / Crisp (1.25.0): the host directive lays banded GPU
             canvases over the strip (webtoon-enhance-coordinator.ts); the imgs register.
             Loading feedback (1.24.0): each img sits in an app-webtoon-page that
             veils its box until load, or offers a retry on error (page-load-state.component.ts). -->
        <div class="reader-viewport webtoon" #scroller (scroll)="onWebtoonScroll()"
             [appWebtoonEnhanceHost]="upscaleBackend()"
             [style.touch-action]="webtoonTouchAction()" tabindex="0"
             (pointerdown)="onReaderPointerDown($event)" (click)="onWebtoonTap($event)"
             (keydown.enter)="onWebtoonEnter($event)">
          @for (entry of pages(); track entry.entryKey) {
            <app-webtoon-page [pageNumber]="$index + 1">
              <img class="webtoon-page" appWebtoonUpscale [src]="pageUrlFor(entry)" loading="lazy"
                   [style.width.%]="webtoonWidthPct()"
                   [style.aspect-ratio]="aspectRatioFor(entry)"
                   [attr.data-index]="$index" alt="Page {{ $index + 1 }}" />
            </app-webtoon-page>
          }
          <!-- End-of-chapter affordance: explicit Previous/Next archive buttons
               (1.7.1: webtoon no longer auto-advances on scroll — owner revert).
               Styled inline to stay within the component CSS budget. -->
          <div class="webtoon-end"
               style="width:100%; box-sizing:border-box; display:flex; flex-direction:column;
                      align-items:center; gap:14px; padding:40px 16px 64px; color:#ccc; text-align:center;">
            <button mat-stroked-button (click)="prevChapter()" [disabled]="!hasPrevChapter()"
                    style="min-width:200px;">
              <mat-icon>skip_previous</mat-icon> Previous archive
            </button>
            <p style="margin:0; opacity:0.7; font-size:14px;">
              {{ nextNeighbor() ? 'Tap Next archive to continue' : 'End of this folder' }}
            </p>
            <button mat-flat-button color="primary" (click)="nextChapter()" [disabled]="!hasNextChapter()"
                    style="min-width:200px;">
              Next archive <mat-icon>skip_next</mat-icon>
            </button>
          </div>
        </div>
      } @else {
        <!-- Paged or double-spread: fixed viewport, one screen at a time.
             Full-surface swipe (added 1.8.0): the pointer gesture is
             tracked from a pointerdown here to a DOCUMENT-level up/cancel/move (see
             the host listeners), so a swipe that starts anywhere on the page and ends
             over the toolbar/bar still resolves. The viewport's touch-action is
             computed per page (touchAction): while the page has no horizontal
             overflow we OWN horizontal pans ('pan-y pinch-zoom' / 'pinch-zoom'), which
             is what stops the browser from cancelling the pointer stream on a
             horizontal drag (the 1.7.0 iPad failure) and from starting its own
             horizontal history-swipe; vertical native scroll (fit-width) and
             pinch-zoom are left to the browser. A page that overflows horizontally
             (zoomed/original) falls back to native panning ('auto'), exactly as in
             1.7.0. The spread row follows the finger (swipeDx) for feedback. -->
        <div class="reader-viewport" #viewport
             [style.touch-action]="touchAction()"
             (pointerdown)="onReaderPointerDown($event)">
          @if (pageLoading()) {
            <app-page-load-indicator class="page-spinner" />
          }
          <!-- Page-turn ghost (added 1.11.0): the page(s) just left stay
               rendered UNDER the incoming row for the length of the transition, so
               Slide pushes over and Reveal wipes across the old page rather than the
               dark background. Same layout classes as the live row so both line up;
               inert (pointer-events: none) and hidden from AT. -->
          @if (outgoing().length > 0) {
            <div class="spread-row outgoing" [class.rtl-flow]="direction() === 'rtl'" aria-hidden="true">
              @for (entry of outgoing(); track entry.entryKey) {
                <img [src]="pageUrlFor(entry)"
                  [class.fit-screen]="fitMode() === 'screen'"
                  [class.fit-width]="fitMode() === 'width'"
                  [class.fit-height]="fitMode() === 'height'"
                  [class.original]="fitMode() === 'original'"
                  [class.paired]="outgoing().length > 1"
                  draggable="false" alt="" />
              }
            </div>
          }
          <div class="spread-row" [class.rtl-flow]="direction() === 'rtl'"
               [class.dragging]="swipeDx() !== 0 || committing()"
               [style.transform]="swipeDx() !== 0 ? 'translateX(' + swipeDx() + 'px)' : null">
            @for (entry of currentSpreadEntries(); track entry.entryKey) {
              <!-- 1.9.0 page-turn transition. The <img> is re-created on every page
                   change (track entryKey), so a CSS keyframe on the fresh element
                   plays exactly once per turn — no Angular animations dep. The enter
                   side is reading-direction aware (navEnter); slide translates, reveal wipes.
                   prefers-reduced-motion disables it in CSS regardless of setting. -->
              <img
                [src]="pageUrlFor(entry)"
                [class.fit-screen]="fitMode() === 'screen'"
                [class.fit-width]="fitMode() === 'width'"
                [class.fit-height]="fitMode() === 'height'"
                [class.original]="fitMode() === 'original'"
                [class.paired]="currentSpreadEntries().length > 1"
                [class.anim-slide]="pageAnimActive() && prefs.pageAnimation() === 'slide'"
                [class.anim-reveal]="pageAnimActive() && prefs.pageAnimation() === 'reveal'"
                [class.from-right]="navEnter() === 'from-right'"
                [class.from-left]="navEnter() === 'from-left'"
                [appUpscale]="upscaleActive()"
                (load)="onPageLoaded()"
                (error)="onPageError()"
                draggable="false"
                alt="Page"
              />
            }
          </div>
          <button class="edge prev" (click)="onEdge('prev')" aria-label="Previous" tabindex="-1"></button>
          <button class="edge next" (click)="onEdge('next')" aria-label="Next" tabindex="-1"></button>
          <!-- Center tap zone (Mihon-style): toggle chrome; edges still navigate.
               onCenterTap swallows the ghost click after a swipe. -->
          <button class="tap-toggle" (click)="onCenterTap()" tabindex="-1"
                  aria-label="Show or hide controls"></button>
        </div>
      }

      <!-- Page slider (reworked 1.8.0). The 1.7.0 scrubber was a 16px hit strip
           glued to the very bottom edge of the screen — exactly where iPadOS reserves
           its swipe-up Home/Dock gesture, so touches there were mostly eaten by the
           OS. The slider now lives in a proper bottom BAR that follows the same
           chrome rules as the toolbar (in flow when windowed; overlaid + auto-hidden
           when fullscreen), with a 44px hit height, a visible thumb, the page numbers
           at both ends (mirrored in RTL so the bar reads in reading order), and the
           live page bubble while dragging. Tap = jump, drag = scrub, arrows = step. -->
      @if (phase() === 'ready') {
        <div class="reader-nav" [class.immersive]="isFullscreen()"
             [class.chrome-hidden]="!chromeVisible()" [class.help-lit]="helpVisible()"
             (mouseenter)="lockChrome(true)" (mouseleave)="lockChrome(false)">
          <span class="nav-end" aria-hidden="true">{{ direction() === 'rtl' ? pageCount() : 1 }}</span>
          <div class="scrub" [class.scrubbing]="scrubbing()" [class.rtl]="direction() === 'rtl'"
               (pointerdown)="onScrubStart($event)" (pointermove)="onScrubMove($event)"
               (pointerup)="onScrubEnd($event)" (pointercancel)="onScrubEnd($event)"
               (keydown)="onRailKey($event)"
               role="slider" tabindex="0" aria-label="Page slider (drag or tap to jump to a page)"
               [attr.aria-valuemin]="1" [attr.aria-valuemax]="pageCount()"
               [attr.aria-valuenow]="currentPage() + 1"
               [attr.aria-valuetext]="'Page ' + (currentPage() + 1) + ' of ' + pageCount()">
            <div class="scrub-track" aria-hidden="true">
              <div class="scrub-fill" [style.width.%]="scrubFillPct()"></div>
            </div>
            <div class="scrub-thumb" [style.left]="scrubThumbLeft()" aria-hidden="true"></div>
            @if (scrubbing()) {
              <div class="scrub-bubble" [style.left]="scrubThumbLeft()" aria-hidden="true">
                {{ currentPage() + 1 }} / {{ pageCount() }}
              </div>
            }
          </div>
          <span class="nav-end" aria-hidden="true">{{ direction() === 'rtl' ? 1 : pageCount() }}</span>
        </div>
        <!-- Persistent minimal cue while the chrome is hidden (fullscreen): the thin
             progress rail, now PASSIVE (pointer-events: none) so it never competes
             with the OS bottom-edge gesture. RTL fills from the right. -->
        @if (!chromeVisible()) {
          <div class="progress-rail" [class.rtl]="direction() === 'rtl'" aria-hidden="true">
            <div class="progress-fill" [style.width.%]="progressPct()"></div>
          </div>
        }
      }

      <!-- Help overlay (toggled by the toolbar '?' button or the '?' key): shows the
           otherwise-invisible tap zones prominently plus the keyboard shortcuts.
           A full-size backdrop button dismisses it; zones/panel are click-through. -->
      @if (helpVisible()) {
        <div class="help-overlay" role="dialog" aria-modal="true" aria-label="Reader controls">
          <button class="help-backdrop" (click)="closeHelp()" aria-label="Close help"></button>
          @if (view() !== 'webtoon') {
            <!-- Tap thirds (labels direction-aware) + a full-width swipe legend across
                 the whole surface: swiping works ANYWHERE, not just in the side zones. -->
            <div class="help-zones" aria-hidden="true">
              <div class="help-zone side"><mat-icon>touch_app</mat-icon><span>Tap<br>{{ leftZoneLabel() }}</span></div>
              <div class="help-zone center"><mat-icon>touch_app</mat-icon><span>Tap<br>Show / hide controls</span></div>
              <div class="help-zone side"><mat-icon>touch_app</mat-icon><span>Tap<br>{{ rightZoneLabel() }}</span></div>
            </div>
            <div class="help-swipe" aria-hidden="true">
              <mat-icon>swipe</mat-icon>
              <span>Swipe anywhere on the page<br>
                <b>←</b> {{ swipeLeftLabel() }} &nbsp;·&nbsp; <b>→</b> {{ swipeRightLabel() }}</span>
            </div>
          } @else if (webtoonNav.tapZonesEnabled()) {
            <!-- Webtoon tap-to-scroll (1.11.0): horizontal BANDS by thirds. -->
            <div class="help-zones vertical" aria-hidden="true">
              <div class="help-zone band"><mat-icon>touch_app</mat-icon><span>Tap<br>Back a screen</span></div>
              <div class="help-zone band center"><mat-icon>touch_app</mat-icon><span>Tap<br>Show / hide controls</span></div>
              <div class="help-zone band"><mat-icon>touch_app</mat-icon><span>Tap<br>Forward a screen</span></div>
            </div>
          }
          <div class="help-panel">
            <h3>Reader controls</h3>
            <ul>
              @if (view() !== 'webtoon') {
                <li><b>Swipe</b> left / right anywhere to turn the page. Start inside the page: the very
                  edge of the screen belongs to the browser's back / forward gesture.</li>
                <li><b>Tap</b> the sides to turn a page, the centre to show / hide the controls.</li>
                <li><kbd>←</kbd> <kbd>→</kbd> previous / next page (follows reading direction) ·
                  <kbd>Home</kbd> <kbd>End</kbd> first / last</li>
                @if (narrowPortrait() && view() === 'spread') {
                  <li>Double page shows in landscape or on a wider screen; this narrow portrait
                    screen shows one page at a time.</li>
                }
                <li><kbd>D</kbd> single / double page · <kbd>O</kbd> shift the double-page pairing
                  from this spread (saved for this archive, for everyone)</li>
              } @else if (webtoonNav.tapZonesEnabled()) {
                <li>Scroll freely, or <b>tap</b> the lower part of the page to move forward a screen
                  ({{ webtoonNav.tapStep() }}%), the upper part to go back, the centre to show / hide
                  the controls. <b>Swipe</b> left / right does the same.</li>
              } @else {
                <li>Scroll to read; tap the page to show or hide the controls.</li>
              }
              <li><b>Page slider</b> (bottom bar): tap or drag to jump to any page; the bubble shows
                where you land{{ direction() === 'rtl' ? '. It runs right to left, like the pages' : '' }}.
                @if (isFullscreen()) { Tap the centre to bring it back when it is hidden. }</li>
              <li><kbd>M</kbd> show / hide the controls · <kbd>F</kbd> fullscreen · <kbd>Esc</kbd> exit ·
                <kbd>?</kbd> this help · <kbd>S</kbd> cycle Downscale filter · <kbd>E</kbd> cycle Upscaling:
                Smooth / Crisp / Enhance (the ones this device can run)</li>
              @if (useInPageImmersive) {
                <li><b>Fullscreen</b> goes immersive here (hides the reader's own bars) instead of
                  the browser's fullscreen - on iPhone/iPad, and in this app installed to the home
                  screen{{ isStandalone ? ', where it is already on by default when you open an archive' : '' }}.
                  The button still toggles it off and back on.</li>
              }
            </ul>
            <p class="help-dismiss">Tap anywhere to close</p>
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    .reader-container {
      display: flex; flex-direction: column;
      position: fixed; inset: 0;
      background: #101012; z-index: 1000;
    }
    .reader-toolbar {
      background: #1c1c1f; color: #eee; flex-shrink: 0;
      transition: transform .2s ease, opacity .2s ease;
    }
    /* Immersive (fullscreen only): the toolbar OVERLAYS the viewport so hiding it
       frees the whole screen. Windowed reading keeps it in normal flow above the
       page, always visible. */
    .reader-toolbar.immersive {
      position: absolute; top: 0; left: 0; right: 0; z-index: 1001;
    }
    .reader-toolbar.immersive.chrome-hidden {
      transform: translateY(-100%); opacity: 0; pointer-events: none;
    }
    .page-info { margin-left: 8px; font-variant-numeric: tabular-nums; }
    .spacer { flex: 1 1 auto; }
    /* Chapter arrows (1.17.0): the glyph implies a direction (skip_previous
       points left / skip_next points right), so it is mirrored when the reading
       direction is RTL to match the manga flow, and picks up the same accent
       tint the reader's other "differs from default" indicators use, so the
       mismatch from the LTR default is visible at a glance. */
    .chapter-arrow.direction-mirrored mat-icon { transform: scaleX(-1); }
    .chapter-arrow.direction-mirrored { color: #b39dff; }
    /* Reader menus' selected-state (1.10.0, F4): accent highlight instead of a
       checkmark, same values as the 1.8.1 browse View menu. The panels render in
       a CDK overlay, so the rules are scoped via the reader-options-menu panel
       class and reach the projected items with ::ng-deep. */
    ::ng-deep .reader-options-menu .selected-option { background: rgba(124, 77, 255, 0.16); }
    ::ng-deep .reader-options-menu .selected-option,
    ::ng-deep .reader-options-menu .selected-option .mat-icon { color: #b39dff; }
    .status {
      flex: 1; display: flex; flex-direction: column;
      align-items: center; justify-content: center; gap: 16px;
      color: #ccc; text-align: center; padding: 24px;
    }
    .status.error mat-icon { font-size: 48px; width: 48px; height: 48px; color: #f4756a; }
    .reader-viewport {
      flex: 1; min-height: 0; position: relative; overflow: auto;
      display: flex;
    }
    .page-spinner { position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); z-index: 2; }
    /* The spread row FILLS the viewport so the image's fit percentages resolve
       against the viewport, not the image's own (content) size. 'safe center'
       centers when the image fits but falls back to start when it overflows, so
       fit-width/fit-height/original stay fully scrollable instead of clipping.
       rtl-flow puts the earlier page on the right. */
    .spread-row {
      width: 100%; height: 100%; position: relative; z-index: 1;
      display: flex; align-items: safe center; justify-content: safe center;
    }
    .spread-row.rtl-flow { flex-direction: row-reverse; }
    /* 1.11.0 page-turn ghost: the outgoing row sits UNDER the live row (z 0 vs 1;
       the edge/tap zones, also z 1, follow in DOM order so they stay on top). */
    .spread-row.outgoing { position: absolute; inset: 0; z-index: 0; pointer-events: none; }
    /* flex:0 0 auto stops flexbox from shrinking the image (which would defeat
       fit-height / original and re-break fit-width). */
    .spread-row img { display: block; flex: 0 0 auto; }
    /* Fit-screen (contain) is the default. Unlike max-* sizing —
       which only ever shrinks an oversized page and leaves a small page at its
       native size — giving the image a full-viewport box plus object-fit:contain
       scales BOTH ways, so small pages are enlarged to fill the screen while the
       aspect ratio is preserved. */
    img.fit-screen { width: 100%; height: 100%; object-fit: contain; object-position: center; }
    img.fit-width  { width: 100%;  height: auto; }
    img.fit-height { height: 100%; width: auto; }
    img.original   { max-width: none; max-height: none; }
    /* When two pages are paired, each takes at most half the width. */
    .spread-row.paired img, .spread-row img.paired { max-width: 50%; height: auto; }
    /* Paired + fit-screen: size each page TO ITS CONTENT (height fills, width from
       aspect, capped at half the viewport) so the two pages pack tight against
       each other in the centre like an open book. height:100% still upscales a
       small page; object-fit:contain only kicks in for an unusually wide page,
       letterboxing it inside its half-cell. NB: a fixed width:50% here would make
       object-fit centre each page inside an over-wide cell, opening a gutter down
       the middle — that was the pass-1 double-spread regression. */
    .spread-row.paired img.fit-screen, .spread-row img.paired.fit-screen {
      width: auto; height: 100%; max-width: 50%; object-fit: contain;
    }
    /* Paired + fit-width: single-page fit-width force-fills the full viewport
       width (scaling small pages up, not just capping large ones down) via
       width:100%. Its paired analog force-fills each page's half of the row:
       width:50% (not max-width alone), so a small page is still scaled up to
       its cell instead of sitting at native size. */
    .spread-row.paired img.fit-width, .spread-row img.paired.fit-width {
      width: 50%; height: auto; max-width: 50%;
    }
    /* Paired + fit-height: single-page fit-height force-fills the viewport
       height via height:100% and lets width run free (page may overflow the
       viewport horizontally; the viewport scrolls rather than clipping). In
       a pair that overflow would spill into or past the other page's cell,
       so the paired analog keeps height:100% but caps width at the half-cell
       and falls back to object-fit:contain (letterboxing top/bottom) for an
       unusually wide page, same as the fit-screen paired override above. */
    .spread-row.paired img.fit-height, .spread-row img.paired.fit-height {
      width: auto; height: 100%; max-width: 50%; object-fit: contain;
    }
    /* Paired + original: single-page original is genuinely unscaled
       (max-width/max-height: none) and relies on viewport scroll for any
       overflow. Paired mode still needs the half-cell width cap so the two
       pages don't draw on top of each other, but must NOT force height:auto
       over the image's native height — that's already the default box
       behavior, restated here so the cap doesn't accidentally pick up any
       future height rule from the generic .paired selector. */
    .spread-row.paired img.original, .spread-row img.paired.original {
      max-width: 50%; max-height: none; height: auto;
    }
    /* Webtoon: full-width column, natural vertical scroll. */
    .reader-viewport.webtoon { flex-direction: column; align-items: center; }
    /* Width is driven by the webtoon width slider, 15–100% of viewport. */
    /* height:auto + the per-page aspect-ratio (set inline from the manifest) reserves
       each page's box before it lazy-loads; the faint background makes the reserved
       placeholder visible while the image streams in. */
    .webtoon-page { height: auto; display: block; max-width: 100%; background: rgba(255, 255, 255, 0.04); }
    .width-slider { width: 140px; }
    .slider-icon { opacity: 0.7; margin-right: 2px; }
    .edge {
      position: absolute; top: 0; bottom: 0; width: 30%;
      background: transparent; border: 0; cursor: pointer; padding: 0; z-index: 1;
    }
    .edge.prev { left: 0; }
    .edge.next { right: 0; }
    /* Center tap zone: the ~40% between the 30%-wide edge nav zones. Toggles chrome. */
    .tap-toggle {
      position: absolute; top: 0; bottom: 0; left: 30%; right: 30%;
      background: transparent; border: 0; padding: 0; z-index: 1; cursor: default;
    }
    /* Swipe feedback: the spread row tracks the finger while dragging (no transition)
       and springs back when a drag is released short of a page turn. The viewport
       never selects/drags/callouts its images, so a swipe is never turned into a
       native image drag (desktop) or a long-press callout (iOS). */
    .spread-row { transition: transform .18s ease; will-change: transform; }
    .spread-row.dragging { transition: none; }
    .reader-viewport {
      user-select: none; -webkit-user-select: none; -webkit-touch-callout: none;
      overscroll-behavior: contain;
    }
    /* Bottom bar with the page slider. Same chrome rules as the
       toolbar: in flow when windowed, overlaid + auto-hidden when fullscreen. */
    .reader-nav {
      flex-shrink: 0; display: flex; align-items: center; gap: 6px;
      height: 48px; padding: 0 10px env(safe-area-inset-bottom, 0);
      background: #1c1c1f; color: #ddd; z-index: 1001;
      transition: transform .2s ease, opacity .2s ease;
    }
    .reader-nav.immersive { position: absolute; left: 0; right: 0; bottom: 0; }
    .reader-nav.immersive.chrome-hidden { transform: translateY(100%); opacity: 0; pointer-events: none; }
    /* Help overlay spotlight: keep the bar above the dimmed backdrop. */
    .reader-nav.help-lit { z-index: 1004; box-shadow: 0 0 0 2px #7c4dff; }
    .nav-end { min-width: 2.2em; text-align: center; font-size: 13px; font-variant-numeric: tabular-nums; opacity: .8; }
    /* The slider: a 44px-tall hit area (touch friendly) around a 6px track and a
       22px thumb; touch-action:none so the drag is never taken by native scroll. */
    .scrub {
      position: relative; flex: 1; height: 44px; cursor: pointer;
      touch-action: none; -webkit-tap-highlight-color: transparent;
    }
    .scrub:focus-visible { outline: 2px solid #7c4dff; outline-offset: -2px; border-radius: 6px; }
    .scrub-track {
      position: absolute; left: 11px; right: 11px; top: 19px; height: 6px;
      border-radius: 3px; background: rgba(255, 255, 255, 0.18); overflow: hidden;
      display: flex;
    }
    .scrub.rtl .scrub-track { justify-content: flex-end; }
    .scrub-fill { height: 100%; flex: none; background: #7c4dff; }
    .scrub-thumb {
      position: absolute; top: 11px; width: 22px; height: 22px; margin-left: -11px;
      border-radius: 50%; background: #fff; border: 3px solid #7c4dff;
      box-shadow: 0 1px 4px rgba(0, 0, 0, 0.5); transition: transform .12s ease;
    }
    .scrub:hover .scrub-thumb, .scrub.scrubbing .scrub-thumb { transform: scale(1.2); }
    .scrub-bubble {
      position: absolute; bottom: 42px; transform: translateX(-50%);
      background: #222; color: #fff; border: 1px solid rgba(255, 255, 255, 0.2);
      border-radius: 8px; padding: 6px 12px; font-size: 15px; font-weight: 600;
      font-variant-numeric: tabular-nums; white-space: nowrap; pointer-events: none;
      box-shadow: 0 4px 14px rgba(0, 0, 0, 0.4);
    }
    /* Passive thin progress cue at the bottom edge while the chrome is hidden. */
    .progress-rail {
      position: fixed; left: 0; right: 0; bottom: 0; height: 3px;
      background: rgba(255, 255, 255, 0.14); pointer-events: none; display: flex; z-index: 1002;
    }
    .progress-rail.rtl { justify-content: flex-end; }
    .progress-fill { height: 100%; flex: none; background: #7c4dff; transition: width .2s ease; }
    /* The tap zones carry no visible affordance during reading (no focus ring, no
       tap highlight). They are surfaced deliberately via the Help overlay instead. */
    .edge, .tap-toggle { -webkit-tap-highlight-color: transparent; }
    .edge:focus, .edge:focus-visible,
    .tap-toggle:focus, .tap-toggle:focus-visible { outline: none; }
    /* Help overlay: prominent, dismissible legend of the zones + shortcuts. */
    .help-overlay { position: fixed; inset: 0; z-index: 1003; }
    .help-backdrop {
      position: absolute; inset: 0; z-index: 0;
      background: rgba(0, 0, 0, 0.55); border: 0; padding: 0; cursor: pointer;
    }
    .help-zones {
      position: absolute; inset: 0; z-index: 1; display: flex;
      pointer-events: none; color: #fff;
    }
    .help-zone {
      display: flex; flex-direction: column; align-items: center; justify-content: flex-start;
      gap: 8px; font-weight: 600; text-align: center; padding: 14vh 8px 0; line-height: 1.3;
      border-inline: 1px dashed rgba(255, 255, 255, 0.35);
    }
    .help-zone mat-icon { font-size: 36px; width: 36px; height: 36px; }
    .help-zone.side { flex: 0 0 30%; background: rgba(124, 77, 255, 0.22); }
    .help-zone.center { flex: 0 0 40%; background: rgba(255, 255, 255, 0.10); }
    /* Webtoon tap-to-scroll legend (1.11.0): three horizontal bands by thirds. */
    .help-zones.vertical { flex-direction: column; }
    .help-zone.band {
      flex: 1 1 0; justify-content: center; padding: 0 8px;
      border-inline: 0; border-block: 1px dashed rgba(255, 255, 255, 0.35);
      background: rgba(124, 77, 255, 0.22);
    }
    .help-zone.band.center { background: rgba(255, 255, 255, 0.10); }
    /* Full-width swipe legend across the middle of the surface. */
    .help-swipe {
      position: absolute; left: 6%; right: 6%; top: 38%; z-index: 1; pointer-events: none;
      display: flex; align-items: center; justify-content: center; gap: 14px;
      padding: 12px 18px; border-radius: 12px; color: #fff; font-weight: 600; line-height: 1.4;
      background: rgba(124, 77, 255, 0.55); border: 2px dashed rgba(255, 255, 255, 0.6);
    }
    .help-swipe mat-icon { font-size: 40px; width: 40px; height: 40px; flex: none; }
    .help-panel {
      position: absolute; left: 50%; bottom: 64px; transform: translateX(-50%);
      z-index: 2; pointer-events: none; max-width: min(92vw, 460px); max-height: 46%; overflow: hidden;
      background: rgba(20, 20, 22, 0.94); color: #eee;
      border: 1px solid rgba(255, 255, 255, 0.15); border-radius: 12px; padding: 14px 18px;
    }
    .help-panel h3 { margin: 0 0 8px; }
    .help-panel ul { margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 6px; font-size: 13px; line-height: 1.35; }
    .help-panel kbd {
      background: #333; border: 1px solid #555; border-radius: 4px;
      padding: 1px 6px; font-family: monospace; font-size: 12px;
    }
    .help-dismiss { margin: 12px 0 0; opacity: 0.65; font-size: 13px; text-align: center; }
    /* 1.9.0 page-turn transition (paged / spread). The incoming <img> is a fresh
       element each turn, so the keyframe plays once on mount. 'Slide' eases the new
       page in from the direction of travel (navEnter; transform). 'Reveal' wipes the
       new page into view from the leading edge (clip-path) for a cover/uncover feel.
       'None' applies no class (instant swap). Both slide and reveal are
       reading-direction aware. (1.9.1: Reveal was a short opacity fade, which read
       almost identically to None; changed to a clip-path wipe so the three modes are
       categorically distinct motions. 1.11.0: the wipe now runs over the outgoing
       page - see .spread-row.outgoing - and uses the same decelerating curve as
       Slide, so it lands softly instead of the symmetric ease's abrupt finish;
       .3s kept. Durations are mirrored in TS by PageTurnMs.)
       Fill mode is 'backwards', not 'both' (1.22.2): each keyframe ends at the
       element's own style, so nothing needs holding afterwards, and a held
       translate3d kept the page on its own compositing layer under the Enhance
       canvas - on iPad (WebKit) that could leave the canvas black after a tap turn. */
    .spread-row img.anim-slide.from-right  { animation: mp-slide-from-right  .22s cubic-bezier(.22,.61,.36,1) backwards; }
    .spread-row img.anim-slide.from-left   { animation: mp-slide-from-left   .22s cubic-bezier(.22,.61,.36,1) backwards; }
    .spread-row img.anim-reveal.from-right { animation: mp-reveal-from-right .3s cubic-bezier(.22,.61,.36,1) backwards; }
    .spread-row img.anim-reveal.from-left  { animation: mp-reveal-from-left  .3s cubic-bezier(.22,.61,.36,1) backwards; }
    @keyframes mp-slide-from-right {
      from { transform: translate3d(22%, 0, 0); opacity: 0.35; }
      to   { transform: translate3d(0, 0, 0);   opacity: 1; }
    }
    @keyframes mp-slide-from-left {
      from { transform: translate3d(-22%, 0, 0); opacity: 0.35; }
      to   { transform: translate3d(0, 0, 0);    opacity: 1; }
    }
    /* Reveal: uncover the new page from the leading edge via an animated clip-path
       inset - from-right uncovers right->left, from-left uncovers left->right. */
    @keyframes mp-reveal-from-right {
      from { clip-path: inset(0 0 0 100%); }
      to   { clip-path: inset(0 0 0 0); }
    }
    @keyframes mp-reveal-from-left {
      from { clip-path: inset(0 100% 0 0); }
      to   { clip-path: inset(0 0 0 0); }
    }
    @media (prefers-reduced-motion: reduce) {
      .reader-toolbar, .progress-fill { transition: none; }
      /* Fall back to an instant page swap when the reader prefers reduced motion,
         regardless of the chosen transition. !important so this reliably wins over
         the per-direction rules above, which are otherwise more specific. */
      .spread-row img.anim-slide, .spread-row img.anim-reveal { animation: none !important; }
    }
  `],
})
export class ReaderComponent implements OnInit, OnDestroy, ReaderOptionsHost, BookmarksPanelHost {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly readState = inject(ReadStateService);
  private readonly bottomSheet = inject(MatBottomSheet);
  private readonly breakpoints = inject(BreakpointObserver);
  // Public so the template can read the persisted page-transition preference.
  readonly prefs = inject(ReaderPreferencesService);
  // Webtoon tap-to-scroll step / on-off (added 1.11.0); per-device.
  readonly webtoonNav = inject(WebtoonNavPreferencesService);
  // Upscaling engines (WebGPU / WebGL2) for the 'e' shortcut and the once-per-session
  // notice - the same resolution the settings menu shows.
  private readonly upscaleSupport = inject(UpscaleSupportService);
  private readonly installHint = inject(InstallHintService);
  readonly fitOptions = FIT_OPTIONS;

  /**
   * Handset-width layout: CDK's XSmall breakpoint (< 600px CSS
   * width) - phones in portrait, a narrow iPad Split View pane. Width-driven, not
   * device-driven, because the trigger is simply that the full bar no longer
   * fits; a phone in landscape (>= 640px) and every tablet/desktop keep the full
   * bar. Live, so rotating the device re-evaluates it.
   */
  readonly compact = toSignal(
    this.breakpoints.observe(Breakpoints.XSmall).pipe(map((r) => r.matches)),
    { initialValue: this.breakpoints.isMatched(Breakpoints.XSmall) },
  );
  /**
   * Narrow PORTRAIT screen: CDK's HandsetPortrait breakpoint
   * (< 600px wide AND portrait) - the one case where a synthetic two-up spread
   * leaves each page unreadably small. Live, so rotating a phone to landscape
   * (>= 640px, not portrait) brings double page straight back. A narrow landscape
   * window is deliberately NOT gated: the owner asked to keep landscape double page.
   */
  readonly narrowPortrait = toSignal(
    this.breakpoints.observe(Breakpoints.HandsetPortrait).pipe(map((r) => r.matches)),
    { initialValue: this.breakpoints.isMatched(Breakpoints.HandsetPortrait) },
  );
  /** True while the phone options sheet is open (drives the trigger's aria-expanded). */
  readonly optionsOpen = signal(false);

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');

  readonly itemId = signal('');

  /** Whether the currently open chapter (the archive) is favorited (1.21.0). */
  readonly currentFavorite = signal(false);
  readonly phase = signal<ReaderPhase>('preparing');
  readonly statusMessage = signal('Loading…');
  readonly currentPage = signal(0);
  readonly pageCount = computed(() => this.pages().length);
  readonly pages = signal<ManifestPageEntry[]>([]);
  readonly pageLoading = signal(true);
  readonly fitMode = signal<FitMode>('screen'); // Default image fit is fit-to-screen
  readonly direction = signal<ReadingDirection>('ltr');
  readonly view = signal<ReaderView>('paged');
  readonly isFullscreen = signal(false);
  // iPhone/iPad/iPod, and iPadOS Safari's Mac-masquerading UA: the Fullscreen
  // API there paints a persistent system close button and status bar the page
  // can't hide, so toggleFullscreen() drives isFullscreen (in-page immersive
  // mode) directly instead, and onFullscreenChange must not undo that (see both).
  readonly isIOSImmersive = isApplePlatformTouch(navigator);
  // 1.20.0 (owner request): the installed home-screen app / standalone PWA has
  // no browser chrome to hide either, so it gets the same in-page immersive
  // treatment as iOS/iPadOS Safari above rather than the Fullscreen API.
  // Computed once at construction, same as isIOSImmersive; see isStandaloneDisplay.
  readonly isStandalone = isStandaloneDisplay(window);
  // Either platform reason to prefer the reader's own immersive mode over the
  // Fullscreen API - see toggleFullscreen() / onFullscreenChange().
  readonly useInPageImmersive = this.isIOSImmersive || this.isStandalone;
  // Double-page pairing phase (the "offset"): when true, page 0 (the cover) is
  // shown alone and pages pair 1-2, 3-4… (right for a typical standalone cover);
  // when false, pairing starts at 0-1, 2-3… No reliable way to infer which a
  // given comic wants, so it's a reader-side toggle (two menu modes). Default on.
  // Since 1.23.0 this device setting is only the FALLBACK for an archive with no
  // saved `spreadLayout`; picking a double-page entry on the first spread updates it.
  readonly coverIsStandalone = signal(this.loadCoverStandalone());
  /**
   * The archive's saved, shared double-page pairing (1.23.0): forced spread-start
   * indices from the manifest, or null when none is saved (then the device fallback
   * above applies). Edited optimistically here and saved to the server (see
   * `applySpreadStarts`); every rule lives in spread-layout.ts.
   */
  readonly spreadLayout = signal<number[] | null>(null);
  readonly webtoonWidthPct = signal<number>(this.loadWebtoonWidth()); // webtoon page-width preference
  // Per-device default page mode (1.2.x). Highlighted in the reading-mode menu; a
  // non-null value overrides the server-resolved layout on every chapter open.
  readonly viewPref = signal<ViewPref | null>(this.loadViewPref());

  // Immersive chrome (2026-09-08): the toolbar + nav auto-hide while reading and
  // reveal on interaction, matching well-known manga readers. `chromeVisible`
  // drives both; `menuOpen`/`toolbarHover` lock it visible mid-interaction.
  readonly chromeVisible = signal(true);
  readonly menuOpen = signal(false);
  readonly toolbarHover = signal(false);
  readonly progressPct = computed(() =>
    this.pageCount() > 0 ? ((this.currentPage() + 1) / this.pageCount()) * 100 : 0);

  /**
   * Page indicator for the toolbar: shows current page(s) as "12-13" when in
   * double-page mode with two distinct pages, or single page number otherwise.
   * Reuses the current spread's page indices to avoid recomputing.
   */
  readonly currentPageIndicator = computed(() => {
    // In non-spread modes (paged, webtoon), just show the current page
    if (this.effectiveView() !== 'spread') {
      return String(this.currentPage() + 1);
    }
    // In spread mode, check if the current spread has 2 distinct pages
    const spread = this.spreads().find((s) => s.includes(this.currentPage()));
    if (!spread || spread.length === 0) return String(this.currentPage() + 1);
    if (spread.length === 2) {
      return `${spread[0] + 1}-${spread[1] + 1}`;
    }
    // Single page in spread (cover, wide page, or odd trailing page)
    return String(spread[0] + 1);
  });

  // Help overlay: reveals the (normally invisible) tap zones prominently and lists
  // keyboard shortcuts. The zones are direction-aware — the physical left edge goes
  // "back" in LTR but "forward" in RTL — so the labels follow `direction`.
  readonly helpVisible = signal(false);
  readonly leftZoneLabel = computed(() => this.direction() === 'rtl' ? 'Next page' : 'Previous page');
  readonly rightZoneLabel = computed(() => this.direction() === 'rtl' ? 'Previous page' : 'Next page');

  // --- Page-turn transition (1.9.0) ---
  // `navEnter` is the PHYSICAL edge the incoming page eases in from, resolved from
  // the travel direction and the reading direction (see enterSideForNav). It is set
  // just before `currentPage` changes so the freshly mounted <img> carries the right
  // class. `committing` suppresses the swipe spring-back transition for one turn so a
  // completed swipe hands straight off to the slide-in instead of snapping the old
  // page back to centre first (the 1.8.x jank this feature removes).
  readonly navEnter = signal<'from-right' | 'from-left' | null>(null);
  readonly committing = signal(false);
  private commitTimer: ReturnType<typeof setTimeout> | null = null;
  // Guards the one-shot onboarding help auto-show so it fires at most once per
  // reader instance (localStorage stops it recurring across instances/sessions).
  private autoHelpChecked = false;

  /**
   * Whether a page-turn transition should play right now. Off when the preference
   * is 'none', while scrubbing (rapid page swaps would strobe), and when the page
   * overflows horizontally or is pinch-zoomed (you don't slide a zoomed-in page,
   * and it keeps the transform inside the viewport so it never adds a scrollbar).
   */
  readonly pageAnimActive = computed(() =>
    this.prefs.pageAnimation() !== 'none'
    && this.view() !== 'webtoon'
    && !this.scrubbing()
    && !this.overflowsX()
    && !this.zoomed());

  /**
   * The physical edge a newly shown page enters from. Forward travel (next page)
   * enters from the right in LTR and from the left in RTL (manga); backward travel
   * mirrors it. Pure, so it is unit-testable without the DOM.
   */
  enterSideForNav(forward: boolean): 'from-right' | 'from-left' {
    const fromRight = forward !== (this.direction() === 'rtl');
    return fromRight ? 'from-right' : 'from-left';
  }

  // Adjacent chapters (archives in the same folder), for auto-advance past the last
  // page (next) and before the first page (previous). Fetched per item from the
  // catalog's neighbor endpoint.
  readonly nextNeighbor = signal<{ id: string; displayName: string } | null>(null);
  readonly prevNeighbor = signal<{ id: string; displayName: string } | null>(null);

  // Chapter-arrow availability: the toolbar
  // prev/next CHAPTER buttons are enabled only when a neighbor archive exists, so
  // they grey out at the ends of a folder. Distinct from page turning.
  readonly hasNextChapter = computed(() => !!this.nextNeighbor());
  readonly hasPrevChapter = computed(() => !!this.prevNeighbor());

  // In-reader bookmarks (1.17.0), fetched per item alongside the neighbors.
  // `ordinal` is the zero-based page index (matches `currentPage`), so the
  // toolbar toggle's state is just "is there a bookmark at this ordinal".
  readonly bookmarks = signal<BookmarkDto[]>([]);
  readonly currentPageBookmark = computed(() =>
    this.bookmarks().find((b) => b.ordinal === this.currentPage()) ?? null);
  readonly isCurrentPageBookmarked = computed(() => this.currentPageBookmark() !== null);

  // Page scrubber: `scrubbing` is true only while the reader
  // is actively dragging the bottom rail, which is what surfaces the prominent page
  // bubble + enlarged bar (so nothing clutters the page otherwise). The thumb knob
  // shows whenever chrome is visible, to advertise that the rail is draggable.
  readonly scrubbing = signal(false);
  // Thumb / bubble position as a percent along the rail, direction-aware (RTL fills
  // from the right, mirroring the progress fill). At n<=1 it pins to the fill edge.
  readonly scrubThumbPct = computed(() => {
    const n = this.pageCount();
    if (n <= 1) return this.direction() === 'rtl' ? 100 : 0;
    let frac = this.currentPage() / (n - 1);
    if (this.direction() === 'rtl') frac = 1 - frac;
    return frac * 100;
  });
  // 1.8.0 slider rework: the track fill is aligned with the thumb (LTR grows from
  // the left, RTL from the right, so the bar reads in page order either way).
  readonly scrubFillPct = computed(() =>
    this.direction() === 'rtl' ? 100 - this.scrubThumbPct() : this.scrubThumbPct());
  // Thumb / bubble centre as a CSS length. The thumb travels the track INSET by
  // its own radius so it never overhangs the ends of the bar; the pointer math in
  // scrubFromClientX uses the same inset so finger and thumb stay aligned.
  readonly scrubThumbLeft = computed(() => {
    const r = ReaderComponent.ScrubThumbRadiusPx;
    return `calc(${r}px + (100% - ${2 * r}px) * ${this.scrubThumbPct() / 100})`;
  });

  // Full-surface swipe (added 1.8.0). `swipeDx` is the live horizontal
  // finger offset while a page swipe is in progress (the spread row follows it);
  // 0 when idle. The help legend labels are phrased by FINGER direction ("swipe
  // left"), the inverse of the tap-zone labels, and mirror in RTL.
  readonly swipeDx = signal(0);
  readonly swipeLeftLabel = computed(() => this.direction() === 'rtl' ? 'Previous page' : 'Next page');
  readonly swipeRightLabel = computed(() => this.direction() === 'rtl' ? 'Next page' : 'Previous page');
  // Measured viewport overflow (see measureOverflow) and visual-viewport zoom.
  // These decide who owns a one-finger drag: the browser (native panning) when the
  // page is wider than the screen or pinch-zoomed in, us otherwise.
  readonly overflowsX = signal(false);
  readonly overflowsY = signal(false);
  readonly zoomed = signal(false);
  /**
   * The paged viewport's CSS touch-action. 'auto' hands the drag to the browser
   * (the page pans natively; we get pointercancel and stand down — 1.7.0
   * behaviour). Otherwise we claim horizontal drags: 'pan-y pinch-zoom' keeps
   * native vertical scrolling when the page is taller than the screen (fit-width),
   * 'pinch-zoom' claims both axes when nothing overflows (fit-screen). Two-finger
   * pinch-zoom is always left native. This is what makes a swipe reliable across
   * the WHOLE surface on iPad: with 'auto', Safari claimed every horizontal touch
   * drag for scrolling and cancelled the pointer stream before pointerup.
   */
  readonly touchAction = computed<string | null>(() => {
    if (this.view() === 'webtoon') return null;
    if (this.overflowsX() || this.zoomed()) return 'auto';
    return this.overflowsY() ? 'pan-y pinch-zoom' : 'pinch-zoom';
  });
  /**
   * Whether the webtoon scroller takes a horizontal swipe as a screen step:
   * only with tap-to-scroll on and the page not pinch-zoomed
   * (a zoomed page pans natively in both axes, as in the paged reader).
   */
  readonly webtoonSwipeEnabled = computed(() => this.webtoonNav.tapZonesEnabled() && !this.zoomed());
  /**
   * The webtoon scroller's touch-action. Vertical panning and pinch-zoom stay
   * native always; horizontal drags are claimed (so the browser neither cancels
   * the pointer stream nor starts a history swipe) only while the swipe is on.
   */
  readonly webtoonTouchAction = computed<string | null>(() =>
    this.webtoonSwipeEnabled() ? 'pan-y pinch-zoom' : null);

  // Fallback exit route (2026-09-11, 1.6.1 owner iPad fix): the item's parent-folder
  // browse view, resolved from the catalog node so goBack() can return there even
  // when the reader was deep-linked (no SPA navigation history to walk back through).
  // A library-root item's parentId is "" (CatalogBrowseService), which resolves to
  // the library's root browse route rather than a nested :nodeId segment.
  readonly fallbackBackRoute = signal<string[]>(['/']);

  // Set when this archive was entered via "previous archive" back-navigation, which
  // asks to land on the LAST page (query param at=end). While the reader is still
  // sitting on that landed last page we suppress progress saves, so merely backing
  // into a chapter never marks it completed/read (the sticky read-mark stays honest).
  private landOnLastPage = false;
  private landedOnLastPage = false;

  readonly viewIcon = computed(() =>
    this.view() === 'webtoon' ? 'view_day'
      : this.view() === 'spread' ? (this.spreadShifted() ? 'auto_stories' : 'import_contacts')
      : 'crop_portrait');

  /**
   * The view actually RENDERED. `view` is what the reader chose
   * (and what the menus highlight); a chosen double page is rendered as single
   * pages on a narrow portrait screen, and comes back on rotation. Everything
   * that depends on the on-screen grouping - the visible entries, end/start
   * detection, spread stepping, the persisted progress index - reads this, so the
   * gated reader behaves exactly like a single-page reader while gated.
   */
  readonly effectiveView = computed<ReaderView>(() =>
    this.view() === 'spread' && this.narrowPortrait() ? 'paged' : this.view());

  /**
   * Is GPU upscaling (Crisp or Enhance) on for the pages currently rendered?
   * Simply the device-wide preference: the paged / double-spread `<img>`s read it
   * through `UpscaleDirective`, which renders the backend it resolved to. Both
   * directives are no-ops when the choice cannot run here and when a page is not
   * upscaled.
   */
  readonly upscaleActive = computed<boolean>(() => this.prefs.upscaler() !== 'smooth');

  /** The backend the webtoon strip renders with (1.25.0), or null for plain images. */
  readonly upscaleBackend = computed(() => this.upscaleSupport.backend());

  /**
   * No silent fallback (1.25.0): a SAVED Upscaling choice that cannot run on this
   * device (Enhance over plain HTTP without WebGL2 float targets, no WebGL2 for
   * Crisp) is announced once per app session; the Upscaling menu shows the reason.
   */
  private readonly renderingNoticeEffect = effect(() => {
    const message = this.upscaleSupport.pendingNotice();
    if (!message) return;
    untracked(() => {
      this.upscaleSupport.markNoticeShown();
      this.snackBar.open(message, 'Dismiss', { duration: 5000 });
    });
  });

  /**
   * "Page quality" is an explicit quality decision, so unlike a rotation it DOES
   * apply to the page on screen: drop the pinned URLs so the current page
   * re-resolves (full resolution, or back to a display-sized bucket) instead of
   * waiting for the next page turn. `untracked` keeps the effect subscribed to
   * the preference alone — refreshVariantTarget reads half a dozen other signals
   * and must not make them all invalidate the cache.
   *
   * 1.20.0: "Downscale filter" (`?filter=`) is the same kind of explicit quality
   * decision, so it re-targets the same way — the effect also depends on
   * `downscaleFilter` and clears the pinned URLs on every change.
   */
  private readonly pageQualityEffect = effect(() => {
    this.prefs.pageQuality();
    this.prefs.downscaleFilter();
    untracked(() => {
      this.pageUrlCache.clear();
      this.refreshVariantTarget();
    });
  });

  /**
   * Page-turn ghost: the entries that were on screen just before
   * the current turn, rendered inert underneath the incoming row for the length
   * of the transition, then dropped. Empty when no transition is playing.
   */
  readonly outgoing = signal<ManifestPageEntry[]>([]);
  private outgoingTimer: ReturnType<typeof setTimeout> | null = null;
  /** Transition lengths, mirroring the CSS animation durations. */
  private static readonly PageTurnMs = { slide: 220, reveal: 300, none: 0 } as const;

  /** The page indices shown together on the current screen (1 for paged, 1–2 for spread). */
  readonly currentSpreadEntries = computed<ManifestPageEntry[]>(() => {
    const all = this.pages();
    if (all.length === 0) return [];
    if (this.effectiveView() !== 'spread') {
      const p = all[this.currentPage()];
      return p ? [p] : [];
    }
    const spread = this.spreads().find((s) => s.includes(this.currentPage())) ?? [this.currentPage()];
    return spread.map((i) => all[i]).filter(Boolean);
  });

  /** Grouping of page indices into spreads (double-page view). */
  readonly spreads = computed<number[][]>(() => this.computeSpreads());

  /** The forced spread starts in effect: the archive's saved layout, else the device fallback. */
  private readonly activeSpreadStarts = computed<number[]>(() =>
    this.spreadLayout() ?? fallbackSpreadStarts(this.pageCount(), (i) => this.isWide(i), this.coverIsStandalone()));

  /** The spread holding the current page (its indices), in double-page grouping. */
  private readonly currentSpread = computed<number[]>(() =>
    this.spreads().find((s) => s.includes(this.currentPage())) ?? [this.currentPage()]);

  /**
   * Is the spread on screen paired on the shifted parity of its run of pages? Drives
   * which double-page entry is highlighted (1.23.0: per current spread, not global).
   */
  readonly spreadShifted = computed<boolean>(() =>
    isShiftedSpread(this.pageCount(), (i) => this.isWide(i), this.currentSpread()));


  private contentVersion = 0;
  private revision = 0;
  // Shared pairing save (1.23.0): debounce timer, the latest unsent layout, and
  // whether a request is on the wire (see flushSpreadSave).
  private spreadSaveTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingSpreadSave: { itemId: string; contentVersion: number; starts: number[] } | null = null;
  private spreadSaveInFlight = false;
  private static readonly SpreadSaveDebounceMs = 400;
  private pollAttempts = 0;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private webtoonSaveTimer: ReturnType<typeof setTimeout> | null = null;
  private hideTimer: ReturnType<typeof setTimeout> | null = null;
  private lastPointerReveal = 0;
  private static readonly ChromeIdleMs = 3000;
  private static readonly RevealHotZonePx = 80;
  private destroyed = false;

  // --- Swipe gesture state (paged/spread only) ---
  // A single-pointer horizontal drag on the paged viewport turns the page,
  // direction-aware like the edge zones. Multi-touch (pinch-zoom) and vertical
  // drags are ignored so native zoom/scroll are never hijacked.
  private static readonly SwipeMinDistancePx = 45;   // deliberate drag threshold
  private static readonly SwipeFlickMinDistancePx = 20; // shorter if it's a fast flick
  private static readonly SwipeFlickVelocity = 0.5;  // px/ms — a quick flick shortcut
  private static readonly SwipeMaxOffAxisRatio = 0.75; // |dy| must stay below this * |dx|
  private static readonly SwipeClickSuppressMs = 400; // swallow the ghost click after a swipe
  private static readonly SwipeSlopPx = 8;           // below this it's a tap, not a drag (no axis lock yet)
  private static readonly SwipeEdgeResistance = 0.35; // rubber-band factor when there's no page that way
  private static readonly ScrubThumbRadiusPx = 11;   // slider thumb radius (track inset) — mirrors the CSS
  private swipePointerId: number | null = null;
  private swipeStartX = 0;
  private swipeStartY = 0;
  private swipeStartT = 0;
  private swipeCancelled = false;
  // Axis lock, decided once the finger has moved past the slop: 'x' follows the
  // finger and turns the page on release; 'y' stands down for the whole gesture
  // (native vertical scroll, or nothing). Stops a wobbly vertical scroll from
  // half-dragging the page sideways.
  private swipeAxis: 'none' | 'x' | 'y' = 'none';
  private swipeDragged = false;
  // Pointers currently down on the paged viewport, by id (a second one means a
  // pinch, which abandons the swipe). Tracked by id, not by count, because the
  // matching up/cancel now arrives at DOCUMENT level, where unrelated pointers
  // (toolbar, slider) also end.
  private readonly activePointers = new Set<number>();
  private lastSwipeAt = 0;
  // Threshold for "the user has actually pinch-zoomed in", vs. visualViewport.scale
  // merely reporting device-pixel rounding noise. An installed Android PWA
  // (standalone WebView, no browser chrome to anchor the layout viewport against)
  // has been observed to settle a hair above 1.0 — e.g. 1.02–1.03 — on first paint
  // with NO user gesture at all, and to stay there indefinitely (unlike a transient
  // layout-settling blip, nothing ever fires another resize to bring it back down).
  // At the old 1.01 cutoff that reads as "zoomed" forever, which parks touchAction
  // at 'auto' and onReaderPointerDown never claims the drag (see claim below) — so
  // swipe paging silently never works on those devices. A real pinch-to-zoom (the
  // in-app feature this signal exists to detect, see touchAction's doc comment)
  // moves the scale well past this, so widening the tolerance loses no real
  // zoom detection while absorbing the installed-PWA rounding jitter.
  private static readonly ZoomedScaleThreshold = 1.05;
  private readonly onVisualViewportChange = (): void => {
    const vv = window.visualViewport;
    this.zoomed.set(!!vv && vv.scale > ReaderComponent.ZoomedScaleThreshold);
  };

  // --- Display-sized page requests (1.19.0 "Image Scaling") --------------------
  //
  // The reader asks the server for a page at roughly the pixels the screen will
  // actually paint (`?maxDim=<bucket>`, snapped to the shared ladder in
  // page-variant.ts) instead of the full-size transcode. Two rules keep it from
  // fighting the reader:
  //
  //  1. The target is a PLAIN FIELD, not a signal. Recomputing it must never
  //     re-run the `[src]` binding of a page already on screen — that would swap
  //     the src mid-read and re-download a page the browser already has.
  //  2. Resolved URLs are CACHED PER ENTRY KEY for the life of the chapter. The
  //     first time a page is needed (rendered or prefetched) it is pinned to the
  //     target current at that moment and keeps it; a new page picks up whatever
  //     the target is by then. That is what makes the prefetch a genuine cache
  //     hit: `prefetchIndices` goes through this same builder, so the warmed URL
  //     and the URL the `<img>` later asks for are byte-identical.
  //
  // Net effect: rotating the device or changing the fit mode re-targets the NEXT
  // pages, never the one being read. A chapter change clears the cache.
  /**
   * The layout-box inputs to `targetMaxDim`, captured at the moment something
   * that can change the box happens (see `refreshVariantTarget`). Everything
   * EXCEPT the page's own aspect ratio, which varies per page (that's the
   * point of the per-page fix below) and so is looked up from the entry
   * itself when a URL is actually built, not carried in this snapshot.
   */
  private variantParams: {
    viewportW: number; viewportH: number; dpr: number; fit: VariantFitMode;
    paired: boolean; webtoonWidthPct: number; full: boolean;
  } = { viewportW: 0, viewportH: 0, dpr: 1, fit: 'screen', paired: false, webtoonWidthPct: 100, full: false };
  private readonly pageUrlCache = new Map<string, string>();

  pageUrlFor(entry: ManifestPageEntry | undefined): string {
    if (!entry) return '';
    const cached = this.pageUrlCache.get(entry.entryKey);
    if (cached !== undefined) return cached;
    const base = `/api/v1/items/${this.itemId()}/pages/${encodeURIComponent(entry.entryKey)}`;
    const p = this.variantParams;
    // Per-page true aspect (manifest width/height), NOT a chapter-wide
    // representative — a webtoon strip's pages vary wildly in height, and
    // capping a tall page's longest edge (its height) to a bucket sized for a
    // shorter page starves its width, which the browser then upscales back
    // out -> visible pixelation. Each page's own aspect keeps the bucket (or
    // the `0` full-size fallback for a page taller than the top rung covers)
    // honest for that page specifically.
    const pageAspect = entry.width > 0 && entry.height > 0 ? entry.height / entry.width : 0;
    const maxDim = p.full ? 0 : targetMaxDim(
      p.viewportW, p.viewportH, p.dpr, p.fit, p.paired, p.webtoonWidthPct, pageAspect,
    );
    const url = withMaxDim(base, maxDim, this.prefs.downscaleFilter());
    this.pageUrlCache.set(entry.entryKey, url);
    return url;
  }

  /**
   * Re-measure the variant layout box for pages loaded from now on. Called
   * wherever that box can change: viewport resize / orientation, fit mode,
   * view (single vs paired vs webtoon), and the webtoon width slider. The
   * per-page aspect ratio is looked up separately, in `pageUrlFor`, once the
   * manifest lands — it is not part of this snapshot.
   *
   * "Page quality: Full" short-circuits to 0, i.e. no `maxDim` param at all.
   */
  refreshVariantTarget(): void {
    if (this.prefs.pageQuality() === 'full') {
      this.variantParams = { ...this.variantParams, full: true };
      return;
    }
    const el = this.viewport()?.nativeElement;
    const viewportW = el?.clientWidth || window.innerWidth || 0;
    const viewportH = el?.clientHeight || window.innerHeight || 0;
    const dpr = typeof window.devicePixelRatio === 'number' ? window.devicePixelRatio : 1;
    const webtoon = this.view() === 'webtoon';
    const fit: VariantFitMode = webtoon ? 'webtoon' : this.fitMode();
    const paired = !webtoon && this.effectiveView() === 'spread';
    this.variantParams = {
      viewportW, viewportH, dpr, fit, paired, webtoonWidthPct: this.webtoonWidthPct(), full: false,
    };
  }

  /**
   * Reserve a webtoon page's height BEFORE it lazy-loads, from the manifest's
   * intrinsic dimensions, so streaming images don't jerk the scroll position
   * (original plan's first ask). Returns a CSS aspect-ratio, or null when a page's
   * dimensions are unknown (then the image sizes itself on load, as before).
   */
  aspectRatioFor(entry: ManifestPageEntry): string | null {
    return entry.width > 0 && entry.height > 0 ? `${entry.width} / ${entry.height}` : null;
  }

  ngOnInit(): void {
    // Pinch-zoom (visual viewport scale) hands one-finger drags back to the browser.
    window.visualViewport?.addEventListener('resize', this.onVisualViewportChange);
    // Sync from the CURRENT scale immediately, rather than leaving `zoomed` at its
    // false default until the first 'resize' fires. On a fresh navigation into the
    // reader (e.g. deep-linked while an installed PWA is already open) there may be
    // no resize event at all before the very first swipe, which would otherwise
    // read a stale "not zoomed" against a viewport that is actually already zoomed.
    this.onVisualViewportChange();
    this.route.paramMap.subscribe((params) => {
      const id = params.get('itemId') ?? '';
      // Send any unsaved pairing for the chapter being left, then forget it: the
      // next archive's layout (or none) arrives with its manifest.
      this.flushSpreadSave();
      this.spreadLayout.set(null);
      this.itemId.set(id);
      this.pollAttempts = 0;
      // Reset the page-prefetch cache for the new chapter (URLs are per-item).
      this.prefetchedUrls.clear();
      this.prefetchImgs = [];
      // Page URLs are pinned per chapter (see pageUrlFor): a new chapter re-targets.
      this.pageUrlCache.clear();
      // "at=end" (set when arriving via previous-chapter back-navigation) asks to
      // land on the last page instead of resuming from saved progress.
      this.landOnLastPage = this.route.snapshot.queryParamMap.get('at') === 'end';
      this.landedOnLastPage = false;
      // Resolve the effective default reading mode for THIS item (1.2.0): per-user
      // item override → nearest folder default → library default → user's personal
      // default → paged LTR. Failure falls back to paged LTR.
      this.api.getEffectiveReaderMode(id).subscribe({
        // Server mode sets direction (RTL for manga) + a baseline view; a per-device
        // preference, if any, then overrides the VIEW only (keeping direction).
        next: (res) => { this.applyDefaultMode(res.readerMode); this.applyDeviceViewPreference(); },
        error: () => { this.applyDeviceViewPreference(); },
      });
      this.loadManifest();
      this.loadNeighbors(id);
      this.loadFallbackBackRoute(id);
      this.loadBookmarks(id);
    });
    // 1.20.0 standalone-immersive default (owner request): the installed
    // home-screen app / standalone PWA has no browser chrome to hide, so it
    // opens the reader already immersive (bars auto-hide, tap-centre reveals)
    // rather than starting windowed like an ordinary browser tab - "by
    // default", not "always": toggleFullscreen() still flips it off and back
    // on. Set once here, AFTER the paramMap subscribe above (whose first,
    // synchronous emission calls loadManifest() -> clearHideTimer(), which
    // would otherwise cancel a timer scheduled before it) — ngOnInit only
    // ever runs once per reader mount, so a later chapter change (a second
    // paramMap emission) can never re-force this back on over a manual toggle.
    if (this.isStandalone) {
      this.isFullscreen.set(true);
      this.scheduleChromeHide();
    }
  }

  /**
   * Load adjacent archives in the folder — feeds the reader-bar/webtoon-footer
   * chapter buttons' enabled state (`hasNextChapter` / `hasPrevChapter`) and the
   * paged/spread end-of-screen chapter advance. Webtoon no longer auto-advances
   * on scroll (1.7.1 revert); its chapter buttons still use these neighbors.
   */
  private loadNeighbors(itemId: string): void {
    this.nextNeighbor.set(null);
    this.prevNeighbor.set(null);
    this.api.getNeighbors(itemId).subscribe({
      next: (n) => { this.nextNeighbor.set(n.next); this.prevNeighbor.set(n.previous); },
      error: () => { /* no neighbors / not available — chapter buttons simply stay disabled */ },
    });
  }

  /**
   * Load this item's bookmarks (1.17.0) — feeds the toolbar toggle's
   * `isCurrentPageBookmarked` state and the bookmarks panel list.
   */
  private loadBookmarks(itemId: string): void {
    this.bookmarks.set([]);
    this.api.getBookmarks(itemId).subscribe({
      next: (list) => this.bookmarks.set(list),
      error: () => { /* bookmarks unavailable — toggle simply reads as unset */ },
    });
  }

  /**
   * Resolve the browse route to fall back to on exit when there is no in-app
   * history to walk back through (see {@link goBack}) — the item's parent folder,
   * or the library's root browse when the item sits at the library root.
   */
  private loadFallbackBackRoute(itemId: string): void {
    this.fallbackBackRoute.set(['/']);
    this.api.getNode(itemId).subscribe({
      next: (node) => {
        this.fallbackBackRoute.set(
          node.parentId
            ? ['/libraries', node.libraryId, 'browse', node.parentId]
            : ['/libraries', node.libraryId, 'browse'],
        );
        // Seed the reader favorite star (1.21.0) from the same node fetch.
        this.currentFavorite.set(!!node.isFavorite);
      },
      error: () => { /* keep the Home fallback — item metadata unavailable */ },
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    window.visualViewport?.removeEventListener('resize', this.onVisualViewportChange);
    this.clearPoll();
    if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
    if (this.commitTimer) clearTimeout(this.commitTimer);
    if (this.outgoingTimer) clearTimeout(this.outgoingTimer);
    this.clearHideTimer();
    this.flushSpreadSave();
    this.saveProgress();
    // 1.7.1: tell the retained browse view this item's read/progress state may
    // have changed, so it can patch the card in place on the next reattach
    // without a full re-fetch or losing the 1.6.2 scroll retention.
    this.readState.notifyChanged(this.itemId());
  }

  private applyDefaultMode(mode: ReaderMode): void {
    switch (mode) {
      case 'PagedRtl': this.view.set('paged'); this.direction.set('rtl'); break;
      case 'DoubleSpread': this.view.set('spread'); break;
      case 'VerticalWebtoon': this.view.set('webtoon'); break;
      default: this.view.set('paged'); this.direction.set('ltr');
    }
  }

  /**
   * Apply the per-device PAGED LAYOUT preference over the server-resolved view.
   * Only the VIEW changes — `direction` (RTL for manga) stays as the server
   * resolved it. Scoped to paged content (Option A fix, 2026-09-11): the server's
   * webtoon resolution is content-orientation-authoritative and must never be
   * overridden by a device-global layout pref left over from a different item —
   * that cross-library bleed was the reader-mode-sticky bug. When no device
   * preference is stored, or the item resolved to webtoon, the server default
   * stands unchanged.
   */
  private applyDeviceViewPreference(): void {
    if (this.view() === 'webtoon') return;
    const pref = this.viewPref();
    if (!pref) return;
    if (pref === 'auto') { this.applyAutoView(); return; }
    this.view.set(pref); // 'paged' | 'spread'
  }

  /** Auto (orientation) resolution: landscape → double page, portrait → single. */
  private applyAutoView(): void {
    const landscape = window.innerWidth >= window.innerHeight;
    this.setView(landscape ? 'spread' : 'paged');
  }

  // Live orientation reactivity: while the device preference is 'auto', flip
  // paged↔spread as the viewport rotates/resizes. Signal dedup makes this a no-op
  // unless the orientation actually crossed the square boundary. Guarded against
  // webtoon (Option A fix) same as applyDeviceViewPreference — 'auto' is a
  // paged-layout preference and must not kick a session's explicit webtoon choice
  // back to paged just because the device rotated.
  @HostListener('window:resize')
  @HostListener('window:orientationchange')
  onViewportChange(): void {
    if (this.phase() === 'ready' && this.viewPref() === 'auto' && this.view() !== 'webtoon') {
      this.applyAutoView();
    }
    this.refreshVariantTarget();
    this.scheduleMeasure();
  }

  @HostListener('document:fullscreenchange')
  onFullscreenChange(): void {
    // On iOS/iPadOS, or in a standalone/installed app, toggleFullscreen() never
    // calls the Fullscreen API (see there), so document.fullscreenElement stays
    // null forever there and must never be allowed to flip isFullscreen back
    // off from underneath it.
    if (this.useInPageImmersive) return;
    // Keep our signal in sync when the browser exits fullscreen via Esc.
    const fs = !!document.fullscreenElement;
    this.isFullscreen.set(fs);
    // Immersive auto-hide is fullscreen-only: entering starts the idle countdown,
    // exiting pins the chrome back on so windowed reading always shows it.
    if (fs) { this.scheduleChromeHide(); }
    else { this.chromeVisible.set(true); this.clearHideTimer(); }
  }

  @HostListener('window:keydown', ['$event'])
  onKeyDown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') return;
    // A key an open overlay (Reading mode / Image fit / settings MatMenu, the phone
    // options bottom sheet) already consumed must not ALSO act here: MatMenu and
    // MatBottomSheet preventDefault() Escape (and MatMenu Up/Down/Home/End) before
    // it bubbles to window, but menuOpen() is already reset by then, so without
    // this the Escape that closes a menu also left the reader. Left/Right and
    // typeahead letters are NOT prevented by MatMenu, so any key whose target sits
    // inside an overlay pane is ignored too (no page turn from inside a menu).
    if (event.defaultPrevented) return;
    if (typeof target.closest === 'function' && target.closest('.cdk-overlay-container')) return;
    // And while a menu / sheet is open but focus has not moved into it yet (a key
    // pressed right as it opens still targets the trigger): menuOpen() is set
    // synchronously on open, so it closes that gap (it is only unusable for the
    // closing Escape, which is handled by defaultPrevented above).
    if (this.menuOpen()) return;
    if (this.phase() !== 'ready') return;
    // Single-letter shortcuts are case-folded so Shift/CapsLock (event.key 'M'/'F')
    // still match the uppercase <kbd> the Help overlay shows; multi-char key names
    // ('ArrowLeft', 'Escape', …) are never case-variant and pass through as-is.
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
    if (key === '?') { this.toggleHelp(); return; }
    if (this.helpVisible() && key === 'Escape') { this.closeHelp(); return; }
    if (key === 'm') { this.toggleChrome(); return; } // toggle chrome in any view
    // Downscale filter affects every page request (webtoon included — see
    // pageUrlFor), unlike page-mode/rendering below, so it must act before the
    // webtoon early-return, same as 'm'.
    if (key === 's') { this.cycleDownscaleFilter(); return; }
    // Upscaling applies to webtoon too since 1.24.0 (banded Enhance), so like 's'
    // it acts before the webtoon early-return.
    if (key === 'e') { this.cycleRendering(); return; }
    if (this.view() === 'webtoon') return; // native scroll drives webtoon

    switch (key) {
      case 'ArrowLeft': this.direction() === 'rtl' ? this.nextPage() : this.prevPage(); break;
      case 'ArrowRight': this.direction() === 'rtl' ? this.prevPage() : this.nextPage(); break;
      case 'Home': this.goToPage(0); break;
      case 'End': this.goToPage(this.pageCount() - 1); break;
      case 'f': this.toggleFullscreen(); break;
      case 'Escape': this.isFullscreen() ? this.toggleFullscreen() : this.goBack(); break;
      case 'd': this.toggleDoublePage(); break;
      case 'o': this.toggleSpreadShift(); break;
    }
  }

  /**
   * 'd': single <-> double page, persisted per-device like the menu's "Single page"
   * / "Double page" radios (`chooseView`), with the same narrow-portrait toast.
   * Reads the current `view()` (not `viewPref`) so it flips relative to what 'auto'
   * resolved to. 1.23.0: it KEEPS the archive's saved pairing (it used to reset the
   * offset), and entering double page makes the page being read start a spread -
   * the owner's fix flow: at a mis-paired spread press `d`, step to the next page,
   * press `d` again, and pairing starts there (see `enterSpreadHere`).
   */
  private toggleDoublePage(): void {
    if (this.view() === 'spread') { this.chooseView('paged'); return; }
    this.chooseView('spread');
    this.enterSpreadHere();
    this.noteNarrowSpread();
  }

  /**
   * 's': cycles the 1.20.0 Downscale filter (sharp -> balanced -> soft -> …),
   * same `DOWNSCALE_FILTER_OPTIONS` order as the Upscaling menu and the same
   * `ReaderPreferencesService` write it uses. Handled in onKeyDown ABOVE the
   * webtoon early-return (like 'm') because `pageUrlFor` applies the filter to
   * every page request regardless of view, not just paged/spread. No-op under
   * Page quality: Full, mirroring `ReaderSettingsMenuComponent.filterDisabled` -
   * the filter has nothing to act on there, so the shortcut must not "enable" it.
   */
  private cycleDownscaleFilter(): void {
    if (this.prefs.pageQuality() === 'full') return;
    const order = DOWNSCALE_FILTER_OPTIONS.map(o => o.value);
    const next = order[(order.indexOf(this.prefs.downscaleFilter()) + 1) % order.length];
    this.prefs.setDownscaleFilter(next);
  }

  /**
   * 'e': cycles Upscaling Smooth -> Crisp -> Enhance -> Smooth (1.25.0; a
   * Smooth/Enhance toggle before), skipping any choice this device cannot run -
   * the options the menu offers disabled. Same `setUpscaler` as the menu. Works
   * in every view, webtoon included (1.24.0).
   */
  private cycleRendering(): void {
    const next = nextUpscaler(this.prefs.upscaler(), this.upscaleSupport.caps());
    if (next !== this.prefs.upscaler()) this.prefs.setUpscaler(next);
  }

  // --- Immersive chrome (auto-hide toolbar + nav) ---

  /**
   * Reveal chrome and (re)arm the idle-hide timer. Cheap enough to call on every
   * mouse move (the signal only notifies when the value actually flips).
   */
  revealChrome(): void {
    this.chromeVisible.set(true);
    this.scheduleChromeHide();
  }

  /**
   * Center tap / 'm' key: flip chrome. Hiding is fullscreen-only (immersive reading
   * == fullscreen); windowed always keeps the chrome shown.
   */
  toggleChrome(): void {
    if (!this.isFullscreen()) { this.chromeVisible.set(true); return; }
    if (this.chromeVisible()) {
      this.chromeVisible.set(false);
      this.clearHideTimer();
    } else {
      this.revealChrome();
    }
  }

  /** Toolbar hover keeps chrome pinned; leaving resumes the idle countdown. */
  lockChrome(hovering: boolean): void {
    this.toolbarHover.set(hovering);
    if (!hovering) this.scheduleChromeHide();
  }

  onMenuClosed(): void {
    this.menuOpen.set(false);
    this.scheduleChromeHide();
  }

  /**
   * Phone "Reader options": open the bottom sheet with this
   * reader as its host (live signals in, actions out - see `ReaderOptionsHost`).
   * Pins the chrome like an open menu does, and releases it on dismiss.
   */
  openOptions(): void {
    if (this.optionsOpen()) return;
    this.optionsOpen.set(true);
    this.menuOpen.set(true);
    const ref = this.bottomSheet.open(ReaderOptionsSheetComponent, {
      data: this as ReaderOptionsHost,
      panelClass: 'reader-options-sheet',
      ariaLabel: 'Reader options',
    });
    ref.afterDismissed().subscribe(() => {
      this.optionsOpen.set(false);
      this.onMenuClosed();
    });
  }

  toggleHelp(): void {
    this.helpVisible.update((v) => !v);
    if (this.helpVisible()) this.revealChrome(); // keep the toolbar up behind the overlay
  }

  closeHelp(): void {
    this.helpVisible.set(false);
    // The first-open install hint waits for the help overlay (a no-op after its first call).
    this.installHint.onReaderOpened();
  }

  /**
   * Toolbar bookmark toggle (1.17.0): add or remove a bookmark on the current
   * page. Delegates to `deleteBookmark` when one already exists at this ordinal
   * (`currentPageBookmark`), so the same button both creates and clears.
   */
  toggleBookmark(): void {
    const existing = this.currentPageBookmark();
    if (existing) {
      this.deleteBookmark(existing);
      return;
    }
    const itemId = this.itemId();
    const ordinal = this.currentPage();
    this.api.addBookmark(itemId, { ordinal }).subscribe({
      next: (result) => {
        const bookmark: BookmarkDto = {
          id: result.id, itemId, ordinal, normalizedAnchor: 0,
          label: null, createdAt: new Date().toISOString(),
        };
        this.bookmarks.update((list) => [...list, bookmark].sort((a, b) => a.ordinal - b.ordinal));
      },
      error: () => this.snackBar.open('Could not add the bookmark.', 'Dismiss', { duration: 3000 }),
    });
  }

  /** BookmarksPanelHost: jump to a bookmark's page (panel row tap). */
  jumpToBookmark(bookmark: BookmarkDto): void {
    this.seekToPage(bookmark.ordinal);
  }

  /** BookmarksPanelHost: remove a bookmark (toolbar toggle or panel delete). */
  deleteBookmark(bookmark: BookmarkDto): void {
    this.api.removeBookmark(bookmark.id).subscribe({
      next: () => this.bookmarks.update((list) => list.filter((b) => b.id !== bookmark.id)),
      error: () => this.snackBar.open('Could not remove the bookmark.', 'Dismiss', { duration: 3000 }),
    });
  }

  /**
   * Open the bookmarks panel with this reader as its host (live signals in,
   * actions out — see `BookmarksPanelHost`). Pins the chrome like the phone
   * options sheet does, and releases it on dismiss.
   */
  openBookmarks(): void {
    this.menuOpen.set(true);
    const ref = this.bottomSheet.open(BookmarksPanelComponent, {
      data: this as BookmarksPanelHost,
      panelClass: 'bookmarks-panel-sheet',
      ariaLabel: 'Bookmarks',
    });
    ref.afterDismissed().subscribe(() => this.onMenuClosed());
  }

  /**
   * One-shot onboarding: on the first reader open on this device, surface the help
   * overlay so the (otherwise invisible) tap zones and shortcuts are discoverable,
   * then mark it seen so it never auto-shows again. Guarded per-instance
   * (`autoHelpChecked`) so opening more chapters in the same session doesn't re-run
   * the check; persistence is per-device via localStorage (no backend preference).
   */
  private maybeAutoShowHelp(): void {
    if (this.autoHelpChecked) return;
    this.autoHelpChecked = true;
    if (this.prefs.hasSeenHelp()) return;
    this.prefs.markHelpSeen();
    this.helpVisible.set(true);
    this.revealChrome(); // keep the toolbar up behind the overlay
  }

  @HostListener('document:mousemove', ['$event'])
  onPointerMove(e: MouseEvent): void {
    if (this.phase() !== 'ready') return;
    // Only a move into the TOP hot-zone reveals chrome (desktop). Moving the mouse
    // elsewhere while reading must not pop the bar; touch reveals via the centre tap.
    if (e.clientY > ReaderComponent.RevealHotZonePx) return;
    const now = Date.now();
    if (now - this.lastPointerReveal < 120) return; // throttle change-detection churn
    this.lastPointerReveal = now;
    this.revealChrome();
  }

  private chromeLocked(): boolean {
    return this.menuOpen() || this.toolbarHover();
  }

  private scheduleChromeHide(): void {
    this.clearHideTimer();
    this.hideTimer = setTimeout(() => {
      // Auto-hide only while fullscreen; windowed reading keeps the chrome visible.
      if (this.isFullscreen() && this.phase() === 'ready' && !this.chromeLocked()) {
        this.chromeVisible.set(false);
      }
    }, ReaderComponent.ChromeIdleMs);
  }

  private clearHideTimer(): void {
    if (this.hideTimer) { clearTimeout(this.hideTimer); this.hideTimer = null; }
  }

  // --- Loading / readiness (unchanged manifest-first flow) ---

  private loadManifest(): void {
    this.phase.set('preparing');
    // Keep chrome visible while not reading (so Back stays reachable); the idle
    // auto-hide only runs once a page is on screen.
    this.chromeVisible.set(true);
    this.clearHideTimer();
    this.statusMessage.set(this.pollAttempts === 0 ? 'Loading…' : 'Preparing this archive…');

    this.api.getManifest(this.itemId()).subscribe({
      next: (res) => {
        if (this.looksLikeManifest(res)) {
          this.onManifestReady(res as ItemManifest);
        } else {
          this.onReadiness(res as unknown as ItemReadiness);
        }
      },
      error: (err: ApiError) => this.onLoadError(err),
    });
  }

  private looksLikeManifest(res: unknown): boolean {
    return !!res && Array.isArray((res as ItemManifest).pages);
  }

  private onReadiness(readiness: ItemReadiness): void {
    const terminal = this.terminalReadinessMessage(readiness);
    if (terminal) { this.fail(terminal); return; }
    this.scheduleRetry();
  }

  private onManifestReady(manifest: ItemManifest): void {
    this.pages.set(manifest.pages);
    this.contentVersion = manifest.contentVersion;
    this.spreadLayout.set(normalizeSpreadStarts(manifest.spreadStarts, manifest.pages.length));
    // The page aspect ratio is only knowable once the manifest is in; re-target
    // before the first <img> resolves its src.
    this.refreshVariantTarget();
    if (manifest.pages.length === 0) {
      this.fail('This archive has no readable pages.');
      return;
    }
    const last = manifest.pages.length - 1;
    this.api.getProgress(this.itemId()).subscribe({
      next: (progress) => {
        this.revision = progress.revision;
        // 1.9.0 open-position rule: the server resolves where a READ archive should
        // open (finished-on-last-page / "always open read from start") into
        // openPageIndex, non-destructively. Unread/Reading items resume as before
        // (openPageIndex == pageIndex). Older servers omit it — fall back to pageIndex.
        const resume = progress.openPageIndex ?? progress.pageIndex;
        const start = this.landOnLastPage
          ? last
          : Math.min(Math.max(resume, 0), last);
        this.consumeLandIntent();
        this.showPage(start);
      },
      error: () => {
        const start = this.landOnLastPage ? last : 0;
        this.consumeLandIntent();
        this.showPage(start);
      },
    });
  }

  /**
   * Applies the "land on last page" intent once: records that the reader is now
   * parked on that landed page (so <see cref="saveProgress"/> won't persist a false
   * completion) and clears the one-shot request.
   */
  private consumeLandIntent(): void {
    this.landedOnLastPage = this.landOnLastPage;
    this.landOnLastPage = false;
  }

  private showPage(index: number): void {
    this.currentPage.set(index);
    this.pageLoading.set(true);
    this.phase.set('ready');
    // Show the chrome briefly on entry, then let it auto-hide for immersion.
    this.revealChrome();
    // Onboarding (1.9.0): auto-show the help overlay the FIRST time this device
    // opens the reader, then remember it (localStorage, per-device) so it never
    // auto-shows again. The '?' button still reopens it manually any time.
    this.maybeAutoShowHelp();
    // iPhone/iPad in a browser tab: first-open "Add to Home Screen" hint (1.23.0). When the
    // help overlay auto-showed, closeHelp() raises it instead, so the two never stack.
    if (!this.helpVisible()) this.installHint.onReaderOpened();
    this.prefetchAround(index);
    if (this.view() === 'webtoon') {
      // Warm the first few pages ahead on entry (before any scroll fires).
      this.prefetchWebtoonAhead(index);
      // Scroll the saved page into view once the DOM is present.
      queueMicrotask(() => this.scrollWebtoonTo(index));
    }
  }

  // Bounded client-side page prefetch: warm the browser cache with the next few
  // (and previous) page images so paging is instant. Page URLs are immutable
  // (content-version-keyed cache headers), so a prefetched image is reused by the
  // reader's <img> without a re-transfer. Ahead-heavy since reading is
  // overwhelmingly forward; manga flips fast, so keep a generous forward buffer.
  //
  // Webtoon (vertical) read-ahead: the paged/spread
  // prefetch above is skipped in webtoon (native lazy-load + aspect placeholders
  // reserve layout). But a fast vertical scroll can outrun native lazy-load and hit
  // unloaded pages. So onWebtoonScroll warms the next few pages ahead of the scroll
  // position via prefetchWebtoonAhead, reusing the same content-version-keyed URLs
  // and the shared dedup/ref pool below. It never crosses the chapter boundary
  // (pages() is per-chapter; clamping to n enforces it). Thresholds are named
  // constants so the main agent can tune feel on real iPad/Android hardware.
  private static readonly PrefetchAhead = 6;
  private static readonly PrefetchBehind = 2;
  /** Pages to warm ahead of the webtoon scroll position (N≈3–4; tunable). */
  private static readonly WebtoonPrefetchAhead = 4;
  private readonly prefetchedUrls = new Set<string>();
  private prefetchImgs: HTMLImageElement[] = [];

  private prefetchAround(index: number): void {
    if (this.view() === 'webtoon') return;
    const all = this.pages();
    const n = all.length;
    if (n === 0) return;

    const targets: number[] = [];
    for (let d = 1; d <= ReaderComponent.PrefetchAhead; d++) {
      if (index + d < n) targets.push(index + d);
    }
    for (let d = 1; d <= ReaderComponent.PrefetchBehind; d++) {
      if (index - d >= 0) targets.push(index - d);
    }

    this.prefetchIndices(targets);
  }

  /**
   * Scroll-driven webtoon prefetch: warm the next {@link WebtoonPrefetchAhead}
   * pages ahead of the current scroll position so a fast vertical scroll doesn't
   * outrun native lazy-load. Reuses the shared content-version-keyed URL pool and
   * dedup set; never crosses the chapter boundary (pages() is per-chapter).
   */
  private prefetchWebtoonAhead(fromIndex: number): void {
    const all = this.pages();
    const n = all.length;
    if (n === 0) return;
    const targets: number[] = [];
    for (let d = 1; d <= ReaderComponent.WebtoonPrefetchAhead; d++) {
      if (fromIndex + d < n) targets.push(fromIndex + d);
    }
    this.prefetchIndices(targets);
  }

  /**
   * Shared prefetch worker: fetches the given page indices into hidden Image
   * objects so the browser warms its cache. URLs are content-version-keyed and
   * immutable, so a prefetched image is reused by the reader's <img> without a
   * re-transfer. Dedup via prefetchedUrls; refs are bounded to avoid GC before
   * caching without leaking across a long reading session.
   */
  private prefetchIndices(targets: number[]): void {
    const all = this.pages();
    for (const i of targets) {
      const url = this.pageUrlFor(all[i]);
      if (!url || this.prefetchedUrls.has(url)) continue;
      this.prefetchedUrls.add(url);
      const img = new Image();
      img.decoding = 'async';
      img.src = url; // browser fetches + caches; the reader <img> reuses it
      this.prefetchImgs.push(img);
      if (this.prefetchImgs.length > 24) this.prefetchImgs.shift();
    }
  }

  private onLoadError(err: ApiError): void {
    if (err?.error === 'not_analyzed' || err?.error === 'preparing') { this.scheduleRetry(); return; }
    this.fail(this.mapErrorCode(err?.error, err?.message));
  }

  private scheduleRetry(): void {
    if (this.destroyed) return;
    this.pollAttempts++;
    this.statusMessage.set('Preparing this archive…');
    if (this.pollAttempts > 40) {
      this.fail('Preparing is taking longer than expected. Please try again.');
      return;
    }
    const delay = Math.min(1000 + this.pollAttempts * 500, 5000);
    this.clearPoll();
    this.pollTimer = setTimeout(() => this.loadManifest(), delay);
  }

  private terminalReadinessMessage(r: ItemReadiness): string | null {
    const s = String(r.state);
    if (s === 'Failed') return this.mapErrorCode(r.error, 'This archive could not be analyzed.');
    if (s === 'Unsupported') return this.mapErrorCode(r.error, 'This archive format is not supported.');
    if (s === 'Encrypted') return 'This archive is password-protected and cannot be opened.';
    if (s === 'Missing') return 'The source file is no longer available.';
    return null;
  }

  private mapErrorCode(code: string | null | undefined, fallback?: string | null): string {
    switch (code) {
      case 'not_analyzed':
      case 'preparing': return 'Preparing this archive…';
      case 'source_missing': return 'The source file is no longer available.';
      case 'not_readable':
      case 'unsupported': return "This item can't be read.";
      case 'encrypted': return 'This archive is password-protected.';
      case 'unsupported_solid': return 'Solid archives are not yet supported for reading.';
      case 'page_not_found': return 'That page could not be found.';
      case 'extraction_failed':
      case 'extraction_error': return 'This page could not be extracted from the archive.';
      case 'not_found': return 'This item no longer exists.';
      default: return fallback || 'Something went wrong loading this archive.';
    }
  }

  private fail(message: string): void {
    this.clearPoll();
    this.clearHideTimer();
    this.chromeVisible.set(true); // keep Back / retry reachable on the error screen
    this.statusMessage.set(message);
    this.phase.set('error');
  }

  retry(): void { this.pollAttempts = 0; this.loadManifest(); }

  // --- Page image lifecycle ---

  onPageLoaded(): void { this.pageLoading.set(false); this.scheduleMeasure(); }
  onPageError(): void {
    this.pageLoading.set(false);
    this.snackBar.open('This page could not be loaded.', 'Dismiss', { duration: 4000 });
  }

  // --- Navigation ---

  /**
   * Advance toward the end (next screen). In spread view, jumps a whole spread.
   * When already on the last screen, the forward gesture auto-advances to the next
   * chapter (next archive in the folder), if there is one.
   */
  nextPage(): void {
    if (this.isAtEnd()) { this.goToNextChapter(); return; }
    this.goToPage(this.nextIndexFrom(this.currentPage(), +1));
  }
  /**
   * Advance toward the start (previous screen). When already on the first screen,
   * the backward gesture auto-advances to the previous archive, landing on ITS last
   * page (mirror of {@link nextPage}), if there is one.
   */
  prevPage(): void {
    if (this.isAtStart()) { this.goToPreviousChapter(); return; }
    this.goToPage(this.nextIndexFrom(this.currentPage(), -1));
  }

  /** True when the current screen is the last page (paged) or last spread (spread). */
  private isAtEnd(): boolean {
    const n = this.pageCount();
    if (n === 0) return false;
    if (this.effectiveView() === 'spread') {
      const groups = this.spreads();
      const gi = groups.findIndex((g) => g.includes(this.currentPage()));
      return gi !== -1 && gi === groups.length - 1;
    }
    return this.currentPage() >= n - 1;
  }

  /** True when the current screen is the first page (paged) or first spread (spread). */
  private isAtStart(): boolean {
    const n = this.pageCount();
    if (n === 0) return false;
    if (this.effectiveView() === 'spread') {
      const groups = this.spreads();
      const gi = groups.findIndex((g) => g.includes(this.currentPage()));
      return gi === 0;
    }
    return this.currentPage() <= 0;
  }

  /** Auto-advance to the next archive in the folder (or tell the reader there's none). */
  private goToNextChapter(): void {
    const next = this.nextNeighbor();
    if (!next) {
      this.snackBar.open('You’ve reached the end. No next archive in this folder.', 'Dismiss', { duration: 3000 });
      return;
    }
    this.saveProgress();
    // 1.7.3: this in-reader transition reuses the SAME component instance (the
    // route only changes :itemId), so ngOnDestroy never runs for the chapter
    // being left — without this notify, the just-finished item's browse card
    // and the Continue row stayed stale until the reader was closed entirely.
    this.readState.notifyChanged(this.itemId());
    this.snackBar.open(`Next archive: ${next.displayName}`, '', { duration: 2000 });
    // 1.7.1 (owner-approved): REPLACE the history entry so chapter-to-chapter
    // navigation via the buttons never builds a chain Back has to walk — Back
    // from any chapter reached this way exits straight to the folder.
    this.router.navigate(['/reader', next.id], { replaceUrl: true });
  }

  /**
   * Auto-advance to the previous archive in the folder, landing on its LAST page
   * (via the at=end query param), or tell the reader there's no previous archive.
   */
  private goToPreviousChapter(): void {
    const prev = this.prevNeighbor();
    if (!prev) {
      this.snackBar.open('You’re at the start. No previous archive in this folder.', 'Dismiss', { duration: 3000 });
      return;
    }
    this.saveProgress();
    // 1.7.3: same in-reader-transition notify as goToNextChapter — see there.
    this.readState.notifyChanged(this.itemId());
    this.snackBar.open(`Previous archive: ${prev.displayName}`, '', { duration: 2000 });
    // 1.7.1: same replaceUrl treatment as goToNextChapter — Back always exits
    // to the folder, never walks a chain of previously-visited chapters.
    this.router.navigate(['/reader', prev.id], { queryParams: { at: 'end' }, replaceUrl: true });
  }

  /**
   * Reader-bar chapter arrows and the webtoon end-of-chapter
   * footer both call these. They reuse the exact same chapter-navigation path as
   * the auto-advance gestures (progress saved, snackbar, `/reader/:id` navigation);
   * the toolbar buttons are disabled when there is no neighbor, so these are safe
   * to call unconditionally.
   */
  nextChapter(): void { this.goToNextChapter(); }
  prevChapter(): void { this.goToPreviousChapter(); }

  /** Next index in reading order, spread-aware (steps over the current spread). */
  private nextIndexFrom(from: number, dir: 1 | -1): number {
    if (this.effectiveView() !== 'spread') return from + dir;
    const groups = this.spreads();
    const gi = groups.findIndex((g) => g.includes(from));
    if (gi === -1) return from + dir;
    const target = groups[gi + dir];
    return target ? target[0] : from + dir;
  }

  private goToPage(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (clamped === this.currentPage()) return;
    // Resolve the transition enter-side from the travel direction before the page
    // swaps, so the freshly mounted <img> animates in from the correct edge.
    this.navEnter.set(this.enterSideForNav(clamped > this.currentPage()));
    this.holdOutgoing();
    this.currentPage.set(clamped);
    this.pageLoading.set(true);
    this.prefetchAround(clamped);
    this.saveProgress();
  }

  /**
   * Keep the page(s) about to leave rendered underneath the incoming row for the
   * length of the chosen transition, then drop them. Only while
   * a transition will actually play: not for 'none', not where `pageAnimActive`
   * gates the animation off, and not under prefers-reduced-motion (the CSS
   * disables the keyframes there, so a ghost would just sit under the new page).
   */
  private holdOutgoing(): void {
    const ms = ReaderComponent.PageTurnMs[this.prefs.pageAnimation()];
    if (ms === 0 || !this.pageAnimActive() || prefersReducedMotion()) return;
    this.outgoing.set(this.currentSpreadEntries());
    if (this.outgoingTimer) clearTimeout(this.outgoingTimer);
    this.outgoingTimer = setTimeout(() => this.outgoing.set([]), ms);
  }

  // --- Page scrubber: draggable position control on the rail ---

  /**
   * Map a fraction (0..1) along the rail to a page index, direction-aware — in RTL
   * the rail fills from the right, so the fraction is mirrored. Pure/clamped so it
   * is unit-testable independently of the DOM.
   */
  private pageForRailFraction(frac: number): number {
    const n = this.pageCount();
    if (n === 0) return 0;
    let f = Math.min(1, Math.max(0, frac));
    if (this.direction() === 'rtl') f = 1 - f;
    return Math.round(f * (n - 1));
  }

  /** Begin scrubbing: reveal the prominent bubble and land the first position. */
  onScrubStart(event: PointerEvent): void {
    if (this.pageCount() === 0) return;
    this.scrubbing.set(true);
    const el = event.currentTarget as HTMLElement;
    el.setPointerCapture?.(event.pointerId); // keep receiving moves outside the strip
    this.scrubFromClientX(event.clientX, el);
    event.preventDefault();
  }

  /** While dragging, update the current page LIVE (no progress save on each tick). */
  onScrubMove(event: PointerEvent): void {
    if (!this.scrubbing()) return;
    this.scrubFromClientX(event.clientX, event.currentTarget as HTMLElement);
  }

  /** Release: land on the current page and persist progress once. */
  onScrubEnd(event: PointerEvent): void {
    if (!this.scrubbing()) return;
    this.scrubbing.set(false);
    (event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId);
    this.saveProgress();
  }

  private scrubFromClientX(clientX: number, el: HTMLElement): void {
    const rect = el.getBoundingClientRect();
    const r = ReaderComponent.ScrubThumbRadiusPx;
    const width = rect.width - 2 * r; // the thumb's travel, inset by its radius at both ends
    if (width <= 0) return;
    this.scrubApply(this.pageForRailFraction((clientX - rect.left - r) / width));
  }

  /**
   * Live scrub: move to a page without saving (save happens once on release). In
   * webtoon this scrolls to the target so the position indicator tracks the scroll;
   * paged/spread swap the visible page. Skips the no-op when the page is unchanged.
   */
  private scrubApply(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (this.view() === 'webtoon') {
      this.currentPage.set(clamped);
      this.scrollWebtoonTo(clamped);
      return;
    }
    if (clamped === this.currentPage()) return;
    this.currentPage.set(clamped);
    this.pageLoading.set(true);
    this.prefetchAround(clamped);
  }

  /** Keyboard seek on the focused rail: arrows step a page (direction-aware); Home/End jump to the ends. */
  onRailKey(event: KeyboardEvent): void {
    const n = this.pageCount();
    if (n === 0) return;
    const rtl = this.direction() === 'rtl';
    let target: number;
    switch (event.key) {
      case 'ArrowRight': target = this.currentPage() + (rtl ? -1 : 1); break;
      case 'ArrowLeft': target = this.currentPage() + (rtl ? 1 : -1); break;
      case 'Home': target = 0; break;
      case 'End': target = n - 1; break;
      default: return;
    }
    event.preventDefault();
    // Stop the window-level reader shortcuts from ALSO handling the arrow (that was
    // a two-page step per press on the focused slider, browser-verified 1.8.0).
    event.stopPropagation();
    this.seekToPage(target);
  }

  /** Applies a seek target (clamped) across all view modes. */
  private seekToPage(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (this.view() === 'webtoon') {
      this.currentPage.set(clamped);
      this.scrollWebtoonTo(clamped); // fires onWebtoonScroll → reconciles + debounced save
    } else {
      this.goToPage(clamped);
    }
  }

  /**
   * Edge-tap navigation. The invisible left/right zones ARE direction-aware
   * (tap the right side in RTL to go back) — this is expected reader behavior and
   * distinct from the page slider, which is the visible chevron controls.
   */
  onEdge(side: 'prev' | 'next'): void {
    // A completed swipe fires a ghost click on the zone it ended over; swallow it so
    // the swipe doesn't also count as an edge tap (double page turn).
    if (this.recentlySwiped()) return;
    const forward = side === 'next';
    (forward !== (this.direction() === 'rtl')) ? this.nextPage() : this.prevPage();
  }

  /** Center tap zone: toggle chrome, unless a swipe just ended here (ghost click). */
  onCenterTap(): void {
    if (this.recentlySwiped()) return;
    this.toggleChrome();
  }

  private recentlySwiped(): boolean {
    return Date.now() - this.lastSwipeAt < ReaderComponent.SwipeClickSuppressMs;
  }

  // --- Swipe gestures: direction-aware page turning on touch ---

  /**
   * Resolve a horizontal drag into a page action, or null when it isn't a page
   * swipe. Rejects predominantly-vertical drags (so native vertical scrolling and
   * pinch-pans are never stolen) and drags too short to be deliberate unless they
   * are a fast flick. Direction-aware to match the edge zones exactly: LTR swipe
   * left = next, right = prev; RTL (manga) mirrors it (swipe left = previous).
   */
  resolveSwipe(dx: number, dy: number, dtMs: number): 'next' | 'prev' | null {
    const side = this.horizontalSwipe(dx, dy, dtMs);
    if (!side) return null;
    const rtl = this.direction() === 'rtl';
    // leftward in LTR advances; leftward in RTL goes back (XOR with the RTL flag).
    return ((side === 'leftward') !== rtl) ? 'next' : 'prev';
  }

  /**
   * Webtoon variant: the same gesture thresholds, but the result
   * is a SCREEN STEP, not a page. Fixed mapping - a vertical strip has no reading
   * direction - swipe left (finger moves left) = forward, right = back, matching
   * the LTR paged reader most webtoon readers already know.
   */
  resolveWebtoonSwipe(dx: number, dy: number, dtMs: number): 1 | -1 | null {
    const side = this.horizontalSwipe(dx, dy, dtMs);
    return side ? (side === 'leftward' ? 1 : -1) : null;
  }

  /** The shared thresholds: which way a deliberate horizontal drag or flick went, or null. */
  private horizontalSwipe(dx: number, dy: number, dtMs: number): 'leftward' | 'rightward' | null {
    const absX = Math.abs(dx);
    const absY = Math.abs(dy);
    if (absX === 0) return null;
    if (absY > absX * ReaderComponent.SwipeMaxOffAxisRatio) return null; // too vertical
    const velocity = dtMs > 0 ? absX / dtMs : Infinity;
    const farEnough = absX >= ReaderComponent.SwipeMinDistancePx;
    const flick = velocity >= ReaderComponent.SwipeFlickVelocity
      && absX >= ReaderComponent.SwipeFlickMinDistancePx;
    if (!farEnough && !flick) return null;
    return dx < 0 ? 'leftward' : 'rightward';
  }

  /**
   * Pointer down on the reader surface (the paged viewport, or the webtoon
   * scroller since 1.11.0) starts a swipe candidate. The rest of the gesture is
   * tracked at DOCUMENT level (the host listeners below), so a swipe that ends
   * over the toolbar, the bottom bar or outside the window still resolves. Nothing
   * is claimed here when the browser owns the drag (touchAction 'auto':
   * overflowing/zoomed page), or in webtoon while tap-to-scroll is off / zoomed.
   */
  onReaderPointerDown(e: PointerEvent): void {
    if (e.pointerType === 'mouse' && e.button !== 0 && e.button !== undefined) return;
    this.activePointers.add(e.pointerId);
    // A second concurrent pointer means a pinch/zoom gesture — abandon any swipe so
    // we never fight the browser's native pinch-zoom.
    if (this.activePointers.size > 1) { this.abandonSwipe(); return; }
    const claim = this.view() === 'webtoon' ? this.webtoonSwipeEnabled() : this.touchAction() !== 'auto';
    if (!claim) { this.swipePointerId = null; return; }
    this.swipePointerId = e.pointerId;
    this.swipeStartX = e.clientX;
    this.swipeStartY = e.clientY;
    this.swipeStartT = e.timeStamp;
    this.swipeCancelled = false;
    this.swipeAxis = 'none';
    this.swipeDragged = false;
  }

  /** Follow the finger: lock the axis past the slop, then drag the spread row along. */
  @HostListener('document:pointermove', ['$event'])
  onReaderPointerMove(e: PointerEvent): void {
    if (this.swipePointerId !== e.pointerId || this.swipeCancelled) return;
    const dx = e.clientX - this.swipeStartX;
    const dy = e.clientY - this.swipeStartY;
    if (this.swipeAxis === 'none') {
      if (Math.max(Math.abs(dx), Math.abs(dy)) < ReaderComponent.SwipeSlopPx) return;
      this.swipeAxis = Math.abs(dy) > Math.abs(dx) * ReaderComponent.SwipeMaxOffAxisRatio ? 'y' : 'x';
    }
    if (this.swipeAxis === 'y') return; // vertical: native scroll (pan-y) or nothing — never ours
    this.swipeDragged = true;
    // The webtoon column does not follow the finger (a strip has nothing to peek
    // at sideways); the gesture only resolves on release.
    if (this.view() !== 'webtoon') this.swipeDx.set(this.followDx(dx));
  }

  /**
   * Visual follow offset for a horizontal drag: clamped to the viewport width and
   * rubber-banded (resisted) when there is no page or chapter in that direction,
   * so the end of a folder is felt rather than silently ignored.
   */
  private followDx(dx: number): number {
    const width = this.viewport()?.nativeElement.clientWidth || window.innerWidth || 1;
    const clamped = Math.max(-width, Math.min(width, dx));
    const forward = (dx < 0) !== (this.direction() === 'rtl');
    const blocked = forward
      ? (this.isAtEnd() && !this.hasNextChapter())
      : (this.isAtStart() && !this.hasPrevChapter());
    return blocked ? clamped * ReaderComponent.SwipeEdgeResistance : clamped;
  }

  @HostListener('document:pointerup', ['$event'])
  onReaderPointerUp(e: PointerEvent): void {
    this.activePointers.delete(e.pointerId);
    if (this.swipePointerId !== e.pointerId) return; // not the tracked pointer
    this.swipePointerId = null;
    const dragged = this.swipeDragged;
    const lockedVertical = this.swipeAxis === 'y';
    this.resetSwipeFollow();
    if (this.swipeCancelled) { this.swipeCancelled = false; return; }
    // A gesture that started as a vertical drag stays one: no page turn even if the
    // finger drifted sideways before lifting.
    if (lockedVertical) return;
    // Any real drag — resolved into a page turn or not — must not ALSO count as the
    // tap the browser synthesizes on release (a short drag over the centre zone
    // would otherwise toggle the chrome).
    if (dragged) this.lastSwipeAt = Date.now();
    const dx = e.clientX - this.swipeStartX;
    const dy = e.clientY - this.swipeStartY;
    const dt = e.timeStamp - this.swipeStartT;
    if (this.view() === 'webtoon') {
      // Webtoon: a horizontal swipe steps a screen; the ghost
      // click that follows must not ALSO resolve as a tap zone.
      const step = this.resolveWebtoonSwipe(dx, dy, dt);
      if (step) { this.lastSwipeAt = Date.now(); this.scrollWebtoonBy(step); }
      return;
    }
    const action = this.resolveSwipe(dx, dy, dt);
    if (!action) return;
    this.lastSwipeAt = Date.now(); // suppress the follow-up ghost click on the zones
    // A completed swipe drag hands straight off to the slide-in: keep the row's
    // transition suppressed (committing) so the old page doesn't spring back to
    // centre before the new page animates in. No-op when the page didn't visibly
    // drag (a flick) — there's nothing to spring back.
    if (dragged) this.beginCommit();
    action === 'next' ? this.nextPage() : this.prevPage();
  }

  /**
   * Suppress the spread-row spring-back transition for the duration of one page
   * turn, so a swipe commit flows into the incoming-page transition instead of
   * snapping the outgoing page back to centre first. Cleared on a timer just past
   * the transition length.
   */
  private beginCommit(): void {
    this.committing.set(true);
    if (this.commitTimer) clearTimeout(this.commitTimer);
    this.commitTimer = setTimeout(() => this.committing.set(false), 240);
  }

  /**
   * The browser took the gesture (native pan/scroll, pinch, or an OS edge gesture
   * such as the iOS back-swipe): stand down for the rest of this pointer.
   */
  @HostListener('document:pointercancel', ['$event'])
  onReaderPointerCancel(e: PointerEvent): void {
    this.activePointers.delete(e.pointerId);
    if (this.swipePointerId === e.pointerId) { this.swipePointerId = null; this.swipeCancelled = true; this.resetSwipeFollow(); }
  }

  private abandonSwipe(): void {
    this.swipeCancelled = true;
    this.swipePointerId = null;
    this.resetSwipeFollow();
  }

  /** Release the follow offset (the CSS transition springs the row back). */
  private resetSwipeFollow(): void {
    this.swipeDx.set(0);
    this.swipeAxis = 'none';
    this.swipeDragged = false;
  }

  /**
   * Re-measure whether the paged viewport overflows the screen (drives
   * touchAction). Deferred a frame so the image / fit change has laid out.
   */
  private scheduleMeasure(): void {
    if (typeof requestAnimationFrame !== 'function') { this.measureOverflow(); return; }
    requestAnimationFrame(() => this.measureOverflow());
  }

  measureOverflow(): void {
    const el = this.viewport()?.nativeElement;
    if (!el) { this.overflowsX.set(false); this.overflowsY.set(false); return; }
    // Measure the pages' LAYOUT boxes (offsetWidth/Height ignore CSS transforms),
    // never the viewport's scroll extent: a page load lands inside the spread
    // row's 180ms spring-back after a swipe, and a translated row inflates
    // scrollWidth, which misreported a fit-screen page as overflowing, flipped
    // touch-action to 'auto', and had the browser cancel every later swipe
    // (browser-verified 1.8.0 regression, fixed here). The 1.11.0 outgoing ghost
    // row is excluded: it duplicates the page widths for the transition's length.
    const pages = Array.from(el.querySelectorAll<HTMLElement>('.spread-row:not(.outgoing) img'));
    const width = pages.reduce((sum, p) => sum + p.offsetWidth, 0);
    const height = pages.reduce((max, p) => Math.max(max, p.offsetHeight), 0);
    this.overflowsX.set(width > el.clientWidth + 1);
    this.overflowsY.set(height > el.clientHeight + 1);
  }

  /**
   * Exit the reader back to the BROWSE view it was opened from (1.6.1 owner iPad
   * fix) — never unconditionally to Home. Escape (line ~630) shares this method.
   *
   * Prefers walking back through real browser history (`Location.back()`) when the
   * reader was reached via in-app navigation, since that's a genuine `popstate` and
   * restores the browse list's native scroll position for free — a fresh
   * `router.navigate` would reload the list at the top. Angular's Router stamps
   * `navigationId` into `history.state` on every navigation, starting at 1 for the
   * first navigation of the session/tab; `navigationId > 1` means at least one
   * earlier in-app navigation exists to go back to. A deep link or a hard refresh
   * makes the reader the session's first navigation (`navigationId` 1 or unset), so
   * there is nothing to walk back to — fall back to the resolved parent-folder route
   * (never Home) instead.
   */
  goBack(): void {
    this.saveProgress();
    const navigationId = (history.state as { navigationId?: number } | null)?.navigationId ?? 0;
    if (navigationId > 1) {
      this.location.back();
    } else {
      this.router.navigate(this.fallbackBackRoute());
    }
  }

  toggleFullscreen(): void {
    if (!this.isFullscreen()) this.installHint.onFullscreenRequested();
    if (this.useInPageImmersive) {
      // Either Safari's own Fullscreen API paints a persistent system close
      // button over the page and keeps the status bar showing (iOS/iPadOS), or
      // there is no browser chrome to hide via the Fullscreen API at all (a
      // standalone/installed app) — either way, skip it and drive the reader's
      // own in-page immersive mode (isFullscreen) directly. onFullscreenChange
      // ignores fullscreenchange here, so this is the only place isFullscreen moves.
      const next = !this.isFullscreen();
      this.isFullscreen.set(next);
      if (next) { this.scheduleChromeHide(); }
      else { this.chromeVisible.set(true); this.clearHideTimer(); }
      return;
    }
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen?.();
    } else {
      document.exitFullscreen?.();
    }
    // isFullscreen() is updated by the fullscreenchange listener.
  }

  setFitMode(mode: FitMode): void {
    this.fitMode.set(mode);
    this.refreshVariantTarget();
    this.scheduleMeasure();
  }
  toggleDirection(): void { this.direction.update((d) => (d === 'ltr' ? 'rtl' : 'ltr')); }
  /** Explicit direction pick (the phone sheet's radio pair; the bar button toggles). */
  setDirection(direction: ReadingDirection): void { this.direction.set(direction); }

  // --- Webtoon width: per-device preference in localStorage ---

  private static readonly WebtoonWidthKey = 'mangapixer-webtoon-width';

  setWebtoonWidth(pct: number): void {
    const clamped = Math.min(100, Math.max(15, Math.round(pct)));
    this.webtoonWidthPct.set(clamped);
    this.refreshVariantTarget();
    try { localStorage.setItem(ReaderComponent.WebtoonWidthKey, String(clamped)); } catch { /* private mode */ }
  }

  private loadWebtoonWidth(): number {
    try {
      const raw = localStorage.getItem(ReaderComponent.WebtoonWidthKey);
      const n = raw ? parseInt(raw, 10) : NaN;
      if (!Number.isNaN(n)) return Math.min(100, Math.max(15, n));
    } catch { /* private mode / unavailable */ }
    return 70; // sensible default
  }

  // --- Per-device default page mode (1.2.x): stored per device in localStorage ---

  private static readonly ViewPrefKey = 'mangapixer-reader-view';
  private static readonly CoverStandaloneKey = 'mangapixer-reader-cover-standalone';

  private loadViewPref(): ViewPref | null {
    try {
      const raw = localStorage.getItem(ReaderComponent.ViewPrefKey);
      // A stored 'webtoon' is a pre-Option-A value (from before webtoon was pulled
      // out of the device-global pref) — it no longer matches and falls through to
      // null, self-healing the stale sticky value instead of needing a migration.
      if (raw === 'auto' || raw === 'paged' || raw === 'spread') return raw;
    } catch { /* private mode / unavailable */ }
    return null; // unset → follow the server-resolved mode
  }

  private saveViewPref(pref: ViewPref): void {
    try { localStorage.setItem(ReaderComponent.ViewPrefKey, pref); } catch { /* private mode */ }
  }

  private saveCoverStandalone(offset: boolean): void {
    try { localStorage.setItem(ReaderComponent.CoverStandaloneKey, offset ? '1' : '0'); } catch { /* private mode */ }
  }

  private loadCoverStandalone(): boolean {
    try {
      const raw = localStorage.getItem(ReaderComponent.CoverStandaloneKey);
      if (raw === '0') return false;
      if (raw === '1') return true;
    } catch { /* private mode / unavailable */ }
    return true; // default: cover shown standalone
  }

  setView(view: ReaderView): void {
    const wasWebtoon = this.view() === 'webtoon';
    this.view.set(view);
    // Entering Vertical swaps the whole page layout (every page at strip
    // width), so the URLs pinned in paged view are the wrong size for all of
    // them, not just the next ones: re-target everything (1.24.1). Leaving
    // Vertical keeps the pins - the strip-sized pages are big enough.
    if (view === 'webtoon' && !wasWebtoon) this.pageUrlCache.clear();
    this.refreshVariantTarget();
    if (view === 'webtoon' && !wasWebtoon) {
      queueMicrotask(() => this.scrollWebtoonTo(this.currentPage()));
    }
  }

  /**
   * Enter double-page view with a chosen DEVICE cover offset. `offset` true keeps
   * the cover (page 0) standalone then pairs 1-2, 3-4…; false pairs from 0-1, 2-3….
   * Since 1.23.0 this is only the fallback for an archive with no saved layout (the
   * menu goes through `chooseSpread`); spreads() re-derives in place either way.
   */
  setSpread(offset: boolean): void {
    this.coverIsStandalone.set(offset);
    this.setView('spread');
  }

  /**
   * Menu handlers. 'auto' | 'paged' | 'spread' are PAGED-LAYOUT picks: a per-device
   * choice, persisted (survives reloads and applies to future PAGED chapters on
   * this device) in addition to switching the current view. 'webtoon' is CONTENT
   * ORIENTATION, not a layout — picking it is a per-item/session-only override
   * (current view only, never persisted device-global); the server resolution
   * remains authoritative for what a freshly opened item defaults to. This split
   * is the Option A fix (2026-09-11) for the reader-mode-sticky bug, and is what
   * keeps both vertical and paged reachable per item/session without either one
   * bleeding across libraries.
   */
  chooseView(pref: ViewPref | 'webtoon'): void {
    // Leaving double page lands on the spread's FIRST page, so `d` twice without
    // moving never forces a new spread start (see enterSpreadHere).
    if (pref !== 'spread') this.anchorToSpreadStart();
    if (pref === 'webtoon') { this.setView('webtoon'); return; }
    this.viewPref.set(pref);
    this.saveViewPref(pref);
    if (pref === 'auto') this.applyAutoView();
    else this.setView(pref);
  }

  /**
   * A double-page menu entry (1.23.0 semantics): "Double page" (`shifted` false) or
   * "Double page (shifted)". From single page it enters double page with the page
   * being read starting a spread (like `d`). Then, if the spread on screen is not on
   * the picked parity, it re-pairs from this spread onward (`toggleSpreadShift`) -
   * unless entering had to force the page to start a spread: that rule wins (the
   * alternative would be to show the page alone), and the highlight then tells the
   * truth. On the archive's first spread the pick also records the device cover
   * fallback, as the old offset did.
   */
  chooseSpread(shifted: boolean): void {
    const fromSingle = this.view() !== 'spread';
    this.chooseView('spread');
    const forcedStart = fromSingle && this.enterSpreadHere();
    if (!forcedStart && this.pageCount() > 0 && this.spreadShifted() !== shifted) this.toggleSpreadShift();
    if (this.currentSpread().includes(0) && !this.isWide(0)) {
      this.coverIsStandalone.set(shifted);
      this.saveCoverStandalone(shifted);
    }
    this.noteNarrowSpread();
  }

  /**
   * The pick is honoured (persisted, highlighted) but a narrow portrait screen
   * renders single pages; say so once, at the moment of choice, so the unchanged
   * page is not mistaken for a broken setting. The phone sheet carries the same
   * note inline (a snackbar would land under it), so only the desktop path toasts.
   */
  private noteNarrowSpread(): void {
    if (this.narrowPortrait() && !this.optionsOpen()) {
      this.snackBar.open('Double page shows in landscape or on a wider screen.', '', { duration: 2500 });
    }
  }

  // --- Shared per-archive pairing (1.23.0) ---

  /**
   * `o` / picking the other double-page entry: flip the pairing parity from the
   * spread on screen onward (to the next wide page). Saved for the archive, for
   * everyone. No-op outside double page; a wide page or a lone page between two
   * wide ones has nothing to re-pair, which is said rather than silently ignored.
   */
  toggleSpreadShift(): void {
    if (this.view() !== 'spread' || this.pageCount() === 0) return;
    const shift = shiftSpreadAt(this.pageCount(), (i) => this.isWide(i), this.activeSpreadStarts(), this.currentPage());
    if (!shift) {
      this.snackBar.open('Nothing to re-pair on this page.', '', { duration: 2000 });
      return;
    }
    this.applySpreadStarts(shift.starts);
    // Show the re-paired spread from its first page. The entries already on screen
    // keep their <img> (tracked by entry key), so no loading spinner is raised. On a
    // narrow portrait screen (double page chosen, single pages shown) the page being
    // read stays put; the new pairing shows once double page returns.
    if (this.effectiveView() === 'spread') this.currentPage.set(shift.anchor);
  }

  /**
   * Entering double page: the page being read must start a spread. Adds a forced
   * start when it would not already; returns whether it had to.
   */
  private enterSpreadHere(): boolean {
    const next = ensureSpreadStart(this.pageCount(), (i) => this.isWide(i), this.activeSpreadStarts(), this.currentPage());
    if (next) this.applySpreadStarts(next);
    return next !== null;
  }

  /** Before leaving double page, move to the first page of the spread on screen. */
  private anchorToSpreadStart(): void {
    if (this.effectiveView() !== 'spread' || this.pageCount() === 0) return;
    const first = this.currentSpread()[0];
    if (first !== this.currentPage()) this.currentPage.set(first);
  }

  /** Optimistic local update, then a debounced save to the server. */
  private applySpreadStarts(starts: number[]): void {
    this.spreadLayout.set(starts);
    this.pendingSpreadSave = { itemId: this.itemId(), contentVersion: this.contentVersion, starts };
    if (this.spreadSaveTimer) clearTimeout(this.spreadSaveTimer);
    this.spreadSaveTimer = setTimeout(() => this.flushSpreadSave(), ReaderComponent.SpreadSaveDebounceMs);
  }

  /**
   * Send the latest pending layout. Rapid toggles coalesce: one request in flight at
   * a time, and whatever is pending when it settles goes next. The pending entry
   * carries its own item id / content version, so a save flushed on a chapter change
   * still targets the chapter it was made in. A failure keeps the local layout for
   * this session and says so, non-blocking.
   */
  private flushSpreadSave(): void {
    if (this.spreadSaveTimer) { clearTimeout(this.spreadSaveTimer); this.spreadSaveTimer = null; }
    if (this.spreadSaveInFlight || !this.pendingSpreadSave) return;
    const { itemId, contentVersion, starts } = this.pendingSpreadSave;
    this.pendingSpreadSave = null;
    this.spreadSaveInFlight = true;
    this.api.setSpreadLayout(itemId, { expectedContentVersion: contentVersion, spreadStarts: starts }).subscribe({
      next: () => { this.spreadSaveInFlight = false; this.flushSpreadSave(); },
      error: () => {
        this.spreadSaveInFlight = false;
        if (!this.destroyed) {
          this.snackBar.open('Could not save the page pairing. It still applies until you leave this archive.',
            'Dismiss', { duration: 4000 });
        }
        this.flushSpreadSave();
      },
    });
  }

  // --- Webtoon scroll tracking ---

  /** Sub-pixel slack for the "scrolled to the very bottom" check below. */
  private static readonly WebtoonBottomEpsilonPx = 2;

  onWebtoonScroll(): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    const imgs = el.querySelectorAll<HTMLElement>('.webtoon-page');
    let idx = this.currentPage();
    // Reaching the true bottom of the scroller must always resolve to the last
    // page (1.6.1 owner iPad fix), regardless of the centre-crossing heuristic
    // below: a short final page (or one whose aspect ratio isn't reserved yet, so
    // it lays out shorter than half the viewport) may never cross the viewport's
    // vertical centre even at max scroll, leaving completion permanently untriggered.
    const atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - ReaderComponent.WebtoonBottomEpsilonPx;
    if (atBottom && imgs.length > 0) {
      idx = imgs.length - 1;
    } else {
      // Otherwise, the "current" page is the one crossing the vertical centre.
      const center = el.scrollTop + el.clientHeight / 2;
      for (let i = 0; i < imgs.length; i++) {
        const top = imgs[i].offsetTop;
        const bottom = top + imgs[i].offsetHeight;
        if (center >= top && center < bottom) { idx = i; break; }
      }
    }
    if (idx !== this.currentPage()) {
      this.currentPage.set(idx);
      // Warm the next few pages ahead of the scroll position so a fast vertical
      // scroll doesn't outrun native lazy-load.
      this.prefetchWebtoonAhead(idx);
      // Debounce progress writes while scrolling.
      if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
      this.webtoonSaveTimer = setTimeout(() => this.saveProgress(), 600);
    }
    // 1.7.1 (owner revert): webtoon no longer auto-advances chapters on scroll —
    // the scroll-up-at-top gesture fought the fullscreen-exit gesture on touch.
    // The explicit prev/next archive buttons (toolbar + end-of-chapter footer)
    // remain the only way to move between chapters in webtoon.
  }

  private scrollWebtoonTo(index: number): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    const img = el.querySelectorAll<HTMLElement>('.webtoon-page')[index];
    if (img) {
      el.scrollTop = img.offsetTop;
    }
  }

  // --- Webtoon tap-to-scroll (added 1.11.0) ---

  /**
   * A tap on the webtoon scroller. With tap-to-scroll on, the tap resolves by
   * vertical thirds of the visible viewport: top = back a screen, bottom =
   * forward, centre = toggle chrome. Off, a tap anywhere toggles the chrome (the
   * pre-1.11.0 behaviour). Taps on the end-of-chapter footer's buttons are theirs
   * alone, and the ghost click after a swipe is swallowed like the paged zones.
   */
  onWebtoonTap(e: MouseEvent): void {
    if (this.recentlySwiped()) return;
    if ((e.target as HTMLElement | null)?.closest?.('button')) return;
    const el = this.scroller()?.nativeElement;
    if (!el || !this.webtoonNav.tapZonesEnabled()) { this.toggleChrome(); return; }
    const rect = el.getBoundingClientRect();
    const zone = webtoonTapZone(rect.height > 0 ? (e.clientY - rect.top) / rect.height : 0.5);
    if (zone === 'toggle') { this.toggleChrome(); return; }
    this.scrollWebtoonBy(zone === 'forward' ? 1 : -1);
  }

  /**
   * Enter on the focused webtoon scroller toggles the chrome, like a centre tap.
   * Only when the scroller ITSELF has focus: Enter on an end-of-chapter button
   * bubbles here too and must stay that button's alone.
   */
  onWebtoonEnter(e: Event): void {
    if (e.target !== e.currentTarget) return;
    this.toggleChrome();
  }

  /**
   * Move the webtoon scroller one step (`tapStep`% of the visible height) forward
   * or back, clamped to the strip. Smooth unless the reader prefers reduced
   * motion. Reaching the very end is not a chapter advance (1.7.1 revert): the
   * end-of-chapter footer, now on screen, is the explicit way on.
   */
  scrollWebtoonBy(direction: 1 | -1): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    const target = webtoonScrollTarget(
      el.scrollTop, el.clientHeight, el.scrollHeight, this.webtoonNav.tapStep(), direction);
    if (target === el.scrollTop) return;
    if (typeof el.scrollTo === 'function') {
      el.scrollTo({ top: target, behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
    } else {
      el.scrollTop = target;
    }
  }

  private clearPoll(): void {
    if (this.pollTimer) { clearTimeout(this.pollTimer); this.pollTimer = null; }
  }

  /**
   * The page index to persist as "reached" for progress/completion (1.6.1 owner
   * iPad fix). In spread view `currentPage` holds the FIRST index of the displayed
   * pair (see {@link nextIndexFrom}), so on the final spread of an odd-first-index
   * pairing it can sit one page short of the manifest's true last index — the
   * server's completion check (`pageIndex >= pageCount - 1`) then never fires even
   * though both pages of that final pair are on screen. Persisting the spread's
   * LAST index instead reflects everything actually visible, in every view mode.
   */
  private effectivePageIndex(): number {
    if (this.effectiveView() !== 'spread') return this.currentPage();
    const spread = this.spreads().find((s) => s.includes(this.currentPage()));
    return spread ? spread[spread.length - 1] : this.currentPage();
  }

  private saveProgress(): void {
    if (this.phase() !== 'ready' || this.pageCount() === 0) return;
    // Suppress the save while parked on a chapter's last page that we merely landed on
    // via previous-chapter back-navigation — persisting it would complete (and, per the
    // sticky read-mark feature, mark read) a chapter the reader never actually read.
    // The moment they navigate off that last page, resume normal saving.
    if (this.landedOnLastPage) {
      if (this.currentPage() >= this.pageCount() - 1) return;
      this.landedOnLastPage = false;
    }
    const pageIndex = this.effectivePageIndex();
    const entry = this.pages()[pageIndex];
    this.api.updateProgress(this.itemId(), {
      pageIndex,
      expectedContentVersion: this.contentVersion,
      mutationId: this.newMutationId(),
      entryKey: entry?.entryKey,
    }, this.revision).subscribe({
      next: (res) => { this.revision = res.revision; },
      error: (err: ApiError) => {
        if (err?.error === 'precondition_failed') {
          this.api.getProgress(this.itemId()).subscribe({
            next: (p) => { this.revision = p.revision; },
            error: () => { /* leave revision as-is */ },
          });
        }
      },
    });
  }

  private newMutationId(): string {
    const c = globalThis.crypto as Crypto | undefined;
    return c?.randomUUID ? c.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  }

  /** A page counts as "wide" (a pre-stitched two-page spread) at/above this aspect. */
  private static readonly WideAspect = 1.2;

  private isWide(index: number): boolean {
    const p = this.pages()[index];
    return !!p && p.width > 0 && p.height > 0 && p.width / p.height >= ReaderComponent.WideAspect;
  }

  /**
   * Group page indices into double-spread pairs. A page forced to start a spread
   * (the standalone-cover offset is page 1 forced) and an odd trailing page leave
   * the page before / themselves alone; everything else is paired.
   * Indices are ascending within a pair — the template's `.rtl-flow` handles
   * right-to-left placement, so navigation can step whole groups either way.
   *
   * Wide pages (2026-09-08, owner "option A"): a landscape page is a stitched
   * spread, so it is never paired — it forms its own group and pairing resumes
   * after it. A wide page also resets the cadence, which tends to self-correct the
   * offset. A portrait page immediately before a wide page renders solo (it has no
   * portrait partner), which is expected.
   */
  private computeSpreads(): number[][] {
    // 1.23.0: the forced spread starts (saved per archive, else the device cover
    // fallback, which reproduces the old offset exactly) drive the grouping.
    return groupSpreads(this.pageCount(), (i) => this.isWide(i), new Set(this.activeSpreadStarts()));
  }
}
