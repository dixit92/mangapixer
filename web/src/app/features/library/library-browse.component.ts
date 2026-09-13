import { Component, inject, signal, computed, effect, viewChild, ElementRef, NgZone, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDividerModule } from '@angular/material/divider';
import { MatSnackBar } from '@angular/material/snack-bar';
import { forkJoin, of, catchError, filter, switchMap } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { ContinueRowComponent } from '../../shared/continue-row/continue-row.component';
import { CatalogNodeDto, PageResponse, ReaderMode, LibraryViewMode, LibraryGridDensity, LibrarySortOrder, LibrarySortDirection, LibraryReadStateFilter, LibraryViewPreferencesDto, JumpIndexBucketDto, ReadMarkDto, ReadingProgressDto } from '../../core/api/api-types';

/**
 * Library browse component. Shows the actual folder/archive tree with keyset
 * pagination, breadcrumbs, and item state indicators.
 *
 * Selection mode (1.2.0) is the single home for card actions (owner review
 * 2026-09-09): tapping cards selects them (mouse or touch — no hover-only
 * affordances), and a STICKY top bar carries every bulk action applied to the
 * selection — mark read / unread for everyone, plus set/clear reading direction
 * for admins (folders only). There is no competing per-card menu.
 *
 * Range selection (1.7.0, file-browser semantics): Shift-click fills the
 * contiguous range from the ANCHOR (the last individually-selected card) to
 * the clicked card, in the currently displayed sort order; Ctrl/Cmd-click and
 * a plain click both toggle one card and move the anchor. Touch has no shift
 * key, so long-press opens "Select to here" instead (entering select mode
 * first if needed) - tap = one, long-press = range fill. "Select all" /
 * "Select all unread" / "Select all read" act over the currently-listed nodes
 * for whole-folder selection.
 *
 * Infinite scroll + sticky navigation (1.8.0): the manual "Load More" button is
 * replaced by an IntersectionObserver sentinel that appends the next cursor page
 * as the user nears the bottom (the 1.7.3 read-state refresh and the 1.6.2
 * reuse-strategy scroll retention are untouched - pages are APPENDED via
 * `nodes.update`, never rebuilt). The A-Z jump rail is sticky just below the
 * sticky top bar and acts as a scroll-spy position indicator: `activeJump`
 * tracks the topmost visible card's bucket. Tapping the top bar's neutral area
 * scrolls the list back to the top. The initial/per-page item count is a
 * per-user preference (`LibraryViewPreferencesDto.libraryPageSize`).
 */
@Component({
  selector: 'app-library-browse',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatIconModule,
    MatButtonModule,
    MatMenuModule,
    MatTooltipModule,
    MatDividerModule,
    CoverImageDirective,
    FolderRollupBadgeComponent,
    ContinueRowComponent,
  ],
  template: `
    <!-- Sticky top bar: breadcrumbs + Select normally; the merged action set while
         selecting. Sticky so the controls stay reachable when scrolling a long
         folder (touch-friendly — requirement 3). -->
    <div class="browse-bar" #browseBar [class.selecting]="selectMode()"
         (click)="onBarClick($event)">
      @if (!selectMode()) {
        <div class="breadcrumbs">
          <!-- Library root is always a clickable crumb. -->
          <a routerLink="/libraries/{{ libraryId() }}/browse">{{ libraryName() || 'Library' }}</a>
          <!-- Ancestors of the current folder (clickable). -->
          @for (crumb of breadcrumbs(); track crumb.id) {
            <span class="sep"> / </span>
            <a routerLink="/libraries/{{ libraryId() }}/browse/{{ crumb.id }}">{{ crumb.displayName }}</a>
          }
          <!-- The current folder itself: plain, non-clickable text (File Explorer
               behavior - you are already in it). Rendered whenever we are inside a
               folder, even at the first level where there are no ancestor crumbs. -->
          @if (currentFolderName()) {
            <span class="sep"> / </span>
            <span class="current" aria-current="page">{{ currentFolderName() }}</span>
          }
        </div>
        <!-- Card size slider (1.6.0): only meaningful for the Card view. Dragging
             resizes the grid live (input); releasing persists the preference
             (change). Subsumes the old comfortable/compact density toggle.
             1.10.0 (F3): on the PHONE breakpoint the inline control is hidden and the
             slider moves into a submenu (the size-menu-trigger below) to reclaim the
             cramped toolbar width; desktop + iPad keep this inline control unchanged. -->
        @if (viewMode() === 'card') {
          <div class="size-control size-control-inline" matTooltip="Card size">
            <mat-icon class="size-icon">photo_size_select_small</mat-icon>
            <input type="range" class="size-slider" aria-label="Card size"
                   [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep"
                   [value]="cardSize()"
                   (input)="onCardSizeInput($event)"
                   (change)="onCardSizeChange($event)">
            <mat-icon class="size-icon">photo_size_select_large</mat-icon>
          </div>
          <!-- Phone-only submenu trigger for the same slider (hidden on desktop/iPad). -->
          <button mat-icon-button class="size-menu-trigger" [matMenuTriggerFor]="sizeMenu"
                  matTooltip="Card size" aria-label="Card size">
            <mat-icon>photo_size_select_large</mat-icon>
          </button>
          <mat-menu #sizeMenu="matMenu" class="size-options-menu">
            <div class="size-control size-control-menu" (click)="$event.stopPropagation()">
              <mat-icon class="size-icon">photo_size_select_small</mat-icon>
              <input type="range" class="size-slider" aria-label="Card size"
                     [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep"
                     [value]="cardSize()"
                     (input)="onCardSizeInput($event)"
                     (change)="onCardSizeChange($event)">
              <mat-icon class="size-icon">photo_size_select_large</mat-icon>
            </div>
          </mat-menu>
        }
        <button mat-stroked-button class="view-toggle" [matMenuTriggerFor]="viewMenu"
                matTooltip="Change how the library is displayed" aria-label="View options">
          <mat-icon>{{ viewIcon() }}</mat-icon> View
        </button>
        <!-- View menu (1.8.1): the selected option in each of the four submenus is
             marked with an accent COLOR HIGHLIGHT (background + text) rather than a
             checkmark - a clearer selected-state that reads at a glance and frees the
             icon slot to keep showing each option's own glyph. Each item is a
             menuitemradio carrying aria-checked so the single-selection semantics are
             exposed to assistive tech; Material's FocusKeyManager drives arrow-key nav
             off the items regardless of role, so keyboard navigation is unchanged. The
             view-options-menu panel class is the styling hook (the menu renders in a
             CDK overlay outside this component's DOM - see the ::ng-deep block). -->
        <mat-menu #viewMenu="matMenu" class="view-options-menu">
          @for (opt of viewOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [class.selected-option]="viewMode() === opt.value"
                    [attr.aria-checked]="viewMode() === opt.value"
                    (click)="setViewMode(opt.value)">
              <mat-icon>{{ opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <mat-divider></mat-divider>
          <span class="menu-caption">Sort by</span>
          @for (opt of sortOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [class.selected-option]="sort() === opt.value"
                    [attr.aria-checked]="sort() === opt.value"
                    (click)="setSort(opt.value)">
              <mat-icon>{{ opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <span class="menu-caption">Order</span>
          @for (opt of sortDirectionOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [class.selected-option]="sortDirection() === opt.value"
                    [attr.aria-checked]="sortDirection() === opt.value"
                    (click)="setSortDirection(opt.value)">
              <mat-icon>{{ opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
          <!-- 1.10.0 (F5): "Items per load" moved OUT of this menu into User Settings
               as an initial-load / performance option (settings.component.ts). It is a
               per-user preference (LibraryViewPreferencesDto.libraryPageSize), not a
               browse control, so it no longer belongs on the browse toolbar. -->
        </mat-menu>
        <!-- Read-state filter (1.10.0, F1): All / Reading / Read / Unread, at the root
             AND in any subfolder. The selected option uses the same accent COLOR
             HIGHLIGHT as the View menu (F4) via the shared view-options-menu panel
             class. The button shows the active state so the filter is visible at a
             glance; it applies at all sizes (not a layout-breaking change). -->
        <button mat-stroked-button class="filter-toggle" [matMenuTriggerFor]="filterMenu"
                [class.filter-active]="readStateFilter() !== 'all'"
                matTooltip="Filter by read state" aria-label="Filter by read state">
          <mat-icon>filter_list</mat-icon> {{ readStateLabel() }}
        </button>
        <mat-menu #filterMenu="matMenu" class="view-options-menu">
          <span class="menu-caption">Show</span>
          @for (opt of readStateOptions; track opt.value) {
            <button mat-menu-item role="menuitemradio"
                    [class.selected-option]="readStateFilter() === opt.value"
                    [attr.aria-checked]="readStateFilter() === opt.value"
                    (click)="setReadStateFilter(opt.value)">
              <mat-icon>{{ opt.icon }}</mat-icon>
              {{ opt.label }}
            </button>
          }
        </mat-menu>
        <button mat-stroked-button class="select-toggle" (click)="toggleSelectMode()">
          <mat-icon>checklist</mat-icon> Select
        </button>
      } @else {
        <span class="count">{{ selected().size }} selected</span>
        <div class="actions">
          <button mat-button [matMenuTriggerFor]="selectMenu" [disabled]="busy() || nodes().length === 0"
                  matTooltip="Select the whole folder">
            <mat-icon>playlist_add_check</mat-icon><span class="lbl">Select</span>
          </button>
          <mat-menu #selectMenu="matMenu">
            <button mat-menu-item (click)="selectAll()">
              <mat-icon>select_all</mat-icon> Select all
            </button>
            <button mat-menu-item (click)="selectAllUnread()">
              <mat-icon>radio_button_unchecked</mat-icon> Select all unread
            </button>
            <button mat-menu-item (click)="selectAllRead()">
              <mat-icon>check_circle</mat-icon> Select all read
            </button>
          </mat-menu>
          <button mat-button (click)="bulkMarkRead(true)" [disabled]="busy() || selected().size === 0">
            <mat-icon>check_circle</mat-icon><span class="lbl">Mark read</span>
          </button>
          <button mat-button (click)="bulkMarkRead(false)" [disabled]="busy() || selected().size === 0">
            <mat-icon>remove_done</mat-icon><span class="lbl">Mark unread</span>
          </button>
          @if (auth.isAdmin()) {
            <button mat-button [matMenuTriggerFor]="dirMenu"
                    [disabled]="busy() || selectedFolderCount() === 0"
                    matTooltip="Set reading direction for selected folders">
              <mat-icon>swap_horiz</mat-icon><span class="lbl">Direction</span>
            </button>
            <mat-menu #dirMenu="matMenu">
              @for (opt of directionOptions; track opt.label) {
                <button mat-menu-item (click)="bulkSetDirection(opt.value)">{{ opt.label }}</button>
              }
            </mat-menu>
          }
        </div>
        <button mat-stroked-button class="done" (click)="toggleSelectMode()">
          <mat-icon>close</mat-icon> Done
        </button>
      }
    </div>

    @if (jumpBuckets().length > 0) {
      <!-- Sticky (1.8.0): stacked directly under the sticky top bar, whose measured
           height is the rail's sticky offset, so both follow the user down the page. -->
      <nav class="jump-rail" aria-label="Jump to letter" [style.top.px]="barHeight()">
        @for (bucket of jumpBuckets(); track bucket.label) {
          <button class="jump-chip" type="button"
                  (click)="jumpToBucket(bucket)"
                  [class.active]="activeJump() === bucket.label"
                  [matTooltip]="bucket.label + ' (' + bucket.count + ')'">
            {{ bucket.label }}
          </button>
        }
      </nav>
    }

    <app-continue-row [node]="nextUnread()" />

    <div class="nodes" [class.card]="viewMode() === 'card'"
         [class.list]="viewMode() === 'list'"
         [style.--card-size]="cardSize() + 'px'">
      @for (node of nodes(); track node.id) {
        <div class="node-wrap" [class.selected]="isSelected(node)">
          <a class="node-card" [routerLink]="selectMode() ? null : getNodeLink(node)"
             (click)="onCardClick($event, node)"
             (pointerdown)="onCardPointerDown($event, node)"
             (pointerup)="onCardPointerUp()"
             (pointercancel)="onCardPointerCancel()"
             (pointerleave)="onCardPointerCancel()">
            <div class="cover">
              @if (node.coverUrl) {
                <img appCover [src]="node.coverUrl" alt="" loading="lazy">
              }
              <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>

              @if (node.isRead) {
                <span class="badge read" matTooltip="Read">✓ Read</span>
              } @else if (node.readingState === 'InProgress') {
                <span class="badge reading">Reading</span>
              }
              <app-folder-rollup-badge [rollup]="node.readRollup" />

              <!-- Admin folders show their current direction override as a small,
                   non-interactive chip (the control now lives in the action bar). -->
              @if (auth.isAdmin() && node.kind === 'Folder' && node.readerDefault) {
                <span class="badge dir" matTooltip="Reading direction override">
                  {{ directionShort(node.readerDefault) }}
                </span>
              }

              @if (selectMode()) {
                <span class="check" [class.on]="isSelected(node)">
                  <mat-icon>{{ isSelected(node) ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
                </span>
              }

              <!-- Touch range fill (long-press "Select to here"): shown only on the
                   long-pressed card, over its cover so it stays reachable without
                   scrolling away from the target item. -->
              @if (rangePromptNode()?.id === node.id) {
                <div class="range-prompt">
                  <button type="button" (click)="onSelectToHereClick($event)">Select to here</button>
                  <button type="button" class="cancel" (click)="onDismissRangePromptClick($event)">Cancel</button>
                </div>
              }
            </div>
            <div class="node-text">
              <div class="node-title" [title]="node.displayName">{{ node.displayName }}</div>
              <div class="node-sub">
                @if (node.pageCount !== null) { {{ node.pageCount }} pages }
                @else if (node.kind === 'Folder' && node.childArchiveCount !== null) { {{ node.childArchiveCount }} items }
                @if (node.availability !== 'Available') { · {{ node.availability }} }
              </div>
            </div>
          </a>
        </div>
      } @empty {
        <p class="empty">This folder is empty.</p>
      }
    </div>

    @if (hasMore()) {
      <!-- Infinite-scroll sentinel (1.8.0): observed by an IntersectionObserver that
           appends the next page as it approaches the viewport. The button is only a
           fallback for engines without IntersectionObserver. -->
      <div class="scroll-sentinel" #sentinel>
        @if (loadingMore()) {
          <span class="loading-more" role="status">Loading more…</span>
        } @else if (!autoLoadSupported) {
          <button mat-raised-button (click)="loadMore()">Load More</button>
        }
      </div>
    }
  `,
  styles: [`
    .menu-caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    /* View menu selected-state (1.8.1): accent highlight replaces the per-item
       checkmark in all four submenus. The panel renders in a CDK overlay, so the
       rules are scoped via class="view-options-menu" on <mat-menu> and reach the
       projected items with ::ng-deep. The subtle background lets Material's
       higher-specificity hover/focus states still read on top. */
    ::ng-deep .view-options-menu .selected-option {
      background: rgba(124, 77, 255, 0.16);
    }
    ::ng-deep .view-options-menu .selected-option,
    ::ng-deep .view-options-menu .selected-option .mat-icon {
      color: #b39dff;
    }
    .browse-bar {
      position: sticky; top: 0; z-index: 20;
      display: flex; align-items: center; gap: 12px;
      margin-bottom: 16px; padding: 10px 12px;
      background: #14141c; border: 1px solid rgba(255,255,255,0.08);
      border-radius: 10px;
    }
    .browse-bar.selecting { background: #1c1730; border-color: rgba(124,77,255,0.5); }
    .breadcrumbs {
      flex: 1 1 auto; min-width: 0;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .breadcrumbs a { text-decoration: none; color: #b39dff; }
    /* Current folder: plain text, not a link. Slightly brighter than the muted
       ancestors' link color to read as "you are here", but no pointer/underline. */
    .breadcrumbs .current { color: #e6e6ee; font-weight: 500; }
    .breadcrumbs.muted { color: #999; }
    .count { font-weight: 600; }
    .actions { flex: 1 1 auto; display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
    .actions mat-icon { margin-right: 4px; }
    .select-toggle mat-icon, .done mat-icon { margin-right: 4px; }
    /* Card size slider (1.6.0). Sits inline in the browse bar between the
       breadcrumbs and the View menu; the small/large icons frame the range. */
    .size-control { display: flex; align-items: center; gap: 6px; flex: 0 0 auto; }
    .size-control .size-icon { font-size: 18px; width: 18px; height: 18px; color: #8a8a99; }
    .size-slider {
      width: 120px; max-width: 34vw; accent-color: #7c4dff; cursor: pointer;
      background: transparent;
    }
    /* Read-state filter button (1.10.0): a normal toolbar control; when a filter is
       active it wears the accent so the constrained view reads at a glance. */
    .filter-toggle mat-icon { margin-right: 4px; }
    .filter-toggle.filter-active {
      border-color: #7c4dff; color: #b39dff;
    }
    .filter-toggle.filter-active mat-icon { color: #b39dff; }
    /* Phone-only card-size submenu trigger (1.10.0, F3). Hidden on desktop + iPad,
       where the inline .size-control-inline slider is used instead. */
    .size-menu-trigger { display: none; flex: 0 0 auto; }
    /* The slider laid out inside its phone submenu gets breathing room + a wider track. */
    ::ng-deep .size-options-menu .size-control-menu { display: flex; align-items: center; gap: 8px; padding: 8px 12px; }
    ::ng-deep .size-options-menu .size-slider { width: 180px; max-width: 60vw; accent-color: #7c4dff; }
    @media (max-width: 560px) { .size-control-inline .size-slider { width: 80px; } }
    /* View modes. Card (1.6.0) is a single cover grid whose card size is a
       continuous slider — the min column width comes from the --card-size custom
       property fed by the component, replacing the former Grid/Poster modes and the
       comfortable/compact density. The gap scales gently with the card size. List
       is a compact row layout with a small thumbnail (unchanged). */
    .nodes.card {
      display: grid;
      gap: clamp(10px, calc(var(--card-size, 150px) * 0.09), 20px);
      grid-template-columns: repeat(auto-fill, minmax(var(--card-size, 150px), 1fr));
    }
    .nodes.list { display: flex; flex-direction: column; gap: 8px; }
    .nodes.list .node-wrap { width: 100%; }
    .nodes.list .node-card {
      display: flex; align-items: center; gap: 12px;
      padding: 6px; border-radius: 8px; background: rgba(255,255,255,0.03);
    }
    .nodes.list .cover { width: 46px; height: 66px; flex: 0 0 auto; border-radius: 4px; }
    .nodes.list .cover-fallback { font-size: 24px; width: 24px; height: 24px; }
    .nodes.list .badge { font-size: 9px; padding: 1px 4px; top: 2px; right: 2px; }
    .nodes.list .badge.dir { bottom: 2px; top: auto; }
    .nodes.list .node-text { flex: 1 1 auto; min-width: 0; }
    .nodes.list .node-title { margin-top: 0; white-space: nowrap; }
    .node-wrap { position: relative; border-radius: 8px; }
    .node-wrap.selected { outline: 2px solid #7c4dff; outline-offset: 3px; }
    .node-card { cursor: pointer; text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative;
      aspect-ratio: 2 / 3;
      border-radius: 8px;
      overflow: hidden;
      background: rgba(255,255,255,0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img {
      width: 100%; height: 100%; object-fit: cover;
      position: relative; z-index: 1;
    }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: #777; position: absolute; z-index: 0; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgba(124, 77, 255, 0.9); color: #fff;
    }
    .badge.read { background: rgba(76, 175, 80, 0.95); }
    .badge.dir { top: auto; bottom: 6px; right: 6px; background: rgba(0,0,0,0.65); }
    .check {
      position: absolute; top: 6px; left: 6px; z-index: 3;
      color: #fff; line-height: 0;
      border-radius: 50%; background: rgba(0, 0, 0, 0.45);
    }
    .check.on { color: #7c4dff; background: #fff; }
    .check mat-icon { font-size: 24px; width: 24px; height: 24px; }
    /* Touch range fill (long-press "Select to here"). A small floating action over
       the long-pressed card's cover so it works without a positioned menu overlay. */
    .range-prompt {
      position: absolute; inset: 0; z-index: 4;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 6px; padding: 8px; background: rgba(10, 8, 20, 0.85); border-radius: 8px;
    }
    .range-prompt button {
      border: none; border-radius: 6px; padding: 6px 10px; font-size: 12px; font-weight: 600;
      cursor: pointer; background: #7c4dff; color: #fff; width: 100%;
    }
    .range-prompt button.cancel { background: rgba(255,255,255,0.12); }
    .node-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .node-sub { font-size: 12px; color: #999; }
    .empty { color: #999; padding: 32px; text-align: center; }
    .scroll-sentinel { min-height: 40px; text-align: center; color: #999; font-size: 12px; }
    /* A–Z/script jump rail (1.4.0 Lane E), root level only. Sticky under the
       top bar (1.8.0): top = measured bar height, z-index below the bar's 20;
       opaque so cards don't show through while stuck. */
    .jump-rail {
      position: sticky; z-index: 10;
      display: flex; flex-wrap: wrap; gap: 4px;
      margin-bottom: 12px; padding: 6px 8px;
      background: #14141c; border-radius: 8px;
    }
    .jump-chip {
      min-width: 28px; padding: 4px 8px; border: none; cursor: pointer;
      background: transparent; color: #b39dff; border-radius: 6px;
      font-size: 12px; font-weight: 600; line-height: 1;
      transition: background 0.1s;
    }
    .jump-chip:hover { background: rgba(124,77,255,0.18); }
    .jump-chip.active { background: #7c4dff; color: #fff; }

    /* Touch / small screens: keep the action bar compact by dropping button labels
       (icons remain, so the controls stay usable) — requirement 1 (dual input). */
    @media (max-width: 560px) {
      .actions .lbl { display: none; }
      .actions mat-icon { margin-right: 0; }
    }

    /* --- PHONE breakpoint only (1.10.0, F3). Desktop + iPad (>=561px) are untouched:
       every rule that changes layout lives inside this media query. It covers two
       findings from mobile use:
        - the card-size slider is relocated OFF the cramped toolbar into a submenu
          (the inline control is hidden; the icon-button trigger is shown);
        - the breadcrumb is redesigned for legibility: it was too small to read/tap.
          The trail is allowed to wrap, the type is larger, and the current folder is
          the prominent, high-contrast element so "where am I" reads at a glance. --- */
    @media (max-width: 560px) {
      .size-control-inline { display: none; }
      .size-menu-trigger { display: inline-flex; }

      .breadcrumbs {
        white-space: normal;         /* let the trail wrap instead of ellipsing away */
        overflow: visible;
        display: flex; flex-wrap: wrap; align-items: baseline; gap: 2px 4px;
        font-size: 15px; line-height: 1.35;
      }
      .breadcrumbs a { padding: 2px 0; }
      .breadcrumbs .sep { color: #6b6b78; }
      /* The current folder: the largest, brightest crumb — the mobile "you are here". */
      .breadcrumbs .current {
        flex-basis: 100%;
        font-size: 18px; font-weight: 700; color: #f0f0f6;
      }
    }
  `],
})
export class LibraryBrowseComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly zone = inject(NgZone);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly readState = inject(ReadStateService);
  readonly auth = inject(AuthService);

  // Per-folder direction override options (1.2.0). null = inherit (clear).
  readonly directionOptions: { value: ReaderMode | null; label: string }[] = [
    { value: null, label: 'Inherit (clear)' },
    { value: 'PagedLtr', label: 'Left-to-right' },
    { value: 'PagedRtl', label: 'Right-to-left' },
    { value: 'VerticalWebtoon', label: 'Vertical' },
  ];

  readonly libraryId = signal('');
  readonly libraryName = signal('');
  readonly parentId = signal<string | null>(null);
  readonly nodes = signal<CatalogNodeDto[]>([]);
  /** Folder's next-to-read archive (1.7.0), rendered as the pinned "Continue" row above the
   *  sorted list. Captured from the initial page's PageResponse.nextUnread (null on load-more). */
  readonly nextUnread = signal<CatalogNodeDto | null>(null);
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  /**
   * Name of the folder currently being viewed (Task B, 1.5.0). Rendered as the
   * last, non-clickable breadcrumb segment. The breadcrumbs endpoint only returns
   * the current folder's *ancestors* (never the node itself), so this is sourced
   * from the existing `GET /nodes/{id}` node lookup - a frontend-only addition,
   * no contract change. Empty at the library root (there is no current folder).
   */
  readonly currentFolderName = signal('');
  readonly hasMore = signal(false);
  private cursor: string | null = null;
  /** True while a browse page (initial or append) is in flight; gates the sentinel. */
  readonly loadingMore = signal(false);
  /** Monotonic request generation: a response from a superseded load is dropped. */
  private loadGen = 0;
  /**
   * Whether the loaded window starts at the listing's first item (cursor null).
   * A jump-rail click for a letter that is already loaded scrolls to it in place
   * only in that case; after a mid-list cursor jump the true first item of a
   * bucket may lie before the window, so the cursor reload is used instead.
   */
  private loadedFromStart = true;
  /** IntersectionObserver is absent in some engines (and jsdom); then the fallback button shows. */
  readonly autoLoadSupported = typeof IntersectionObserver !== 'undefined';
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private sentinelObserver: IntersectionObserver | null = null;

  /**
   * Initial/per-page item count (1.8.0, per-user). Replaces the former hardcoded
   * 50. Persisted as `libraryPageSize`; 0/absent/out-of-range resolves to the
   * default so pre-1.8.0 rows and older clients behave as before.
   */
  readonly defaultPageSize = 50;
  readonly pageSizeMin = 10;
  readonly pageSizeMax = 500;
  readonly pageSize = signal<number>(this.defaultPageSize);

  /**
   * Read-state filter (1.10.0, F1): restricts the listed archives to All / Reading /
   * Read / Unread, at the library root AND in any subfolder. Server-side (an additive
   * `readState` browse param), so it composes with the keyset pagination + infinite
   * scroll. A transient toolbar control, NOT a persisted preference — it survives
   * folder navigation within the session (resetList keeps it) but is not round-tripped
   * through library-preferences. 'all' sends no param (server default = unfiltered).
   */
  readonly readStateFilter = signal<LibraryReadStateFilter>('all');
  readonly readStateOptions: { value: LibraryReadStateFilter; label: string; icon: string }[] = [
    { value: 'all', label: 'All', icon: 'filter_list' },
    { value: 'reading', label: 'Reading', icon: 'auto_stories' },
    { value: 'read', label: 'Read', icon: 'check_circle' },
    { value: 'unread', label: 'Unread', icon: 'radio_button_unchecked' },
  ];
  /** Toolbar button label: "Filter" when inactive, else the active option's label. */
  readonly readStateLabel = computed(() =>
    this.readStateFilter() === 'all'
      ? 'Filter'
      : this.readStateOptions.find((o) => o.value === this.readStateFilter())?.label ?? 'Filter');

  /** Measured height of the sticky top bar: the jump rail's sticky offset. */
  readonly barHeight = signal(0);
  private readonly browseBar = viewChild<ElementRef<HTMLElement>>('browseBar');
  private barResize: ResizeObserver | null = null;

  // Scroll-spy (1.8.0): the element whose scroll events drive the active letter.
  private scrollTarget: HTMLElement | Window | null = null;
  private scrollSpyFrame: number | null = null;
  private readonly onScroll = (): void => this.scheduleScrollSpy();

  // Jump-index rail (1.4.0 Lane E). Only loaded at the library root (no parentId);
  // subfolders don't have a per-folder jump index. The rail is a name-sort
  // navigation aid, so it is hidden when the sort is not "name".
  readonly jumpBuckets = signal<JumpIndexBucketDto[]>([]);
  readonly activeJump = signal<string | null>(null);

  // Selection mode (1.2.0 read-marks + merged card actions).
  readonly selectMode = signal(false);
  readonly selected = signal<Set<string>>(new Set());
  readonly busy = signal(false);

  /**
   * Range selection (1.7.0). The ANCHOR is the index (within the currently
   * displayed `nodes()` order) of the last INDIVIDUALLY selected/deselected
   * item — a plain click, a ctrl/cmd-click, or the item long-press entered
   * select mode on. Shift-click and touch "Select to here" both fill the
   * inclusive range between the anchor and the target from this index, so the
   * range always follows the active sort/direction (requirement: range is
   * over the visible, currently-displayed order).
   */
  readonly anchorIndex = signal<number | null>(null);

  /**
   * The card currently showing the long-press "Select to here" action (touch
   * range-fill). Null when no prompt is open. Only one card shows the prompt
   * at a time.
   */
  readonly rangePromptNode = signal<CatalogNodeDto | null>(null);

  private longPressTimer: ReturnType<typeof setTimeout> | null = null;
  private longPressTriggered = false;
  private readonly longPressMs = 550;

  // Per-user library view mode. Tolerant: unknown/legacy persisted values fall back.
  // 1.6.0: the former Grid and Poster modes are merged into a single Card view whose
  // size is the continuous slider below; List stays a separate mode.
  readonly viewMode = signal<LibraryViewMode>('card');
  // Legacy density (1.2.0), subsumed by the card-size slider. Kept only so the stored
  // preference round-trips unchanged; there is no longer any density UI.
  readonly density = signal<LibraryGridDensity>('comfortable');
  readonly viewOptions: { value: LibraryViewMode; label: string; icon: string }[] = [
    { value: 'card', label: 'Card', icon: 'grid_view' },
    { value: 'list', label: 'List', icon: 'view_list' },
  ];
  readonly viewIcon = computed(() =>
    this.viewOptions.find((o) => o.value === this.viewMode())?.icon ?? 'grid_view');

  // Card size (1.6.0): the grid's min column width in px. The slider range spans the
  // legacy grid/poster x density sizes (112-210px today) with a little headroom on
  // each end. Persisted as a stringified px value in LibraryViewPreferencesDto.cardSize.
  readonly cardSizeMin = 110;
  readonly cardSizeMax = 260;
  readonly cardSizeStep = 10;
  readonly defaultCardSize = 150;
  readonly cardSize = signal<number>(this.defaultCardSize);

  // Per-user browse sort (post-1.2.0). Folders stay first in every mode; the sort
  // orders within kind. Omitted on the request → the server uses the stored pref;
  // we pass it explicitly so a change reorders immediately without a persist race.
  readonly sort = signal<LibrarySortOrder>('name');
  readonly sortOptions: { value: LibrarySortOrder; label: string; icon: string }[] = [
    { value: 'name', label: 'Name', icon: 'sort_by_alpha' },
    { value: 'recentlyAdded', label: 'Recently added', icon: 'schedule' },
    { value: 'recentlyRead', label: 'Recently read', icon: 'history' },
  ];

  // Ascending/descending toggle for the active sort (1.5.0). Named "sortDirection"
  // (not "direction") to stay distinct from the unrelated per-folder reading
  // direction (LTR/RTL/Vertical) already on this component. Default mirrors the
  // server's per-sort default (name -> asc; everything else -> desc) until the
  // stored preference loads.
  readonly sortDirection = signal<LibrarySortDirection>('asc');
  readonly sortDirectionOptions: { value: LibrarySortDirection; label: string; icon: string }[] = [
    { value: 'asc', label: 'Ascending', icon: 'arrow_upward' },
    { value: 'desc', label: 'Descending', icon: 'arrow_downward' },
  ];

  /** How many currently-selected nodes are folders (gates the Direction action). */
  readonly selectedFolderCount = computed(() => {
    const ids = this.selected();
    return this.nodes().filter((n) => ids.has(n.id) && n.kind === 'Folder').length;
  });

  constructor() {
    // The sentinel lives inside `@if (hasMore())`, so it comes and goes; re-arm the
    // observer on the element the view query currently resolves to.
    effect(() => this.observeSentinel(this.sentinel()?.nativeElement ?? null));
    // The top bar's height depends on wrapping/selection state; measure it live so
    // the rail's sticky offset stays exact.
    effect(() => this.observeBarHeight(this.browseBar()?.nativeElement ?? null));
    // A new page or a rail change moves the bucket boundaries: recompute.
    effect(() => { this.nodes(); this.jumpBuckets(); this.scheduleScrollSpy(); });
  }

  ngOnInit(): void {
    // Scroll-spy listener (1.8.0). Registered outside the zone: scroll fires many
    // times per second and the handler only writes a signal (rAF-throttled), which
    // schedules change detection on its own; no per-event zone turn is needed.
    this.scrollTarget = this.scrollParent() ?? (typeof window !== 'undefined' ? window : null);
    this.zone.runOutsideAngular(() =>
      this.scrollTarget?.addEventListener('scroll', this.onScroll, { passive: true }));

    // 1.7.1 stale-read-status fix: subscribed ONCE here, so it stays alive across
    // the 1.6.2 reuse strategy's detach/reattach round-trip through the reader
    // (ngOnInit never re-runs on reattach — that's the whole point of retaining
    // the instance). Patches only the affected card; never re-fetches the list
    // or touches scroll.
    this.readState.itemChanged$.subscribe((itemId) => this.refreshNodeStatus(itemId));

    // 1.7.3: the pinned Continue row is purely presentational — it renders
    // whatever `nextUnread` this component hands it and never re-fetches on its
    // own — so without this it kept pointing at a just-finished chapter after
    // exiting the reader. `switchMap` cancels a still-in-flight refresh if
    // another item change arrives before it resolves (guards against
    // back-to-back reader exits/advances piling up requests); the targeted
    // pageSize:1 fetch reads only `PageResponse.nextUnread` (folder-scoped,
    // independent of pagination — see api-types.ts), so it never touches
    // `nodes()`, the cursor, or the 1.6.2 scroll retention.
    this.readState.itemChanged$.pipe(
      filter(() => !!this.libraryId()),
      switchMap(() => this.api.browseLibrary(
        this.libraryId(), this.parentId(), null, 1, this.sort(), this.sortDirection())
        .pipe(catchError(() => of(null)))),
    ).subscribe((res) => { if (res) this.nextUnread.set(res.nextUnread ?? null); });

    // Load the per-user view preference FIRST (tolerate unknown values), then start
    // routing. Sequencing matters: the sort must be known before the first browse so
    // it issues a single, correctly-ordered request. (Loading nodes from both the
    // prefs handler and the route subscription would double-append — duplicate rows.)
    this.api.getLibraryPreferences().subscribe({
      next: (p) => {
        this.applyStoredView(p);
        if (p.sort === 'recentlyAdded' || p.sort === 'recentlyRead') this.sort.set(p.sort);
        // Direction is optional/tolerant like the other fields: an unset or
        // unrecognized value falls back to the sort-specific default (matches
        // CatalogController.ParseDirection) so pre-1.5.0 stored preferences
        // keep their existing ordering.
        this.sortDirection.set(
          p.direction === 'asc' || p.direction === 'desc' ? p.direction : this.defaultDirectionFor(this.sort()));
        this.subscribeToRoute();
      },
      error: () => this.subscribeToRoute(), // keep defaults, still load
    });
  }

  ngOnDestroy(): void {
    this.scrollTarget?.removeEventListener('scroll', this.onScroll);
    this.sentinelObserver?.disconnect();
    this.barResize?.disconnect();
    if (this.scrollSpyFrame !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.scrollSpyFrame);
    }
  }

  /** Sort-specific default direction, matching the server's fallback (1.5.0). */
  private defaultDirectionFor(sort: LibrarySortOrder): LibrarySortDirection {
    return sort === 'name' ? 'asc' : 'desc';
  }

  /**
   * Map a stored preference blob onto the Card/List view + card size (1.6.0),
   * tolerating pre-1.6.0 values. A stored 'list' stays List; everything else -
   * 'card', the legacy 'grid'/'poster', and any unknown value - becomes Card. The
   * card size uses the stored cardSize when present and valid, otherwise it is
   * DERIVED from the legacy viewMode + density so existing users keep the effective
   * card size they had before Grid/Poster were merged.
   */
  private applyStoredView(p: LibraryViewPreferencesDto): void {
    this.viewMode.set(p.viewMode === 'list' ? 'list' : 'card');
    this.density.set(p.density === 'compact' ? 'compact' : 'comfortable');
    this.cardSize.set(this.resolveCardSize(p));
    this.pageSize.set(this.resolvePageSize(p));
  }

  /** Stored libraryPageSize when it is a sane integer, else the default (50). */
  private resolvePageSize(p: LibraryViewPreferencesDto): number {
    const n = Number(p.libraryPageSize);
    return Number.isInteger(n) && n >= this.pageSizeMin && n <= this.pageSizeMax ? n : this.defaultPageSize;
  }

  /**
   * Resolve the initial card size: the stored numeric cardSize if usable, else the
   * px width the legacy viewMode+density combination used (poster 210/168, grid
   * 150/112), else the default. Keeps pre-1.6.0 stored prefs visually unchanged.
   */
  private resolveCardSize(p: LibraryViewPreferencesDto): number {
    const stored = Number(p.cardSize);
    if (p.cardSize != null && p.cardSize !== '' && Number.isFinite(stored)) {
      return this.clampCardSize(stored);
    }
    const compact = p.density === 'compact';
    if (p.viewMode === 'poster') return compact ? 168 : 210;
    if (p.viewMode === 'grid') return compact ? 112 : 150;
    return this.defaultCardSize;
  }

  /** Subscribe to the route params and load each folder as it is navigated. */
  private subscribeToRoute(): void {
    this.route.paramMap.subscribe((params) => {
      const libId = params.get('libraryId')!;
      const parentId = params.get('nodeId');
      this.libraryId.set(libId);
      this.parentId.set(parentId);
      this.resetList();
      this.loadLibraryName(libId);
      this.loadNodes();
      // The jump rail is a library-root navigation aid (1.4.0 Lane E). It is
      // only meaningful for the name sort in ascending order — its bucket
      // cursors assume A→Z order — and only over the UNFILTERED listing (1.10.0).
      if (this.shouldShowJumpRail()) this.loadJumpIndex(libId);
      else this.jumpBuckets.set([]);
      if (parentId) {
        this.loadBreadcrumbs(parentId);
        this.loadCurrentFolder(parentId);
      } else {
        this.breadcrumbs.set([]);
        this.currentFolderName.set('');
      }
    });
  }

  /** Load the per-library A–Z/script jump index (1.4.0 Lane E). */
  private loadJumpIndex(libId: string): void {
    this.api.getJumpIndex(libId).subscribe({
      next: (res) => this.jumpBuckets.set(res.buckets),
      error: () => this.jumpBuckets.set([]),
    });
  }

  /**
   * Jump to a bucket. If the window was loaded from the listing start and the
   * bucket's first item is already loaded, just scroll to it (1.8.0 - no reload,
   * nothing lost). Otherwise set the cursor to the bucket's firstCursor and
   * reload from the top (a null cursor means the start of the listing).
   */
  jumpToBucket(bucket: JumpIndexBucketDto): void {
    this.activeJump.set(bucket.label);
    if (this.loadedFromStart) {
      const index = this.nodes().findIndex((n) => jumpLabelFor(n.displayName) === bucket.label);
      if (index !== -1 && this.scrollToCard(index)) return;
    }
    this.cursor = bucket.firstCursor;
    this.loadedFromStart = bucket.firstCursor === null;
    this.nodes.set([]);
    this.hasMore.set(false);
    this.clearSelection();
    this.loadNodes();
    this.scrollToTop('auto');
  }

  /**
   * Scroll-spy (1.8.0): set `activeJump` to the bucket of the topmost visible
   * card - the first card whose bottom edge clears the sticky bar + rail stack.
   * Cards in a grid row share that edge, so within the first visible row the
   * current label is kept if any card in the row carries it: the indicator
   * doesn't flip on a row that merely ends the previous letter, and a click
   * that scrolled to a letter's first card shows that letter even when an
   * earlier card shares its row. Public so tests can drive it synchronously.
   */
  updateActiveJump(): void {
    if (this.jumpBuckets().length === 0) return;
    const hostEl = this.host.nativeElement;
    if (!hostEl.isConnected) return; // detached by the reuse strategy: nothing visible
    const nodes = this.nodes();
    const cards = hostEl.querySelectorAll<HTMLElement>('.node-wrap');
    if (nodes.length === 0 || cards.length !== nodes.length) return;

    const threshold = this.stickyBottom();
    // Cards are in document order, so their bottom edges are non-decreasing:
    // binary-search the first one that ends below the sticky stack.
    let lo = 0, hi = cards.length - 1, first = cards.length - 1;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (cards[mid].getBoundingClientRect().bottom > threshold) { first = mid; hi = mid - 1; }
      else lo = mid + 1;
    }
    const rowBottom = cards[first].getBoundingClientRect().bottom;
    const current = this.activeJump();
    let label = jumpLabelFor(nodes[first].displayName);
    for (let i = first; i < cards.length && Math.abs(cards[i].getBoundingClientRect().bottom - rowBottom) < 1; i++) {
      if (jumpLabelFor(nodes[i].displayName) === current) { label = current; break; }
    }
    this.activeJump.set(label);
  }

  /**
   * Tap the top bar's neutral area to scroll back to the top (1.8.0). Clicks on
   * the breadcrumb links, the slider, and the View/Select/action buttons are
   * left alone (their own handlers run; no scroll).
   */
  onBarClick(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.closest('a, button, input, label, .size-control, [role="menuitem"]')) return;
    this.scrollToTop('smooth');
  }

  /** Scroll the list's actual scroll container (the window unless an ancestor scrolls) to the top. */
  scrollToTop(behavior: ScrollBehavior = 'smooth'): void {
    const target = this.scrollParent() ?? (typeof window !== 'undefined' ? window : null);
    try { target?.scrollTo({ top: 0, behavior }); } catch { /* jsdom: scrollTo not implemented */ }
  }

  /**
   * Scroll so the card at `index` sits just under the sticky bar + rail. False if
   * it isn't rendered. The sticky stack's bottom edge depends on the scroll
   * position (in normal flow near the top of the page, pinned once scrolled), so
   * one scroll computed from the pre-scroll geometry can land the card a stack's
   * height off; re-measure after each instant scroll and correct until it settles.
   */
  private scrollToCard(index: number): boolean {
    const card = this.host.nativeElement.querySelectorAll<HTMLElement>('.node-wrap')[index];
    if (!card) return false;
    const target = this.scrollParent() ?? (typeof window !== 'undefined' ? window : null);
    for (let pass = 0; pass < 3; pass++) {
      const delta = card.getBoundingClientRect().top - this.stickyBottom() - 8;
      if (Math.abs(delta) < 1) break;
      try { target?.scrollBy({ top: delta, behavior: 'auto' }); } catch { return true; /* jsdom */ }
    }
    return true;
  }

  /**
   * The nearest scrolling ancestor, or null when the document itself scrolls
   * (today's layout: `main.content` has no overflow, so the window is the
   * container the reuse strategy's scroll restoration retains).
   */
  private scrollParent(): HTMLElement | null {
    if (typeof getComputedStyle === 'undefined') return null;
    let el = this.host.nativeElement.parentElement;
    while (el && el !== document.body && el !== document.documentElement) {
      const oy = getComputedStyle(el).overflowY;
      if (oy === 'auto' || oy === 'scroll') return el;
      el = el.parentElement;
    }
    return null;
  }

  /** Viewport-y of the bottom edge of the sticky stack (rail if shown, else the bar). */
  private stickyBottom(): number {
    const rail = this.host.nativeElement.querySelector<HTMLElement>('.jump-rail');
    if (rail) return rail.getBoundingClientRect().bottom;
    return this.browseBar()?.nativeElement.getBoundingClientRect().bottom ?? 0;
  }

  private scheduleScrollSpy(): void {
    if (this.scrollSpyFrame !== null || typeof requestAnimationFrame === 'undefined') return;
    this.scrollSpyFrame = requestAnimationFrame(() => {
      this.scrollSpyFrame = null;
      this.updateActiveJump();
    });
  }

  /** (Re)attach the infinite-scroll observer to the current sentinel element. */
  private observeSentinel(el: HTMLElement | null): void {
    this.sentinelObserver?.disconnect();
    this.sentinelObserver = null;
    if (!el || !this.autoLoadSupported) return;
    // The root margin pre-fetches a screen or so before the sentinel is in view.
    this.sentinelObserver = new IntersectionObserver(
      (entries) => { if (entries.some((e) => e.isIntersecting)) this.loadMore(); },
      { root: this.scrollParent(), rootMargin: '0px 0px 600px 0px' });
    this.sentinelObserver.observe(el);
  }

  /**
   * IntersectionObserver reports threshold CROSSINGS only: after a short page is
   * appended the sentinel may still be inside the margin without ever leaving it,
   * so nothing would fire again. Re-observing yields a fresh entry for the
   * current state, which loads the next page when it is still needed.
   */
  private rearmSentinel(): void {
    const el = this.sentinel()?.nativeElement;
    if (!el || !this.sentinelObserver) return;
    this.sentinelObserver.unobserve(el);
    this.sentinelObserver.observe(el);
  }

  private observeBarHeight(el: HTMLElement | null): void {
    this.barResize?.disconnect();
    this.barResize = null;
    if (!el) return;
    this.barHeight.set(el.offsetHeight);
    if (typeof ResizeObserver === 'undefined') return;
    this.barResize = new ResizeObserver(() => this.barHeight.set(el.offsetHeight));
    this.barResize.observe(el);
  }

  /** Reset the list window to "not loaded": cursor, nodes, paging, rail state, selection. */
  private resetList(): void {
    this.cursor = null;
    this.loadedFromStart = true;
    this.nodes.set([]);
    this.hasMore.set(false);
    this.activeJump.set(null);
    this.clearSelection();
  }

  /** Resolve the library's display name for the breadcrumb root (reader-accessible). */
  private loadLibraryName(libId: string): void {
    this.api.getLibraries().subscribe({
      next: (libs) => this.libraryName.set(libs.find((l) => l.id === libId)?.name ?? ''),
      error: () => { /* fall back to "Library" in the template */ },
    });
  }

  /** Append the next page (sentinel callback / fallback button). No-op while one is in flight. */
  loadMore(): void {
    if (this.loadingMore() || !this.hasMore()) return;
    this.loadNodes();
  }

  /**
   * Change the read-state filter (1.10.0, F1): reload the list from the top at the new
   * filter. Not persisted (a transient view control). The jump rail is a name-sort
   * navigation aid built over the UNFILTERED listing, so it is hidden while a filter is
   * active and restored when the filter returns to All (subject to the usual name+asc+
   * root conditions).
   */
  setReadStateFilter(filter: LibraryReadStateFilter): void {
    if (this.readStateFilter() === filter) return;
    this.readStateFilter.set(filter);
    this.resetList();
    this.loadNodes();
    if (this.shouldShowJumpRail()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
    this.scrollToTop('auto');
  }

  /**
   * Whether the A-Z jump rail applies: it is a library-root name-sort (ascending)
   * navigation aid whose bucket cursors assume the full, unfiltered A->Z listing, so it
   * is meaningless in a subfolder, under another sort/direction, or while a read-state
   * filter is narrowing the listing.
   */
  private shouldShowJumpRail(): boolean {
    return !this.parentId()
      && this.sort() === 'name'
      && this.sortDirection() === 'asc'
      && this.readStateFilter() === 'all';
  }

  getNodeLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') {
      return ['/libraries', this.libraryId(), 'browse', node.id];
    }
    return ['/reader', node.id];
  }

  /** Short label for a folder's direction override chip. */
  directionShort(mode: ReaderMode): string {
    switch (mode) {
      case 'PagedLtr': return 'LTR';
      case 'PagedRtl': return 'RTL';
      case 'VerticalWebtoon': return 'Vertical';
      default: return 'Spread';
    }
  }

  // --- View mode (1.2.0, per-user persisted) ---

  setViewMode(mode: LibraryViewMode): void {
    if (this.viewMode() === mode) return;
    this.viewMode.set(mode);
    this.persistView();
  }

  /**
   * Live card-size preview while dragging the slider (1.6.0): only updates the
   * signal (which drives the grid's --card-size), deliberately WITHOUT persisting,
   * so a drag doesn't fire a PUT per pixel. The commit happens on `change`.
   */
  onCardSizeInput(event: Event): void {
    this.cardSize.set(this.clampCardSize(Number((event.target as HTMLInputElement).value)));
  }

  /** Commit the card size when the slider is released (change): persists it. */
  onCardSizeChange(event: Event): void {
    this.setCardSize(Number((event.target as HTMLInputElement).value));
  }

  /** Set the card size (clamped to the slider range) and persist the preference. */
  setCardSize(px: number): void {
    const size = this.clampCardSize(px);
    this.cardSize.set(size);
    this.persistView();
  }

  private clampCardSize(px: number): number {
    if (!Number.isFinite(px)) return this.defaultCardSize;
    return Math.min(this.cardSizeMax, Math.max(this.cardSizeMin, Math.round(px)));
  }

  /** Change the browse sort: persist the preference and reorder from the top. */
  setSort(s: LibrarySortOrder): void {
    if (this.sort() === s) return;
    this.sort.set(s);
    this.persistView();
    this.resetList();
    this.loadNodes();
    // The jump rail is only valid for the name sort in ascending order (the
    // cursor is a raw SortKey that assumes A→Z order; other sorts ignore it).
    if (this.shouldShowJumpRail()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
  }

  /** Toggle ascending/descending for the active sort: persist and reorder from the top. */
  setSortDirection(d: LibrarySortDirection): void {
    if (this.sortDirection() === d) return;
    this.sortDirection.set(d);
    this.persistView();
    this.resetList();
    this.loadNodes();
    if (this.shouldShowJumpRail()) this.loadJumpIndex(this.libraryId());
    else this.jumpBuckets.set([]);
  }

  private persistView(): void {
    this.api.setLibraryPreferences({
      viewMode: this.viewMode(),
      density: this.density(),
      sort: this.sort(),
      direction: this.sortDirection(),
      cardSize: String(this.cardSize()),
      libraryPageSize: this.pageSize(),
    }).subscribe({ error: () => { /* non-fatal: the choice still applies this session */ } });
  }

  // --- Selection mode (1.2.0) ---

  toggleSelectMode(): void {
    this.selectMode.update((v) => !v);
    if (!this.selectMode()) this.clearSelection();
  }

  isSelected(node: CatalogNodeDto): boolean {
    return this.selected().has(node.id);
  }

  /**
   * Desktop card click, file-browser semantics (1.7.0):
   *  - a long-press already handled this pointer session (touch) - suppress
   *    the trailing click entirely.
   *  - Shift-click, with an anchor set: fills the inclusive range from the
   *    anchor to the clicked card (display order) into the selection.
   *  - Plain click or Ctrl/Cmd-click: toggles just this card and moves the
   *    anchor to it (Ctrl/Cmd is equivalent to a plain click here, since
   *    every click in select mode already toggles rather than replacing the
   *    selection - it is accepted so the file-browser modifier still "works").
   * Outside select mode, a click is a normal navigation and this is a no-op.
   */
  onCardClick(event: MouseEvent, node: CatalogNodeDto): void {
    if (this.longPressTriggered) {
      // The long-press action already ran (or its prompt is open); this is
      // the click that follows the touch release and must be swallowed.
      this.longPressTriggered = false;
      event.preventDefault();
      event.stopPropagation();
      return;
    }
    if (!this.selectMode()) return;
    event.preventDefault();
    event.stopPropagation();

    const index = this.nodes().findIndex((n) => n.id === node.id);
    if (index === -1) return;

    if (event.shiftKey && this.anchorIndex() !== null) {
      this.selectRange(this.anchorIndex()!, index);
      return;
    }
    this.toggleOne(node, index);
  }

  /** Toggle a single node's selection and move the range anchor to it. */
  private toggleOne(node: CatalogNodeDto, index: number): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (next.has(node.id)) next.delete(node.id);
      else next.add(node.id);
      return next;
    });
    this.anchorIndex.set(index);
  }

  /** Add the inclusive range [fromIndex, toIndex] (display order) to the selection. */
  private selectRange(fromIndex: number, toIndex: number): void {
    const lo = Math.min(fromIndex, toIndex);
    const hi = Math.max(fromIndex, toIndex);
    const rangeIds = this.nodes().slice(lo, hi + 1).map((n) => n.id);
    this.selected.update((set) => new Set([...set, ...rangeIds]));
    this.anchorIndex.set(toIndex);
  }

  // --- Touch range selection: long-press -> "Select to here" (1.7.0) ---

  /** Start the long-press timer for a touch/pen pointer only; mouse uses shift-click instead. */
  onCardPointerDown(event: PointerEvent, node: CatalogNodeDto): void {
    if (event.pointerType !== 'touch' && event.pointerType !== 'pen') return;
    this.clearLongPressTimer();
    this.longPressTimer = setTimeout(() => this.onLongPress(node), this.longPressMs);
  }

  /** A normal tap released before the long-press fired: just cancel the timer. */
  onCardPointerUp(): void {
    this.clearLongPressTimer();
  }

  /** Pointer left/cancelled (scroll, interruption): cancel the pending long-press. */
  onCardPointerCancel(): void {
    this.clearLongPressTimer();
  }

  private clearLongPressTimer(): void {
    if (this.longPressTimer !== null) {
      clearTimeout(this.longPressTimer);
      this.longPressTimer = null;
    }
  }

  /**
   * Long-press fired on `node`. Mental model: tap = one, long-press = range fill.
   *  - Not yet in select mode: enter it and select+anchor this card (nothing to
   *    fill a range from yet).
   *  - In select mode with no anchor yet (nothing individually selected since
   *    entering select mode / after Select all* reset it): same as above.
   *  - In select mode with an anchor: open "Select to here" on this card so the
   *    range is filled only on explicit confirmation.
   * `longPressTriggered` suppresses the click event that the browser fires on
   * touch release right after this.
   */
  private onLongPress(node: CatalogNodeDto): void {
    this.longPressTriggered = true;
    const index = this.nodes().findIndex((n) => n.id === node.id);
    if (index === -1) return;

    if (!this.selectMode()) {
      this.selectMode.set(true);
      this.toggleOne(node, index);
      return;
    }
    if (this.anchorIndex() === null) {
      this.toggleOne(node, index);
      return;
    }
    this.rangePromptNode.set(node);
  }

  /** Button click wrapper: stop the click from bubbling to the card's own click handler. */
  onSelectToHereClick(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.confirmSelectToHere();
  }

  /** Button click wrapper: stop the click from bubbling to the card's own click handler. */
  onDismissRangePromptClick(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.dismissRangePrompt();
  }

  /** Confirm the "Select to here" prompt: fill the range from the anchor to the prompted card. */
  confirmSelectToHere(): void {
    const target = this.rangePromptNode();
    const anchor = this.anchorIndex();
    this.rangePromptNode.set(null);
    if (target === null || anchor === null) return;
    const index = this.nodes().findIndex((n) => n.id === target.id);
    if (index !== -1) this.selectRange(anchor, index);
  }

  /** Dismiss the "Select to here" prompt without changing the selection. */
  dismissRangePrompt(): void {
    this.rangePromptNode.set(null);
  }

  // --- Whole-folder selection (1.7.0) ---

  /** Select every currently-listed node (respects the active sort/filter, not just the loaded page). */
  selectAll(): void {
    this.selected.set(new Set(this.nodes().map((n) => n.id)));
    const count = this.nodes().length;
    this.anchorIndex.set(count > 0 ? count - 1 : null);
  }

  /** Select every currently-listed node that is not yet marked read. */
  selectAllUnread(): void {
    this.selected.set(new Set(this.nodes().filter((n) => !n.isRead).map((n) => n.id)));
    this.anchorIndex.set(null);
  }

  /** Select every currently-listed node that is marked read. */
  selectAllRead(): void {
    this.selected.set(new Set(this.nodes().filter((n) => n.isRead).map((n) => n.id)));
    this.anchorIndex.set(null);
  }

  clearSelection(): void {
    this.selected.set(new Set());
    this.anchorIndex.set(null);
    this.rangePromptNode.set(null);
    this.clearLongPressTimer();
    this.longPressTriggered = false;
  }

  /**
   * Sticky read-mark over the selection: archives individually, folders recursively
   * over their descendant archives. Visible archive badges update; folder effects
   * (on items inside them) are reported by count. Selection is kept so more actions
   * can be applied to the same set.
   *
   * Mark-UNREAD (1.9.0): clearing the sticky read-mark (DELETE .../read) is now a
   * deliberate FULL RESET server-side - it wipes both the read-mark AND the reading
   * position in one call, so an InProgress archive leaves both the browse "Reading"
   * badge and the continue-reading strip with no separate progress reset. The old
   * compensating DELETE .../progress call (retired with rule 6) is gone, so no UI
   * action can leave a read-badge-with-no-position state. (Single-page archives
   * auto-marking read on OPEN is intended and untouched - this is only mark-unread.)
   */
  bulkMarkRead(read: boolean): void {
    const ids = this.selected();
    const chosen = this.nodes().filter((n) => ids.has(n.id));
    const archives = chosen.filter((n) => n.kind === 'Archive');
    const folders = chosen.filter((n) => n.kind === 'Folder');

    const calls = [
      ...archives.map((a) => this.api.setItemRead(a.id, read)),
      ...folders.map((f) => this.api.setFolderRead(f.id, read)),
    ];
    if (calls.length === 0) return;

    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        const archiveIds = new Set(archives.map((a) => a.id));
        this.nodes.update((list) =>
          list.map((n) => {
            if (!archiveIds.has(n.id)) return n;
            const updated = { ...n, isRead: read };
            // Marking unread is a full reset: drop the "Reading" state and last-read
            // page so the badge disappears and the item is no longer mid-read.
            if (!read) {
              updated.readingState = 'Unread';
              updated.lastReadPage = null;
            }
            return updated;
          }));

        const folderNote = folders.length
          ? ` and ${folders.length} folder${folders.length > 1 ? 's' : ''}`
          : '';
        this.snackBar.open(
          `Marked ${read ? 'read' : 'unread'}: ${archives.length} item${archives.length === 1 ? '' : 's'}${folderNote}`,
          'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  /**
   * Admin-only: set (or clear, when mode is null) the global reading-direction
   * override on every selected folder. Archives in the selection are ignored.
   */
  bulkSetDirection(mode: ReaderMode | null): void {
    const ids = this.selected();
    const folders = this.nodes().filter((n) => ids.has(n.id) && n.kind === 'Folder');
    if (folders.length === 0) return;

    const calls = folders.map((f) =>
      mode ? this.api.setFolderReaderDefault(f.id, mode) : this.api.clearFolderReaderDefault(f.id));

    this.busy.set(true);
    forkJoin(calls).subscribe({
      next: () => {
        const fids = new Set(folders.map((f) => f.id));
        this.nodes.update((list) =>
          list.map((n) => (fids.has(n.id) ? { ...n, readerDefault: mode } : n)));
        const label = this.directionOptions.find((o) => o.value === mode)?.label ?? 'updated';
        this.snackBar.open(
          `Reading direction (${label}) on ${folders.length} folder${folders.length === 1 ? '' : 's'}`,
          'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  private loadNodes(): void {
    const libId = this.libraryId();
    if (!libId) return;

    // Initial page (no cursor) replaces; infinite-scroll pages (cursor set) APPEND
    // through `nodes.update`, so `@for (track node.id)` only inserts the new cards
    // and the retained DOM/scroll position is untouched. A response from a load
    // that a later reset superseded (folder change, sort, page size) is dropped.
    const initial = this.cursor === null;
    const gen = ++this.loadGen;
    this.loadingMore.set(true);
    this.api.browseLibrary(libId, this.parentId(), this.cursor, this.pageSize(), this.sort(), this.sortDirection(), this.readStateFilter()).subscribe({
      next: (response: PageResponse<CatalogNodeDto>) => {
        if (gen !== this.loadGen) return;
        this.nodes.update((current) => initial ? [...response.items] : [...current, ...response.items]);
        this.hasMore.set(response.hasMore);
        this.cursor = response.nextCursor;
        this.loadingMore.set(false);
        if (initial) this.nextUnread.set(response.nextUnread ?? null);
        if (response.hasMore) this.rearmSentinel();
      },
      error: () => { if (gen === this.loadGen) this.loadingMore.set(false); },
    });
  }

  /**
   * Patch one node's read/progress fields in place from a fresh server fetch
   * (1.7.1). Fixes the stale card the 1.6.2 browse-retention regression left
   * behind: finishing a chapter and pressing Back re-attaches this SAME
   * instance (no `ngOnInit`, no re-fetch), so without this the just-finished
   * item keeps showing its pre-reading read/progress state until a manual
   * refresh. No-ops when the item isn't currently listed (a different folder,
   * or scrolled off a page not yet loaded); never re-fetches the whole list or
   * disturbs scroll.
   *
   * Deliberately does NOT use `GET /nodes/{id}` (`ApiService.getNode`): that
   * endpoint does not populate per-user reading state at all (it always
   * returns `isRead: false, readingState: null` regardless of the caller's
   * progress) — discovered live-testing this fix, where it patched a
   * just-completed card back to looking unread. The read-mark and progress
   * endpoints below are the ones that are actually user-scoped.
   */
  private refreshNodeStatus(itemId: string): void {
    if (!this.nodes().some((n) => n.id === itemId)) return;
    forkJoin({
      read: this.api.getReadMark(itemId).pipe(
        catchError(() => of<ReadMarkDto>({ itemId, isRead: false }))),
      progress: this.api.getProgress(itemId).pipe(
        catchError(() => of<ReadingProgressDto | null>(null))),
    }).subscribe(({ read, progress }) => {
      this.nodes.update((list) => list.map((n) =>
        n.id === itemId
          ? {
              ...n,
              isRead: read.isRead,
              readingState: progress?.state ?? n.readingState,
              lastReadPage: progress ? progress.pageIndex : n.lastReadPage,
            }
          : n));
    });
  }

  private loadBreadcrumbs(nodeId: string): void {
    this.api.getBreadcrumbs(nodeId).subscribe({
      next: (response) => this.breadcrumbs.set(response.trail),
    });
  }

  /**
   * Resolve the current folder's display name for the trailing (non-clickable)
   * breadcrumb segment (Task B). Uses the existing node-lookup endpoint; on error
   * we clear the name so the breadcrumb simply omits the current segment rather
   * than showing a stale one.
   */
  private loadCurrentFolder(nodeId: string): void {
    this.api.getNode(nodeId).subscribe({
      next: (node) => this.currentFolderName.set(node.displayName),
      error: () => this.currentFolderName.set(''),
    });
  }
}

/**
 * Client-side port of the server's `JumpIndexService.BucketLabelFor` (1.8.0
 * scroll-spy). The browse DTO carries no bucket, so the rail's active letter is
 * derived from the visible card's display name with the same rules the server
 * used to build the buckets: skip a small set of leading punctuation, Latin
 * letters -> A-Z, digits -> "#", recognised script blocks -> their group label,
 * anything else -> "Other". Keep in sync with the server when the rules change.
 */
export function jumpLabelFor(displayName: string): string {
  if (!displayName) return 'Other';
  const skippable = ' \t"\'()[]{}-_.,!?*#@~';
  let i = 0;
  while (i < displayName.length) {
    const ch = displayName[i];
    if (/[\p{L}\p{Nd}]/u.test(ch)) break;
    if (skippable.includes(ch)) { i++; continue; }
    break;
  }
  if (i >= displayName.length) return 'Other';

  const cp = displayName.codePointAt(i)!;
  if ((cp >= 0x41 && cp <= 0x5a) || (cp >= 0x61 && cp <= 0x7a)) {
    return String.fromCodePoint(cp).toUpperCase();
  }
  if (/\p{Nd}/u.test(displayName[i])) return '#';

  const c = cp;
  if ((c >= 0x3040 && c <= 0x30ff) || (c >= 0xff65 && c <= 0xff9f)) return 'Kana';
  if ((c >= 0x1100 && c <= 0x11ff) || (c >= 0xac00 && c <= 0xd7af) || (c >= 0x3130 && c <= 0x318f)) return 'Hangul';
  if ((c >= 0x4e00 && c <= 0x9fff) || (c >= 0x3400 && c <= 0x4dbf) ||
      (c >= 0x20000 && c <= 0x2ffff) || (c >= 0x2e80 && c <= 0x2eff) || (c >= 0x2f00 && c <= 0x2fdf)) return 'CJK';
  if (c >= 0x0400 && c <= 0x052f) return 'Cyrillic';
  if ((c >= 0x0370 && c <= 0x03ff) || (c >= 0x1f00 && c <= 0x1fff)) return 'Greek';
  if ((c >= 0x0600 && c <= 0x06ff) || (c >= 0x0750 && c <= 0x077f)) return 'Arabic';
  if (c >= 0x0590 && c <= 0x05ff) return 'Hebrew';
  if (c >= 0x0e00 && c <= 0x0e7f) return 'Thai';
  return 'Other';
}
