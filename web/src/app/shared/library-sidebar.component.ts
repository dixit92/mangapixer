import { Component, inject, signal } from '@angular/core';
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
 *  - Each library icon carries a reading-direction badge (Task C) via the shared
 *    `readerModeGlyph` helper.
 *
 * Desktop collapse persists to localStorage under a **shell-scoped** key (the old
 * home-scoped `mangaplex-home-nav-collapsed` key belonged to the home page). On
 * narrow screens the media query turns the column into a horizontal scroll rail
 * and collapse is neutralized (a desktop-only affordance).
 */
@Component({
  selector: 'app-library-sidebar',
  standalone: true,
  imports: [CommonModule, RouterLink, MatIconModule, MatTooltipModule],
  template: `
    <nav class="sidebar" [class.collapsed]="collapsed()" aria-label="Libraries">
      <div class="nav-head">
        <span class="nav-head-label">Library</span>
        <button type="button" class="nav-collapse" (click)="toggleCollapsed()"
                [matTooltip]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
                [attr.aria-label]="collapsed() ? 'Expand sidebar' : 'Collapse sidebar'"
                [attr.aria-expanded]="!collapsed()">
          <mat-icon>{{ collapsed() ? 'chevron_right' : 'chevron_left' }}</mat-icon>
        </button>
      </div>

      <a class="nav-item" routerLink="/" [class.active]="isHome()"
         [attr.aria-current]="isHome() ? 'page' : null"
         [matTooltip]="collapsed() ? 'Home' : ''" matTooltipPosition="right">
        <mat-icon>home</mat-icon><span class="nav-label">Home</span>
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
       collapsible to an icon rail on desktop; a horizontal scroll rail on narrow
       screens. Height uses the toolbar offset (64px). */
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
    }
    .nav-item:hover { background: rgba(255,255,255,0.06); }
    .nav-item.active { background: var(--mp-accent-bg); color: var(--mp-accent); }
    .nav-item mat-icon { font-size: 20px; width: 20px; height: 20px; flex: 0 0 auto; }
    .nav-label { flex: 1 1 auto; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    /* Reading-direction badge (Task C): a muted, non-interactive glyph tucked
       against the count. Subtle by design - the label lives in the tooltip /
       aria-label. */
    .nav-dir { flex: 0 0 auto; color: #8a8a99; opacity: 0.9; }
    .nav-count { flex: 0 0 auto; font-size: 12px; color: #999; }
    /* Collapsed rail: icons only, centered; labels/counts/direction hidden (names
       surface as tooltips). */
    .sidebar.collapsed .nav-item { justify-content: center; padding: 9px 0; }
    .sidebar.collapsed .nav-label,
    .sidebar.collapsed .nav-dir,
    .sidebar.collapsed .nav-count { display: none; }

    /* Narrow screens: horizontal scroll rail above the content. Collapse is a
       desktop-only affordance, so the collapsed class is neutralized and the
       toggle is hidden. */
    @media (max-width: 700px) {
      .sidebar, .sidebar.collapsed {
        flex: 0 0 auto; flex-basis: auto; flex-direction: row; width: 100%;
        height: auto; position: static; align-self: stretch;
        overflow-x: auto; padding: 8px var(--mp-gutter);
        border-right: none; border-bottom: 1px solid var(--mp-nav-border);
      }
      .nav-head { display: none; }
      .sidebar.collapsed .nav-item { justify-content: flex-start; padding: 8px 12px; }
      .sidebar.collapsed .nav-label { display: inline; }
      .nav-item { width: auto; white-space: nowrap; }
      .nav-dir, .nav-count { display: none; }
    }
  `],
})
export class LibrarySidebarComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly libraries = signal<LibraryDto[]>([]);

  /**
   * Desktop sidebar collapse. Shell-scoped localStorage key (distinct from the
   * old home-only `mangaplex-home-nav-collapsed`). Ignored on narrow screens
   * (the sidebar is a horizontal rail there - see the media query).
   */
  private static readonly CollapsedKey = 'mangaplex-nav-collapsed';
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
    try { return localStorage.getItem(LibrarySidebarComponent.CollapsedKey) === '1'; } catch { return false; }
  }
}
