import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule, MatCheckboxChange } from '@angular/material/checkbox';

import { AuthService } from '../../core/auth/auth.service';
import { ApiService } from '../../core/api/api.service';
import { ApiError, LibraryDto } from '../../core/api/api-types';
import { ReadingPreferencesCardComponent } from './reading-preferences-card.component';

/**
 * Settings component. Allows the user to change their password and to mark
 * libraries "Private" (1.4.0) — the per-user, server-persisted designation that
 * the Incognito session toggle hides from listing/discovery surfaces. See
 * {@link IncognitoService} for the session-state half of the design.
 */
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCheckboxModule,
    ReadingPreferencesCardComponent,
  ],
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Account Settings</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <h3>Change Password</h3>
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field appearance="outline">
            <mat-label>Current Password</mat-label>
            <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" required>
          </mat-form-field>

          <mat-form-field appearance="outline">
            <mat-label>New Password</mat-label>
            <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required>
          </mat-form-field>

          <mat-form-field appearance="outline">
            <mat-label>Confirm New Password</mat-label>
            <input matInput type="password" formControlName="confirmPassword" autocomplete="new-password" required>
          </mat-form-field>

          @if (error()) {
            <div class="error">{{ error() }}</div>
          }
          @if (success()) {
            <div class="success">Password changed successfully.</div>
          }

          <button
            mat-raised-button
            color="primary"
            type="submit"
            [disabled]="form.invalid || loading()"
          >
            {{ loading() ? 'Changing...' : 'Change Password' }}
          </button>
        </form>
      </mat-card-content>
    </mat-card>

    <mat-card class="private-libraries-card">
      <mat-card-header>
        <mat-card-title>Private Libraries</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">
          Libraries marked Private are hidden from Home, search, and browse while
          Incognito is on (the default each time you open the app). A direct
          reader link still opens. Turn Incognito off from the user menu to see
          everything again.
        </p>

        @if (privateLibrariesError()) {
          <div class="error">{{ privateLibrariesError() }}</div>
        }

        @if (librariesLoading()) {
          <p class="muted">Loading libraries…</p>
        } @else if (libraries().length === 0) {
          <p class="muted">No libraries yet.</p>
        } @else {
          <div class="private-library-list">
            @for (lib of libraries(); track lib.id) {
              <mat-checkbox
                [checked]="privateLibraryIds().has(lib.id)"
                [disabled]="privateSaving()"
                (change)="togglePrivate(lib.id, $event)"
              >
                {{ lib.name }}
              </mat-checkbox>
            }
          </div>
        }
      </mat-card-content>
    </mat-card>

    <app-reading-preferences-card />
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 0 auto; }
    .private-libraries-card { margin-top: 24px; }
    form { display: flex; flex-direction: column; gap: 16px; }
    .error { color: #f44336; font-size: 14px; }
    .success { color: #4caf50; font-size: 14px; }
    .muted { color: #999; font-size: 14px; }
    .hint { color: #999; font-size: 13px; margin: 0 0 16px; }
    h3 { margin: 0 0 16px 0; }
    .private-library-list { display: flex; flex-direction: column; gap: 8px; }
  `],
})
export class SettingsComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly api = inject(ApiService);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal(false);

  readonly libraries = signal<LibraryDto[]>([]);
  readonly librariesLoading = signal(true);
  readonly privateLibraryIds = signal<ReadonlySet<string>>(new Set());
  readonly privateSaving = signal(false);
  readonly privateLibrariesError = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', Validators.required],
  });

  ngOnInit(): void {
    this.api.getAllLibraries().subscribe({
      next: (libs) => { this.libraries.set(libs); this.librariesLoading.set(false); },
      error: () => this.librariesLoading.set(false),
    });
    this.api.getPrivateLibraries().subscribe({
      next: (dto) => this.privateLibraryIds.set(new Set(dto.libraryIds)),
      error: () => { /* leave the list empty; nothing marked Private is a safe default */ },
    });
  }

  /** Marks/unmarks a library Private. Sends the whole updated set (replacement semantics). */
  togglePrivate(libraryId: string, change: MatCheckboxChange): void {
    const previous = this.privateLibraryIds();
    const next = new Set(previous);
    if (change.checked) next.add(libraryId); else next.delete(libraryId);

    this.privateLibraryIds.set(next);
    this.privateSaving.set(true);
    this.privateLibrariesError.set(null);

    this.api.setPrivateLibraries([...next]).subscribe({
      next: () => this.privateSaving.set(false),
      error: (err: ApiError) => {
        this.privateLibraryIds.set(previous);
        this.privateSaving.set(false);
        this.privateLibrariesError.set(err.message || 'Failed to update Private libraries');
      },
    });
  }

  submit(): void {
    if (this.form.invalid) return;

    const newPassword = this.form.value.newPassword!;
    const confirmPassword = this.form.value.confirmPassword!;

    if (newPassword !== confirmPassword) {
      this.error.set('Passwords do not match.');
      return;
    }

    this.loading.set(true);
    this.error.set(null);
    this.success.set(false);

    this.auth.changePassword({
      currentPassword: this.form.value.currentPassword!,
      newPassword,
    }).subscribe({
      next: () => {
        this.loading.set(false);
        this.success.set(true);
        this.form.reset();
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err.message || 'Password change failed');
      },
    });
  }
}
