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

/**
 * Main layout shell. Contains the toolbar with navigation and user menu.
 * Responsive: collapses menu on small screens.
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

    <main class="content" [class.full-bleed]="isHome()">
      <router-outlet></router-outlet>
    </main>
  `,
  styles: [`
    .brand {
      text-decoration: none;
      color: inherit;
      font-weight: 500;
      margin-right: 16px;
    }
    .spacer { flex: 1 1 auto; }
    /* Density (1.5.0): the old shell hard-capped content at 1200px and centered
       it, leaving large empty gutters on a normal desktop monitor. It now fills
       up to --mp-content-max with a responsive side gutter. */
    .content {
      padding: var(--mp-gutter-y) var(--mp-gutter);
      max-width: var(--mp-content-max);
      margin: 0 auto;
    }
    /* Home is full-bleed so its library sidebar can sit on the actual left edge
       of the window (it manages its own inner padding). */
    .content.full-bleed { padding: 0; max-width: none; }
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
   * True on the home route (`/`). Home is rendered full-bleed so its library
   * sidebar reaches the window's left edge; every other route keeps the
   * width-capped, gutter-padded content column. Tracked reactively off router
   * navigation (seeded with the current URL for the first paint / a deep link).
   */
  private isHomeUrl(): boolean {
    return this.router.url.split(/[?#]/)[0] === '/';
  }
  readonly isHome = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.isHomeUrl()),
      startWith(this.isHomeUrl()),
    ),
    { initialValue: this.isHomeUrl() },
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
