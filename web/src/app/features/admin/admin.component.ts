import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import {
  AdminUserDto,
  DirectoryListingDto,
  LibraryDto,
  RegisterLibraryRequest,
  CreateUserRequest,
} from '../../core/api/api-types';

/**
 * Admin component. Shows library and user administration.
 * No file delete/rename controls are provided.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatListModule,
    MatIconModule,
    MatFormFieldModule,
    MatInputModule,
    MatCheckboxModule,
  ],
  template: `
    <h2>Administration</h2>

    <!-- Libraries -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Libraries</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loadingLibs()) {
          <p>Loading...</p>
        } @else if (libraries().length === 0) {
          <p>No libraries registered.</p>
        } @else {
          <mat-list>
            @for (lib of libraries(); track lib.id) {
              <mat-list-item>
                <mat-icon matListItemIcon>folder</mat-icon>
                <div matListItemTitle>{{ lib.name }}</div>
                <div matListItemLine>
                  @if (lib.itemCount !== null) { {{ lib.itemCount }} items }
                  @if (lib.isScanning) { — Scanning... }
                </div>
                <button matListItemMetaIcon matIconButton (click)="triggerScan(lib.id)">
                  <mat-icon>refresh</mat-icon>
                </button>
              </mat-list-item>
            }
          </mat-list>
        }

        <mat-divider></mat-divider>
        <h4>Register New Library</h4>
        <mat-form-field appearance="outline" floatLabel="always">
          <mat-label>Display Name</mat-label>
          <input matInput [(ngModel)]="newLibName" placeholder="My Manga Collection">
        </mat-form-field>
        <mat-form-field appearance="outline" floatLabel="always">
          <mat-label>Root Path (server-side mount)</mat-label>
          <input matInput [(ngModel)]="newLibPath" placeholder="/media/library1">
        </mat-form-field>
        <button mat-stroked-button type="button" class="browse-btn" (click)="toggleBrowser()">
          <mat-icon>folder_open</mat-icon> {{ browserOpen() ? 'Hide browser' : 'Browse…' }}
        </button>
        <button mat-raised-button color="primary" (click)="registerLibrary()" [disabled]="!newLibName() || !newLibPath()">
          Register
        </button>

        @if (browserOpen()) {
          <div class="browser">
            @if (browseLoading()) {
              <p>Loading…</p>
            } @else if (listing() && !listing()!.available) {
              <p class="browser-hint">
                No media browse root is configured or accessible on the server.
                Mount your media read-only (e.g. at <code>/media</code>) or set
                <code>MangaPlex:Storage:MediaRoot</code>, then reload — or type the
                path above directly.
              </p>
            } @else if (listing()) {
              <div class="browser-bar">
                <button mat-icon-button type="button" (click)="browseUp()"
                        [disabled]="listing()!.parent === null" aria-label="Up one folder">
                  <mat-icon>arrow_upward</mat-icon>
                </button>
                <span class="browser-path" [title]="listing()!.current || ''">{{ listing()!.current }}</span>
                <button mat-flat-button color="primary" type="button" (click)="useCurrentFolder()">
                  Use this folder
                </button>
              </div>
              @if (listing()!.entries.length === 0) {
                <p class="browser-hint">No subfolders here.</p>
              } @else {
                <mat-list class="browser-list">
                  @for (entry of listing()!.entries; track entry.path) {
                    <mat-list-item>
                      <mat-icon matListItemIcon>folder</mat-icon>
                      <div matListItemTitle class="browser-entry"
                           (click)="entry.hasChildren ? browseInto(entry.path) : useFolder(entry.path)">
                        {{ entry.name }}
                      </div>
                      <span matListItemMeta class="browser-actions">
                        <button mat-button type="button" (click)="useFolder(entry.path)">Select</button>
                        @if (entry.hasChildren) {
                          <button mat-icon-button type="button"
                                  (click)="browseInto(entry.path)" aria-label="Open folder">
                            <mat-icon>chevron_right</mat-icon>
                          </button>
                        }
                      </span>
                    </mat-list-item>
                  }
                </mat-list>
              }
            }
          </div>
        }
      </mat-card-content>
    </mat-card>

    <!-- Users -->
    <mat-card>
      <mat-card-header>
        <mat-card-title>Users</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loadingUsers()) {
          <p>Loading...</p>
        } @else {
          <mat-list>
            @for (user of users(); track user.id) {
              <mat-list-item>
                <mat-icon matListItemIcon>{{ user.isAdmin ? 'admin_panel_settings' : 'person' }}</mat-icon>
                <div matListItemTitle>{{ user.username }}</div>
                <div matListItemLine>
                  {{ user.isAdmin ? 'Admin' : 'Reader' }}
                  @if (!user.isActive) { — Disabled }
                </div>
                <button matListItemMetaIcon matIconButton (click)="resetPassword(user.id)" matTooltip="Reset password">
                  <mat-icon>vpn_key</mat-icon>
                </button>
              </mat-list-item>
            }
          </mat-list>
        }

        <mat-divider></mat-divider>
        <h4>Create New User</h4>
        <mat-form-field appearance="outline">
          <mat-label>Username</mat-label>
          <input matInput [(ngModel)]="newUsername">
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label>Password</mat-label>
          <input matInput type="password" [(ngModel)]="newUserPassword">
        </mat-form-field>
        <p>
          <mat-checkbox [(ngModel)]="newUserIsAdmin">Admin role</mat-checkbox>
        </p>
        <button mat-raised-button color="primary" (click)="createUser()" [disabled]="!newUsername() || !newUserPassword()">
          Create User
        </button>
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    mat-form-field { margin-right: 12px; width: 200px; }
    mat-divider { margin: 16px 0; }
    h4 { margin: 8px 0; }
    .browse-btn { margin-right: 12px; }
    .browser {
      margin-top: 12px;
      border: 1px solid rgba(255, 255, 255, 0.12);
      border-radius: 8px;
      padding: 8px 12px;
      max-width: 640px;
    }
    .browser-bar {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .browser-path {
      flex: 1;
      font-family: monospace;
      font-size: 13px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      opacity: 0.85;
    }
    .browser-hint { font-size: 13px; opacity: 0.8; }
    .browser-list { max-height: 260px; overflow-y: auto; }
    .browser-entry { cursor: pointer; }
    .browser-actions { display: inline-flex; align-items: center; gap: 4px; }
  `],
})
export class AdminComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);

  readonly loadingLibs = signal(true);
  readonly loadingUsers = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly users = signal<AdminUserDto[]>([]);

  readonly newLibName = signal('');
  readonly newLibPath = signal('');

  // Directory browser state for the library-registration path picker.
  readonly browserOpen = signal(false);
  readonly browseLoading = signal(false);
  readonly listing = signal<DirectoryListingDto | null>(null);

  readonly newUsername = signal('');
  readonly newUserPassword = signal('');
  readonly newUserIsAdmin = signal(false);

  ngOnInit(): void {
    this.loadLibraries();
    this.loadUsers();
  }

  private loadLibraries(): void {
    this.api.getLibraries().subscribe({
      next: (libs) => {
        this.libraries.set(libs);
        this.loadingLibs.set(false);
      },
      error: () => this.loadingLibs.set(false),
    });
  }

  private loadUsers(): void {
    this.api.listUsers().subscribe({
      next: (users) => {
        this.users.set(users);
        this.loadingUsers.set(false);
      },
      error: () => this.loadingUsers.set(false),
    });
  }

  registerLibrary(): void {
    const request: RegisterLibraryRequest = {
      displayName: this.newLibName(),
      rootPath: this.newLibPath(),
    };
    this.api.registerLibrary(request).subscribe({
      next: (lib) => {
        this.libraries.update(libs => [...libs, lib]);
        this.newLibName.set('');
        this.newLibPath.set('');
        this.snackBar.open(`Library "${lib.name}" registered`, 'Close', { duration: 3000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  toggleBrowser(): void {
    const opening = !this.browserOpen();
    this.browserOpen.set(opening);
    if (opening && this.listing() === null) {
      // Start at the current typed path if any, else the browse root.
      this.load(this.newLibPath() || undefined);
    }
  }

  browseInto(path: string): void {
    this.load(path);
  }

  browseUp(): void {
    const parent = this.listing()?.parent;
    if (parent) this.load(parent);
  }

  useFolder(path: string): void {
    this.newLibPath.set(path);
    this.browserOpen.set(false);
  }

  useCurrentFolder(): void {
    const current = this.listing()?.current;
    if (current) this.useFolder(current);
  }

  private load(path?: string): void {
    this.browseLoading.set(true);
    this.api.browseLibraryPaths(path).subscribe({
      next: (listing) => {
        this.listing.set(listing);
        this.browseLoading.set(false);
      },
      error: (err) => {
        this.browseLoading.set(false);
        this.snackBar.open(`Browse failed: ${err.message}`, 'Close', { duration: 5000 });
      },
    });
  }

  triggerScan(libraryId: string): void {
    this.api.triggerScan(libraryId).subscribe({
      next: () => this.snackBar.open('Scan triggered', 'Close', { duration: 3000 }),
      error: (err) => this.snackBar.open(`Scan failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  createUser(): void {
    const request: CreateUserRequest = {
      username: this.newUsername(),
      password: this.newUserPassword(),
      isAdmin: this.newUserIsAdmin(),
    };
    this.api.createUser(request).subscribe({
      next: (user) => {
        this.users.update(users => [...users, user]);
        this.newUsername.set('');
        this.newUserPassword.set('');
        this.newUserIsAdmin.set(false);
        this.snackBar.open(`User "${user.username}" created`, 'Close', { duration: 3000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }

  resetPassword(userId: string): void {
    this.api.resetUserPassword(userId).subscribe({
      next: (res) => {
        this.snackBar.open(`Temporary password: ${res.temporaryPassword}`, 'Close', { duration: 10000 });
      },
      error: (err) => this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 5000 }),
    });
  }
}
