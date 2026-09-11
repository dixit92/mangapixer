import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { AuthService } from '../../core/auth/auth.service';
import { ApiError } from '../../core/api/api-types';

@Component({
  selector: 'app-activate',
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
    <div class="activate-container">
      <mat-card>
        <mat-card-header>
          <mat-card-title>Activate your account</mat-card-title>
          <mat-card-subtitle>Choose a password to get started</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          @if (!token()) {
            <div class="error">Invalid activation link. Please ask your administrator for a new one.</div>
          } @else {
            <form [formGroup]="form" (ngSubmit)="submit()">
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
                  Set password & activate
                }
              </button>
            </form>
          }
        </mat-card-content>
      </mat-card>
    </div>
  `,
  styles: [`
    .activate-container {
      display: flex;
      justify-content: center;
      align-items: center;
      min-height: 60vh;
    }
    mat-card {
      max-width: 440px;
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
export class ActivateComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly token = signal<string | null>(null);

  readonly form = this.fb.nonNullable.group(
    {
      password: ['', [Validators.required, Validators.minLength(8)]],
      confirm: ['', Validators.required],
    },
    { validators: [passwordsMatch] },
  );

  ngOnInit(): void {
    this.token.set(this.route.snapshot.queryParamMap.get('token'));
  }

  submit(): void {
    const t = this.token();
    if (this.form.invalid || !t) return;

    this.loading.set(true);
    this.error.set(null);

    this.auth.activateAccount({
      token: t,
      password: this.form.value.password!,
    }).subscribe({
      next: () => {
        this.loading.set(false);
        this.router.navigate(['/']);
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err?.message || 'Activation failed. The link may be expired or already used.');
      },
    });
  }
}

function passwordsMatch(group: AbstractControl): ValidationErrors | null {
  const password = group.get('password')?.value;
  const confirm = group.get('confirm')?.value;
  return password === confirm ? null : { mismatch: true };
}
