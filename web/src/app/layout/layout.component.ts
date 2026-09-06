import { Component, inject } from '@angular/core';
import { RouterOutlet, RouterLink, Router } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';

import { AuthService } from '../core/auth/auth.service';

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
          <div class="user-info">{{ auth.currentUser()?.username }}</div>
          <button mat-menu-item routerLink="/settings">Settings</button>
          @if (auth.isAdmin()) {
            <button mat-menu-item routerLink="/admin">Admin</button>
          }
          <button mat-menu-item (click)="logout()">Logout</button>
        </mat-menu>
      } @else {
        <button mat-button routerLink="/login">Login</button>
      }
    </mat-toolbar>

    <main class="content">
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
    .content { padding: 16px; max-width: 1200px; margin: 0 auto; }
    .user-info { padding: 8px 16px; font-weight: 500; }
  `],
})
export class LayoutComponent {
  readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  logout(): void {
    this.auth.logout().subscribe(() => {
      this.router.navigate(['/login']);
    });
  }
}
