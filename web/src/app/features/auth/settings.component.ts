import { Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule, MatCheckboxChange } from '@angular/material/checkbox';
import { MatSelectModule, MatSelectChange } from '@angular/material/select';

import { AuthService } from '../../core/auth/auth.service';
import { ApiService } from '../../core/api/api.service';
import { ApiError, LibraryDto, LibraryViewPreferencesDto } from '../../core/api/api-types';
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
    MatSelectModule,
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

    <mat-card class="home-window-card">
      <mat-card-header>
        <mat-card-title>New Chapters</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <p class="hint" id="home-window-hint">
          New chapters: show items added within the last N days on the home page.
          A smaller window highlights just what's new; a larger window surfaces
          more of your library's recent activity.
        </p>

        @if (homeWindowLoaded()) {
          <mat-form-field appearance="outline">
            <mat-label>Days</mat-label>
            <input
              matInput
              type="number"
              [min]="homeWindowMin"
              [max]="homeWindowMax"
              [value]="homeWindowDays()"
              [disabled]="homeWindowSaving()"
              aria-describedby="home-window-hint"
              (change)="setHomeWindowDays($event)"
            >
          </mat-form-field>
          @if (homeWindowError()) {
            <div class="error">{{ homeWindowError() }}</div>
          }
        } @else {
          <p class="muted">Loading…</p>
        }

        <div class="home-libraries">
          <h4>Show new chapters from</h4>
          <p class="hint">
            Choose which libraries contribute cards to the home "New chapters" row.
            Unchecked libraries are hidden from that row; they still appear everywhere else.
          </p>
          @if (homeLibrariesError()) {
            <div class="error">{{ homeLibrariesError() }}</div>
          }
          @if (librariesLoading()) {
            <p class="muted">Loading libraries…</p>
          } @else if (libraries().length === 0) {
            <p class="muted">No libraries yet.</p>
          } @else {
            <div class="home-library-list">
              @for (lib of libraries(); track lib.id) {
                <mat-checkbox
                  [checked]="isHomeLibraryShown(lib.id)"
                  [disabled]="homeLibrariesSaving()"
                  (change)="toggleHomeLibrary(lib.id, $event)"
                >
                  {{ lib.name }}
                </mat-checkbox>
              }
            </div>
          }
        </div>
      </mat-card-content>
    </mat-card>

    <mat-card class="performance-card">
      <mat-card-header>
        <mat-card-title>Performance</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <p class="hint">
          How many items the library loads per batch as you scroll. A smaller size
          loads the first screen faster on slow connections or very large libraries; a
          larger size scrolls further with fewer pauses. Applies the next time a library
          view loads.
        </p>

        @if (pageSizeLoaded()) {
          <mat-form-field appearance="outline">
            <mat-label>Items per load</mat-label>
            <mat-select [value]="pageSize()" [disabled]="pageSizeSaving()"
                        (selectionChange)="setPageSize($event)">
              @for (n of pageSizeOptions; track n) {
                <mat-option [value]="n">{{ n }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          @if (pageSizeError()) {
            <div class="error">{{ pageSizeError() }}</div>
          }
        } @else {
          <p class="muted">Loading…</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 0 auto; }
    .private-libraries-card { margin-top: 24px; }
    .home-window-card { margin-top: 24px; }
    .home-window-card mat-form-field { width: 120px; }
    .home-libraries { margin-top: 8px; }
    .home-libraries h4 { margin: 8px 0; font-size: 14px; }
    .home-library-list { display: flex; flex-direction: column; gap: 8px; }
    .performance-card { margin-top: 24px; }
    .performance-card mat-form-field { width: 160px; }
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

  // Items-per-load performance option (1.10.0, F5). Relocated OUT of the browse
  // View menu into Settings: it is the per-user initial/per-page item count for the
  // library's infinite scroll (LibraryViewPreferencesDto.libraryPageSize, reused from
  // 1.8.0 — not a new preference), which is an initial-load/performance trade-off, not
  // a browse control. The full preferences blob is loaded first and echoed back on
  // save so the browse view's other fields (viewMode, sort, direction, cardSize) are
  // never clobbered by this single change.
  readonly pageSizeOptions = [25, 50, 100, 200];
  readonly defaultPageSize = 50;
  private readonly pageSizeMin = 10;
  private readonly pageSizeMax = 500;
  readonly pageSize = signal<number>(this.defaultPageSize);
  readonly pageSizeLoaded = signal(false);
  readonly pageSizeSaving = signal(false);
  readonly pageSizeError = signal<string | null>(null);
  /** Last-loaded library-view preferences, echoed back on save so nothing else is lost. */
  private libraryPrefs: LibraryViewPreferencesDto | null = null;

  // Home "New chapters" recency window, in days (1.12.0 refinement). Per-user override for
  // RecentChaptersService's previously-hardcoded 30-day window, stored on the same
  // LibraryViewPreferencesDto blob as libraryPageSize (homeRecentWindowDays, reusing this
  // screen's existing "load once, echo back on save" preferences round-trip rather than a
  // new endpoint).
  readonly homeWindowMin = 1;
  readonly homeWindowMax = 365;
  readonly defaultHomeWindowDays = 30;
  readonly homeWindowDays = signal<number>(this.defaultHomeWindowDays);
  readonly homeWindowLoaded = signal(false);
  readonly homeWindowSaving = signal(false);
  readonly homeWindowError = signal<string | null>(null);

  // Home "New chapters" library visibility (1.12.0 refinement): the per-user, server-
  // persisted EXCLUDED set (GET/PUT /reading/home-libraries). A library is SHOWN on the
  // home New-chapters row when it is NOT in this set. This picker moved here from the home
  // page (owner refinement) so all New-chapters configuration lives under Settings.
  readonly homeExcludedLibraryIds = signal<ReadonlySet<string>>(new Set());
  readonly homeLibrariesSaving = signal(false);
  readonly homeLibrariesError = signal<string | null>(null);

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
    this.api.getHomeLibraries().subscribe({
      next: (dto) => this.homeExcludedLibraryIds.set(new Set(dto.excludedLibraryIds)),
      error: () => { /* leave empty: showing every library is the safe default */ },
    });
    this.api.getLibraryPreferences().subscribe({
      next: (p) => {
        this.libraryPrefs = p;
        this.pageSize.set(this.resolvePageSize(p.libraryPageSize));
        this.pageSizeLoaded.set(true);
        this.homeWindowDays.set(this.resolveHomeWindowDays(p.homeRecentWindowDays));
        this.homeWindowLoaded.set(true);
      },
      error: () => {
        this.pageSizeLoaded.set(true); // show the default (50)
        this.homeWindowLoaded.set(true); // show the default (30)
      },
    });
  }

  /** A stored libraryPageSize that is a sane integer, else the default (50). */
  private resolvePageSize(value: number | undefined): number {
    const n = Number(value);
    return Number.isInteger(n) && n >= this.pageSizeMin && n <= this.pageSizeMax ? n : this.defaultPageSize;
  }

  /**
   * A stored homeRecentWindowDays clamped to 1-365, mirroring the server's own clamp
   * (RecentChaptersService); 0/unset/non-finite/non-positive falls back to the default (30).
   */
  private resolveHomeWindowDays(value: number | undefined): number {
    const n = Number(value);
    if (!Number.isFinite(n) || n <= 0) return this.defaultHomeWindowDays;
    return Math.min(Math.max(Math.trunc(n), this.homeWindowMin), this.homeWindowMax);
  }

  /**
   * Persist the chosen items-per-load size. Echoes the whole last-loaded library-view
   * preferences blob back with only libraryPageSize changed, so the browse view's other
   * presentation fields round-trip untouched. Reverts the control on failure.
   */
  setPageSize(change: MatSelectChange): void {
    const next = Number(change.value);
    if (!this.pageSizeOptions.includes(next) || next === this.pageSize()) return;
    const previous = this.pageSize();
    this.pageSize.set(next);
    this.pageSizeSaving.set(true);
    this.pageSizeError.set(null);

    const body: LibraryViewPreferencesDto = {
      ...(this.libraryPrefs ?? { viewMode: 'card', density: 'comfortable', sort: 'name' }),
      libraryPageSize: next,
    };
    this.api.setLibraryPreferences(body).subscribe({
      next: () => { this.libraryPrefs = body; this.pageSizeSaving.set(false); },
      error: (err: ApiError) => {
        this.pageSize.set(previous);
        this.pageSizeSaving.set(false);
        this.pageSizeError.set(err.message || 'Failed to save the items-per-load setting');
      },
    });
  }

  /**
   * Persist the chosen home "New chapters" window (days). Clamps the raw input the same way
   * the server does, echoes the whole last-loaded library-view preferences blob back with only
   * homeRecentWindowDays changed (like setPageSize), and reverts the control on failure.
   */
  setHomeWindowDays(event: Event): void {
    const input = event.target as HTMLInputElement;
    const next = this.resolveHomeWindowDays(Number(input.value));
    input.value = String(next); // reflect the clamped value even on a no-op edit

    if (next === this.homeWindowDays()) return;

    const previous = this.homeWindowDays();
    this.homeWindowDays.set(next);
    this.homeWindowSaving.set(true);
    this.homeWindowError.set(null);

    const body: LibraryViewPreferencesDto = {
      ...(this.libraryPrefs ?? { viewMode: 'card', density: 'comfortable', sort: 'name' }),
      homeRecentWindowDays: next,
    };
    this.api.setLibraryPreferences(body).subscribe({
      next: () => { this.libraryPrefs = body; this.homeWindowSaving.set(false); },
      error: (err: ApiError) => {
        this.homeWindowDays.set(previous);
        this.homeWindowSaving.set(false);
        this.homeWindowError.set(err.message || 'Failed to save the new chapters window');
      },
    });
  }

  isHomeLibraryShown(libraryId: string): boolean {
    return !this.homeExcludedLibraryIds().has(libraryId);
  }

  /**
   * Show/hide one library on the home "New chapters" row. Checked = SHOWN (not excluded).
   * Sends the whole updated EXCLUDED set (replacement semantics); reverts on failure.
   */
  toggleHomeLibrary(libraryId: string, change: MatCheckboxChange): void {
    const previous = this.homeExcludedLibraryIds();
    const next = new Set(previous);
    if (change.checked) next.delete(libraryId); else next.add(libraryId);
    this.homeExcludedLibraryIds.set(next);
    this.homeLibrariesSaving.set(true);
    this.homeLibrariesError.set(null);
    this.api.putHomeLibraries([...next]).subscribe({
      next: () => this.homeLibrariesSaving.set(false),
      error: (err: ApiError) => {
        this.homeExcludedLibraryIds.set(previous);
        this.homeLibrariesSaving.set(false);
        this.homeLibrariesError.set(err.message || 'Failed to update which libraries show new chapters');
      },
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
