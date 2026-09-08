import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../core/auth/auth.service';
import { ApiError } from '../../core/api/api-types';

/**
 * Login component. Submits credentials via the AuthService.
 * No token is stored in localStorage. The server sets an httpOnly session cookie.
 */
@Component({
  selector: 'app-login',
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
    <div class="login-container">
      <mat-card>
        <mat-card-header>
          <mat-card-title>MangaPlex Login</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field appearance="outline">
              <mat-label>Username</mat-label>
              <input matInput formControlName="username" autocomplete="username" required>
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>Password</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="current-password" required>
            </mat-form-field>

            @if (error()) {
              <div class="error">{{ error() }}</div>
            }

            <button
              mat-raised-button
              color="primary"
              type="submit"
              [disabled]="form.invalid || loading()"
            >
              @if (loading()) {
                <mat-spinner diameter="20"></mat-spinner>
              } @else {
                Login
              }
            </button>
          </form>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    .login-container {
      display: flex;
      justify-content: center;
      align-items: center;
      min-height: 60vh;
    }
    mat-card {
      max-width: 400px;
      width: 100%;
    }
    form {
      display: flex;
      flex-direction: column;
      gap: 16px;
    }
    .error {
      color: #f44336;
      font-size: 14px;
    }
    button[type="submit"] {
      align-self: flex-end;
    }
  `],
})
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group({
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  submit(): void {
    if (this.form.invalid) return;

    this.loading.set(true);
    this.error.set(null);

    this.auth.login({
      username: this.form.value.username!,
      password: this.form.value.password!,
    }).subscribe({
      next: (user) => {
        this.loading.set(false);
        // Accounts with a temporary password must change it before entering the app.
        this.router.navigate([user.forcePasswordChange ? '/password-change' : '/']);
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err.message || 'Login failed');
      },
    });
  }
}
