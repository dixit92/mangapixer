import {
  Component, ElementRef, Injector, NgZone, OnDestroy, OnInit, afterNextRender, computed, effect, inject, signal, viewChild,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { NavigationEnd, NavigationStart, Router, RouterLink } from '@angular/router';
import { Subscription, catchError, filter, forkJoin, of } from 'rxjs';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { MatDividerModule } from '@angular/material/divider';

import { ApiService } from '../../core/api/api.service';
import { FavoritesStateService } from '../../core/favorites/favorites-state.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import {
  CARD_SIZE_MAX, CARD_SIZE_MIN, CARD_SIZE_STEP, DeviceDisplayPreferencesService, LIST_COLUMNS_MAX, LIST_COLUMNS_MIN, LIST_COLUMNS_STEP,
} from '../../core/preferences/device-display-preferences.service';
import {
  FavoritesDisplayPreferencesService, FavoritesSort, FavoritesSortDirection,
} from '../../core/preferences/favorites-display-preferences.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { StackCardComponent } from '../../shared/stack-card/stack-card.component';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { NodeRowComponent } from '../../shared/node-row/node-row.component';
import { ContextLineComponent } from '../../shared/context-line/context-line.component';
import { JumpRailComponent, bucketOwners } from '../library/jump-rail.component';
import { isLongLevel } from '../library/jump-scrub';
import {
  CatalogNodeDto, FavoriteLibraryGroupDto, FavoritesQuery, JumpIndexBucketDto, LibraryDto, LibraryReadStateFilter, LibraryViewMode,
  PageResponse, ReadMarkDto, ReadingProgressDto,
} from '../../core/api/api-types';
import {
  favoriteLink,
  favoriteQueryParams,
  favoriteStackLabel,
  favoriteTrackKey,
  isFavoriteStack,
} from './favorite-stack';

/** The Favorites kind filter (owner decision 7): a stack counts as archives. */
export type FavoritesKindFilter = 'all' | 'folders' | 'archives';

/** One entry of the Sort menu: a sort and, for Name, its direction. */
interface SortOption {
  readonly sort: FavoritesSort;
  readonly direction: FavoritesSortDirection;
  readonly label: string;
  readonly icon: string;
  readonly hint?: string;
}

/** 1.51.0 lane F: a library section header of the grouped list (owner decision 10: plain, not collapsible). */
export interface FavoritesGroupHeader {
  readonly libraryId: string;
  readonly name: string;
  readonly count: number | null;
}

/**
 * 1.51.0 lane F: the header to show before each loaded entry of a list grouped by library - one wherever the library changes (and at
 * the top of the loaded window), else null. Names and counts from the answer's `favoriteGroups`, a library missing there by the viewer's
 * library list (count unknown).
 */
export function groupHeaders(
  nodes: readonly CatalogNodeDto[], groups: readonly FavoriteLibraryGroupDto[], libraries: readonly LibraryDto[],
): (FavoritesGroupHeader | null)[] {
  return nodes.map((node, i) => {
    if (i > 0 && nodes[i - 1].libraryId === node.libraryId) return null;
    const group = groups.find((g) => g.libraryId === node.libraryId);
    const name = group?.name ?? libraries.find((l) => l.id === node.libraryId)?.name ?? '';
    return { libraryId: node.libraryId, name, count: group?.count ?? null };
  });
}

/**
 * Favorites view (1.21.0) — the dedicated in-shell destination the sidebar "Favorites"
 * entry routes to. Reuses the browse/search card grammar (cover + kind badge + star)
 * over the `GET /favorites` source with the same keyset paging as browse. Unstarring a card here (or anywhere else, via
 * `FavoritesStateService.changed$`) drops it from the list in place.
 *
 * Series info (1.27.0): the (i) in the cover's bottom-left corner and the hover summary
 * (cover and title), for singles and stacks alike (a stack's is its folder's). 1.48.0: an archive's follows the series it
 * sits in (anchored, as the home Continue row).
 *
 * Read state (1.48.0): per-card Read / Reading badges for archives and the folder rollup badge for folders and stacks (a stack's
 * covers the whole folder), as browse cards; the kind badge and a stack's count moved to the top-left corner for them.
 *
 * Stacks (1.27.0): two or more starred archives in one folder arrive as ONE item for that
 * folder (`favoriteStackCount`), shown as a stacked card that opens the folder filtered to
 * favorites (`?favorites=1`, transient). See `favorite-stack.ts`.
 *
 * 1.50.0 (lane F, the owner's "the favorites tab is barren" - decisions 6-11): browse's display controls on the page's own bar - Card
 * / List (List through the shared `app-node-row`), the card-size and list-columns sliders (inline on desktop and tablet, inside the View
 * menu on a phone, as browse), a Sort menu (Recently favorited, Name A-Z / Z-A, Recently added, Recently updated) and a Filter menu (read
 * state, kind, library) - plus infinite scroll (an IntersectionObserver sentinel, as browse). View mode, sizes and sort are this
 * device's own (`FavoritesDisplayPreferencesService`, seeded once from browse's values on the device); the filters are transient. The
 * place survives a reader round trip: the route reuse strategy retains this view, so Back lands on the same list, filters and scroll; a
 * fresh visit (not Back) reloads the list with the same sort and filters.
 *
 * 1.51.0 (lane F, owner decisions 2, 8-10): a Recently read sort; under the name sort the jump rail (`app-jump-rail`, letters from the
 * favorites' own names, `GET favorites/jump-index`) - a chip whose first card is loaded scrolls to it, any other reloads the list from
 * the chip's cursor and the page then scrolls UP too (`prevCursor` / `before`, browse's top sentinel); "Group by library" (a toolbar
 * toggle, per device): the list library by library with a plain header (name + count) wherever the library changes, in both views, no
 * rail while grouped and no header while one library is filtered.
 */
@Component({
  selector: 'app-favorites',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatIconModule,
    MatButtonModule,
    MatTooltipModule,
    MatMenuModule,
    MatDividerModule,
    CoverImageDirective,
    StarToggleComponent,
    InfoToggleComponent,
    SeriesInfoHoverDirective,
    StackCardComponent,
    FolderRollupBadgeComponent,
    NodeRowComponent,
    ContextLineComponent,
    JumpRailComponent,
  ],
  template: `
    <div class="fav-bar" data-testid="favorites-bar">
      <h2>Favorites</h2>
      @if (viewMode() === 'card') {
        <div class="size-control size-control-inline" matTooltip="Card size">
          <mat-icon class="size-icon">zoom_out</mat-icon>
          <input type="range" class="size-slider" aria-label="Card size" data-testid="favorites-card-size"
                 [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep" [value]="cardSize()"
                 (input)="onCardSizeInput($event)" (change)="onCardSizeChange($event)">
          <mat-icon class="size-icon">zoom_in</mat-icon>
        </div>
      } @else {
        <div class="size-control size-control-inline" matTooltip="List columns">
          <mat-icon class="size-icon">view_agenda</mat-icon>
          <input type="range" class="size-slider" aria-label="List columns" data-testid="favorites-list-columns"
                 [min]="listColumnsMin" [max]="listColumnsMax" [step]="listColumnsStep" [value]="listColumns()"
                 (input)="onListColumnsInput($event)" (change)="onListColumnsChange($event)">
          <mat-icon class="size-icon">view_module</mat-icon>
        </div>
      }
      <button mat-stroked-button class="view-toggle" [matMenuTriggerFor]="viewMenu" data-testid="favorites-view-button"
              matTooltip="Change how your favorites are displayed" aria-label="View options">
        <mat-icon>{{ viewMode() === 'list' ? 'view_list' : 'grid_view' }}</mat-icon> <span class="lbl">View</span>
      </button>
      <!-- The View menu, as browse's: view mode, sort (with the order for Name), and on a phone the size slider. -->
      <mat-menu #viewMenu="matMenu" class="view-options-menu">
        @for (opt of viewOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio" [attr.data-testid]="'favorites-view-' + opt.value"
                  [class.selected-option]="viewMode() === opt.value" [attr.aria-checked]="viewMode() === opt.value"
                  (click)="setViewMode(opt.value)">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        <mat-divider></mat-divider>
        <span class="menu-caption">Sort by</span>
        @for (opt of sortOptions; track opt.label) {
          <button mat-menu-item role="menuitemradio" [attr.data-testid]="'favorites-sort-' + sortKey(opt)"
                  [class.selected-option]="isSort(opt)" [attr.aria-checked]="isSort(opt)"
                  [matTooltip]="opt.hint ?? ''" matTooltipPosition="right"
                  (click)="setSort(opt.sort, opt.direction)">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        @if (viewMode() === 'card') {
          <mat-divider></mat-divider>
          <div class="view-size-section" role="presentation" (click)="$event.stopPropagation()">
            <span class="menu-caption">Card size</span>
            <div class="size-control size-control-menu">
              <mat-icon class="size-icon">zoom_out</mat-icon>
              <input type="range" class="size-slider" aria-label="Card size"
                     [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep" [value]="cardSize()"
                     (input)="onCardSizeInput($event)" (change)="onCardSizeChange($event)">
              <mat-icon class="size-icon">zoom_in</mat-icon>
            </div>
          </div>
        } @else {
          <mat-divider></mat-divider>
          <div class="view-size-section" role="presentation" (click)="$event.stopPropagation()">
            <span class="menu-caption">List columns</span>
            <div class="size-control size-control-menu">
              <mat-icon class="size-icon">view_agenda</mat-icon>
              <input type="range" class="size-slider" aria-label="List columns"
                     [min]="listColumnsMin" [max]="listColumnsMax" [step]="listColumnsStep" [value]="listColumns()"
                     (input)="onListColumnsInput($event)" (change)="onListColumnsChange($event)">
              <mat-icon class="size-icon">view_module</mat-icon>
            </div>
          </div>
        }
      </mat-menu>
      <button mat-stroked-button class="filter-toggle" [matMenuTriggerFor]="filterMenu" data-testid="favorites-filter-button"
              [class.filter-active]="filterActive()" matTooltip="Filter your favorites" aria-label="Filter your favorites">
        <mat-icon>filter_list</mat-icon> <span class="lbl">{{ filterLabel() }}</span>
      </button>
      <mat-menu #filterMenu="matMenu" class="view-options-menu">
        <span class="menu-caption">Show</span>
        @for (opt of readStateOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio" [attr.data-testid]="'favorites-read-' + opt.value"
                  [class.selected-option]="readState() === opt.value" [attr.aria-checked]="readState() === opt.value"
                  (click)="setReadState(opt.value)">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        <span class="menu-caption">Kind</span>
        @for (opt of kindOptions; track opt.value) {
          <button mat-menu-item role="menuitemradio" [attr.data-testid]="'favorites-kind-' + opt.value"
                  [class.selected-option]="kind() === opt.value" [attr.aria-checked]="kind() === opt.value"
                  (click)="setKind(opt.value)">
            <mat-icon>{{ opt.icon }}</mat-icon>
            {{ opt.label }}
          </button>
        }
        @if (libraries().length > 1) {
          <span class="menu-caption">Library</span>
          <button mat-menu-item role="menuitemradio" data-testid="favorites-library-all"
                  [class.selected-option]="libraryId() === null" [attr.aria-checked]="libraryId() === null"
                  (click)="setLibrary(null)">
            <mat-icon>local_library</mat-icon>
            All libraries
          </button>
          @for (lib of libraries(); track lib.id) {
            <button mat-menu-item role="menuitemradio" [attr.data-testid]="'favorites-library-' + lib.id"
                    [class.selected-option]="libraryId() === lib.id" [attr.aria-checked]="libraryId() === lib.id"
                    (click)="setLibrary(lib.id)">
              <mat-icon>{{ lib.icon || 'collections_bookmark' }}</mat-icon>
              <span class="menu-lib-name">{{ lib.name }}</span>
            </button>
          }
        }
      </mat-menu>
      @if (libraries().length > 1) {
        <!-- 1.51.0 (owner addition, decision 9): Group by library, remembered on this device. Icon-only on a phone (.lbl). -->
        <button mat-stroked-button class="group-toggle" data-testid="favorites-group-button"
                [class.group-active]="groupByLibrary()" [attr.aria-pressed]="groupByLibrary()"
                matTooltip="Group by library" aria-label="Group by library" (click)="toggleGroupByLibrary()">
          <mat-icon>library_books</mat-icon> <span class="lbl">Group</span>
        </button>
      }
    </div>

    <!-- 1.51.0 (decisions 2, 8): the jump rail under the name sort, never while grouped; the phone picker and strip are the rail's own. -->
    @if (railShown()) {
      <app-jump-rail [buckets]="jumpBuckets()" mode="letters" [active]="activeJump()" [top]="0" (jump)="jumpToBucket($event)" />
    }

    @if (hasPrevious()) {
      <!-- 1.51.0: after a jump the window starts mid-list; scrolling up to here prepends the page above (scroll-anchored, as browse). -->
      <div class="scroll-sentinel top-sentinel" #topSentinel data-testid="favorites-top-sentinel">
        @if (loadingPrevious()) {
          <span role="status">Loading previous…</span>
        } @else if (!autoLoadSupported) {
          <button mat-stroked-button (click)="loadPrevious()">Load previous</button>
        }
      </div>
    }

    <div class="nodes" [class.card]="viewMode() === 'card'" [class.list]="viewMode() === 'list'"
         [class.scrub-gutter]="scrubGutter()" data-testid="favorites-list"
         [style.--card-size]="cardSize() + 'px'" [style.--list-columns]="listColumns()">
      @for (node of nodes(); track trackKey(node); let i = $index) {
        @if (headers()[i]; as header) {
          <!-- 1.51.0 (decision 10): a plain library header wherever the library changes. -->
          <div class="lib-header" role="heading" aria-level="3" data-testid="favorites-group-header">
            <span class="lib-name">{{ header.name }}</span>
            @if (header.count !== null) {
              <span class="lib-count">{{ header.count }}</span>
            }
          </div>
        }
        @if (viewMode() === 'list') {
          <!-- List view (1.50.0): the shared row, with the favorites' stack count, link query and anchored (i). -->
          <app-node-row class="fav-row fav-item" [node]="node" [link]="getNodeLink(node)" [queryParams]="stackParams(node)"
                        [subtitle]="rowSub(node)" [selectable]="false" [hoverId]="hoverNodeId(node)"
                        [stackCount]="isStack(node) ? node.favoriteStackCount ?? null : null"
                        [showStar]="!isStack(node)" [anchoredInfo]="node.kind === 'Archive'"
                        [contextName]="contextOf(node)" />
        } @else if (isStack(node)) {
          <!-- Stack (1.27.0): two or more starred archives in one folder. Opens the
               folder with a transient "Favorites only" filter; no star here (it would
               star the folder itself). -->
          <div class="fav-card fav-item">
            <a #cardEl [routerLink]="getNodeLink(node)" [queryParams]="stackParams(node)" class="fav-link stack-link"
               [attr.aria-label]="node.displayName + ', ' + stackLabel(node)">
              <app-stack-card [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">
                @if (coverSrc(node); as src) {
                  <img appCover [src]="src" alt="" loading="lazy">
                }
                <mat-icon class="cover-fallback">folder</mat-icon>
                <span class="count-badge" [matTooltip]="stackLabel(node)">{{ node.favoriteStackCount }}</span>
                <app-folder-rollup-badge [rollup]="node.readRollup" />
                <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo" [overlay]="true" />
              </app-stack-card>
              <div class="fav-title" [title]="node.displayName"
                   [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
              <div class="fav-sub">{{ stackLabel(node) }}</div>
            </a>
          </div>
        } @else {
          <div class="fav-card fav-item">
            <a #cardEl [routerLink]="getNodeLink(node)" class="fav-link">
              <div class="cover" [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">
                @if (coverSrc(node); as src) {
                  <img appCover [src]="src" alt="" loading="lazy">
                }
                <mat-icon class="cover-fallback">{{ kindIcon(node) }}</mat-icon>
                <span class="kind-badge" [matTooltip]="kindLabel(node)" [attr.aria-label]="kindLabel(node)" role="img">
                  <mat-icon>{{ kindIcon(node) }}</mat-icon>
                </span>
                <!-- Read state (1.48.0), as a browse card: an archive's Read / Reading, a folder's rollup. -->
                @if (node.isRead) {
                  <span class="badge read" matTooltip="Read">✓ Read</span>
                } @else if (node.readingState === 'InProgress') {
                  <span class="badge reading">Reading</span>
                }
                <app-folder-rollup-badge [rollup]="node.readRollup" />
                <app-star-toggle [nodeId]="node.id" [favorite]="true" [overlay]="true" [compact]="true" />
                <app-info-toggle [nodeId]="node.id" [hasSeriesInfo]="!!node.hasSeriesInfo"
                                 [anchored]="node.kind === 'Archive'" [overlay]="true" />
              </div>
              <div class="fav-title" [title]="node.displayName"
                   [appSeriesInfoHover]="hoverNodeId(node)" [hoverAnchor]="cardEl">{{ node.displayName }}</div>
              <!-- 1.50.0 (lane W): the folder an archive belongs to, under its name. -->
              <app-context-line [name]="contextOf(node)" />
            </a>
          </div>
        }
      } @empty {
        @if (loaded() && !loading()) {
          @if (filterActive()) {
            <div class="empty" data-testid="favorites-empty-filtered">
              <p>No favorites match these filters.</p>
              <button mat-stroked-button (click)="clearFilters()">Clear filters</button>
            </div>
          } @else {
            <p class="empty">
              <mat-icon>star_border</mat-icon>
              No favorites yet. Tap the star on any archive or folder to add it here.
            </p>
          }
        }
      }
    </div>

    @if (hasMore()) {
      <!-- Infinite scroll (1.50.0, as browse): the sentinel loads the next page as it nears the viewport; the button is the fallback
           for engines without IntersectionObserver. -->
      <div class="scroll-sentinel" #sentinel>
        @if (loading()) {
          <span role="status">Loading more…</span>
        } @else if (!autoLoadSupported) {
          <button mat-stroked-button (click)="loadMore()">Load more</button>
        }
      </div>
    }
  `,
  styles: [`
    .fav-bar {
      display: flex; align-items: center; gap: 12px; flex-wrap: wrap;
      margin-bottom: 12px;
    }
    .fav-bar h2 { margin: 0; flex: 1 1 auto; min-width: 0; }
    .size-control { display: flex; align-items: center; gap: 6px; flex: 0 0 auto; }
    .size-control .size-icon,
    ::ng-deep .view-options-menu .size-control-menu .size-icon {
      font-size: 18px; width: 18px; height: 18px; color: var(--mp-text-dim);
    }
    .size-slider { width: 120px; max-width: 34vw; accent-color: var(--mp-accent-strong); cursor: pointer; background: transparent; }
    .view-toggle mat-icon, .filter-toggle mat-icon { margin-right: 4px; }
    .filter-toggle.filter-active, .group-toggle.group-active { border-color: var(--mp-accent-strong); color: var(--mp-accent); }
    .group-toggle mat-icon { margin-right: 4px; }
    .group-toggle.group-active mat-icon { color: var(--mp-accent); }
    /* 1.51.0: the library header of the grouped list - a full-width row of the card grid or the list grid. */
    .lib-header {
      grid-column: 1 / -1; min-width: 0;
      display: flex; align-items: baseline; gap: 8px;
      margin-top: 12px; padding-bottom: 4px;
      border-bottom: 1px solid rgb(var(--mp-ink-rgb) / 0.12);
    }
    .lib-header:first-child { margin-top: 0; }
    .lib-name {
      min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      font-size: 15px; font-weight: 600; color: var(--mp-text);
    }
    .lib-count { flex: none; font-size: 12px; color: var(--mp-text-muted); font-variant-numeric: tabular-nums; }
    .filter-toggle.filter-active mat-icon { color: var(--mp-accent); }
    .menu-caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: var(--mp-text-dim);
    }
    .menu-lib-name { display: inline-block; max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; vertical-align: bottom; }
    ::ng-deep .view-options-menu .selected-option { background: rgb(var(--mp-accent-strong-rgb) / 0.16); }
    ::ng-deep .view-options-menu .selected-option,
    ::ng-deep .view-options-menu .selected-option .mat-icon { color: var(--mp-accent); }
    /* The phone keeps the sliders in the View menu (as browse, 1.10.2); desktop and tablet use the inline ones. */
    ::ng-deep .view-options-menu .view-size-section { display: none; padding: 2px 12px 10px; }
    ::ng-deep .view-options-menu .size-control-menu { display: flex; align-items: center; gap: 8px; padding: 6px 0 0; }
    ::ng-deep .view-options-menu .size-slider { width: 180px; max-width: 60vw; accent-color: var(--mp-accent-strong); }
    .nodes.card {
      display: grid;
      gap: clamp(10px, calc(var(--card-size, 150px) * 0.09), 20px);
      grid-template-columns: repeat(auto-fill, minmax(var(--card-size, 150px), 1fr));
      padding-top: 8px; /* room for a stack's paper edges */
    }
    .nodes.list { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; }
    @media (min-width: 960px) {
      .nodes.list { grid-template-columns: repeat(var(--list-columns, 2), minmax(0, 1fr)); column-gap: 16px; }
    }
    .fav-card { min-width: 0; }
    .fav-link { text-decoration: none; color: inherit; display: block; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgb(var(--mp-ink-rgb) / 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 44px; width: 44px; height: 44px; color: var(--mp-text-faint); position: absolute; z-index: 0; }
    /* 1.48.0: top-left - the top-right corner belongs to the read badges, as on a browse card. */
    .kind-badge {
      position: absolute; top: 4px; left: 4px; z-index: 2;
      display: flex; align-items: center; justify-content: center;
      width: 20px; height: 20px; border-radius: 50%;
      background: rgb(var(--mp-shade-rgb) / 0.65); color: var(--mp-on-scrim);
    }
    .kind-badge mat-icon { font-size: 14px; width: 14px; height: 14px; line-height: 14px; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgb(var(--mp-accent-strong-rgb) / 0.9); color: var(--mp-on-accent);
    }
    .badge.read { background: rgb(var(--mp-success-strong-rgb) / 0.95); }
    .fav-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .fav-sub { font-size: 12px; color: var(--mp-text-muted); }
    /* Stack (1.27.0): the shared stacked card (app-stack-card, 1.29.0) draws the paper
       edges; the number of favorites inside sits in the top-left corner (1.48.0: the top-right one is the folder's read badge). */
    .count-badge {
      position: absolute; top: 6px; left: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 10px;
      background: rgb(var(--mp-accent-strong-rgb) / 0.92); color: var(--mp-on-accent);
    }
    .scroll-sentinel { min-height: 40px; margin: 16px 0; text-align: center; color: var(--mp-text-muted); font-size: 12px; }
    .empty {
      grid-column: 1 / -1;
      color: var(--mp-text-muted); padding: 48px 16px; text-align: center;
      display: flex; flex-direction: column; align-items: center; gap: 8px;
    }
    .empty p { margin: 0; }
    .empty mat-icon { font-size: 40px; width: 40px; height: 40px; color: var(--mp-text-faint); }
    @media (max-width: 599.98px) {
      /* 1.51.0 (mobile sweep M7): clear of the phone scrub strip on the right edge (it took the right-hand stars' taps). */
      .nodes.scrub-gutter { margin-right: max(0px, calc(44px - var(--mp-gutter))); }
      /* 1.51.0 (gate sweep N1): that 28 px cost the default card size its second column at 360 px (one 300 px card per screen). A
         card may give up to 20 px of its minimum so a second column fits; a card size chosen large stays one column, as before. */
      .nodes.scrub-gutter:not(.list) {
        grid-template-columns: repeat(auto-fill, minmax(min(var(--card-size, 150px), max(calc(50% - 10px), calc(var(--card-size, 150px) - 20px))), 1fr));
      }
      ::ng-deep .view-options-menu .view-size-section { display: block; }
      .size-control-inline { display: none; }
      .fav-bar .lbl { display: none; }
      .fav-bar .view-toggle mat-icon, .fav-bar .filter-toggle mat-icon, .fav-bar .group-toggle mat-icon { margin-right: 0; }
    }
  `],
})
export class FavoritesComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly favorites = inject(FavoritesStateService);
  private readonly readStates = inject(ReadStateService);
  private readonly display = inject(FavoritesDisplayPreferencesService);
  private readonly browseDisplay = inject(DeviceDisplayPreferencesService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly zone = inject(NgZone);
  private readonly injector = inject(Injector);
  /** 1.52.0 (sweep N2): the entry a reloading jump is for - once its page is drawn it is scrolled to the top, as a loaded one is. */
  private pendingJumpTarget: string | null = null;

  readonly nodes = signal<CatalogNodeDto[]>([]);
  readonly hasMore = signal(false);
  readonly loading = signal(false);
  readonly loaded = signal(false);

  // Display (per device, 1.50.0).
  readonly viewMode = this.display.viewMode;
  readonly cardSize = this.display.cardSize;
  readonly listColumns = this.display.listColumns;
  readonly sortChoice = this.display.sort;
  readonly cardSizeMin = CARD_SIZE_MIN;
  readonly cardSizeMax = CARD_SIZE_MAX;
  readonly cardSizeStep = CARD_SIZE_STEP;
  readonly listColumnsMin = LIST_COLUMNS_MIN;
  readonly listColumnsMax = LIST_COLUMNS_MAX;
  readonly listColumnsStep = LIST_COLUMNS_STEP;

  readonly viewOptions: { value: LibraryViewMode; label: string; icon: string }[] = [
    { value: 'card', label: 'Card', icon: 'grid_view' },
    { value: 'list', label: 'List', icon: 'view_list' },
  ];
  readonly sortOptions: readonly SortOption[] = [
    { sort: 'favorited', direction: 'desc', label: 'Recently favorited', icon: 'star' },
    { sort: 'name', direction: 'asc', label: 'Name (A-Z)', icon: 'sort_by_alpha' },
    { sort: 'name', direction: 'desc', label: 'Name (Z-A)', icon: 'sort_by_alpha' },
    { sort: 'recentlyAdded', direction: 'desc', label: 'Recently added', icon: 'new_releases', hint: 'New items appear' },
    { sort: 'recentlyUpdated', direction: 'desc', label: 'Recently updated', icon: 'update', hint: 'Folders with new content' },
    { sort: 'recentlyRead', direction: 'desc', label: 'Recently read', icon: 'history', hint: 'What you read last' },
  ];

  // Group by library (1.51.0, per device - decision 9). Effective only without a library filter: one library needs no header.
  readonly groupByLibrary = this.display.groupByLibrary;
  readonly grouped = computed(() => this.groupByLibrary() && this.libraryId() === null);
  /** The answer's library sections (names + counts of the whole list), from the latest fresh load. */
  readonly groups = signal<FavoriteLibraryGroupDto[]>([]);
  /** The header before each loaded entry (null = none), while grouped. */
  readonly headers = computed<(FavoritesGroupHeader | null)[]>(() =>
    this.grouped() ? groupHeaders(this.nodes(), this.groups(), this.libraries()) : []);

  // Jump rail (1.51.0, decisions 2 and 8): under the name sort only, hidden while grouped. `activeJump` = the bucket of the topmost card.
  readonly jumpBuckets = signal<JumpIndexBucketDto[]>([]);
  readonly activeJump = signal<JumpIndexBucketDto | null>(null);
  readonly railShown = computed(() => this.sortChoice().sort === 'name' && !this.grouped() && this.jumpBuckets().length > 0);
  /** 1.51.0 (mobile sweep M7): the rail's phone scrub strip shows (a long list) - the list keeps its 44 px free. */
  readonly scrubGutter = computed(() => this.railShown() && isLongLevel(this.jumpBuckets()));
  private readonly jumpOwners = computed(() => bucketOwners(this.nodes(), this.jumpBuckets()));
  private jumpGeneration = 0;

  // Backward paging (1.51.0): a window that starts mid-list (after a jump) pages up through `before`.
  readonly hasPrevious = signal(false);
  readonly loadingPrevious = signal(false);
  private prevCursor: string | null = null;
  private readonly topSentinel = viewChild<ElementRef<HTMLElement>>('topSentinel');
  private topSentinelObserver: IntersectionObserver | null = null;
  private scrollTarget: HTMLElement | Window | null = null;
  private scrollSpyFrame: number | null = null;
  private readonly onScroll = (): void => this.scheduleScrollSpy();

  // Filters (transient, owner decision 9).
  readonly readState = signal<LibraryReadStateFilter>('all');
  readonly kind = signal<FavoritesKindFilter>('all');
  readonly libraryId = signal<string | null>(null);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly readStateOptions: { value: LibraryReadStateFilter; label: string; icon: string }[] = [
    { value: 'all', label: 'All', icon: 'filter_list' },
    { value: 'reading', label: 'Reading', icon: 'auto_stories' },
    { value: 'read', label: 'Read', icon: 'check_circle' },
    { value: 'unread', label: 'Unread', icon: 'radio_button_unchecked' },
  ];
  readonly kindOptions: { value: FavoritesKindFilter; label: string; icon: string }[] = [
    { value: 'all', label: 'Folders and archives', icon: 'apps' },
    { value: 'folders', label: 'Folders', icon: 'folder' },
    { value: 'archives', label: 'Archives', icon: 'menu_book' },
  ];
  readonly filterActive = computed(() => this.readState() !== 'all' || this.kind() !== 'all' || this.libraryId() !== null);
  /** The Filter button's label: the read state when only it is set, "Filtered" for any other filter, else "Filter". */
  readonly filterLabel = computed(() => {
    if (this.kind() !== 'all' || this.libraryId() !== null) return 'Filtered';
    const state = this.readState();
    return state === 'all' ? 'Filter' : this.readStateOptions.find((o) => o.value === state)?.label ?? 'Filter';
  });

  /** IntersectionObserver is absent in some engines (and jsdom); then the fallback button shows. */
  readonly autoLoadSupported = typeof IntersectionObserver !== 'undefined';
  private readonly sentinel = viewChild<ElementRef<HTMLElement>>('sentinel');
  private sentinelObserver: IntersectionObserver | null = null;

  private cursor: string | null = null;
  /** Bumped by every fresh load: an answer of an older load (another sort or filter) is dropped. */
  private generation = 0;
  private readonly subs = new Subscription();
  /** The trigger of the navigation in flight: Back / Forward (popstate) re-shows the retained view as it was. */
  private lastTrigger: 'imperative' | 'popstate' | 'hashchange' = 'imperative';
  /** The router left this view since it was shown (it may be retained by the reuse strategy and shown again). */
  private away = false;

  constructor() {
    // The sentinel lives inside `@if (hasMore())`, so it comes and goes; re-arm the observer on the current element.
    effect(() => this.observeSentinel(this.sentinel()?.nativeElement ?? null));
    // 1.51.0: the top sentinel lives inside `@if (hasPrevious())`; re-arm on the current element.
    effect(() => this.observeTopSentinel(this.topSentinel()?.nativeElement ?? null));
    // The scroll-spy listens on the scroller that holds this view (the shell's content pane, else the window).
    afterNextRender(() => this.attachScrollSpy());
  }

  ngOnInit(): void {
    this.seedDisplayOnce();
    this.load(false);
    this.loadJumpIndex();
    this.subs.add(this.api.getLibraries().pipe(catchError(() => of([] as LibraryDto[])))
      .subscribe((libs) => this.libraries.set(libs)));
    // A node unstarred anywhere drops out of this list in place. A stack stays: it
    // stands for its starred children, not for the folder's own star.
    this.subs.add(this.favorites.changed$.subscribe((change) => {
      if (!change.favorite) {
        this.nodes.update((list) => list.filter((n) => n.id !== change.nodeId || isFavoriteStack(n)));
      }
    }));
    // Back from the reader the retained view keeps its cards: patch the read state of the one that changed (as browse).
    this.subs.add(this.readStates.itemChanged$.subscribe((itemId) => this.refreshNodeStatus(itemId)));
    // Route reuse (1.50.0): this instance is retained across a reader round trip. Back (popstate) shows it as it was - same list,
    // filters and scroll; any other arrival (the sidebar, a link) reloads it with the same sort and filters.
    this.subs.add(this.router.events.pipe(filter((e) => e instanceof NavigationStart))
      .subscribe((e) => { this.lastTrigger = (e as NavigationStart).navigationTrigger ?? 'imperative'; }));
    this.subs.add(this.router.events.pipe(filter((e) => e instanceof NavigationEnd))
      .subscribe((e) => {
        const here = (e as NavigationEnd).urlAfterRedirects.split('?')[0] === '/favorites';
        if (!here) {
          this.away = true;
          return;
        }
        if (!this.away) return; // the navigation that created this view
        this.away = false;
        if (this.lastTrigger !== 'popstate' && this.host.nativeElement.isConnected) this.reload();
      }));
  }

  ngOnDestroy(): void {
    this.subs.unsubscribe();
    this.sentinelObserver?.disconnect();
    this.topSentinelObserver?.disconnect();
    this.scrollTarget?.removeEventListener('scroll', this.onScroll);
    if (this.scrollSpyFrame !== null && typeof cancelAnimationFrame !== 'undefined') cancelAnimationFrame(this.scrollSpyFrame);
  }

  // --- Display ---

  setViewMode(mode: LibraryViewMode): void {
    this.display.setViewMode(mode);
  }

  onCardSizeInput(event: Event): void {
    this.display.previewCardSizeOnly(Number((event.target as HTMLInputElement).value));
  }

  onCardSizeChange(event: Event): void {
    this.display.setCardSize(Number((event.target as HTMLInputElement).value));
  }

  onListColumnsInput(event: Event): void {
    this.display.previewListColumnsOnly(Number((event.target as HTMLInputElement).value));
  }

  onListColumnsChange(event: Event): void {
    this.display.setListColumns(Number((event.target as HTMLInputElement).value));
  }

  sortKey(opt: SortOption): string {
    return opt.sort === 'name' && opt.direction === 'desc' ? 'name-desc' : opt.sort;
  }

  isSort(opt: SortOption): boolean {
    const now = this.sortChoice();
    return now.sort === opt.sort && (opt.sort !== 'name' || now.direction === opt.direction);
  }

  setSort(sort: FavoritesSort, direction: FavoritesSortDirection): void {
    const now = this.sortChoice();
    if (now.sort === sort && (sort !== 'name' || now.direction === direction)) return;
    this.display.setSort(sort, direction);
    this.reload();
  }

  // --- Filters ---

  setReadState(state: LibraryReadStateFilter): void {
    if (this.readState() === state) return;
    this.readState.set(state);
    this.reload();
  }

  setKind(kind: FavoritesKindFilter): void {
    if (this.kind() === kind) return;
    this.kind.set(kind);
    this.reload();
  }

  setLibrary(libraryId: string | null): void {
    if (this.libraryId() === libraryId) return;
    this.libraryId.set(libraryId);
    this.reload();
  }

  /** 1.51.0: Group by library on / off - stored on this device, the list reloads from the top. */
  toggleGroupByLibrary(): void {
    this.display.setGroupByLibrary(!this.groupByLibrary());
    this.reload();
  }

  clearFilters(): void {
    this.readState.set('all');
    this.kind.set('all');
    this.libraryId.set(null);
    this.reload();
  }

  // --- Paging ---

  loadMore(): void {
    if (this.hasMore() && !this.loading()) this.load(true);
  }

  private reload(): void {
    this.cursor = null;
    this.hasMore.set(false);
    this.load(false);
    this.loadJumpIndex();
  }

  /** The listing's query: sort, filters and (1.51.0) the grouping - sent only while it applies, so other calls stay as they were. */
  private query(): FavoritesQuery {
    const choice = this.sortChoice();
    const query: FavoritesQuery = {
      sort: choice.sort,
      direction: choice.direction,
      readState: this.readState(),
      kind: this.kind(),
      libraryId: this.libraryId(),
    };
    return this.grouped() ? { ...query, groupByLibrary: true } : query;
  }

  /**
   * A fresh window (`append` false: the first page, or - 1.51.0 - the page a jump lands on, from `start`) replaces the list and takes the
   * window-top state (`prevCursor` / `hasPrevious`) and the library sections; an append adds the next page below.
   */
  private load(append: boolean, start: string | null = null): void {
    const generation = append ? this.generation : ++this.generation;
    this.loading.set(true);
    if (!append) this.loadingPrevious.set(false);
    this.api.getFavorites(append ? this.cursor : start, 50, this.query()).subscribe({
      next: (page) => {
        if (generation !== this.generation) return;
        this.nodes.update((prev) => (append ? [...prev, ...page.items] : page.items));
        if (!append && this.pendingJumpTarget) this.revealJumpTarget(this.pendingJumpTarget);
        this.cursor = page.nextCursor;
        this.hasMore.set(page.hasMore);
        if (!append) {
          this.prevCursor = page.prevCursor ?? null;
          this.hasPrevious.set(page.hasPrevious ?? false);
          this.groups.set(page.favoriteGroups ?? []);
        }
        this.loading.set(false);
        this.loaded.set(true);
        this.rearmSentinel();
        if (this.hasPrevious()) this.rearmTopSentinel();
      },
      error: () => {
        if (generation !== this.generation) return;
        this.loading.set(false);
        this.loaded.set(true);
      },
    });
  }

  /**
   * 1.51.0: load and PREPEND the page above the window's first entry (after a mid-list jump), keeping what is on screen in place. Never
   * while another page loads; dropped when a fresh load replaced the window meanwhile.
   */
  loadPrevious(): void {
    if (this.loading() || this.loadingPrevious() || !this.hasPrevious() || !this.prevCursor) return;
    const generation = this.generation;
    const container = this.scrollParent();
    const before = this.scrollHeightOf(container);
    this.loadingPrevious.set(true);
    this.api.getFavorites(null, 50, { ...this.query(), before: this.prevCursor }).subscribe({
      next: (page: PageResponse<CatalogNodeDto>) => {
        if (generation !== this.generation) return;
        if (page.items.length > 0) this.nodes.update((list) => [...page.items, ...list]);
        this.prevCursor = page.prevCursor ?? null;
        this.hasPrevious.set(page.hasPrevious ?? false);
        this.loadingPrevious.set(false);
        this.anchorAfterPrepend(container, before);
        if (this.hasPrevious()) this.rearmTopSentinel();
      },
      error: () => { if (generation === this.generation) this.loadingPrevious.set(false); },
    });
  }

  // --- Jump rail (1.51.0) ---

  /** The rail of the current list: under the name sort and not grouped (else none), for the same direction and filters. */
  private loadJumpIndex(): void {
    const generation = ++this.jumpGeneration;
    this.jumpBuckets.set([]);
    this.activeJump.set(null);
    if (this.sortChoice().sort !== 'name' || this.grouped()) return;
    const { direction, readState, kind, libraryId } = this.query();
    this.api.getFavoritesJumpIndex({ direction, readState, kind, libraryId }).pipe(catchError(() => of(null))).subscribe((rail) => {
      if (generation !== this.jumpGeneration) return;
      this.jumpBuckets.set(rail?.buckets ?? []);
    });
  }

  /**
   * A chip: when its first card is loaded, scroll to it (nothing reloads); otherwise reload the list from the chip's cursor (null = the
   * top) - the window then starts mid-list and pages both ways.
   */
  jumpToBucket(bucket: JumpIndexBucketDto): void {
    this.activeJump.set(bucket);
    const index = bucket.firstNodeId ? this.nodes().findIndex((n) => n.id === bucket.firstNodeId) : -1;
    if (index !== -1 && this.scrollToCard(index)) return;
    this.cursor = null;
    this.hasMore.set(false);
    this.hasPrevious.set(false);
    this.prevCursor = null;
    this.nodes.set([]);
    this.pendingJumpTarget = bucket.firstNodeId ?? null;
    this.load(false, bucket.firstCursor ?? null);
    this.scrollListToTop();
  }

  /**
   * 1.52.0 (sweep N2): the page a jump reloaded is drawn below the page's own header, and on a short screen (a phone held sideways,
   * 390 px tall) the picked letter's first row sat under the bottom edge. Once drawn, it is scrolled to the top like a loaded one - before
   * the page above it is prepended (which keeps what is on screen in place).
   */
  private revealJumpTarget(nodeId: string): void {
    this.pendingJumpTarget = null;
    afterNextRender(() => {
      const index = this.nodes().findIndex((n) => n.id === nodeId);
      if (index !== -1) this.scrollToCard(index);
    }, { injector: this.injector });
  }

  /**
   * Scroll-spy: the bucket of the topmost card below the rail (by position - each bucket's first entry, `bucketOwners`). Public so tests
   * can drive it.
   */
  updateActiveJump(): void {
    const buckets = this.jumpBuckets();
    if (buckets.length === 0 || !this.host.nativeElement.isConnected) return;
    const cards = this.host.nativeElement.querySelectorAll<HTMLElement>('.fav-item');
    const nodes = this.nodes();
    if (nodes.length === 0 || cards.length !== nodes.length) return;
    const rail = this.host.nativeElement.querySelector<HTMLElement>('.jump-rail');
    const railShown = !!rail && (typeof getComputedStyle === 'undefined' || getComputedStyle(rail).display !== 'none');
    const threshold = railShown ? rail!.getBoundingClientRect().bottom : (this.scrollParent()?.getBoundingClientRect().top ?? 0);
    let first = cards.length - 1;
    for (let i = 0; i < cards.length; i++) {
      if (cards[i].getBoundingClientRect().bottom > threshold) { first = i; break; }
    }
    const owners = this.jumpOwners();
    if (owners[first] === undefined || owners[first] < 0) return; // no bucket starts in this window: keep the chip
    const current = this.activeJump();
    // Near the end of the list the page cannot scroll a chip's first card up to the top: keep the chip while the page cannot scroll
    // further and that card is on screen (browse's 1.47.0 rule).
    if (current && this.atScrollEnd() && this.cardOnScreen(cards, current, threshold)) return;
    // Cards of one grid row share their bottom edge: keep the current chip while any card of the first visible row is in it (browse's
    // rule - a click that scrolled to a bucket's first card mid-row shows that bucket).
    const rowBottom = cards[first].getBoundingClientRect().bottom;
    let active = buckets[owners[first]];
    for (let i = first; i < cards.length && Math.abs(cards[i].getBoundingClientRect().bottom - rowBottom) < 1; i++) {
      if (current && buckets[owners[i]] === current) { active = current; break; }
    }
    this.activeJump.set(active);
  }

  /** True when the list's scroller cannot scroll any further (at its end, or nothing to scroll); false without a layout (jsdom). */
  private atScrollEnd(): boolean {
    const el = this.scrollParent() ?? (typeof document !== 'undefined' ? (document.scrollingElement as HTMLElement | null) : null);
    return !!el && el.clientHeight > 0 && el.scrollTop + el.clientHeight >= el.scrollHeight - 2;
  }

  /** The first card of `bucket` is loaded and at least partly below the rail and above the screen's bottom. */
  private cardOnScreen(cards: NodeListOf<HTMLElement>, bucket: JumpIndexBucketDto, threshold: number): boolean {
    const index = bucket.firstNodeId ? this.nodes().findIndex((n) => n.id === bucket.firstNodeId) : -1;
    const card = index >= 0 ? cards[index] : undefined;
    if (!card) return false;
    const box = card.getBoundingClientRect();
    const bottom = typeof window !== 'undefined' ? window.innerHeight : 0;
    return box.bottom > threshold && box.top < bottom;
  }

  private scrollToCard(index: number): boolean {
    const card = this.host.nativeElement.querySelectorAll<HTMLElement>('.fav-item')[index];
    if (!card) return false;
    try { card.scrollIntoView({ block: 'start', behavior: 'auto' }); } catch { /* jsdom */ }
    const rail = this.host.nativeElement.querySelector<HTMLElement>('.jump-rail');
    const container = this.scrollParent();
    // Below the sticky rail, not under it.
    if (rail && container && typeof getComputedStyle !== 'undefined' && getComputedStyle(rail).display !== 'none') {
      try { container.scrollBy({ top: -(rail.offsetHeight + 8), behavior: 'auto' }); } catch { /* jsdom */ }
    }
    return true;
  }

  private scrollListToTop(): void {
    const container = this.scrollParent();
    try {
      if (container) container.scrollTo({ top: 0, behavior: 'auto' });
      else if (typeof window !== 'undefined') window.scrollTo({ top: 0, behavior: 'auto' });
    } catch { /* jsdom */ }
  }

  private attachScrollSpy(): void {
    this.scrollTarget?.removeEventListener('scroll', this.onScroll);
    this.scrollTarget = this.scrollParent() ?? (typeof window !== 'undefined' ? window : null);
    this.scrollTarget?.addEventListener('scroll', this.onScroll, { passive: true });
  }

  private scheduleScrollSpy(): void {
    if (this.scrollSpyFrame !== null || typeof requestAnimationFrame === 'undefined') return;
    this.scrollSpyFrame = requestAnimationFrame(() => {
      this.scrollSpyFrame = null;
      this.updateActiveJump();
    });
  }

  /** (Re)attach the backward-paging observer to the current top-sentinel element. */
  private observeTopSentinel(el: HTMLElement | null): void {
    this.topSentinelObserver?.disconnect();
    this.topSentinelObserver = null;
    if (!el || !this.autoLoadSupported) return;
    this.topSentinelObserver = new IntersectionObserver(
      (entries) => { if (entries.some((e) => e.isIntersecting)) this.zone.run(() => this.loadPrevious()); },
      { root: this.scrollParent(), rootMargin: '600px 0px 0px 0px' });
    this.topSentinelObserver.observe(el);
  }

  private rearmTopSentinel(): void {
    const el = this.topSentinel()?.nativeElement;
    if (!el || !this.topSentinelObserver) return;
    this.topSentinelObserver.unobserve(el);
    this.topSentinelObserver.observe(el);
  }

  private scrollHeightOf(container: HTMLElement | null): number {
    if (container) return container.scrollHeight;
    return typeof document !== 'undefined' ? document.documentElement.scrollHeight : 0;
  }

  /** After a prepend, scroll down by the height the new cards added, so the cards on screen stay put (browse's anchor). */
  private anchorAfterPrepend(container: HTMLElement | null, prevHeight: number): void {
    const apply = (): void => {
      const delta = this.scrollHeightOf(container) - prevHeight;
      if (delta <= 0) return;
      const target = container ?? (typeof window !== 'undefined' ? window : null);
      try { target?.scrollBy({ top: delta, behavior: 'auto' }); } catch { /* jsdom */ }
    };
    if (typeof requestAnimationFrame !== 'undefined') requestAnimationFrame(apply);
    else apply();
  }

  /** (Re)attach the infinite-scroll observer to the current sentinel element (browse's pattern, 1.8.0). */
  private observeSentinel(el: HTMLElement | null): void {
    this.sentinelObserver?.disconnect();
    this.sentinelObserver = null;
    if (!el || !this.autoLoadSupported) return;
    this.sentinelObserver = new IntersectionObserver(
      (entries) => { if (entries.some((e) => e.isIntersecting)) this.zone.run(() => this.loadMore()); },
      { root: this.scrollParent(), rootMargin: '0px 0px 600px 0px' });
    this.sentinelObserver.observe(el);
  }

  /** A short page may leave the sentinel inside the margin without a new crossing: re-observe so it fires again when needed. */
  private rearmSentinel(): void {
    const el = this.sentinel()?.nativeElement;
    if (!el || !this.sentinelObserver) return;
    this.sentinelObserver.unobserve(el);
    this.sentinelObserver.observe(el);
  }

  /** The nearest scrolling ancestor (the shell's content pane), or null for the window. */
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

  /**
   * Owner decision 10: a device without its own Favorites display values starts from browse's values on that device - its card size
   * and list columns (device copy, else the server value browse would seed) and the per-user browse view mode - stored once.
   */
  private seedDisplayOnce(): void {
    if (!this.display.needsSeed()) return;
    this.api.getLibraryPreferences().pipe(catchError(() => of(null))).subscribe((prefs) => {
      const shown = this.browseDisplay.shownFor(prefs);
      this.display.seedOnce({ ...shown, viewMode: prefs?.viewMode === 'list' ? 'list' : 'card' });
    });
  }

  private refreshNodeStatus(itemId: string): void {
    if (!this.nodes().some((n) => n.id === itemId && n.kind === 'Archive')) return;
    forkJoin({
      read: this.api.getReadMark(itemId).pipe(catchError(() => of<ReadMarkDto>({ itemId, isRead: false }))),
      progress: this.api.getProgress(itemId).pipe(catchError(() => of<ReadingProgressDto | null>(null))),
    }).subscribe(({ read, progress }) => {
      this.nodes.update((list) => list.map((n) => n.id === itemId && n.kind === 'Archive'
        ? { ...n, isRead: read.isRead, readingState: progress?.state ?? n.readingState, lastReadPage: progress ? progress.pageIndex : n.lastReadPage }
        : n));
    });
  }

  // --- Card helpers ---

  getNodeLink(node: CatalogNodeDto): string[] {
    return favoriteLink(node);
  }

  /** Series info (1.27.0): the (i) and the hover summary, for the item's own information. */
  hoverNodeId(node: CatalogNodeDto): string | null {
    return node.hasSeriesInfo ? node.id : null;
  }

  isStack(node: CatalogNodeDto): boolean {
    return isFavoriteStack(node);
  }

  stackParams(node: CatalogNodeDto): Record<string, string> | null {
    return favoriteQueryParams(node);
  }

  stackLabel(node: CatalogNodeDto): string {
    return favoriteStackLabel(node);
  }

  /** A list row's subtitle: a stack's count of favorites, a folder, or an archive's pages. */
  rowSub(node: CatalogNodeDto): string {
    if (this.isStack(node)) return this.stackLabel(node);
    if (node.kind === 'Folder') return 'Folder';
    return node.pageCount ? `${node.pageCount} pages` : 'Archive';
  }

  /** The archive context line (lane W): an archive's folder name; none for a folder or a stack (the card is the folder). */
  contextOf(node: CatalogNodeDto): string | null {
    return node.kind === 'Archive' && !this.isStack(node) ? node.contextName ?? null : null;
  }

  trackKey(node: CatalogNodeDto): string {
    return favoriteTrackKey(node);
  }

  coverSrc(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }

  kindIcon(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'folder' : 'menu_book';
  }

  kindLabel(node: CatalogNodeDto): string {
    return node.kind === 'Folder' ? 'Folder' : 'Archive';
  }
}
