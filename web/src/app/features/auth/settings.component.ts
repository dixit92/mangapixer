import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';

import { AuthService } from '../../core/auth/auth.service';
import { ApiError } from '../../core/api/api-types';

/**
 * Settings component. Allows the user to change their password.
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
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 0 auto; }
    form { display: flex; flex-direction: column; gap: 16px; }
    .error { color: #f44336; font-size: 14px; }
    .success { color: #4caf50; font-size: 14px; }
    h3 { margin: 0 0 16px 0; }
  `],
})
export class SettingsComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly success = signal(false);

  readonly form = this.fb.nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(8)]],
    confirmPassword: ['', Validators.required],
  });

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
