import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../core/auth/auth.service';
import { ApiError } from '../../core/api/api-types';

/**
 * Forced password-change screen. Shown when a signed-in account has
 * ForcePasswordChange set (admin-created accounts, admin resets). Until the
 * password is changed the server rejects every non-auth request, so this screen
 * is the only thing such an account can use.
 *
 * On success the security stamp rotates (invalidating the temp-password session),
 * so we immediately re-sign-in with the new password and continue to the app —
 * the user never sees a second login prompt.
 */
@Component({
  selector: 'app-password-change',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <div class="pc-container">
      <mat-card>
        <mat-card-header>
          <mat-card-title>Set a new password</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          <p class="hint">
            Your account was given a temporary password. Choose a new password to
            continue{{ username() ? ', ' + username() : '' }}.
          </p>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field appearance="outline">
              <mat-label>Current (temporary) password</mat-label>
              <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" required>
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>New password</mat-label>
              <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required>
              @if (form.controls.newPassword.touched && form.controls.newPassword.hasError('minlength')) {
                <mat-error>At least 8 characters.</mat-error>
              }
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>Confirm new password</mat-label>
              <input matInput type="password" formControlName="confirmPassword" autocomplete="new-password" required>
              @if (form.controls.confirmPassword.touched && form.hasError('mismatch')) {
                <mat-error>Passwords do not match.</mat-error>
              }
            </mat-form-field>

            @if (error()) {
              <div class="error">{{ error() }}</div>
            }

            <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || loading()">
              @if (loading()) {
                <mat-spinner diameter="20"></mat-spinner>
              } @else {
                Change password
              }
            </button>
          </form>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    .pc-container { display: flex; justify-content: center; align-items: center; min-height: 60vh; }
    mat-card { max-width: 440px; width: 100%; }
    .hint { color: #aaa; font-size: 14px; margin-bottom: 8px; }
    form { display: flex; flex-direction: column; gap: 16px; }
    .error { color: #f44336; font-size: 14px; }
    button[type="submit"] { align-self: flex-end; }
  `],
})
export class PasswordChangeComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly username = signal(this.auth.currentUser()?.username ?? '');

  readonly form = this.fb.nonNullable.group(
    {
      currentPassword: ['', Validators.required],
      newPassword: ['', [Validators.required, Validators.minLength(8)]],
      confirmPassword: ['', Validators.required],
    },
    { validators: [passwordsMatch] },
  );

  submit(): void {
    if (this.form.invalid) return;
    this.loading.set(true);
    this.error.set(null);

    const username = this.auth.currentUser()?.username ?? '';
    const currentPassword = this.form.value.currentPassword!;
    const newPassword = this.form.value.newPassword!;

    this.auth.changePassword({ currentPassword, newPassword }).subscribe({
      next: () => {
        // The temp-password session is now invalid (security stamp rotated).
        // Re-sign-in with the new password so the user lands in the app directly.
        this.auth.login({ username, password: newPassword }).subscribe({
          next: () => {
            this.loading.set(false);
            this.router.navigate(['/']);
          },
          error: () => {
            // Change succeeded but auto-login failed — send them to the login form.
            this.loading.set(false);
            this.router.navigate(['/login']);
          },
        });
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err?.message || 'Could not change password.');
      },
    });
  }
}

/** Cross-field validator: new + confirm must match. */
function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  const nw = group.get('newPassword')?.value;
  const confirm = group.get('confirmPassword')?.value;
  return nw && confirm && nw !== confirm ? { mismatch: true } : null;
}
