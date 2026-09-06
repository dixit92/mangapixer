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
 * First-run setup wizard. Shown only when the instance has no users yet
 * (audit finding F2 — the server ships no default credential). Creates the
 * first admin account, which is signed in immediately on success.
 */
@Component({
  selector: 'app-setup',
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
    <div class="setup-container">
      <mat-card>
        <mat-card-header>
          <mat-card-title>Welcome to MangaPlex</mat-card-title>
          <mat-card-subtitle>Create your administrator account</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <p class="intro">
            This is the first run of this instance. Choose the admin username and
            password you will use to sign in. There is no default account.
          </p>
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field appearance="outline">
              <mat-label>Admin username</mat-label>
              <input matInput formControlName="username" autocomplete="username" required>
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>Password</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="new-password" required>
              <mat-hint>At least 8 characters.</mat-hint>
            </mat-form-field>

            <mat-form-field appearance="outline">
              <mat-label>Confirm password</mat-label>
              <input matInput type="password" formControlName="confirm" autocomplete="new-password" required>
            </mat-form-field>

            @if (form.hasError('mismatch') && form.get('confirm')?.touched) {
              <div class="error">Passwords do not match.</div>
            }
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
                Create account
              }
            </button>
          </form>
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    .setup-container {
      display: flex;
      justify-content: center;
      align-items: center;
      min-height: 60vh;
    }
    mat-card {
      max-width: 440px;
      width: 100%;
    }
    .intro {
      font-size: 14px;
      opacity: 0.8;
      margin: 8px 0 16px;
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
export class SetupComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group(
    {
      username: ['', Validators.required],
      password: ['', [Validators.required, Validators.minLength(8)]],
      confirm: ['', Validators.required],
    },
    { validators: [passwordsMatch] },
  );

  submit(): void {
    if (this.form.invalid) return;

    this.loading.set(true);
    this.error.set(null);

    this.auth.setup({
      username: this.form.value.username!,
      password: this.form.value.password!,
    }).subscribe({
      next: () => {
        this.loading.set(false);
        this.router.navigate(['/']);
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err.message || 'Setup failed');
      },
    });
  }
}

function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  const password = group.get('password')?.value;
  const confirm = group.get('confirm')?.value;
  return password === confirm ? null : { mismatch: true };
}
