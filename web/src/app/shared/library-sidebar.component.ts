import { Component, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs/operators';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../core/api/api.service';
import { LibraryDto } from '../core/api/api-types';
import { readerModeGlyph } from './reader-mode-glyph';

/**
 * App-shell library navigator (1.5.0). Extracted out of `home.component`, where
 * the sidebar was fused to the home page and only *filtered* home's
 * continue-reading strip. Here it is a real navigator rendered by the shell
 * (`layout.component`) on content routes, so you can switch libraries from
 * anywhere - including while deep inside a different library.
 *
 * Key differences from the old in-home sidebar:
 *  - Items **navigate** (`routerLink` to `/` and `/libraries/:id/browse`) rather
 *    than emitting a selection. The owner's intent is cross-library navigation.
 *  - The active item is **derived from the current route** (URL parse), not a
 *    local selection signal - so it stays correct on deep links and when you
 *    navigate by any means (a breadcrumb, the browser back button, a card).
 *  - Each library icon carries a reading-direction badge via the shared
 *    `readerModeGlyph` helper.
 *
 * Desktop collapse persists to localStorage under a **shell-scoped** key (the old
 * home-scoped `mangapixer-home-nav-collapsed` key belonged to the home page).
 *
 * **Page mode (1.10.0, F3):** on phone breakpoints, `layout.component` no longer
 * mounts this component inside the shell at all - it mounts a dedicated route
 * (`mobile-library-nav.component`) instead, reached via a nav control in the
 * toolbar. That page renders this same component with `[pageMode]="true"`,
 * which swaps the narrow sticky column (and its collapse affordance, which
 * makes no sense as a standalone page) for a plain full-width vertical list.
 * Desktop/tablet keep the default (shell, `pageMode` false) rendering
 * unchanged.
 */
@Component({
  selector: 'app-library-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, MatIconModule, MatTooltipModule],
  template: `
    <nav class="sidebar" [class.collapsed]="collapsed() && !pageMode()" [class.page-mode]="pageMode()" aria-label="Libraries">
      @if (!pageMode()) {
        <div class="nav-head">
          <span class="nav-head-label">Library</span>
          <button type="button" class="nav-collapse" (click)="toggleCollapsed()"
                  [matTooltip]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
                  [attr.aria-label]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
                  [attr.aria-expanded]="!collapsed()">
            <mat-icon>{{ collapsed() ? 'chevron_right' : 'chevron_left' }}</mat-icon>
          </button>
        </div>
      }

      <a class="nav-item" routerLink="/" [class.active]="isHome()"
         [attr.aria-current]="isHome() ? 'page' : null"
         [matTooltip]="collapsed() ? 'Home' : ''" matTooltipPosition="right">
        <mat-icon>home</mat-icon><span class="nav-label">Home</span>
      </a>

      <a class="nav-item" routerLink="/favorites" [class.active]="isFavorites()"
         [attr.aria-current]="isFavorites() ? 'page' : null"
         [matTooltip]="collapsed() ? 'Favorites' : ''" matTooltipPosition="right">
        <mat-icon>star</mat-icon><span class="nav-label">Favorites</span>
      </a>

      @for (lib of libraries(); track lib.id) {
        <a class="nav-item" routerLink="/libraries/{{ lib.id }}/browse"
           [class.active]="activeLibraryId() === lib.id"
           [attr.aria-current]="activeLibraryId() === lib.id ? 'page' : null"
           [matTooltip]="collapsed() ? lib.name : ''" matTooltipPosition="right">
          <mat-icon>folder</mat-icon>
          <span class="nav-label">{{ lib.name }}</span>
          @if (glyph(lib); as g) {
            <mat-icon class="nav-dir" [matTooltip]="g.label" [attr.aria-label]="'Reading direction: ' + g.label">{{ g.icon }}</mat-icon>
          }
          @if (lib.itemCount !== null) { <span class="nav-count">{{ lib.itemCount }}</span> }
        </a>
      }
    </nav>
  `,
  styles: [`
    /* Ported from the old home sidebar (1.5.0 taste pass) and generalized for the
       shell. The sidebar is a full-height panel pinned to the window's left edge;
       collapsible to an icon rail on desktop/tablet. On phone it isn't mounted
       in the shell at all (1.10.0, F3 - see the media query below and
       layout.component). Height uses the toolbar offset (64px). */
    .sidebar {
      flex: 0 0 var(--mp-nav-width);
      display: flex; flex-direction: column; gap: 2px;
      padding: 12px 10px;
      background: var(--mp-nav-bg);
      border-right: 1px solid var(--mp-nav-border);
      position: sticky; top: 0; align-self: flex-start;
      height: calc(100dvh - 64px);
      box-sizing: border-box;
      transition: flex-basis .16s ease;
    }
    .sidebar.collapsed { flex-basis: var(--mp-nav-width-collapsed); }
    /* Page mode (1.10.0, F3): the dedicated phone nav page renders this same
       component full-width instead of as a narrow sticky shell column. No
       collapse affordance (the nav-head is omitted entirely - see template),
       full width, roomier touch targets. Applies at every viewport width;
       in practice only the phone page uses it. */
    .sidebar.page-mode {
      position: static;
      height: auto;
      flex: 1 1 auto;
      width: 100%;
      box-sizing: border-box;
      border-right: none;
      padding: 4px var(--mp-gutter) var(--mp-gutter-y);
    }
    .sidebar.page-mode .nav-item { padding: 14px 12px; }
    .nav-head {
      display: flex; align-items: center; justify-content: space-between;
      padding: 2px 6px 8px; min-height: 32px;
    }
    .nav-head-label {
      font-size: 11px; font-weight: 700; letter-spacing: 0.6px;
      text-transform: uppercase; color: #8a8a99; white-space: nowrap; overflow: hidden;
    }
    .sidebar.collapsed .nav-head { justify-content: center; }
    .sidebar.collapsed .nav-head-label { display: none; }
    .nav-collapse {
      display: inline-flex; align-items: center; justify-content: center;
      width: 30px; height: 30px; flex: 0 0 auto;
      border: none; border-radius: 8px; background: transparent; color: #b8b8c4;
      cursor: pointer; transition: background .12s ease;
    }
    .nav-collapse:hover { background: rgba(255,255,255,0.08); }
    .nav-collapse mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .nav-item {
      display: flex; align-items: center; gap: 10px;
      width: 100%; padding: 9px 12px; border: none; border-radius: 8px;
      background: transparent; color: inherit; font: inherit; text-align: left;
      text-decoration: none; cursor: pointer; transition: background .12s ease;
      overflow: hidden;
      /* border-box so the 12px horizontal padding stays INSIDE the 100% width -
         without it the row is 24px wider than its column, so the active-highlight
         background and the trailing count spill past the sidebar's right border. */
      box-sizing: border-box;
    }
    .nav-item:hover { background: rgba(255,255,255,0.06); }
    .nav-item.active { background: var(--mp-accent-bg); color: var(--mp-accent); }
    .nav-item mat-icon { font-size: 20px; width: 20px; height: 20px; flex: 0 0 auto; }
    /* min-width: 0 is required for a flex item to actually shrink below its
       content's intrinsic width - without it the ellipsis below never
       triggers and the trailing glyph/count get pushed past the sidebar's
       right edge instead of the name truncating. */
    .nav-label { flex: 1 1 auto; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    /* Reading-direction badge: a muted, non-interactive glyph tucked against
       the count. Subtle by design - the label lives in the tooltip /
       aria-label. */
    .nav-dir { flex: 0 0 auto; color: #8a8a99; opacity: 0.9; }
    /* Fixed, right-aligned count column so 1- to 5-digit counts (e.g. 8715)
       don't reflow the row or spill past the sidebar edge - the name (above)
       truncates first instead. */
    .nav-count {
      flex: 0 0 auto; min-width: 2.5em; text-align: right;
      font-size: 12px; color: #999; font-variant-numeric: tabular-nums;
    }
    /* Collapsed rail: icons only, centered; labels/counts/direction hidden (names
       surface as tooltips). */
    .sidebar.collapsed .nav-item { justify-content: center; padding: 9px 0; }
    .sidebar.collapsed .nav-label,
    .sidebar.collapsed .nav-dir,
    .sidebar.collapsed .nav-count { display: none; }

    /* Narrow screens (1.10.0, F3): the old horizontal scroll rail at the top of
       the shell is gone - layout.component stops mounting this component in
       the shell altogether at this width and shows a nav control that routes to
       the dedicated phone page (page-mode) instead. This rule is a defensive
       fallback that hides the shell variant outright if it were ever mounted
       here, rather than falling back to the old cramped rail. page-mode is
       untouched by this query and keeps its normal full-width vertical list at
       every viewport width - that page IS the phone experience. */
    @media (max-width: 599.98px) {
      .sidebar:not(.page-mode) {
        display: none;
      }
    }
  `],
})
export class LibrarySidebarComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  /**
   * Page mode (1.10.0, F3): renders as a plain full-width vertical list with no
   * collapse affordance, for the dedicated phone nav page. Defaults to the
   * existing shell (narrow, sticky, collapsible) rendering used on
   * desktop/tablet - unchanged.
   */
  readonly pageMode = input(false);

  readonly libraries = signal<LibraryDto[]>([]);

  /**
   * Desktop/tablet sidebar collapse. Shell-scoped localStorage key (distinct
   * from the old home-only `mangapixer-home-nav-collapsed`). Never applied in
   * page mode (1.10.0, F3) - see the template's `[class.collapsed]` binding.
   */
  private static readonly CollapsedKey = 'mangapixer-nav-collapsed';
  readonly collapsed = signal<boolean>(this.loadCollapsed());

  /** Current top-level path, tracked reactively off navigation (seeded for first paint). */
  private path(): string {
    return this.router.url.split(/[?#]/)[0];
  }
  private readonly currentPath = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.path()),
      startWith(this.path()),
    ),
    { initialValue: this.path() },
  );

  /** Home is active only on the exact root path. */
  readonly isHome = () => this.currentPath() === '/';

  /** Favorites is active on the dedicated favorites route (1.21.0). */
  readonly isFavorites = () => this.currentPath() === '/favorites';

  /**
   * The library id in the current URL, or null. Matches `/libraries/:id` and any
   * deeper path (`/libraries/:id/browse`, `/libraries/:id/browse/:nodeId`), so a
   * library stays highlighted while you browse a subfolder inside it. The bare
   * `/libraries` list route has no id and highlights nothing.
   */
  readonly activeLibraryId = () => {
    const m = /^\/libraries\/([^/]+)/.exec(this.currentPath());
    return m ? m[1] : null;
  };

  constructor() {
    this.api.getLibraries().subscribe({
      next: (libs) => this.libraries.set(libs),
      error: () => this.libraries.set([]),
    });
  }

  glyph(lib: LibraryDto) {
    return readerModeGlyph(lib.defaultReaderMode);
  }

  toggleCollapsed(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    try { localStorage.setItem(LibrarySidebarComponent.CollapsedKey, next ? '1' : '0'); } catch { /* private mode */ }
  }

  private loadCollapsed(): boolean {
    // Default to COLLAPSED (1.10.3): a fresh device / no stored choice
    // starts with the shell sidebar closed; an explicit stored '0'/'1' is respected.
    try {
      const v = localStorage.getItem(LibrarySidebarComponent.CollapsedKey);
      return v === null ? true : v === '1';
    } catch { return true; }
  }
}
