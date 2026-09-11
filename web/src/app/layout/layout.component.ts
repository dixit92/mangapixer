import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, Router, NavigationEnd } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs/operators';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';

import { AuthService } from '../core/auth/auth.service';
import { IncognitoService } from '../core/incognito/incognito.service';
import { LibrarySidebarComponent } from '../shared/library-sidebar.component';

/**
 * Main layout shell. Toolbar + user menu, and (1.5.0) the persistent
 * app-shell **library sidebar** rendered as a two-column shell beside the
 * router-outlet on content routes.
 *
 * The sidebar is excluded from the immersive reader (`/reader/:id`), which is a
 * fullscreen overlay; the auth routes (login / setup / activate /
 * password-change) are top-level routes *outside* this component, so they never
 * mount the shell and need no gating here. Responsive: the shell stacks and the
 * sidebar becomes a horizontal rail on narrow screens.
 */
@Component({
  selector: 'app-layout',
  standalone: true,
  imports: [
    RouterOutlet,
    RouterLink,
    MatToolbarModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    LibrarySidebarComponent,
  ],
  template: `
    <mat-toolbar color="primary">
      <a routerLink="/" class="brand">MangaPlex</a>
      <span class="spacer"></span>

      @if (auth.isAuthenticated()) {
        <button mat-button routerLink="/libraries">Libraries</button>
        <button mat-button routerLink="/search">Search</button>

        <button mat-icon-button [matMenuTriggerFor]="userMenu">
          <mat-icon>account_circle</mat-icon>
        </button>
        <mat-menu #userMenu="matMenu">
          <div class="user-info">
            <mat-icon>person</mat-icon>{{ auth.currentUser()?.username }}
          </div>
          <button mat-menu-item (click)="toggleIncognito()">
            <mat-icon>{{ incognito.isIncognito() ? 'visibility_off' : 'visibility' }}</mat-icon>
            Incognito: {{ incognito.isIncognito() ? 'On' : 'Off' }}
          </button>
          <button mat-menu-item routerLink="/settings">
            <mat-icon>settings</mat-icon>Settings
          </button>
          @if (auth.isAdmin()) {
            <button mat-menu-item routerLink="/admin">
              <mat-icon>admin_panel_settings</mat-icon>MangaPlex Administration
            </button>
          }
          <button mat-menu-item (click)="logout()">
            <mat-icon>logout</mat-icon>Logout
          </button>
        </mat-menu>
      } @else {
        <button mat-button routerLink="/login">Login</button>
      }
    </mat-toolbar>

    <div class="shell">
      @if (showSidebar()) {
        <app-library-sidebar></app-library-sidebar>
      }
      <main class="content" [class.full-bleed]="!showSidebar()">
        <router-outlet></router-outlet>
      </main>
    </div>
  `,
  styles: [`
    .brand {
      text-decoration: none;
      color: inherit;
      font-weight: 500;
      margin-right: 16px;
    }
    .spacer { flex: 1 1 auto; }
    /* Two-column app shell (1.5.0): the persistent library sidebar sits on the
       window's left edge with the content column beside it. The sidebar reaches
       the edge itself, so the content column no longer needs the old home
       full-bleed hack. */
    .shell {
      display: flex; align-items: stretch;
      min-height: calc(100dvh - 64px); /* 64px = mat-toolbar height */
    }
    /* Density (1.5.0): the old shell hard-capped content at 1200px; the content
       column now fills the space left of the sidebar with a responsive gutter. */
    .content {
      flex: 1 1 auto; min-width: 0;
      padding: var(--mp-gutter-y) var(--mp-gutter);
    }
    /* Sidebar-less routes (the immersive reader) get the full viewport width with
       no gutter - the reader paints its own fullscreen surface. */
    .content.full-bleed { padding: 0; }
    /* Narrow screens: stack the shell so the sidebar becomes a horizontal rail
       above the content (the sidebar's own media query switches it to a row). */
    @media (max-width: 700px) {
      .shell { flex-direction: column; min-height: 0; }
    }
    .user-info {
      padding: 8px 16px; font-weight: 500;
      display: flex; align-items: center; gap: 8px; opacity: 0.85;
    }
    .user-info mat-icon { font-size: 20px; width: 20px; height: 20px; }
  `],
})
export class LayoutComponent {
  readonly auth = inject(AuthService);
  readonly incognito = inject(IncognitoService);
  private readonly router = inject(Router);

  /**
   * Whether the app-shell library sidebar (and the two-column, gutter-padded
   * content layout) is shown. The only in-shell route that must be excluded is
   * the immersive reader (`/reader/:id`) - a fullscreen overlay. The auth routes
   * live outside this component entirely, so they need no check here. Tracked
   * reactively off router navigation (seeded with the current URL for the first
   * paint / a deep link).
   */
  private isReaderUrl(): boolean {
    return this.router.url.split(/[?#]/)[0].startsWith('/reader');
  }
  readonly showSidebar = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => !this.isReaderUrl()),
      startWith(!this.isReaderUrl()),
    ),
    { initialValue: !this.isReaderUrl() },
  );

  /**
   * Toggles Incognito, then reloads so every already-fetched view re-requests with
   * the new `X-Incognito` header (stale lists were the "needs a manual refresh /
   * navigate away and back" bug). The toggle survives the reload because
   * IncognitoService persists it to sessionStorage.
   */
  toggleIncognito(): void {
    this.incognito.toggle();
    window.location.reload();
  }

  logout(): void {
    this.auth.logout().subscribe(() => {
      this.router.navigate(['/login']);
    });
  }
}
