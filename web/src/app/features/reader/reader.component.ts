import { Component, inject, signal, computed, OnInit, OnDestroy, HostListener, ElementRef, viewChild } from '@angular/core';
import { CommonModule, Location } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSliderModule } from '@angular/material/slider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { ManifestPageEntry, ItemManifest, ItemReadiness, ApiError, ReaderMode } from '../../core/api/api-types';

type ReaderPhase = 'preparing' | 'ready' | 'error';
type ReaderView = 'paged' | 'spread' | 'webtoon';
// Per-device default PAGED LAYOUT (owner request, 1.2.x; scoped to paged-only,
// 2026-09-11 "Option A" fix). A device-local override of single/double/auto page
// layout, distinct from the server's content-semantic ReaderMode. 'auto' picks
// paged (portrait) / spread (landscape) live as the device rotates. `null` (unset)
// means "follow whatever the server resolves for the item". This can no longer
// hold 'webtoon': webtoon-vs-paged is CONTENT ORIENTATION, resolved server-side
// (`ReaderModeResolver`) and must never be forced device-global across libraries
// (that was the reader-mode-sticky bug — vertical bleeding from a webtoon into the
// next manga opened on the same device). Picking "Vertical (webtoon)" from the
// menu is instead a per-item/session-only override; see `chooseView`.
type ViewPref = 'auto' | 'paged' | 'spread';
type FitMode = 'screen' | 'width' | 'height' | 'original';

/**
 * Manifest-first reader (audit defects D3, D14, D36) with paged / double-spread /
 * vertical-webtoon views. All views address pages by the manifest's opaque entry
 * keys, never numeric indices.
 *
 * Reader-view requirements (2026-09-07 review, see the MVP-gap checkpoint):
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
 *     first page (0-1, 2-3…) and "Double page (offset cover)" keeps the cover
 *     standalone then pairs (1-2, 3-4…). Which a comic needs can't be inferred, so
 *     it's an explicit reader choice; see `coverIsStandalone` / `setSpread`.
 *     A wide (landscape) page — typically a pre-stitched two-page spread — is never
 *     paired; it renders solo, full width, in both double modes (see `isWide` /
 *     `computeSpreads`). This also tends to self-correct the pairing cadence.
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
 *     catalog neighbor endpoint); with no next chapter it shows a brief notice.
 *     Applies to paged / spread; webtoon (scroll-driven) is not auto-advanced.
 *  9. Previous-chapter advance (2026-09-09): symmetrically, the backward gesture on
 *     the first screen loads the PREVIOUS archive (`prevNeighbor`) and lands on its
 *     last page (via the `at=end` query param). Merely landing there does not save
 *     progress, so it never falsely completes/marks-read an unread chapter.
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
          @if (phase() === 'ready') { {{ currentPage() + 1 }} / {{ pageCount() }} }
        </span>
        <span class="spacer"></span>

        <!-- Requirement 3 (revised 2026-09-08): controls stay visible in fullscreen. -->
        @if (phase() === 'ready') {
          <!-- 1.7.0 reader-bar CHAPTER arrows: move between archives in the folder
               (distinct from page turning); disabled at the ends of the folder. -->
          <button mat-icon-button (click)="prevChapter()" [disabled]="!hasPrevChapter()"
                  [matTooltip]="prevNeighbor() ? 'Previous chapter: ' + prevNeighbor()!.displayName : 'No previous chapter'"
                  [attr.aria-label]="prevNeighbor() ? 'Previous chapter: ' + prevNeighbor()!.displayName : 'No previous chapter'">
            <mat-icon>skip_previous</mat-icon>
          </button>
          <button mat-icon-button (click)="nextChapter()" [disabled]="!hasNextChapter()"
                  [matTooltip]="nextNeighbor() ? 'Next chapter: ' + nextNeighbor()!.displayName : 'No next chapter'"
                  [attr.aria-label]="nextNeighbor() ? 'Next chapter: ' + nextNeighbor()!.displayName : 'No next chapter'">
            <mat-icon>skip_next</mat-icon>
          </button>
          <button mat-icon-button [matMenuTriggerFor]="modeMenu" matTooltip="Reading mode" aria-label="Reading mode"
                  (menuOpened)="menuOpen.set(true)" (menuClosed)="onMenuClosed()">
            <mat-icon>{{ viewIcon() }}</mat-icon>
          </button>
          <mat-menu #modeMenu="matMenu">
            <button mat-menu-item (click)="chooseView('auto')">
              <mat-icon>{{ viewPref() === 'auto' && view() !== 'webtoon' ? 'check' : 'screen_rotation' }}</mat-icon> Auto (orientation)</button>
            <button mat-menu-item (click)="chooseView('paged')">
              <mat-icon>{{ viewPref() === 'paged' && view() !== 'webtoon' ? 'check' : 'crop_portrait' }}</mat-icon> Single page</button>
            <button mat-menu-item (click)="chooseSpread(false)">
              <mat-icon>{{ viewPref() === 'spread' && !coverIsStandalone() && view() !== 'webtoon' ? 'check' : 'import_contacts' }}</mat-icon> Double page</button>
            <button mat-menu-item (click)="chooseSpread(true)">
              <mat-icon>{{ viewPref() === 'spread' && coverIsStandalone() && view() !== 'webtoon' ? 'check' : 'auto_stories' }}</mat-icon> Double page (offset cover)</button>
            <button mat-menu-item (click)="chooseView('webtoon')">
              <mat-icon>{{ view() === 'webtoon' ? 'check' : 'view_day' }}</mat-icon> Vertical (webtoon)</button>
          </mat-menu>

          @if (view() === 'webtoon') {
            <!-- Requirement 6: webtoon width slider replaces the inoperative fit menu. -->
            <mat-icon class="slider-icon" aria-hidden="true">width_normal</mat-icon>
            <mat-slider class="width-slider" min="30" max="100" step="5"
                        matTooltip="Page width" aria-label="Webtoon page width">
              <input matSliderThumb [value]="webtoonWidthPct()"
                     (valueChange)="setWebtoonWidth($event)" aria-label="Webtoon page width">
            </mat-slider>
          } @else {
            <button mat-icon-button [matMenuTriggerFor]="fitMenu" matTooltip="Image fit" aria-label="Image fit"
                    (menuOpened)="menuOpen.set(true)" (menuClosed)="onMenuClosed()">
              <mat-icon>aspect_ratio</mat-icon>
            </button>
            <mat-menu #fitMenu="matMenu">
              <button mat-menu-item (click)="setFitMode('screen')">Fit screen</button>
              <button mat-menu-item (click)="setFitMode('width')">Fit width</button>
              <button mat-menu-item (click)="setFitMode('height')">Fit height</button>
              <button mat-menu-item (click)="setFitMode('original')">Original size</button>
            </mat-menu>
          }

          @if (view() !== 'webtoon') {
            <button mat-icon-button (click)="toggleDirection()"
                    [matTooltip]="direction() === 'rtl' ? 'Right-to-left (manga)' : 'Left-to-right'"
                    [attr.aria-label]="direction() === 'rtl' ? 'Switch to left-to-right' : 'Switch to right-to-left'">
              <mat-icon>{{ direction() === 'rtl' ? 'format_textdirection_r_to_l' : 'format_textdirection_l_to_r' }}</mat-icon>
            </button>
          }

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
        <!-- Vertical continuous scroll; progress tracked by scroll position. -->
        <div class="reader-viewport webtoon" #scroller (scroll)="onWebtoonScroll()" (click)="toggleChrome()">
          @for (entry of pages(); track entry.entryKey) {
            <img class="webtoon-page" [src]="pageUrlFor(entry)" loading="lazy"
                 [style.width.%]="webtoonWidthPct()"
                 [style.aspect-ratio]="aspectRatioFor(entry)"
                 [attr.data-index]="$index" alt="Page {{ $index + 1 }}" />
          }
          <!-- End-of-chapter affordance: explicit Previous/Next chapter buttons
               (1.7.1: webtoon no longer auto-advances on scroll — owner revert).
               Styled inline to stay within the component CSS budget. -->
          <div class="webtoon-end"
               style="width:100%; box-sizing:border-box; display:flex; flex-direction:column;
                      align-items:center; gap:14px; padding:40px 16px 64px; color:#ccc; text-align:center;">
            <button mat-stroked-button (click)="prevChapter()" [disabled]="!hasPrevChapter()"
                    style="min-width:200px;">
              <mat-icon>skip_previous</mat-icon> Previous chapter
            </button>
            <p style="margin:0; opacity:0.7; font-size:14px;">
              {{ nextNeighbor() ? 'Tap Next chapter to continue' : 'End of this folder' }}
            </p>
            <button mat-flat-button color="primary" (click)="nextChapter()" [disabled]="!hasNextChapter()"
                    style="min-width:200px;">
              Next chapter <mat-icon>skip_next</mat-icon>
            </button>
          </div>
        </div>
      } @else {
        <!-- Paged or double-spread: fixed viewport, one screen at a time.
             Pointer handlers add direction-aware swipe page-turning (requirement 1).
             Native touch-action is kept: on an overflowing (zoomed) page a horizontal
             drag scrolls natively and fires pointercancel (we treat it as cancelled),
             and two-finger pinch-zoom is guarded off via the multi-pointer check —
             so neither native panning nor pinch-zoom is hijacked. -->
        <div class="reader-viewport"
             (pointerdown)="onReaderPointerDown($event)"
             (pointerup)="onReaderPointerUp($event)"
             (pointercancel)="onReaderPointerCancel($event)">
          @if (pageLoading()) {
            <mat-spinner class="page-spinner" diameter="36"></mat-spinner>
          }
          <div class="spread-row" [class.rtl-flow]="direction() === 'rtl'">
            @for (entry of currentSpreadEntries(); track entry.entryKey) {
              <img
                [src]="pageUrlFor(entry)"
                [class.fit-screen]="fitMode() === 'screen'"
                [class.fit-width]="fitMode() === 'width'"
                [class.fit-height]="fitMode() === 'height'"
                [class.original]="fitMode() === 'original'"
                [class.paired]="currentSpreadEntries().length > 1"
                (load)="onPageLoaded()"
                (error)="onPageError()"
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

      <!-- Persistent minimal cue: a very thin progress bar, always visible.
           In RTL it fills from the right and recedes left as pages advance.
           The bar sits in a taller invisible hit strip so it can be tapped/clicked
           (or arrow-keyed) to jump to a page — interactive page-jump. -->
      @if (phase() === 'ready') {
        <div class="rail-hit" [class.scrubbing]="scrubbing()"
             (pointerdown)="onScrubStart($event)" (pointermove)="onScrubMove($event)"
             (pointerup)="onScrubEnd($event)" (pointercancel)="onScrubEnd($event)"
             (keydown)="onRailKey($event)"
             role="slider" tabindex="0" aria-label="Reading position (drag to scrub pages)"
             [attr.aria-valuemin]="1" [attr.aria-valuemax]="pageCount()"
             [attr.aria-valuenow]="currentPage() + 1">
          <div class="progress-rail" [class.rtl]="direction() === 'rtl'" aria-hidden="true">
            <div class="progress-fill" [style.width.%]="progressPct()"></div>
          </div>
          <!-- Prominent page bubble: appears ONLY while scrubbing, so nothing
               clutters the page during normal reading. Styled inline to stay within
               the component CSS budget; positioned by percent along the rail. -->
          @if (scrubbing()) {
            <div class="scrub-bubble" [style.left.%]="scrubThumbPct()" aria-hidden="true"
                 style="position:absolute; bottom:24px; transform:translateX(-50%);
                        background:rgba(20,20,22,0.96); color:#fff; border:1px solid rgba(255,255,255,0.18);
                        border-radius:8px; padding:6px 12px; font-size:15px; font-weight:600;
                        font-variant-numeric:tabular-nums; white-space:nowrap; pointer-events:none;
                        box-shadow:0 4px 14px rgba(0,0,0,0.4);">
              {{ currentPage() + 1 }} / {{ pageCount() }}
            </div>
          }
        </div>
      }

      <!-- Help overlay (toggled by the toolbar '?' button or the '?' key): shows the
           otherwise-invisible tap zones prominently plus the keyboard shortcuts.
           A full-size backdrop button dismisses it; zones/panel are click-through. -->
      @if (helpVisible()) {
        <div class="help-overlay" role="dialog" aria-modal="true" aria-label="Reader controls">
          <button class="help-backdrop" (click)="closeHelp()" aria-label="Close help"></button>
          @if (view() !== 'webtoon') {
            <div class="help-zones" aria-hidden="true">
              <div class="help-zone side"><mat-icon>chevron_left</mat-icon><span>{{ leftZoneLabel() }}</span></div>
              <div class="help-zone center"><mat-icon>touch_app</mat-icon><span>Show / hide menu</span></div>
              <div class="help-zone side"><mat-icon>chevron_right</mat-icon><span>{{ rightZoneLabel() }}</span></div>
            </div>
          }
          <div class="help-panel">
            <h3>Reader controls</h3>
            <ul>
              @if (view() !== 'webtoon') {
                <li><kbd>←</kbd> <kbd>→</kbd> — previous / next page (follows reading direction)</li>
                <li><kbd>Home</kbd> <kbd>End</kbd> — first / last page</li>
              } @else {
                <li>Scroll to read; tap the page to show or hide the toolbar</li>
              }
              <li><kbd>M</kbd> — show / hide the toolbar</li>
              <li><kbd>F</kbd> — fullscreen · <kbd>Esc</kbd> — exit</li>
              <li><kbd>?</kbd> — this help</li>
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
      width: 100%; height: 100%;
      display: flex; align-items: safe center; justify-content: safe center;
    }
    .spread-row.rtl-flow { flex-direction: row-reverse; }
    /* flex:0 0 auto stops flexbox from shrinking the image (which would defeat
       fit-height / original and re-break fit-width). */
    .spread-row img { display: block; flex: 0 0 auto; }
    /* Requirement 1: fit-screen (contain) is the default. Unlike max-* sizing —
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
    /* Webtoon: full-width column, natural vertical scroll. */
    .reader-viewport.webtoon { flex-direction: column; align-items: center; }
    /* Width is driven by the webtoon width slider (requirement 6), 30–100% of viewport. */
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
    /* Persistent minimal progress cue — a very thin bar pinned to the bottom edge,
       shown regardless of chrome visibility so position is always readable. */
    /* Invisible taller strip that makes the 3px rail a usable tap/click target
       (mouse and touch). Sits at the very bottom, above the reading zones. */
    .rail-hit {
      position: fixed; left: 0; right: 0; bottom: 0; height: 16px;
      z-index: 1002; cursor: pointer;
      display: flex; align-items: flex-end;
    }
    .rail-hit:focus-visible { outline: 2px solid #7c4dff; outline-offset: -2px; }
    .progress-rail {
      position: relative; width: 100%; height: 3px;
      background: rgba(255, 255, 255, 0.14); pointer-events: none;
      display: flex; transition: height .12s ease;
    }
    /* Grow the bar slightly on hover so the seek affordance is discoverable (mouse). */
    .rail-hit:hover .progress-rail, .rail-hit:focus-visible .progress-rail { height: 6px; }
    /* RTL: fill sits at the right edge and grows leftward as pages advance. */
    .progress-rail.rtl { justify-content: flex-end; }
    .progress-fill { height: 100%; flex: none; background: #7c4dff; transition: width .2s ease; }
    /* Scrubber (requirement 2) + webtoon end footer: the bulk of
       these styles are applied inline in the template (kept out of the component
       stylesheet to stay within the CSS budget). Only the scrubbing state that a
       plain inline attribute can't express lives here. */
    .rail-hit.scrubbing { height: 28px; }
    .rail-hit.scrubbing .progress-rail { height: 8px; }
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
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 8px; font-weight: 600; text-align: center; padding: 0 8px;
      border-inline: 1px dashed rgba(255, 255, 255, 0.35);
    }
    .help-zone mat-icon { font-size: 40px; width: 40px; height: 40px; }
    .help-zone.side { flex: 0 0 30%; background: rgba(124, 77, 255, 0.22); }
    .help-zone.center { flex: 0 0 40%; background: rgba(255, 255, 255, 0.10); }
    .help-panel {
      position: absolute; left: 50%; bottom: 12%; transform: translateX(-50%);
      z-index: 2; pointer-events: none; max-width: min(92vw, 440px);
      background: rgba(20, 20, 22, 0.94); color: #eee;
      border: 1px solid rgba(255, 255, 255, 0.15); border-radius: 12px; padding: 16px 20px;
    }
    .help-panel h3 { margin: 0 0 10px; }
    .help-panel ul { margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 8px; font-size: 14px; }
    .help-panel kbd {
      background: #333; border: 1px solid #555; border-radius: 4px;
      padding: 1px 6px; font-family: monospace; font-size: 12px;
    }
    .help-dismiss { margin: 12px 0 0; opacity: 0.65; font-size: 13px; text-align: center; }
    @media (prefers-reduced-motion: reduce) {
      .reader-toolbar, .progress-fill { transition: none; }
    }
  `],
})
export class ReaderComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly readState = inject(ReadStateService);

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  readonly itemId = signal('');
  readonly phase = signal<ReaderPhase>('preparing');
  readonly statusMessage = signal('Loading…');
  readonly currentPage = signal(0);
  readonly pageCount = computed(() => this.pages().length);
  readonly pages = signal<ManifestPageEntry[]>([]);
  readonly pageLoading = signal(true);
  readonly fitMode = signal<FitMode>('screen'); // Requirement 1
  readonly direction = signal<'ltr' | 'rtl'>('ltr');
  readonly view = signal<ReaderView>('paged');
  readonly isFullscreen = signal(false);
  // Double-page pairing phase (the "offset"): when true, page 0 (the cover) is
  // shown alone and pages pair 1-2, 3-4… (right for a typical standalone cover);
  // when false, pairing starts at 0-1, 2-3… No reliable way to infer which a
  // given comic wants, so it's a reader-side toggle (two menu modes). Default on.
  readonly coverIsStandalone = signal(this.loadCoverStandalone());
  readonly webtoonWidthPct = signal<number>(this.loadWebtoonWidth()); // requirement 6
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

  // Help overlay: reveals the (normally invisible) tap zones prominently and lists
  // keyboard shortcuts. The zones are direction-aware — the physical left edge goes
  // "back" in LTR but "forward" in RTL — so the labels follow `direction`.
  readonly helpVisible = signal(false);
  readonly leftZoneLabel = computed(() => this.direction() === 'rtl' ? 'Next page' : 'Previous page');
  readonly rightZoneLabel = computed(() => this.direction() === 'rtl' ? 'Previous page' : 'Next page');

  // Adjacent chapters (archives in the same folder), for auto-advance past the last
  // page (next) and before the first page (previous). Fetched per item from the
  // catalog's neighbor endpoint.
  readonly nextNeighbor = signal<{ id: string; displayName: string } | null>(null);
  readonly prevNeighbor = signal<{ id: string; displayName: string } | null>(null);

  // 1.7.0 reader touch-UX. Chapter-arrow availability (requirement 3): the toolbar
  // prev/next CHAPTER buttons are enabled only when a neighbor archive exists, so
  // they grey out at the ends of a folder. Distinct from page turning.
  readonly hasNextChapter = computed(() => !!this.nextNeighbor());
  readonly hasPrevChapter = computed(() => !!this.prevNeighbor());

  // 1.7.0 page scrubber (requirement 2): `scrubbing` is true only while the reader
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

  // Fallback exit route (2026-09-11, 1.6.1 owner iPad fix): the item's parent-folder
  // browse view, resolved from the catalog node so goBack() can return there even
  // when the reader was deep-linked (no SPA navigation history to walk back through).
  // A library-root item's parentId is "" (CatalogBrowseService), which resolves to
  // the library's root browse route rather than a nested :nodeId segment.
  readonly fallbackBackRoute = signal<string[]>(['/']);

  // Set when this chapter was entered via "previous chapter" back-navigation, which
  // asks to land on the LAST page (query param at=end). While the reader is still
  // sitting on that landed last page we suppress progress saves, so merely backing
  // into a chapter never marks it completed/read (the sticky read-mark stays honest).
  private landOnLastPage = false;
  private landedOnLastPage = false;

  readonly viewIcon = computed(() =>
    this.view() === 'webtoon' ? 'view_day'
      : this.view() === 'spread' ? (this.coverIsStandalone() ? 'auto_stories' : 'import_contacts')
      : 'crop_portrait');

  /** The page indices shown together on the current screen (1 for paged, 1–2 for spread). */
  readonly currentSpreadEntries = computed<ManifestPageEntry[]>(() => {
    const all = this.pages();
    if (all.length === 0) return [];
    if (this.view() !== 'spread') {
      const p = all[this.currentPage()];
      return p ? [p] : [];
    }
    const spread = this.spreads().find((s) => s.includes(this.currentPage())) ?? [this.currentPage()];
    return spread.map((i) => all[i]).filter(Boolean);
  });

  /** Grouping of page indices into spreads (double-page view). */
  readonly spreads = computed<number[][]>(() => this.computeSpreads());


  private contentVersion = 0;
  private revision = 0;
  private pollAttempts = 0;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private webtoonSaveTimer: ReturnType<typeof setTimeout> | null = null;
  private hideTimer: ReturnType<typeof setTimeout> | null = null;
  private lastPointerReveal = 0;
  private static readonly ChromeIdleMs = 3000;
  private static readonly RevealHotZonePx = 80;
  private destroyed = false;

  // --- Swipe gesture state (requirement 1, paged/spread only) ---
  // A single-pointer horizontal drag on the paged viewport turns the page,
  // direction-aware like the edge zones. Multi-touch (pinch-zoom) and vertical
  // drags are ignored so native zoom/scroll are never hijacked.
  private static readonly SwipeMinDistancePx = 45;   // deliberate drag threshold
  private static readonly SwipeFlickMinDistancePx = 20; // shorter if it's a fast flick
  private static readonly SwipeFlickVelocity = 0.5;  // px/ms — a quick flick shortcut
  private static readonly SwipeMaxOffAxisRatio = 0.75; // |dy| must stay below this * |dx|
  private static readonly SwipeClickSuppressMs = 400; // swallow the ghost click after a swipe
  private swipePointerId: number | null = null;
  private swipeStartX = 0;
  private swipeStartY = 0;
  private swipeStartT = 0;
  private swipeCancelled = false;
  private activePointers = 0;
  private lastSwipeAt = 0;

  pageUrlFor(entry: ManifestPageEntry | undefined): string {
    return entry ? `/api/v1/items/${this.itemId()}/pages/${encodeURIComponent(entry.entryKey)}` : '';
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
    this.route.paramMap.subscribe((params) => {
      const id = params.get('itemId') ?? '';
      this.itemId.set(id);
      this.pollAttempts = 0;
      // Reset the page-prefetch cache for the new chapter (URLs are per-item).
      this.prefetchedUrls.clear();
      this.prefetchImgs = [];
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
    });
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
      },
      error: () => { /* keep the Home fallback — item metadata unavailable */ },
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.clearPoll();
    if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
    this.clearHideTimer();
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
  }

  @HostListener('document:fullscreenchange')
  onFullscreenChange(): void {
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
    if (this.phase() !== 'ready') return;
    if (event.key === '?') { this.toggleHelp(); return; }
    if (this.helpVisible() && event.key === 'Escape') { this.closeHelp(); return; }
    if (event.key === 'm') { this.toggleChrome(); return; } // toggle chrome in any view
    if (this.view() === 'webtoon') return; // native scroll drives webtoon

    switch (event.key) {
      case 'ArrowLeft': this.direction() === 'rtl' ? this.nextPage() : this.prevPage(); break;
      case 'ArrowRight': this.direction() === 'rtl' ? this.prevPage() : this.nextPage(); break;
      case 'Home': this.goToPage(0); break;
      case 'End': this.goToPage(this.pageCount() - 1); break;
      case 'f': this.toggleFullscreen(); break;
      case 'Escape': this.isFullscreen() ? this.toggleFullscreen() : this.goBack(); break;
    }
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

  toggleHelp(): void {
    this.helpVisible.update((v) => !v);
    if (this.helpVisible()) this.revealChrome(); // keep the toolbar up behind the overlay
  }

  closeHelp(): void { this.helpVisible.set(false); }

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
    this.statusMessage.set(this.pollAttempts === 0 ? 'Loading…' : 'Preparing this chapter…');

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
    if (manifest.pages.length === 0) {
      this.fail('This chapter has no readable pages.');
      return;
    }
    const last = manifest.pages.length - 1;
    this.api.getProgress(this.itemId()).subscribe({
      next: (progress) => {
        this.revision = progress.revision;
        const start = this.landOnLastPage
          ? last
          : Math.min(Math.max(progress.pageIndex, 0), last);
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
  // Webtoon (vertical) read-ahead (2026-09-10, post-1.3.0 lane D): the paged/spread
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
    this.statusMessage.set('Preparing this chapter…');
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
    if (s === 'Failed') return this.mapErrorCode(r.error, 'This chapter could not be analyzed.');
    if (s === 'Unsupported') return 'This archive format is not supported.';
    if (s === 'Encrypted') return 'This archive is password-protected and cannot be opened.';
    if (s === 'Missing') return 'The source file is no longer available.';
    return null;
  }

  private mapErrorCode(code: string | null | undefined, fallback?: string | null): string {
    switch (code) {
      case 'not_analyzed':
      case 'preparing': return 'Preparing this chapter…';
      case 'source_missing': return 'The source file is no longer available.';
      case 'not_readable':
      case 'unsupported': return "This item can't be read.";
      case 'encrypted': return 'This archive is password-protected.';
      case 'page_not_found': return 'That page could not be found.';
      case 'extraction_failed':
      case 'extraction_error': return 'This page could not be extracted from the archive.';
      case 'not_found': return 'This item no longer exists.';
      default: return fallback || 'Something went wrong loading this chapter.';
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

  onPageLoaded(): void { this.pageLoading.set(false); }
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
   * the backward gesture auto-advances to the previous chapter, landing on ITS last
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
    if (this.view() === 'spread') {
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
    if (this.view() === 'spread') {
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
      this.snackBar.open('You’ve reached the end — no next chapter in this folder.', 'Dismiss', { duration: 3000 });
      return;
    }
    this.saveProgress();
    // 1.7.3: this in-reader transition reuses the SAME component instance (the
    // route only changes :itemId), so ngOnDestroy never runs for the chapter
    // being left — without this notify, the just-finished item's browse card
    // and the Continue row stayed stale until the reader was closed entirely.
    this.readState.notifyChanged(this.itemId());
    this.snackBar.open(`Next chapter: ${next.displayName}`, '', { duration: 2000 });
    // 1.7.1 (owner-approved): REPLACE the history entry so chapter-to-chapter
    // navigation via the buttons never builds a chain Back has to walk — Back
    // from any chapter reached this way exits straight to the folder.
    this.router.navigate(['/reader', next.id], { replaceUrl: true });
  }

  /**
   * Auto-advance to the previous archive in the folder, landing on its LAST page
   * (via the at=end query param), or tell the reader there's no previous chapter.
   */
  private goToPreviousChapter(): void {
    const prev = this.prevNeighbor();
    if (!prev) {
      this.snackBar.open('You’re at the start — no previous chapter in this folder.', 'Dismiss', { duration: 3000 });
      return;
    }
    this.saveProgress();
    // 1.7.3: same in-reader-transition notify as goToNextChapter — see there.
    this.readState.notifyChanged(this.itemId());
    this.snackBar.open(`Previous chapter: ${prev.displayName}`, '', { duration: 2000 });
    // 1.7.1: same replaceUrl treatment as goToNextChapter — Back always exits
    // to the folder, never walks a chain of previously-visited chapters.
    this.router.navigate(['/reader', prev.id], { queryParams: { at: 'end' }, replaceUrl: true });
  }

  /**
   * Reader-bar chapter arrows (requirement 3) and the webtoon end-of-chapter
   * footer both call these. They reuse the exact same chapter-navigation path as
   * the auto-advance gestures (progress saved, snackbar, `/reader/:id` navigation);
   * the toolbar buttons are disabled when there is no neighbor, so these are safe
   * to call unconditionally.
   */
  nextChapter(): void { this.goToNextChapter(); }
  prevChapter(): void { this.goToPreviousChapter(); }

  /** Next index in reading order, spread-aware (steps over the current spread). */
  private nextIndexFrom(from: number, dir: 1 | -1): number {
    if (this.view() !== 'spread') return from + dir;
    const groups = this.spreads();
    const gi = groups.findIndex((g) => g.includes(from));
    if (gi === -1) return from + dir;
    const target = groups[gi + dir];
    return target ? target[0] : from + dir;
  }

  private goToPage(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (clamped === this.currentPage()) return;
    this.currentPage.set(clamped);
    this.pageLoading.set(true);
    this.prefetchAround(clamped);
    this.saveProgress();
  }

  // --- Page scrubber (requirement 2): draggable position control on the rail ---

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
    if (rect.width === 0) return;
    this.scrubApply(this.pageForRailFraction((clientX - rect.left) / rect.width));
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
   * distinct from requirement 2, which is about the visible chevron controls.
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

  // --- Swipe gestures (requirement 1): direction-aware page turning on touch ---

  /**
   * Resolve a horizontal drag into a page action, or null when it isn't a page
   * swipe. Rejects predominantly-vertical drags (so native vertical scrolling and
   * pinch-pans are never stolen) and drags too short to be deliberate unless they
   * are a fast flick. Direction-aware to match the edge zones exactly: LTR swipe
   * left = next, right = prev; RTL (manga) mirrors it (swipe left = previous).
   */
  resolveSwipe(dx: number, dy: number, dtMs: number): 'next' | 'prev' | null {
    const absX = Math.abs(dx);
    const absY = Math.abs(dy);
    if (absX === 0) return null;
    if (absY > absX * ReaderComponent.SwipeMaxOffAxisRatio) return null; // too vertical
    const velocity = dtMs > 0 ? absX / dtMs : Infinity;
    const farEnough = absX >= ReaderComponent.SwipeMinDistancePx;
    const flick = velocity >= ReaderComponent.SwipeFlickVelocity
      && absX >= ReaderComponent.SwipeFlickMinDistancePx;
    if (!farEnough && !flick) return null;
    const leftward = dx < 0;
    const rtl = this.direction() === 'rtl';
    // leftward in LTR advances; leftward in RTL goes back (XOR with the RTL flag).
    return (leftward !== rtl) ? 'next' : 'prev';
  }

  onReaderPointerDown(e: PointerEvent): void {
    this.activePointers++;
    // A second concurrent pointer means a pinch/zoom gesture — abandon any swipe so
    // we never fight the browser's native pinch-zoom.
    if (this.activePointers > 1) {
      this.swipeCancelled = true;
      this.swipePointerId = null;
      return;
    }
    if (this.view() === 'webtoon') return; // webtoon stays native vertical scroll
    this.swipePointerId = e.pointerId;
    this.swipeStartX = e.clientX;
    this.swipeStartY = e.clientY;
    this.swipeStartT = e.timeStamp;
    this.swipeCancelled = false;
  }

  onReaderPointerUp(e: PointerEvent): void {
    this.activePointers = Math.max(0, this.activePointers - 1);
    if (this.swipePointerId !== e.pointerId) return; // not the tracked pointer
    this.swipePointerId = null;
    if (this.swipeCancelled || this.view() === 'webtoon') { this.swipeCancelled = false; return; }
    const action = this.resolveSwipe(
      e.clientX - this.swipeStartX,
      e.clientY - this.swipeStartY,
      e.timeStamp - this.swipeStartT,
    );
    if (!action) return;
    this.lastSwipeAt = Date.now(); // suppress the follow-up ghost click on the zones
    action === 'next' ? this.nextPage() : this.prevPage();
  }

  onReaderPointerCancel(e: PointerEvent): void {
    this.activePointers = Math.max(0, this.activePointers - 1);
    if (this.swipePointerId === e.pointerId) { this.swipePointerId = null; this.swipeCancelled = true; }
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
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen?.();
    } else {
      document.exitFullscreen?.();
    }
    // isFullscreen() is updated by the fullscreenchange listener.
  }

  setFitMode(mode: FitMode): void { this.fitMode.set(mode); }
  toggleDirection(): void { this.direction.update((d) => (d === 'ltr' ? 'rtl' : 'ltr')); }

  // --- Webtoon width (requirement 6): per-device preference in localStorage ---

  private static readonly WebtoonWidthKey = 'mangaplex-webtoon-width';

  setWebtoonWidth(pct: number): void {
    const clamped = Math.min(100, Math.max(30, Math.round(pct)));
    this.webtoonWidthPct.set(clamped);
    try { localStorage.setItem(ReaderComponent.WebtoonWidthKey, String(clamped)); } catch { /* private mode */ }
  }

  private loadWebtoonWidth(): number {
    try {
      const raw = localStorage.getItem(ReaderComponent.WebtoonWidthKey);
      const n = raw ? parseInt(raw, 10) : NaN;
      if (!Number.isNaN(n)) return Math.min(100, Math.max(30, n));
    } catch { /* private mode / unavailable */ }
    return 70; // sensible default
  }

  // --- Per-device default page mode (1.2.x): stored per device in localStorage ---

  private static readonly ViewPrefKey = 'mangaplex-reader-view';
  private static readonly CoverStandaloneKey = 'mangaplex-reader-cover-standalone';

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
    if (view === 'webtoon' && !wasWebtoon) {
      queueMicrotask(() => this.scrollWebtoonTo(this.currentPage()));
    }
  }

  /**
   * Enter double-page view with a chosen pairing offset. `offset` true keeps the
   * cover (page 0) standalone then pairs 1-2, 3-4…; false pairs from 0-1, 2-3….
   * Changing the offset re-derives spreads() (which reads coverIsStandalone), so
   * the current screen re-pairs in place without a reload.
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
    if (pref === 'webtoon') { this.setView('webtoon'); return; }
    this.viewPref.set(pref);
    this.saveViewPref(pref);
    if (pref === 'auto') this.applyAutoView();
    else this.setView(pref);
  }

  chooseSpread(offset: boolean): void {
    this.coverIsStandalone.set(offset);
    this.saveCoverStandalone(offset);
    this.chooseView('spread');
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
      // scroll doesn't outrun native lazy-load (post-1.3.0 lane D).
      this.prefetchWebtoonAhead(idx);
      // Debounce progress writes while scrolling.
      if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
      this.webtoonSaveTimer = setTimeout(() => this.saveProgress(), 600);
    }
    // 1.7.1 (owner revert): webtoon no longer auto-advances chapters on scroll —
    // the scroll-up-at-top gesture fought the fullscreen-exit gesture on touch.
    // The explicit prev/next chapter buttons (toolbar + end-of-chapter footer)
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
    if (this.view() !== 'spread') return this.currentPage();
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
   * Group page indices into double-spread pairs. A standalone cover (page 0) and
   * an odd trailing page each occupy a spread alone; everything else is paired.
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
    const n = this.pageCount();
    if (n === 0) return [];
    const groups: number[][] = [];
    let i = 0;
    // Offset: keep a (non-wide) cover standalone. A wide cover is solo regardless,
    // handled by the loop below.
    if (this.coverIsStandalone() && !this.isWide(0)) { groups.push([0]); i = 1; }
    while (i < n) {
      if (this.isWide(i)) { groups.push([i]); i += 1; continue; }
      if (i + 1 < n && !this.isWide(i + 1)) { groups.push([i, i + 1]); i += 2; }
      else { groups.push([i]); i += 1; }
    }
    return groups;
  }
}
